using Microsoft.Extensions.Hosting;
using VPass.PrintService.Config;

namespace VPass.PrintService.Core;

/// <summary>Periodic safety-net poll (default 45 s, server can tune via heartbeat). Catches anything the socket missed.</summary>
public sealed class TimerTriggerService : BackgroundService
{
    private readonly TriggerBus _bus;
    private readonly AppState _state;
    private readonly ConfigHolder _config;

    public TimerTriggerService(TriggerBus bus, AppState state, ConfigHolder config)
    {
        _bus = bus; _state = state; _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int seconds = Math.Clamp(_state.PollIntervalSeconds, 15, 600);
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); } catch (OperationCanceledException) { break; }
            if (_config.Current.IsPaired && !_state.NeedsPairing)
            {
                _bus.Signal(Trigger.Timer);
            }
        }
    }
}
