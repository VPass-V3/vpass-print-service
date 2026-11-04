using System.Text.Json;
using VPass.PrintService.Config;

namespace VPass.PrintService.Core;

public enum ServiceState
{
    Unpaired,
    Running,
    Degraded,
    NeedsPairing
}

/// <summary>
/// In-memory status shared by the background services, serialised to status.json after every
/// heartbeat so `vpass-print status` can report without talking to the service.
/// </summary>
public sealed class AppState
{
    private readonly object _gate = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public ServiceState State { get; private set; } = ServiceState.Unpaired;
    public string? LastError { get; private set; }
    public DateTime? LastErrorUtc { get; private set; }
    public bool SocketConnected { get; set; }
    public DateTime? LastSocketMessageUtc { get; set; }
    public DateTime? LastPollUtc { get; set; }
    public DateTime? LastPrintUtc { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
    public bool PrinterOnline { get; set; }
    public int QueueDepth { get; set; }
    public long PrintedCount { get; private set; }
    public long FailedCount { get; private set; }
    public int PollIntervalSeconds { get; set; } = 45;
    public string? LatestVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    public string? DownloadUrl { get; set; }

    public TimeSpan Uptime => DateTime.UtcNow - _startedUtc;

    public void SetRunning()
    {
        lock (_gate)
        {
            if (State != ServiceState.NeedsPairing)
            {
                State = ServiceState.Running;
            }
        }
    }

    public void SetDegraded(string error)
    {
        lock (_gate)
        {
            if (State != ServiceState.NeedsPairing)
            {
                State = ServiceState.Degraded;
            }
            LastError = error;
            LastErrorUtc = DateTime.UtcNow;
        }
    }

    public void SetUnpaired()
    {
        lock (_gate) { State = ServiceState.Unpaired; }
    }

    public void MarkNeedsPairing(string reason)
    {
        lock (_gate)
        {
            State = ServiceState.NeedsPairing;
            LastError = reason;
            LastErrorUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Called after a successful (re)pair detected via config change.</summary>
    public void ClearNeedsPairing()
    {
        lock (_gate)
        {
            if (State == ServiceState.NeedsPairing)
            {
                State = ServiceState.Running;
                LastError = null;
            }
        }
    }

    public void RecordError(string error)
    {
        lock (_gate)
        {
            LastError = error;
            LastErrorUtc = DateTime.UtcNow;
        }
    }

    public void RecordPrinted()
    {
        lock (_gate)
        {
            PrintedCount++;
            LastPrintUtc = DateTime.UtcNow;
        }
    }

    public void RecordFailed() { lock (_gate) { FailedCount++; } }

    public bool NeedsPairing => State == ServiceState.NeedsPairing;
}

/// <summary>Writes status.json for the CLI `status` verb.</summary>
public sealed class StatusWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly AppPaths _paths;

    public StatusWriter(AppPaths paths) { _paths = paths; }

    public void Write(AppState state, DeviceConfig config)
    {
        try
        {
            var snapshot = new
            {
                writtenUtc = DateTime.UtcNow,
                version = Api.AppVersion.Current,
                state = state.State.ToString(),
                paired = config.IsPaired,
                printerId = config.PrinterId,
                printerName = config.PrinterName,
                site = config.SiteName,
                location = config.LocationName ?? "all locations",
                osPrinterName = config.OsPrinterName,
                printerOnline = state.PrinterOnline,
                socketConnected = state.SocketConnected,
                queueDepth = state.QueueDepth,
                printedCount = state.PrintedCount,
                failedCount = state.FailedCount,
                lastPollUtc = state.LastPollUtc,
                lastPrintUtc = state.LastPrintUtc,
                lastHeartbeatUtc = state.LastHeartbeatUtc,
                lastSocketMessageUtc = state.LastSocketMessageUtc,
                lastError = state.LastError,
                lastErrorUtc = state.LastErrorUtc,
                tokenExpiresOn = config.TokenExpiresOn,
                latestVersion = state.LatestVersion,
                updateAvailable = state.UpdateAvailable,
                uptimeSeconds = (long)state.Uptime.TotalSeconds
            };
            string tmp = _paths.StatusPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Json));
            File.Move(tmp, _paths.StatusPath, overwrite: true);
        }
        catch
        {
            // status file is a convenience; never fail the service over it
        }
    }
}
