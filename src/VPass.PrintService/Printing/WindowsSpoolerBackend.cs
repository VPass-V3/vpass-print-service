using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace VPass.PrintService.Printing;

/// <summary>
/// Prints through the Windows spooler with the installed driver (Brother QL, Dymo, Zebra, PDF…),
/// so anything in Settings → Printers works. Image is scaled to the printable area preserving
/// aspect ratio and top-centred. Only compiled/used on Windows: System.Drawing.Common throws
/// PlatformNotSupportedException elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSpoolerBackend : IPrinterBackend
{
    private readonly ILogger<WindowsSpoolerBackend> _log;

    public WindowsSpoolerBackend(ILogger<WindowsSpoolerBackend> log) { _log = log; }

    public string Name => "windows-spooler";

    public IReadOnlyList<string> ListPrinters()
    {
        var list = new List<string>();
        foreach (string? p in PrinterSettings.InstalledPrinters)
        {
            if (!string.IsNullOrEmpty(p)) list.Add(p);
        }
        return list;
    }

    public bool IsOnline(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return false;
        var settings = new PrinterSettings { PrinterName = printerName };
        if (!settings.IsValid) return false;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT WorkOffline, PrinterStatus FROM Win32_Printer WHERE Name = '{Escape(printerName)}'");
            foreach (ManagementObject o in searcher.Get())
            {
                bool offline = o["WorkOffline"] is bool b && b;
                return !offline;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "WMI printer status unavailable; assuming online");
        }
        return true;
    }

    public IReadOnlyList<string> ListPaperSizes(string printerName)
    {
        var settings = new PrinterSettings { PrinterName = printerName };
        if (!settings.IsValid) return Array.Empty<string>();
        var list = new List<string>();
        foreach (PaperSize size in settings.PaperSizes)
        {
            // hundredths of an inch -> mm
            list.Add($"{size.PaperName}  ({size.Width * 0.254:0} x {size.Height * 0.254:0} mm)");
        }
        return list;
    }

    /// <summary>How long to watch the spooler for a media / offline error before treating the job as handed over.</summary>
    public static TimeSpan SpoolerWatch { get; set; } = TimeSpan.FromSeconds(20);

    public void Print(string imagePath, PrintOptions options)
    {
        var settings = new PrinterSettings { PrinterName = options.PrinterName };
        if (!settings.IsValid)
        {
            throw new PrinterOfflineException($"Printer '{options.PrinterName}' is not installed on this PC (run: vpass-print printers)");
        }
        if (!IsOnline(options.PrinterName))
        {
            throw new PrinterOfflineException($"Printer '{options.PrinterName}' is offline");
        }
        settings.Copies = (short)Math.Clamp(options.Copies, 1, 10);

        using Image image = Image.FromFile(imagePath);
        string documentName = "VPass badge " + Guid.NewGuid().ToString("N")[..8];
        using var doc = new PrintDocument
        {
            PrinterSettings = settings,
            PrintController = new StandardPrintController(), // no progress dialog, works in a service
            DocumentName = documentName,
            OriginAtMargins = false
        };
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        if (!string.IsNullOrWhiteSpace(options.PaperName))
        {
            PaperSize? match = null;
            foreach (PaperSize size in settings.PaperSizes)
            {
                if (string.Equals(size.PaperName, options.PaperName, StringComparison.OrdinalIgnoreCase)
                    || size.PaperName.StartsWith(options.PaperName, StringComparison.OrdinalIgnoreCase))
                {
                    match = size;
                    break;
                }
            }
            if (match == null)
            {
                throw new PrintException($"Paper '{options.PaperName}' is not offered by '{options.PrinterName}' (run: vpass-print papers)");
            }
            doc.DefaultPageSettings.PaperSize = match;
        }

        Exception? pageError = null;
        doc.PrintPage += (_, e) =>
        {
            try
            {
                RectangleF area = e.PageSettings.PrintableArea; // hundredths of an inch
                float areaW = area.Width, areaH = area.Height;
                if (e.PageSettings.Landscape)
                {
                    (areaW, areaH) = (areaH, areaW);
                }
                float scale = Math.Min(areaW / image.Width, areaH / image.Height);
                float w = image.Width * scale, h = image.Height * scale;
                float x = (areaW - w) / 2f;
                e.Graphics!.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(image, new RectangleF(x, 0, w, h));
            }
            catch (Exception ex)
            {
                pageError = ex;
            }
            e.HasMorePages = false;
        };

        try
        {
            doc.Print();
        }
        catch (InvalidPrinterException ex)
        {
            throw new PrinterOfflineException(ex.Message);
        }
        catch (Win32Exception ex)
        {
            // The spooler reports a stopped/offline device as a Win32 error at Print() time.
            throw new PrinterOfflineException("Spooler error: " + ex.Message);
        }
        if (pageError != null)
        {
            throw new PrintException("Rendering to the printer failed: " + pageError.Message, pageError);
        }

        // Print() only means "accepted by the spooler". A Brother/Dymo driver that gets the wrong media
        // (e.g. die-cut defaults while a continuous roll is loaded) parks the job in the queue with an
        // error that nobody sees from a service session, so watch the queue before claiming success.
        WaitForSpooler(options.PrinterName, documentName);
    }

    private void WaitForSpooler(string printerName, string documentName)
    {
        DateTime deadline = DateTime.UtcNow + SpoolerWatch;
        string? lastStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            string? status;
            try
            {
                status = QueryJobStatus(documentName);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Spooler query failed; assuming the job was handed over");
                return;
            }
            if (status == null)
            {
                return; // gone from the queue = sent to the printer
            }
            lastStatus = status;
            string lower = status.ToLowerInvariant();
            if (lower.Contains("error") || lower.Contains("offline") || lower.Contains("paper") || lower.Contains("intervention") || lower.Contains("blocked"))
            {
                CancelJob(documentName);
                throw new PrinterOfflineException(
                    $"Windows print queue reports '{status}' for '{printerName}'. Check the paper/media in the driver's Printing Defaults (Properties > Advanced) or set --paper; run: vpass-print papers");
            }
            Thread.Sleep(500);
        }
        _log.LogWarning("Job '{Doc}' still in the queue after {Seconds}s (status '{Status}'); treating as handed over", documentName, SpoolerWatch.TotalSeconds, lastStatus);
    }

    private static string? QueryJobStatus(string documentName)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT JobStatus, Status, Document FROM Win32_PrintJob WHERE Document = '{Escape(documentName)}'");
        foreach (ManagementObject job in searcher.Get())
        {
            string js = job["JobStatus"] as string ?? "";
            string st = job["Status"] as string ?? "";
            return string.IsNullOrWhiteSpace(js) ? (string.IsNullOrWhiteSpace(st) ? "Queued" : st) : js;
        }
        return null;
    }

    private void CancelJob(string documentName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_PrintJob WHERE Document = '{Escape(documentName)}'");
            foreach (ManagementObject job in searcher.Get())
            {
                job.Delete();
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not remove the failed job from the queue");
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
