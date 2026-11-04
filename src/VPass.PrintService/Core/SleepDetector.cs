using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VPass.PrintService.Socket;

namespace VPass.PrintService.Core;

/// <summary>
/// Detects that the process was suspended (PC asleep, VM paused, laptop lid closed) by noticing a
/// 5 s timer that took far longer than 5 s to fire. Works identically for a Windows service, a
/// launchd daemon and a systemd unit — unlike SystemEvents.PowerModeChanged, which needs a
/// message loop. On resume: reconnect the socket, poll immediately and heartbeat immediately.
/// </summary>
public sealed class SleepDetector : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DriftThreshold = TimeSpan.FromSeconds(30);

    private readonly TriggerBus _bus;
    private readonly WakeSocket _socket;
    private readonly HeartbeatService _heartbeat;
    private readonly ILogger<SleepDetector> _log;

    public SleepDetector(TriggerBus bus, WakeSocket socket, HeartbeatService heartbeat, ILogger<SleepDetector> log)
    {
        _bus = bus; _socket = socket; _heartbeat = heartbeat; _log = log;
    }

    /// <summary>True when a delay of <paramref name="expected"/> actually took <paramref name="actual"/> and the gap exceeds the threshold.</summary>
    public static bool ShouldTriggerResume(TimeSpan expected, TimeSpan actual) => actual - expected > DriftThreshold;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        DateTime last = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TickInterval, ct); } catch (OperationCanceledException) { break; }
            DateTime now = DateTime.UtcNow;
            TimeSpan actual = now - last;
            last = now;
            if (ShouldTriggerResume(TickInterval, actual))
            {
                _log.LogInformation("Resume detected (gap {Gap:0}s). Reconnecting and polling.", actual.TotalSeconds);
                _socket.RequestReconnect();
                _bus.Signal(Trigger.Resume);
                _heartbeat.RequestNow();
            }
        }
    }
}
