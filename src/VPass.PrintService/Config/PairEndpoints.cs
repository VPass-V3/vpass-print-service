using VPass.PrintService.Api;

namespace VPass.PrintService.Config;

public sealed record Endpoints(string Region, string ApiBaseUrl, string SocketUrl);

/// <summary>
/// Decides which URLs the device keeps after pairing. Precedence: explicit --api-url/--socket-url,
/// then an explicit URL override from the server (PrintServicePublicApiUrl / PrintServicePublicSocketUrl
/// env), then the region the device actually paired through. The server's <c>region</c> field
/// (its ServerLocation env) is informational only: the dev cluster reports "qa", and following it
/// pointed devices at the wrong API (v1.0.0 bug).
/// </summary>
public static class PairEndpoints
{
    public static Endpoints Resolve(string cliRegion, string pairedApiBase, string? cliApiUrl, string? cliSocketUrl, PairResponse resp)
    {
        string region = Regions.Normalise(cliRegion);
        Regions.TryGet(region, out var byRegion);

        string api = FirstUrl(cliApiUrl, resp.ApiBaseUrl, pairedApiBase, byRegion.ApiBaseUrl);
        string socket = FirstUrl(cliSocketUrl, resp.SocketUrl, byRegion.SocketUrl);
        return new Endpoints(region, api.TrimEnd('/'), socket.TrimEnd('/'));
    }

    private static string FirstUrl(params string?[] candidates) =>
        candidates.First(c => !string.IsNullOrWhiteSpace(c) && Uri.IsWellFormedUriString(c.Trim(), UriKind.Absolute))!.Trim();
}
