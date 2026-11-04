using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VPass.PrintService.Api;
using VPass.PrintService.Config;
using VPass.PrintService.Printing;

namespace VPass.PrintService.Core;

/// <summary>
/// The reliability core. A single consumer drains <see cref="TriggerBus"/>; every trigger (startup,
/// socket connect, socket message, timer, resume from sleep, retry) does the same thing: pull
/// pending jobs from the server, print each, ack each. Because delivery is pull-based and the server
/// keeps the job until acked, a missed socket message, a sleeping PC or a server restart never loses
/// a badge. The <see cref="AckLedger"/> stops a re-delivered job (lost ack) from printing twice.
/// </summary>
public sealed class JobLoop : BackgroundService
{
    private const int FetchLimit = 10;
    private const int MaxRoundsPerDrain = 50;

    private readonly IPrinterApiClient _api;
    private readonly IPrinterBackend _backend;
    private readonly AckLedger _ledger;
    private readonly AppState _state;
    private readonly TriggerBus _bus;
    private readonly ConfigHolder _config;
    private readonly AppPaths _paths;
    private readonly ILogger<JobLoop> _log;

    private int _httpFailures;
    private int _offlineFailures;
    private DateTime _lastUnpairedLog = DateTime.MinValue;

    public JobLoop(IPrinterApiClient api, IPrinterBackend backend, AckLedger ledger, AppState state, TriggerBus bus,
        ConfigHolder config, AppPaths paths, ILogger<JobLoop> log)
    {
        _api = api; _backend = backend; _ledger = ledger; _state = state; _bus = bus; _config = config; _paths = paths; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _bus.Signal(Trigger.Startup);
        try
        {
            await foreach (Trigger trigger in _bus.Reader.ReadAllAsync(ct))
            {
                // Coalesce bursts (socket message + timer + resume) into one drain.
                while (_bus.Reader.TryRead(out _)) { }
                try
                {
                    await DrainAsync(trigger, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Unexpected error in job loop");
                    _state.RecordError(ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public sealed record DrainResult(int Printed, int Duplicates, int Failed, string? StoppedBecause);

    internal enum Outcome { Printed, Duplicate, Failed, PrinterOffline }

    /// <summary>One pass: fetch → print → ack until the server has nothing left (or we must stop).</summary>
    internal async Task<DrainResult> DrainAsync(Trigger trigger, CancellationToken ct)
    {
        DeviceConfig cfg = _config.Current;
        if (!cfg.IsPaired)
        {
            _state.SetUnpaired();
            if ((DateTime.UtcNow - _lastUnpairedLog).TotalMinutes >= 10)
            {
                _lastUnpairedLog = DateTime.UtcNow;
                _log.LogWarning("Not paired. Run: vpass-print pair <code> --region <au|us|uk|ca>");
            }
            return new DrainResult(0, 0, 0, "unpaired");
        }
        if (_state.NeedsPairing)
        {
            return new DrainResult(0, 0, 0, "needs-pairing");
        }
        if (!cfg.HasPrinter)
        {
            // Don't lease jobs we cannot print; they wait on the server (and expire after 10 min).
            _state.SetDegraded("No printer selected. Run: vpass-print set-printer \"<name>\"");
            return new DrainResult(0, 0, 0, "no-printer");
        }

        int printed = 0, dup = 0, failed = 0;
        _log.LogDebug("Drain ({Trigger})", trigger);

        for (int round = 0; round < MaxRoundsPerDrain; round++)
        {
            PendingJobsResponse pending;
            try
            {
                pending = await _api.GetPendingAsync(FetchLimit, ct);
                _httpFailures = 0;
                _state.LastPollUtc = DateTime.UtcNow;
            }
            catch (RevokedException ex)
            {
                _state.MarkNeedsPairing(ex.Message);
                _log.LogError("Server rejected this device ({Reason}). Printing stopped until it is paired again from the admin portal.", ex.Message);
                return new DrainResult(printed, dup, failed, "revoked");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is ApiException)
            {
                _httpFailures++;
                int delay = Math.Min(60, 1 << Math.Min(_httpFailures, 6));
                _state.RecordError("API unreachable: " + ex.Message);
                _log.LogWarning("Could not fetch jobs ({Error}); retrying in {Delay}s", ex.Message, delay);
                ScheduleRetry(TimeSpan.FromSeconds(delay), ct);
                return new DrainResult(printed, dup, failed, "api-unreachable");
            }

            _state.QueueDepth = pending.Jobs.Count;
            if (pending.Jobs.Count == 0)
            {
                if (_state.State == ServiceState.Degraded && _backend.IsOnline(cfg.OsPrinterName))
                {
                    _state.SetRunning();
                }
                break;
            }

            foreach (PrintJob job in pending.Jobs)
            {
                ct.ThrowIfCancellationRequested();
                Outcome outcome = await ProcessAsync(job, cfg, ct);
                switch (outcome)
                {
                    case Outcome.Printed: printed++; break;
                    case Outcome.Duplicate: dup++; break;
                    case Outcome.Failed: failed++; break;
                    case Outcome.PrinterOffline:
                        _offlineFailures++;
                        int delay = Math.Min(60, 10 << Math.Min(_offlineFailures - 1, 3));
                        _log.LogWarning("Printer unavailable; will retry in {Delay}s", delay);
                        ScheduleRetry(TimeSpan.FromSeconds(delay), ct);
                        return new DrainResult(printed, dup, failed, "printer-offline");
                }
                if (_state.NeedsPairing)
                {
                    return new DrainResult(printed, dup, failed, "revoked");
                }
            }
        }

        _offlineFailures = 0;
        if (printed > 0 || dup > 0 || failed > 0)
        {
            _log.LogInformation("Drain done: {Printed} printed, {Dup} duplicates skipped, {Failed} failed", printed, dup, failed);
        }
        return new DrainResult(printed, dup, failed, null);
    }

    private async Task<Outcome> ProcessAsync(PrintJob job, DeviceConfig cfg, CancellationToken ct)
    {
        if (_ledger.Contains(job.JobId))
        {
            _log.LogInformation("Job {JobId} already printed by this device; acking without printing", job.JobId);
            await SafeAckAsync(job.JobId, new AckRequest { Status = "printed", AppVersion = AppVersion.Current }, ct);
            return Outcome.Duplicate;
        }

        if (string.IsNullOrWhiteSpace(job.BadgeImage))
        {
            _log.LogWarning("Job {JobId} has no badge image", job.JobId);
            _state.RecordFailed();
            await SafeAckAsync(job.JobId, new AckRequest { Status = "failed", Retriable = false, Error = "Empty badge image", AppVersion = AppVersion.Current }, ct);
            return Outcome.Failed;
        }

        string? file = null;
        try
        {
            file = BadgeImage.PrepareFile(job.BadgeImage, job.TemplateType, cfg.RotateLandscape, _paths.TempDir);
            _backend.Print(file, new PrintOptions(cfg.OsPrinterName, cfg.PaperName, job.TemplateType, cfg.RotateLandscape, cfg.Copies));

            // Ledger before ack: if the ack is lost and the server re-delivers, we must not print again.
            _ledger.Add(job.JobId);
            _state.RecordPrinted();
            _state.SetRunning();
            _log.LogInformation("Printed job {JobId} (template {Template}, attempt {Attempt}) on {Printer}", job.JobId, job.TemplateType, job.Attempt, cfg.OsPrinterName);
            await SafeAckAsync(job.JobId, new AckRequest { Status = "printed", AppVersion = AppVersion.Current }, ct);
            return Outcome.Printed;
        }
        catch (PrinterOfflineException ex)
        {
            _state.SetDegraded(ex.Message);
            _log.LogWarning("Job {JobId}: {Error}", job.JobId, ex.Message);
            await SafeAckAsync(job.JobId, new AckRequest { Status = "failed", Retriable = true, Error = ex.Message, AppVersion = AppVersion.Current }, ct);
            return Outcome.PrinterOffline;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state.RecordFailed();
            _state.RecordError(ex.Message);
            _log.LogError(ex, "Job {JobId} failed", job.JobId);
            await SafeAckAsync(job.JobId, new AckRequest { Status = "failed", Retriable = false, Error = Truncate(ex.Message, 900), AppVersion = AppVersion.Current }, ct);
            return Outcome.Failed;
        }
        finally
        {
            if (file != null)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private async Task SafeAckAsync(long jobId, AckRequest ack, CancellationToken ct)
    {
        try
        {
            await _api.AckAsync(jobId, ack, ct);
        }
        catch (RevokedException ex)
        {
            _state.MarkNeedsPairing(ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The server will re-deliver after the lease; the ledger makes that harmless.
            _log.LogWarning("Ack for job {JobId} failed: {Error}", jobId, ex.Message);
        }
    }

    private void ScheduleRetry(TimeSpan delay, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                _bus.Signal(Trigger.Retry);
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
