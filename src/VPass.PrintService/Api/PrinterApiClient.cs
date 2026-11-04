using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VPass.PrintService.Config;

namespace VPass.PrintService.Api;

public interface IPrinterApiClient
{
    Task<PairResponse> PairAsync(string apiBaseUrl, PairRequest request, CancellationToken ct);
    Task<MeResponse> MeAsync(CancellationToken ct);
    Task<PendingJobsResponse> GetPendingAsync(int limit, CancellationToken ct);
    Task AckAsync(long jobId, AckRequest request, CancellationToken ct);
    Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct);
    Task<TokenResponse> RefreshTokenAsync(CancellationToken ct);
}

/// <summary>
/// Thin typed client for server_mobile's PrinterController. Every authenticated call sends the
/// device JWT; a 401 (the server answers {statusMessage:"REVOKED"}) becomes
/// <see cref="RevokedException"/>, which the job loop and heartbeat treat as "stop until re-paired".
/// Transport failures surface as HttpRequestException / TaskCanceledException and are retried by
/// the callers' own backoff.
/// </summary>
public sealed class PrinterApiClient : IPrinterApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly ConfigHolder _config;

    public PrinterApiClient(HttpClient http, ConfigHolder config)
    {
        _http = http;
        _config = config;
        _http.Timeout = TimeSpan.FromSeconds(40);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("vpass-print-service/" + AppVersion.Current);
    }

    public async Task<PairResponse> PairAsync(string apiBaseUrl, PairRequest request, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, Combine(apiBaseUrl, "printer/pair"))
        {
            Content = JsonContent.Create(request, options: Json)
        };
        using var response = await _http.SendAsync(msg, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        PairResponse? parsed = TryParse<PairResponse>(body);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ApiException(429, parsed?.StatusMessage ?? "Too many pairing attempts, try again in a minute");
        }
        if (!response.IsSuccessStatusCode || parsed == null || parsed.StatusCode != 200 || string.IsNullOrEmpty(parsed.DeviceToken))
        {
            throw new ApiException((int)response.StatusCode, parsed?.StatusMessage ?? $"Pairing failed (HTTP {(int)response.StatusCode})");
        }
        return parsed;
    }

    public Task<MeResponse> MeAsync(CancellationToken ct) =>
        SendAuthed<MeResponse>(HttpMethod.Get, "printer/me", null, ct);

    public Task<PendingJobsResponse> GetPendingAsync(int limit, CancellationToken ct) =>
        SendAuthed<PendingJobsResponse>(HttpMethod.Get, $"printer/jobs/pending?limit={limit}", null, ct);

    public Task AckAsync(long jobId, AckRequest request, CancellationToken ct) =>
        SendAuthed<ApiEnvelope>(HttpMethod.Post, $"printer/jobs/{jobId}/ack", request, ct);

    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) =>
        SendAuthed<HeartbeatResponse>(HttpMethod.Post, "printer/heartbeat", request, ct);

    public Task<TokenResponse> RefreshTokenAsync(CancellationToken ct) =>
        SendAuthed<TokenResponse>(HttpMethod.Post, "printer/token/refresh", new { }, ct);

    private async Task<T> SendAuthed<T>(HttpMethod method, string path, object? body, CancellationToken ct) where T : class
    {
        DeviceConfig cfg = _config.Current;
        string? token = ConfigStore.GetToken(cfg);
        if (!cfg.IsPaired || string.IsNullOrEmpty(token))
        {
            throw new RevokedException("Device is not paired");
        }

        using var msg = new HttpRequestMessage(method, Combine(cfg.ApiBaseUrl, path));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            msg.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        using var response = await _http.SendAsync(msg, ct);
        string text = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            ApiEnvelope? env = TryParse<ApiEnvelope>(text);
            throw new RevokedException(env?.StatusMessage ?? "Unauthorized");
        }
        if (!response.IsSuccessStatusCode)
        {
            ApiEnvelope? env = TryParse<ApiEnvelope>(text);
            throw new ApiException((int)response.StatusCode, env?.StatusMessage ?? $"HTTP {(int)response.StatusCode} from {path}");
        }

        T? parsed = TryParse<T>(text);
        if (parsed == null)
        {
            throw new ApiException((int)response.StatusCode, $"Unreadable response from {path}");
        }
        return parsed;
    }

    private static T? TryParse<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize<T>(body, Json); }
        catch (JsonException) { return null; }
    }

    private static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}

public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static string Platform { get; } =
        (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + "-" + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    public static string OsDescription { get; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
}
