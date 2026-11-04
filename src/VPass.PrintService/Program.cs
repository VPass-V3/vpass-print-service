using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VPass.PrintService;
using VPass.PrintService.Api;
using VPass.PrintService.Config;
using VPass.PrintService.Hosting;
using VPass.PrintService.Install;
using VPass.PrintService.Printing;

// vpass-print: VPass badge print service + its command line.
//   run            background service (what the OS service manager launches)
//   pair <code>    enrol this PC with a pairing code from the admin portal
//   printers       list OS printers
//   set-printer    choose the OS printer / paper / rotation
//   test-print     print a test pattern
//   install        register with the OS service manager (admin / sudo)
//   uninstall | restart | status | logs | version | help

string verb = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
var paths = AppPaths.ForCli(verb);
Cli cli = Cli.Parse(args.Skip(1));

try
{
    switch (verb)
    {
        case "run":
            return await ServiceHost.RunAsync(args.Skip(1).ToArray(), paths);
        case "pair":
            return await Commands.PairAsync(paths, cli);
        case "printers":
            return Commands.ListPrinters(paths);
        case "papers":
            return Commands.ListPapers(paths, cli);
        case "set-printer":
            return Commands.SetPrinter(paths, cli);
        case "test-print":
            return Commands.TestPrint(paths, cli);
        case "install":
            return Commands.Install(paths, cli);
        case "uninstall":
            return Commands.Uninstall(paths);
        case "restart":
            return Commands.Restart(paths);
        case "status":
            return Commands.Status(paths);
        case "logs":
            return await Commands.LogsAsync(paths, cli);
        case "version":
        case "--version":
            Console.WriteLine(AppVersion.Current);
            return 0;
        default:
            Commands.Help();
            return verb is "help" or "--help" or "-h" ? 0 : 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    if (ex is UnauthorizedAccessException || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(OperatingSystem.IsWindows()
            ? "Run this command from an elevated (Administrator) Command Prompt."
            : "Run this command with sudo.");
    }
    return 1;
}

static class Commands
{
    private static bool Interactive => !Console.IsInputRedirected && Environment.UserInteractive;

    public static void Help()
    {
        Console.WriteLine($@"VPass Print Service {AppVersion.Current}

Usage: vpass-print <command> [options]

  pair <code> [--region au|us|uk|ca|qa|dev] [--printer ""<name>""] [--paper <name>] [--no-rotate]
                       Enrol this PC with the pairing code shown in the admin portal
                       (Setup > Badge Printing > Manage printers). Picks the printer interactively
                       when --printer is omitted.
  printers             List printers installed on this PC (* = selected)
  papers [--printer ""<name>""]
                       List the paper / media sizes the printer driver offers (for --paper)
  set-printer ""<name>"" [--paper <name>] [--rotate|--no-rotate] [--copies N]
                       Choose the printer badges go to
  test-print [--printer ""<name>""] [--landscape]
                       Print a test pattern
  install [--account DOMAIN\user --password ***]
                       Install and start the background service (Administrator / sudo).
                       --account: Windows only; needed when the badge printer is a per-user network printer.
  uninstall            Stop and remove the background service
  restart              Restart the background service
  status               Show pairing, printer, socket and last activity
  logs [--lines N] [--follow]
                       Show the service log
  version              Print the version

Data directory: {new AppPaths().DataDir}
Badges are rendered by the VPass server with the template chosen in the admin portal; this service only prints them.");
    }

    public static async Task<int> PairAsync(AppPaths paths, Cli cli)
    {
        if (cli.Positional.Count == 0)
        {
            Console.Error.WriteLine("Usage: vpass-print pair <code> [--region au|us|uk|ca|qa|dev]");
            return 2;
        }
        paths.EnsureDirectories();
        var store = new ConfigStore(paths);
        var holder = new ConfigHolder(store);
        DeviceConfig current = holder.Current;

        string region = Regions.Normalise(cli.Get("region") ?? (string.IsNullOrEmpty(current.Region) ? "au" : current.Region));
        Regions.TryGet(region, out var endpoints);
        string apiBase = cli.Get("api-url") ?? endpoints.ApiBaseUrl;

        Console.WriteLine($"Pairing with {apiBase} (region {region}) ...");
        var api = new PrinterApiClient(new HttpClient(), holder);
        PairResponse resp = await api.PairAsync(apiBase, new PairRequest
        {
            PairingCode = cli.Positional[0],
            DeviceId = current.DeviceId,
            Hostname = Environment.MachineName,
            Platform = AppVersion.Platform,
            AppVersion = AppVersion.Current
        }, CancellationToken.None);

        Endpoints final = PairEndpoints.Resolve(region, apiBase, cli.Get("api-url"), cli.Get("socket-url"), resp);
        holder.Update(c =>
        {
            c.Region = final.Region;
            c.ApiBaseUrl = final.ApiBaseUrl;
            c.SocketUrl = final.SocketUrl;
            c.PrinterId = resp.PrinterId;
            c.OrgId = resp.OrgId;
            c.SiteId = resp.SiteId;
            c.LocationId = resp.LocationId;
            c.PrinterName = resp.PrinterName ?? "";
            c.SiteName = resp.SiteName ?? "";
            c.LocationName = resp.LocationName;
            c.Channel = resp.Channel ?? ("Printer-" + resp.PrinterId);
            ConfigStore.SetToken(c, resp.DeviceToken!);
            c.TokenExpiresOn = resp.TokenExpiresOn;
            c.PairedOn = DateTime.UtcNow;
            if (cli.Has("paper")) c.PaperName = cli.Get("paper");
            if (cli.Has("no-rotate")) c.RotateLandscape = false;
            if (cli.Has("rotate")) c.RotateLandscape = true;
        });

        Console.WriteLine($"Paired as '{resp.PrinterName}' — {resp.SiteName}{(resp.LocationName != null ? " / " + resp.LocationName : " (all locations)")}.");
        Console.WriteLine($"API {final.ApiBaseUrl}   socket {final.SocketUrl}   region {final.Region}");

        string? printer = cli.Get("printer");
        IPrinterBackend backend = PrinterBackendFactory.Create(NullLoggerFactory.Instance);
        if (string.IsNullOrWhiteSpace(printer))
        {
            printer = ChoosePrinter(backend, holder.Current.OsPrinterName);
        }
        if (!string.IsNullOrWhiteSpace(printer))
        {
            holder.Update(c => c.OsPrinterName = printer!);
            Console.WriteLine($"Badges will print on '{printer}'.");
        }
        else
        {
            Console.WriteLine("No printer selected yet. Run: vpass-print printers   then   vpass-print set-printer \"<name>\"");
        }

        Console.WriteLine();
        Console.WriteLine("Next: if the service is already installed it picks this up within a few seconds.");
        Console.WriteLine(OperatingSystem.IsWindows()
            ? "Otherwise run (as Administrator):  vpass-print install"
            : "Otherwise run:  sudo vpass-print install");
        return 0;
    }

    private static string? ChoosePrinter(IPrinterBackend backend, string currentName)
    {
        IReadOnlyList<string> printers;
        try { printers = backend.ListPrinters(); }
        catch (Exception ex) { Console.WriteLine("Could not list printers: " + ex.Message); return null; }
        if (printers.Count == 0)
        {
            Console.WriteLine("No printers are installed on this PC (visible to this account).");
            return null;
        }
        if (!Interactive)
        {
            return printers.Count == 1 ? printers[0] : (printers.Contains(currentName) ? currentName : null);
        }
        Console.WriteLine();
        Console.WriteLine("Printers on this PC:");
        for (int i = 0; i < printers.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {printers[i]}{(printers[i] == currentName ? "  (current)" : "")}");
        }
        Console.Write($"Choose the badge printer [1-{printers.Count}, Enter to skip]: ");
        string? input = Console.ReadLine();
        return int.TryParse(input, out int n) && n >= 1 && n <= printers.Count ? printers[n - 1] : null;
    }

    public static int ListPrinters(AppPaths paths)
    {
        DeviceConfig cfg = new ConfigStore(paths).Load();
        IPrinterBackend backend = PrinterBackendFactory.Create(NullLoggerFactory.Instance);
        IReadOnlyList<string> printers = backend.ListPrinters();
        if (printers.Count == 0)
        {
            Console.WriteLine("No printers found. On Windows, printers added for one user are not visible to a service running as LocalSystem; use `install --account`.");
            return 0;
        }
        foreach (string p in printers)
        {
            string mark = p == cfg.OsPrinterName ? "*" : " ";
            string online = backend.IsOnline(p) ? "ready" : "offline";
            Console.WriteLine($"{mark} {p}  [{online}]");
        }
        return 0;
    }

    public static int ListPapers(AppPaths paths, Cli cli)
    {
        DeviceConfig cfg = new ConfigStore(paths).Load();
        string printer = cli.Get("printer") ?? cfg.OsPrinterName;
        if (string.IsNullOrWhiteSpace(printer))
        {
            Console.Error.WriteLine("No printer selected. Use --printer \"<name>\" or vpass-print set-printer.");
            return 2;
        }
        IPrinterBackend backend = PrinterBackendFactory.Create(NullLoggerFactory.Instance);
        IReadOnlyList<string> papers = backend.ListPaperSizes(printer);
        if (papers.Count == 0)
        {
            Console.WriteLine($"No paper sizes reported for '{printer}'.");
            return 0;
        }
        Console.WriteLine($"Paper sizes for '{printer}'  (current: {cfg.PaperName ?? "driver default"}):");
        foreach (string p in papers) Console.WriteLine("  " + p);
        Console.WriteLine("Pin one with: vpass-print set-printer \"" + printer + "\" --paper \"<name>\"");
        return 0;
    }

    public static int SetPrinter(AppPaths paths, Cli cli)
    {
        if (cli.Positional.Count == 0 && !cli.Has("paper") && !cli.Has("rotate") && !cli.Has("no-rotate") && !cli.Has("copies"))
        {
            Console.Error.WriteLine("Usage: vpass-print set-printer \"<printer name>\" [--paper <name>] [--rotate|--no-rotate] [--copies N]");
            return 2;
        }
        paths.EnsureDirectories();
        var holder = new ConfigHolder(new ConfigStore(paths));
        IPrinterBackend backend = PrinterBackendFactory.Create(NullLoggerFactory.Instance);
        string? name = cli.Positional.Count > 0 ? cli.Positional[0] : null;
        if (name != null)
        {
            IReadOnlyList<string> known = backend.ListPrinters();
            if (!known.Contains(name))
            {
                string? match = known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                if (match != null) name = match;
                else Console.WriteLine($"Warning: '{name}' is not in the printer list right now (saved anyway).");
            }
        }
        holder.Update(c =>
        {
            if (name != null) c.OsPrinterName = name;
            if (cli.Has("paper")) c.PaperName = string.IsNullOrWhiteSpace(cli.Get("paper")) ? null : cli.Get("paper");
            if (cli.Has("no-rotate")) c.RotateLandscape = false;
            if (cli.Has("rotate")) c.RotateLandscape = true;
            if (cli.Has("copies")) c.Copies = Math.Clamp(cli.GetInt("copies", 1), 1, 10);
        });
        DeviceConfig cfg = holder.Current;
        Console.WriteLine($"Printer: {cfg.OsPrinterName}   paper: {cfg.PaperName ?? "(default)"}   rotate landscape templates: {(cfg.RotateLandscape ? "yes" : "no")}   copies: {cfg.Copies}");
        Console.WriteLine("The running service applies this within a few seconds.");
        return 0;
    }

    public static int TestPrint(AppPaths paths, Cli cli)
    {
        paths.EnsureDirectories();
        DeviceConfig cfg = new ConfigStore(paths).Load();
        string printer = cli.Get("printer") ?? cfg.OsPrinterName;
        if (string.IsNullOrWhiteSpace(printer))
        {
            Console.Error.WriteLine("No printer selected. Use --printer \"<name>\" or vpass-print set-printer.");
            return 2;
        }
        IPrinterBackend backend = PrinterBackendFactory.Create(NullLoggerFactory.Instance);
        string file = BadgeImage.CreateTestPattern(paths.TempDir, cli.Has("landscape"));
        try
        {
            Console.WriteLine($"Printing test pattern to '{printer}' ...");
            backend.Print(file, new PrintOptions(printer, cli.Get("paper") ?? cfg.PaperName, cli.Has("landscape") ? 3 : 1, cfg.RotateLandscape));
            Console.WriteLine("Sent to the printer.");
            return 0;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static int Install(AppPaths paths, Cli cli)
    {
        paths.EnsureDirectories();
        IServiceInstaller installer = ServiceInstaller.ForCurrentOs(paths);
        Console.WriteLine("Installing " + installer.Describe());
        installer.Install(cli.Get("account"), cli.Get("password"));
        Console.WriteLine("Service installed and started.");
        DeviceConfig cfg = new ConfigStore(paths).Load();
        if (!cfg.IsPaired) Console.WriteLine("Not paired yet: run  vpass-print pair <code> --region <au|us|uk|ca>");
        else if (!cfg.HasPrinter) Console.WriteLine("No printer selected yet: run  vpass-print printers  then  vpass-print set-printer \"<name>\"");
        Console.WriteLine("Check with: vpass-print status");
        return 0;
    }

    public static int Uninstall(AppPaths paths)
    {
        IServiceInstaller installer = ServiceInstaller.ForCurrentOs(paths);
        installer.Uninstall();
        Console.WriteLine("Service removed. Configuration kept in " + paths.DataDir);
        return 0;
    }

    public static int Restart(AppPaths paths)
    {
        ServiceInstaller.ForCurrentOs(paths).Restart();
        Console.WriteLine("Service restarted.");
        return 0;
    }

    public static int Status(AppPaths paths)
    {
        DeviceConfig cfg = new ConfigStore(paths).Load();
        Console.WriteLine($"VPass Print Service {AppVersion.Current}  ({AppVersion.Platform})");
        Console.WriteLine($"Data dir : {paths.DataDir}{(paths.ReadOnly ? "  (read-only here: use sudo / Administrator to change settings)" : "")}");
        if (cfg.IsPaired)
        {
            Console.WriteLine($"Server   : {cfg.ApiBaseUrl}   socket {cfg.SocketUrl}");
        }
        if (!cfg.IsPaired)
        {
            Console.WriteLine("Paired   : NO  — run: vpass-print pair <code> --region <au|us|uk|ca>");
        }
        else
        {
            Console.WriteLine($"Paired   : yes — '{cfg.PrinterName}' @ {cfg.SiteName}{(cfg.LocationName != null ? " / " + cfg.LocationName : " (all locations)")}  [printer #{cfg.PrinterId}, {cfg.Region}]");
            Console.WriteLine($"Token    : valid until {cfg.TokenExpiresOn:yyyy-MM-dd}");
        }
        Console.WriteLine($"Printer  : {(cfg.HasPrinter ? cfg.OsPrinterName : "NOT SET — run: vpass-print set-printer \"<name>\"")}  paper={cfg.PaperName ?? "default"}  rotate-landscape={(cfg.RotateLandscape ? "yes" : "no")}");

        if (!File.Exists(paths.StatusPath))
        {
            Console.WriteLine("Service  : no status yet (not installed or never ran). Install with: vpass-print install");
            return 0;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(paths.StatusPath));
            var r = doc.RootElement;
            DateTime written = r.GetProperty("writtenUtc").GetDateTime();
            double age = (DateTime.UtcNow - written).TotalSeconds;
            string state = r.GetProperty("state").GetString() ?? "?";
            Console.WriteLine($"Service  : {state}{(age > 180 ? "  (status is " + (int)(age / 60) + " min old — service stopped?)" : "")}");
            Console.WriteLine($"Socket   : {(r.GetProperty("socketConnected").GetBoolean() ? "connected" : "disconnected")}   printer ready: {(r.GetProperty("printerOnline").GetBoolean() ? "yes" : "no")}   queue: {r.GetProperty("queueDepth").GetInt32()}");
            Console.WriteLine($"Printed  : {r.GetProperty("printedCount").GetInt64()} since start   failed: {r.GetProperty("failedCount").GetInt64()}   uptime: {TimeSpan.FromSeconds(r.GetProperty("uptimeSeconds").GetInt64()):d\\.hh\\:mm\\:ss}");
            Console.WriteLine($"Last poll: {Fmt(r, "lastPollUtc")}   last print: {Fmt(r, "lastPrintUtc")}   last heartbeat: {Fmt(r, "lastHeartbeatUtc")}");
            if (r.TryGetProperty("lastError", out var err) && err.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                Console.WriteLine($"Last error: {err.GetString()}  ({Fmt(r, "lastErrorUtc")})");
            }
            if (r.TryGetProperty("updateAvailable", out var upd) && upd.GetBoolean())
            {
                Console.WriteLine($"Update   : {r.GetProperty("latestVersion").GetString()} available");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Service  : status file unreadable (" + ex.Message + ")");
        }
        return 0;

        static string Fmt(System.Text.Json.JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetDateTime().ToLocalTime().ToString("HH:mm:ss") : "never";
    }

    public static async Task<int> LogsAsync(AppPaths paths, Cli cli)
    {
        if (!Directory.Exists(paths.LogDir))
        {
            Console.WriteLine("No logs yet.");
            return 0;
        }
        string? latest = Directory.GetFiles(paths.LogDir, "vpass-print-*.log").OrderByDescending(f => f).FirstOrDefault();
        if (latest == null)
        {
            Console.WriteLine("No logs yet.");
            return 0;
        }
        int lines = cli.GetInt("lines", 100);
        using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string all = await reader.ReadToEndAsync();
        string[] split = all.Split('\n');
        foreach (string line in split.Skip(Math.Max(0, split.Length - lines)))
        {
            Console.WriteLine(line);
        }
        if (!cli.Has("follow")) return 0;
        while (true)
        {
            await Task.Delay(1000);
            string? more = await reader.ReadToEndAsync();
            if (!string.IsNullOrEmpty(more)) Console.Write(more);
        }
    }
}
