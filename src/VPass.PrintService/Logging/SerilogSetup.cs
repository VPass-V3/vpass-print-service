using Serilog;
using Serilog.Events;

namespace VPass.PrintService.Logging;

public static class SerilogSetup
{
    /// <summary>Rolling daily files (7 kept, 10 MB each) plus console when attached to a terminal. Logs carry job ids, never visitor names.</summary>
    public static Serilog.Core.Logger Create(string logDir, string? level, bool console)
    {
        LogEventLevel minimum = Enum.TryParse(level, true, out LogEventLevel parsed) ? parsed : LogEventLevel.Information;
        var cfg = new LoggerConfiguration()
            .MinimumLevel.Is(minimum)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(logDir, "vpass-print-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        if (console)
        {
            cfg = cfg.WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }
        return cfg.CreateLogger();
    }
}
