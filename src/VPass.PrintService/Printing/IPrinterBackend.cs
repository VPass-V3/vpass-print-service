namespace VPass.PrintService.Printing;

/// <param name="PrinterName">OS printer: Windows spooler name or CUPS queue.</param>
/// <param name="PaperName">Optional paper / media name; null = printer default.</param>
/// <param name="TemplateType">VPass badge template 1-4 (3 and 4 are landscape).</param>
/// <param name="RotateLandscape">Whether landscape templates were rotated 90° before printing.</param>
public sealed record PrintOptions(string PrinterName, string? PaperName, int? TemplateType, bool RotateLandscape, int Copies = 1);

/// <summary>Transient: printer offline / disabled / not found. The job is acked as retriable and retried later.</summary>
public sealed class PrinterOfflineException : Exception
{
    public PrinterOfflineException(string message) : base(message) { }
}

/// <summary>Permanent failure for this job (bad image, driver error). The job is acked as failed.</summary>
public sealed class PrintException : Exception
{
    public PrintException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IPrinterBackend
{
    string Name { get; }
    IReadOnlyList<string> ListPrinters();
    bool IsOnline(string? printerName);
    /// <summary>Paper / media names the driver offers for <paramref name="printerName"/> (for --paper).</summary>
    IReadOnlyList<string> ListPaperSizes(string printerName);
    /// <summary>Print a PNG/JPEG file scaled to the printable area. Blocks until the spooler has handed the job to the printer (or reports an error).</summary>
    void Print(string imagePath, PrintOptions options);
}

public static class PrinterBackendFactory
{
    public static IPrinterBackend Create(Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSpoolerBackend(loggerFactory.CreateLogger<WindowsSpoolerBackend>());
        }
        return new CupsBackend(loggerFactory.CreateLogger<CupsBackend>());
    }
}
