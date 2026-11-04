using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VPass.PrintService.Api;
using VPass.PrintService.Config;
using VPass.PrintService.Core;

namespace VPass.PrintService.Socket;

/// <summary>
/// WowSocks subscriber for channel Printer-{PrinterID}. Messages carry only {type:"print_job",jobId}
/// and are used purely as a wake-up: every message, and every (re)connect, signals the job loop to
/// poll. Reconnects forever with capped exponential backoff + jitter; never the source of truth.
///
/// WowSocks pings every 30 s; ClientWebSocket answers pongs internally and does not surface them,
/// so "no data frame" is not a health signal. A dead TCP connection (sleep, VPN change) is covered
/// by <see cref="SleepDetector"/> → <see cref="RequestReconnect"/> and by the timer poll; as a last
/// resort the socket is recycled after <see cref="RecycleAfter"/> without any frame.
/// </summary>
public sealed class WakeSocket : BackgroundService
{
    private static readonly TimeSpan RecycleAfter = TimeSpan.FromMinutes(30);

    private readonly ConfigHolder _config;
    private readonly AppState _state;
    private readonly TriggerBus _bus;
    private readonly ILogger<WakeSocket> _log;
    private CancellationTokenSource _reconnect = new();

    public WakeSocket(ConfigHolder config, AppState state, TriggerBus bus, ILogger<WakeSocket> log)
    {
        _config = config; _state = state; _bus = bus; _log = log;
    }

    /// <summary>Abort the current connection and reconnect immediately (resume from sleep, re-pair).</summary>
    public void RequestReconnect()
    {
        try { _reconnect.Cancel(); } catch (ObjectDisposedException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        int attempt = 0;
        var rng = new Random();
        while (!ct.IsCancellationRequested)
        {
            DeviceConfig cfg = _config.Current;
            string? token = cfg.IsPaired ? ConfigStore.GetToken(cfg) : null;
            if (token == null || _state.NeedsPairing || string.IsNullOrWhiteSpace(cfg.SocketUrl) || string.IsNullOrWhiteSpace(cfg.Channel))
            {
                _state.SocketConnected = false;
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            if (_reconnect.IsCancellationRequested)
            {
                _reconnect.Dispose();
                _reconnect = new CancellationTokenSource();
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _reconnect.Token);
            DateTime connectedAt = DateTime.MinValue;

            try
            {
                string url = $"{cfg.SocketUrl.TrimEnd('/')}/{cfg.Channel}?source=printservice&v={AppVersion.Current}&jwt={Uri.EscapeDataString(token)}";
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await ws.ConnectAsync(new Uri(url), connectTimeout.Token);
                }

                connectedAt = DateTime.UtcNow;
                _state.SocketConnected = true;
                _log.LogInformation("Socket connected to {Channel}", cfg.Channel);
                _bus.Signal(Trigger.SocketConnected);

                await ReceiveLoopAsync(ws, linked.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                _log.LogDebug("Socket reconnect requested");
            }
            catch (Exception ex)
            {
                _log.LogDebug("Socket closed: {Error}", ex.Message);
            }
            finally
            {
                _state.SocketConnected = false;
            }

            if (_reconnect.IsCancellationRequested)
            {
                attempt = 0;
                continue; // immediate
            }
            if (connectedAt != DateTime.MinValue && (DateTime.UtcNow - connectedAt).TotalSeconds > 60)
            {
                attempt = 0; // was healthy for a while
            }
            int delay = Math.Min(60, 1 << Math.Min(attempt, 6));
            attempt++;
            double jitter = rng.NextDouble();
            if (attempt > 1)
            {
                _log.LogInformation("Socket disconnected; reconnecting in {Delay:0.#}s", delay + jitter);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(delay + jitter), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();
        DateTime lastFrame = DateTime.UtcNow;
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var recycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recycle.CancelAfter(RecycleAfter);
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, recycle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogDebug("Recycling socket after {Minutes} min without frames", RecycleAfter.TotalMinutes);
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "recycle", CancellationToken.None); } catch { }
                return;
            }

            lastFrame = DateTime.UtcNow;
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            if (result.MessageType == WebSocketMessageType.Text)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;
                string message = text.ToString();
                text.Clear();
                HandleMessage(message);
            }
        }
    }

    private void HandleMessage(string message)
    {
        _state.LastSocketMessageUtc = DateTime.UtcNow;
        if (message.Contains("Invalid JWT", StringComparison.OrdinalIgnoreCase))
        {
            // WowSocks closes right after this; the heartbeat decides whether we are actually revoked.
            _log.LogWarning("Socket server rejected the token; waiting for the server to confirm");
            return;
        }
        _log.LogDebug("Socket message: {Message}", message.Length > 200 ? message[..200] : message);
        _bus.Signal(Trigger.SocketMessage);
    }
}
