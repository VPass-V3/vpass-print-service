using Microsoft.Extensions.Logging;
using VPass.PrintService.Util;

namespace VPass.PrintService.Printing;

/// <summary>
/// macOS / Linux printing via CUPS command line tools (lp / lpstat). Works with any queue the OS
/// knows, including AirPrint/IPP Everywhere label printers. fit-to-page scales the badge to the
/// media; landscape templates are pre-rotated by <see cref="BadgeImage"/>.
/// </summary>
public sealed class CupsBackend : IPrinterBackend
{
    private readonly ILogger<CupsBackend> _log;

    public CupsBackend(ILogger<CupsBackend> log) { _log = log; }

    public string Name => "cups";

    public IReadOnlyList<string> ListPrinters()
    {
        var result = ProcessRunner.Run("lpstat", new[] { "-p" }, TimeSpan.FromSeconds(15));
        var list = new List<string>();
        foreach (string line in result.StdOut.Split('\n'))
        {
            // "printer Brother_QL_820NWB is idle.  enabled since ..."
            string t = line.Trim();
            if (t.StartsWith("printer ", StringComparison.Ordinal))
            {
                string[] parts = t.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) list.Add(parts[1]);
            }
        }
        return list;
    }

    public bool IsOnline(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return false;
        try
        {
            var result = ProcessRunner.Run("lpstat", new[] { "-p", printerName }, TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0) return false;
            string text = result.StdOut.ToLowerInvariant();
            return !text.Contains("disabled") && !text.Contains("not accepting");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "lpstat failed");
            return false;
        }
    }

    public void Print(string imagePath, PrintOptions options)
    {
        var args = new List<string> { "-d", options.PrinterName, "-o", "fit-to-page" };
        if (options.Copies > 1) { args.Add("-n"); args.Add(Math.Clamp(options.Copies, 1, 10).ToString()); }
        if (!string.IsNullOrWhiteSpace(options.PaperName)) { args.Add("-o"); args.Add("media=" + options.PaperName); }
        args.Add("-t"); args.Add("VPass badge");
        args.Add(imagePath);

        ProcessResult result;
        try
        {
            result = ProcessRunner.Run("lp", args.ToArray(), TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            throw new PrintException("Could not run lp: " + ex.Message, ex);
        }

        if (result.ExitCode != 0)
        {
            string err = (result.StdErr + " " + result.StdOut).Trim();
            string lower = err.ToLowerInvariant();
            if (lower.Contains("does not exist") || lower.Contains("not accepting") || lower.Contains("disabled")
                || lower.Contains("unable to connect") || lower.Contains("no such") || lower.Contains("unknown destination"))
            {
                throw new PrinterOfflineException($"Printer '{options.PrinterName}': {err}");
            }
            throw new PrintException($"lp failed ({result.ExitCode}): {err}");
        }
        _log.LogDebug("lp: {Output}", result.StdOut.Trim());
    }
}
