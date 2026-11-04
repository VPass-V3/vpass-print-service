namespace VPass.PrintService.Api;

// Mirrors server_mobile Models/{Request,Response}Model/Printer*.cs (camelCase JSON).

public sealed class ApiEnvelope
{
    public int StatusCode { get; set; }
    public string? StatusMessage { get; set; }
}

public sealed class PairRequest
{
    public string PairingCode { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Platform { get; set; } = "";
    public string AppVersion { get; set; } = "";
}

public sealed class PairResponse
{
    public int StatusCode { get; set; }
    public string? StatusMessage { get; set; }
    public string? DeviceToken { get; set; }
    public DateTime? TokenExpiresOn { get; set; }
    public int PrinterId { get; set; }
    public int OrgId { get; set; }
    public int SiteId { get; set; }
    public int? LocationId { get; set; }
    public string? PrinterName { get; set; }
    public string? SiteName { get; set; }
    public string? LocationName { get; set; }
    public string? Region { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? SocketUrl { get; set; }
    public string? Channel { get; set; }
}

public sealed class MeResponse
{
    public int StatusCode { get; set; }
    public string? StatusMessage { get; set; }
    public int PrinterId { get; set; }
    public string? PrinterName { get; set; }
    public string? SiteName { get; set; }
    public string? LocationName { get; set; }
    public int? TemplateType { get; set; }
    public string? Channel { get; set; }
    public DateTime? TokenExpiresOn { get; set; }
}

public sealed class PrintJob
{
    public long JobId { get; set; }
    public int? VisitId { get; set; }
    public int? TemplateType { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime ExpiresOn { get; set; }
    public int Attempt { get; set; }
    /// <summary>Base64 JPEG rendered server-side with the organisation's selected template.</summary>
    public string? BadgeImage { get; set; }
    public string? Payload { get; set; }
}

public sealed class PendingJobsResponse
{
    public int StatusCode { get; set; }
    public string? StatusMessage { get; set; }
    public DateTime ServerTime { get; set; }
    public List<PrintJob> Jobs { get; set; } = new();
}

public sealed class AckRequest
{
    public string Status { get; set; } = "printed";
    public string? Error { get; set; }
    public bool Retriable { get; set; }
    public string? AppVersion { get; set; }
}

public sealed class HeartbeatRequest
{
    public string? AppVersion { get; set; }
    public string? Os { get; set; }
    public string? Platform { get; set; }
    public string? Hostname { get; set; }
    public string? OsPrinterName { get; set; }
    public bool PrinterOnline { get; set; }
    public int QueueDepth { get; set; }
    public bool SocketConnected { get; set; }
    public long UptimeSeconds { get; set; }
}

public sealed class HeartbeatResponse
{
    public int StatusCode { get; set; }
    public string? StatusMessage { get; set; }
    public DateTime ServerTime { get; set; }
    public string? PrinterName { get; set; }
    public string? LatestVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    public string? DownloadUrl { get; set; }
    public int PollIntervalSeconds { get; set; }
}

public sealed class TokenResponse
{
    public int StatusCode { get; set; }
    public string? StatusMessage { get; set; }
    public string? DeviceToken { get; set; }
    public DateTime? TokenExpiresOn { get; set; }
}
