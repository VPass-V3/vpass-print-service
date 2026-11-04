using System.Diagnostics;
using VPass.PrintService.Config;
using VPass.PrintService.Util;

namespace VPass.PrintService.Install;

public interface IServiceInstaller
{
    string Describe();
    void Install(string? account, string? password);
    void Uninstall();
    void Restart();
}

public static class ServiceInstaller
{
    public static IServiceInstaller ForCurrentOs(AppPaths paths)
    {
        if (OperatingSystem.IsWindows()) return new WindowsServiceInstaller(paths);
        if (OperatingSystem.IsMacOS()) return new LaunchdInstaller(paths);
        return new SystemdInstaller(paths);
    }

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("Cannot determine executable path");

    internal static void Require(ProcessResult r, string what)
    {
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException($"{what} failed ({r.ExitCode}): {(r.StdErr + " " + r.StdOut).Trim()}");
        }
    }
}

/// <summary>Windows Service via sc.exe. LocalSystem by default; use --account for a user that can see per-user network printers.</summary>
public sealed class WindowsServiceInstaller : IServiceInstaller
{
    private readonly AppPaths _paths;
    public WindowsServiceInstaller(AppPaths paths) { _paths = paths; }

    public string Describe() => $"Windows Service '{AppPaths.ServiceName}' (sc.exe)";

    public void Install(string? account, string? password)
    {
        string exe = ServiceInstaller.ExecutablePath;
        var create = new List<string>
        {
            "create", AppPaths.ServiceName,
            "binPath=", $"\"{exe}\" run",
            "start=", "delayed-auto",
            "DisplayName=", AppPaths.DisplayName
        };
        if (!string.IsNullOrWhiteSpace(account))
        {
            create.Add("obj="); create.Add(account);
            if (!string.IsNullOrEmpty(password)) { create.Add("password="); create.Add(password); }
        }
        ServiceInstaller.Require(ProcessRunner.Run("sc.exe", create.ToArray(), TimeSpan.FromSeconds(30)), "sc create");
        ProcessRunner.Run("sc.exe", new[] { "description", AppPaths.ServiceName, "Prints VPass visitor badges on the connected label printer." }, TimeSpan.FromSeconds(30));
        // Restart on crash: 5 s, 10 s, then every 60 s; counter resets after a day.
        ProcessRunner.Run("sc.exe", new[] { "failure", AppPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000" }, TimeSpan.FromSeconds(30));
        ProcessRunner.Run("sc.exe", new[] { "failureflag", AppPaths.ServiceName, "1" }, TimeSpan.FromSeconds(30));
        ServiceInstaller.Require(ProcessRunner.Run("sc.exe", new[] { "start", AppPaths.ServiceName }, TimeSpan.FromSeconds(60)), "sc start");
    }

    public void Uninstall()
    {
        ProcessRunner.Run("sc.exe", new[] { "stop", AppPaths.ServiceName }, TimeSpan.FromSeconds(60));
        Thread.Sleep(2000);
        ServiceInstaller.Require(ProcessRunner.Run("sc.exe", new[] { "delete", AppPaths.ServiceName }, TimeSpan.FromSeconds(30)), "sc delete");
    }

    public void Restart()
    {
        ProcessRunner.Run("sc.exe", new[] { "stop", AppPaths.ServiceName }, TimeSpan.FromSeconds(60));
        Thread.Sleep(2000);
        ServiceInstaller.Require(ProcessRunner.Run("sc.exe", new[] { "start", AppPaths.ServiceName }, TimeSpan.FromSeconds(60)), "sc start");
    }
}

/// <summary>macOS LaunchDaemon (runs at boot, before login, KeepAlive).</summary>
public sealed class LaunchdInstaller : IServiceInstaller
{
    private readonly AppPaths _paths;
    public LaunchdInstaller(AppPaths paths) { _paths = paths; }

    private static string PlistPath => $"/Library/LaunchDaemons/{AppPaths.LaunchdLabel}.plist";

    public string Describe() => $"launchd daemon {AppPaths.LaunchdLabel} ({PlistPath})";

    public void Install(string? account, string? password)
    {
        string exe = ServiceInstaller.ExecutablePath;
        string plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
  <key>Label</key><string>{AppPaths.LaunchdLabel}</string>
  <key>ProgramArguments</key>
  <array>
    <string>{exe}</string>
    <string>run</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>5</integer>
  <key>EnvironmentVariables</key>
  <dict>
    <key>VPASS_PRINT_DATA_DIR</key><string>{_paths.DataDir}</string>
    <key>PATH</key><string>/usr/bin:/bin:/usr/sbin:/sbin:/usr/local/bin</string>
  </dict>
  <key>StandardOutPath</key><string>{Path.Combine(_paths.LogDir, "launchd.out.log")}</string>
  <key>StandardErrorPath</key><string>{Path.Combine(_paths.LogDir, "launchd.err.log")}</string>
</dict>
</plist>
";
        File.WriteAllText(PlistPath, plist);
        ProcessRunner.Run("chown", new[] { "root:wheel", PlistPath }, TimeSpan.FromSeconds(10));
        ProcessRunner.Run("chmod", new[] { "644", PlistPath }, TimeSpan.FromSeconds(10));
        ProcessRunner.Run("launchctl", new[] { "bootout", "system/" + AppPaths.LaunchdLabel }, TimeSpan.FromSeconds(30), throwOnTimeout: false);
        ServiceInstaller.Require(ProcessRunner.Run("launchctl", new[] { "bootstrap", "system", PlistPath }, TimeSpan.FromSeconds(30)), "launchctl bootstrap");
    }

    public void Uninstall()
    {
        ProcessRunner.Run("launchctl", new[] { "bootout", "system/" + AppPaths.LaunchdLabel }, TimeSpan.FromSeconds(30), throwOnTimeout: false);
        if (File.Exists(PlistPath)) File.Delete(PlistPath);
    }

    public void Restart()
    {
        ServiceInstaller.Require(ProcessRunner.Run("launchctl", new[] { "kickstart", "-k", "system/" + AppPaths.LaunchdLabel }, TimeSpan.FromSeconds(30)), "launchctl kickstart");
    }
}

/// <summary>systemd unit (Restart=always, after network and CUPS).</summary>
public sealed class SystemdInstaller : IServiceInstaller
{
    private readonly AppPaths _paths;
    public SystemdInstaller(AppPaths paths) { _paths = paths; }

    private static string UnitPath => $"/etc/systemd/system/{AppPaths.SystemdUnit}.service";

    public string Describe() => $"systemd unit {AppPaths.SystemdUnit} ({UnitPath})";

    public void Install(string? account, string? password)
    {
        string exe = ServiceInstaller.ExecutablePath;
        string user = string.IsNullOrWhiteSpace(account) ? "root" : account;
        string unit = $@"[Unit]
Description={AppPaths.DisplayName}
After=network-online.target cups.service
Wants=network-online.target

[Service]
Type=notify
ExecStart={exe} run
Restart=always
RestartSec=5
User={user}
Environment=VPASS_PRINT_DATA_DIR={_paths.DataDir}
Environment=DOTNET_gcServer=0

[Install]
WantedBy=multi-user.target
";
        File.WriteAllText(UnitPath, unit);
        ServiceInstaller.Require(ProcessRunner.Run("systemctl", new[] { "daemon-reload" }, TimeSpan.FromSeconds(30)), "systemctl daemon-reload");
        ServiceInstaller.Require(ProcessRunner.Run("systemctl", new[] { "enable", "--now", AppPaths.SystemdUnit }, TimeSpan.FromSeconds(60)), "systemctl enable");
    }

    public void Uninstall()
    {
        ProcessRunner.Run("systemctl", new[] { "disable", "--now", AppPaths.SystemdUnit }, TimeSpan.FromSeconds(60), throwOnTimeout: false);
        if (File.Exists(UnitPath)) File.Delete(UnitPath);
        ProcessRunner.Run("systemctl", new[] { "daemon-reload" }, TimeSpan.FromSeconds(30), throwOnTimeout: false);
    }

    public void Restart()
    {
        ServiceInstaller.Require(ProcessRunner.Run("systemctl", new[] { "restart", AppPaths.SystemdUnit }, TimeSpan.FromSeconds(60)), "systemctl restart");
    }
}
