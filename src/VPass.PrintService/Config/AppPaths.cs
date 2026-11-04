namespace VPass.PrintService.Config;

/// <summary>
/// Per-OS data directory for config, token, ack ledger, status file and logs. Shared between the
/// background service and the CLI (which runs as the installing user), so it lives in a machine-wide
/// location. Override with VPASS_PRINT_DATA_DIR; falls back to the user profile when the machine
/// location is not writable (developer runs without elevation).
/// </summary>
public sealed class AppPaths
{
    public const string ServiceName = "VPassPrintService";
    public const string DisplayName = "VPass Print Service";
    public const string LaunchdLabel = "io.vpass.printservice";
    public const string SystemdUnit = "vpass-print-service";

    public string DataDir { get; }
    /// <summary>True when the directory exists but this process cannot write to it (CLI without sudo).</summary>
    public bool ReadOnly { get; }
    public string ConfigPath => Path.Combine(DataDir, "config.json");
    public string LedgerPath => Path.Combine(DataDir, "acked.json");
    public string StatusPath => Path.Combine(DataDir, "status.json");
    public string LogDir => Path.Combine(DataDir, "logs");
    public string TempDir => Path.Combine(DataDir, "tmp");

    public AppPaths(string? dataDir = null, bool readOnly = false)
    {
        DataDir = dataDir ?? ResolveDataDir();
        ReadOnly = readOnly;
    }

    /// <summary>
    /// For read-only verbs (status, logs, printers, version, help): if the machine-wide directory
    /// exists, report on it even when it is not writable, so `vpass-print status` without sudo shows
    /// the real service state instead of an empty per-user fallback.
    /// </summary>
    public static AppPaths ForCli(string verb)
    {
        string? overrideDir = Environment.GetEnvironmentVariable("VPASS_PRINT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return new AppPaths(overrideDir);
        }
        bool readOnlyVerb = verb is "status" or "logs" or "printers" or "papers" or "version" or "help" or "--help" or "-h" or "--version";
        string machine = DefaultMachineDir();
        if (readOnlyVerb && Directory.Exists(machine) && !IsWritable(machine) && File.Exists(Path.Combine(machine, "config.json")))
        {
            return new AppPaths(machine, readOnly: true);
        }
        return new AppPaths();
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(TempDir);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                // Config holds the device token: owner-only.
                File.SetUnixFileMode(DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // best effort (e.g. filesystem without POSIX modes)
            }
        }
    }

    public static string DefaultMachineDir()
    {
        if (OperatingSystem.IsWindows())
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "VPass", "PrintService");
        }
        if (OperatingSystem.IsMacOS())
        {
            return "/Library/Application Support/VPass/PrintService";
        }
        return "/var/lib/vpass-print-service";
    }

    public static string UserFallbackDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vpass-print-service");

    private static string ResolveDataDir()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("VPASS_PRINT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir;
        }

        string machine = DefaultMachineDir();
        if (IsWritable(machine))
        {
            return machine;
        }
        return UserFallbackDir();
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
