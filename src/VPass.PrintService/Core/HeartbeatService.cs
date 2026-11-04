using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VPass.PrintService.Api;
using VPass.PrintService.Config;
using VPass.PrintService.Printing;
using VPass.PrintService.Socket;

namespace VPass.PrintService.Core;

/// <summary>
/// Every 60 s: report version / OS printer state / queue depth to the server (adminapp shows
/// online + "update available"), pick up server tuning (poll interval), detect re-pairing done
/// with the CLI while running, and write status.json for `vpass-print status`.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IPrinterApiClient _api;
    private readonly IPrinterBackend _backend;
    private readonly AppState _state;
    private readonly ConfigHolder _config;
    private readonly StatusWriter _status;
    private readonly TriggerBus _bus;
    private readonly WakeSocket _socket;
    private readonly ILogger<HeartbeatService> _log;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    private string _lastIdentity = "";
    private string? _lastAnnouncedVersion;
    private DateTime _lastNeedsPairingLog = DateTime.MinValue;

    public HeartbeatService(IPrinterApiClient api, IPrinterBackend backend, AppState state, ConfigHolder config,
        StatusWriter status, TriggerBus bus, WakeSocket socket, ILogger<HeartbeatService> log)
    {
        _api = api; _backend = backend; _state = state; _config = config; _status = status; _bus = bus; _socket = socket; _log = log;
    }

    public void RequestNow() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DeviceConfig cfg = _config.Current;
            DetectRepair(cfg);
            await BeatAsync(cfg, ct);
            _status.Write(_state, cfg);
            try { await _wake.WaitAsync(Interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>`vpass-print pair` while the service runs: new printer id / token → leave NeedsPairing and restart the pipeline.</summary>
    private void DetectRepair(DeviceConfig cfg)
    {
        string identity = $"{cfg.PrinterId}|{cfg.DeviceTokenProtected?.GetHashCode()}|{cfg.OsPrinterName}";
        if (identity == _lastIdentity)
        {
            return;
        }
        bool first = _lastIdentity.Length == 0;
        _lastIdentity = identity;
        if (first) return;

        _log.LogInformation("Configuration changed (printer {PrinterId}, OS printer '{OsPrinter}')", cfg.PrinterId, cfg.OsPrinterName);
        if (cfg.IsPaired)
        {
            _state.ClearNeedsPairing();
            _socket.RequestReconnect();
            _bus.Signal(Trigger.Startup);
        }
    }

    private async Task BeatAsync(DeviceConfig cfg, CancellationToken ct)
    {
        if (!cfg.IsPaired)
        {
            _state.SetUnpaired();
            return;
        }
        if (_state.NeedsPairing)
        {
            if ((DateTime.UtcNow - _lastNeedsPairingLog).TotalMinutes >= 10)
            {
                _lastNeedsPairingLog = DateTime.UtcNow;
                _log.LogWarning("Waiting to be paired again ({Reason}). Generate a new code in the admin portal and run: vpass-print pair <code>", _state.LastError);
            }
            return;
        }

        bool printerOnline = false;
        try { printerOnline = cfg.HasPrinter && _backend.IsOnline(cfg.OsPrinterName); }
        catch (Exception ex) { _log.LogDebug(ex, "Printer status check failed"); }
        _state.PrinterOnline = printerOnline;

        try
        {
            HeartbeatResponse resp = await _api.HeartbeatAsync(new HeartbeatRequest
            {
                AppVersion = AppVersion.Current,
                Os = AppVersion.OsDescription,
                Platform = AppVersion.Platform,
                Hostname = Environment.MachineName,
                OsPrinterName = cfg.OsPrinterName,
                PrinterOnline = printerOnline,
                QueueDepth = _state.QueueDepth,
                SocketConnected = _state.SocketConnected,
                UptimeSeconds = (long)_state.Uptime.TotalSeconds
            }, ct);

            _state.LastHeartbeatUtc = DateTime.UtcNow;
            if (_state.State == ServiceState.Unpaired)
            {
                _state.SetRunning(); // paired and the server accepts us: no longer "Unpaired" in status
            }
            if (resp.PollIntervalSeconds > 0) _state.PollIntervalSeconds = resp.PollIntervalSeconds;
            _state.LatestVersion = resp.LatestVersion;
            _state.UpdateAvailable = resp.UpdateAvailable;
            _state.DownloadUrl = resp.DownloadUrl;
            if (resp.UpdateAvailable && resp.LatestVersion != _lastAnnouncedVersion)
            {
                _lastAnnouncedVersion = resp.LatestVersion;
                _log.LogInformation("Update available: {Latest} (installed {Current}). {Url}", resp.LatestVersion, AppVersion.Current, resp.DownloadUrl);
            }
            if (!string.IsNullOrEmpty(resp.PrinterName) && resp.PrinterName != cfg.PrinterName)
            {
                _config.Update(c => c.PrinterName = resp.PrinterName!);
            }
            if (_state.State == ServiceState.Degraded && printerOnline && cfg.HasPrinter)
            {
                _state.SetRunning();
            }
        }
        catch (RevokedException ex)
        {
            _state.MarkNeedsPairing(ex.Message);
            _log.LogError("Server rejected this device ({Reason}). Printing stopped until it is paired again.", ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state.SetDegraded("Heartbeat failed: " + ex.Message);
            _log.LogWarning("Heartbeat failed: {Error}", ex.Message);
        }
    }
}
