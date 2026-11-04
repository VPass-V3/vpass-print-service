using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;
using VPass.PrintService.Api;
using VPass.PrintService.Config;
using VPass.PrintService.Core;
using VPass.PrintService.Logging;
using VPass.PrintService.Printing;
using VPass.PrintService.Socket;

namespace VPass.PrintService.Hosting;

/// <summary>Composes the background service (`vpass-print run`). Works as a console app, Windows Service, launchd daemon or systemd unit.</summary>
public static class ServiceHost
{
    public static async Task<int> RunAsync(string[] args, AppPaths paths)
    {
        paths.EnsureDirectories();
        var store = new ConfigStore(paths);
        var holder = new ConfigHolder(store);
        bool isService = WindowsServiceHelpers.IsWindowsService() || Environment.GetEnvironmentVariable("INVOCATION_ID") != null;
        Log.Logger = SerilogSetup.Create(paths.LogDir, holder.Current.LogLevel, console: !isService);

        try
        {
            Log.Information("VPass Print Service {Version} starting ({Platform}); data dir {DataDir}", AppVersion.Current, AppVersion.Platform, paths.DataDir);

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Services.AddWindowsService(o => o.ServiceName = AppPaths.ServiceName);
            builder.Services.AddSystemd();
            builder.Services.AddSerilog(Log.Logger);

            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(holder);
            builder.Services.AddSingleton<AppState>();
            builder.Services.AddSingleton<TriggerBus>();
            builder.Services.AddSingleton<StatusWriter>();
            builder.Services.AddSingleton(new AckLedger(paths.LedgerPath));
            builder.Services.AddSingleton<IPrinterBackend>(sp => PrinterBackendFactory.Create(sp.GetRequiredService<ILoggerFactory>()));
            builder.Services.AddSingleton<IPrinterApiClient>(sp => new PrinterApiClient(
                new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
                sp.GetRequiredService<ConfigHolder>()));

            builder.Services.AddSingleton<WakeSocket>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<WakeSocket>());
            builder.Services.AddSingleton<HeartbeatService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatService>());
            builder.Services.AddHostedService<JobLoop>();
            builder.Services.AddHostedService<TimerTriggerService>();
            builder.Services.AddHostedService<SleepDetector>();
            builder.Services.AddHostedService<TokenRefresher>();

            using IHost host = builder.Build();
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Service terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
