using System.Text.Json.Serialization;

namespace VPass.PrintService.Config;

/// <summary>Everything the service needs to run, written by `pair` / `set-printer`, read by `run`.</summary>
public sealed class DeviceConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>au | us | uk | ca | qa | dev — used to derive URLs when the server did not override them.</summary>
    public string Region { get; set; } = "au";
    public string ApiBaseUrl { get; set; } = "";
    /// <summary>e.g. wss://ws.vpass.io/v1 — the channel is appended as a path segment.</summary>
    public string SocketUrl { get; set; } = "";

    public int PrinterId { get; set; }
    public int OrgId { get; set; }
    public int SiteId { get; set; }
    public int? LocationId { get; set; }
    public string PrinterName { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string? LocationName { get; set; }
    public string Channel { get; set; } = "";
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Device JWT. DPAPI (LocalMachine) protected on Windows, plain in a 0600 file elsewhere.</summary>
    public string? DeviceTokenProtected { get; set; }
    public bool TokenIsProtected { get; set; }
    public DateTime? TokenExpiresOn { get; set; }
    public DateTime? PairedOn { get; set; }

    /// <summary>OS printer (spooler name / CUPS queue). Empty = not configured yet.</summary>
    public string OsPrinterName { get; set; } = "";
    /// <summary>Optional paper/media name (Windows PaperSize name or CUPS media=). Null = printer default.</summary>
    public string? PaperName { get; set; }
    /// <summary>Rotate landscape templates (3, 4) 90° so the long edge runs along a continuous label roll. Default on.</summary>
    public bool RotateLandscape { get; set; } = true;
    public int Copies { get; set; } = 1;

    /// <summary>Serilog level: Verbose | Debug | Information | Warning | Error</summary>
    public string LogLevel { get; set; } = "Information";

    [JsonIgnore]
    public bool IsPaired => PrinterId > 0 && !string.IsNullOrEmpty(DeviceTokenProtected);

    [JsonIgnore]
    public bool HasPrinter => !string.IsNullOrWhiteSpace(OsPrinterName);
}
