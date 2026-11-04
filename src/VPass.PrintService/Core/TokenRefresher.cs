using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VPass.PrintService.Api;
using VPass.PrintService.Config;

namespace VPass.PrintService.Core;

/// <summary>Device tokens live 24 months; swap in a fresh one when fewer than 30 days remain.</summary>
public sealed class TokenRefresher : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromDays(30);

    private readonly IPrinterApiClient _api;
    private readonly ConfigHolder _config;
    private readonly AppState _state;
    private readonly ILogger<TokenRefresher> _log;

    public TokenRefresher(IPrinterApiClient api, ConfigHolder config, AppState state, ILogger<TokenRefresher> log)
    {
        _api = api; _config = config; _state = state; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { break; }
            await TryRefreshAsync(ct);
            try { await Task.Delay(CheckInterval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TryRefreshAsync(CancellationToken ct)
    {
        DeviceConfig cfg = _config.Current;
        if (!cfg.IsPaired || _state.NeedsPairing || cfg.TokenExpiresOn == null)
        {
            return;
        }
        if (cfg.TokenExpiresOn.Value - DateTime.UtcNow > RefreshWindow)
        {
            return;
        }
        try
        {
            TokenResponse resp = await _api.RefreshTokenAsync(ct);
            if (resp.StatusCode == 200 && !string.IsNullOrEmpty(resp.DeviceToken))
            {
                _config.Update(c =>
                {
                    ConfigStore.SetToken(c, resp.DeviceToken!);
                    c.TokenExpiresOn = resp.TokenExpiresOn;
                });
                _log.LogInformation("Device token refreshed; now valid until {Expiry:u}", resp.TokenExpiresOn);
            }
        }
        catch (RevokedException ex)
        {
            _state.MarkNeedsPairing(ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Token refresh failed: {Error}", ex.Message);
        }
    }
}
