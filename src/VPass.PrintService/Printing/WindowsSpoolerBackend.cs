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
        using var doc = new PrintDocument
        {
            PrinterSettings = settings,
            PrintController = new StandardPrintController(), // no progress dialog, works in a service
            DocumentName = "VPass badge",
            OriginAtMargins = false
        };
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        if (!string.IsNullOrWhiteSpace(options.PaperName))
        {
            foreach (PaperSize size in settings.PaperSizes)
            {
                if (string.Equals(size.PaperName, options.PaperName, StringComparison.OrdinalIgnoreCase))
                {
                    doc.DefaultPageSettings.PaperSize = size;
                    break;
                }
            }
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
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
