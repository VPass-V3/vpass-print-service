using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPass.PrintService.Config;

/// <summary>
/// Atomic JSON persistence for <see cref="DeviceConfig"/> plus token protection. The service
/// re-reads the file when it changes, so `vpass-print pair` / `set-printer` take effect without
/// a restart (see <see cref="ConfigHolder"/>).
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("vpass-print-service-v1");
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public ConfigStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string Path => _paths.ConfigPath;

    public DeviceConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_paths.ConfigPath))
            {
                return new DeviceConfig();
            }
            try
            {
                string json = File.ReadAllText(_paths.ConfigPath);
                return JsonSerializer.Deserialize<DeviceConfig>(json, JsonOptions) ?? new DeviceConfig();
            }
            catch (JsonException)
            {
                // Corrupt file: keep it for inspection and start over rather than crash-loop.
                File.Move(_paths.ConfigPath, _paths.ConfigPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
                return new DeviceConfig();
            }
        }
    }

    public void Save(DeviceConfig config)
    {
        lock (_gate)
        {
            _paths.EnsureDirectories();
            string tmp = _paths.ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
            File.Move(tmp, _paths.ConfigPath, overwrite: true);
        }
    }

    public DateTime? LastWriteUtc() => File.Exists(_paths.ConfigPath) ? File.GetLastWriteTimeUtc(_paths.ConfigPath) : null;

    public static void SetToken(DeviceConfig config, string token)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.LocalMachine);
                config.DeviceTokenProtected = Convert.ToBase64String(cipher);
                config.TokenIsProtected = true;
                return;
            }
            catch (CryptographicException)
            {
                // fall through to plain storage
            }
        }
        config.DeviceTokenProtected = token;
        config.TokenIsProtected = false;
    }

    public static string? GetToken(DeviceConfig config)
    {
        if (string.IsNullOrEmpty(config.DeviceTokenProtected))
        {
            return null;
        }
        if (!config.TokenIsProtected)
        {
            return config.DeviceTokenProtected;
        }
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(config.DeviceTokenProtected), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// Live view of the config for the running service. Re-reads the file when its timestamp changes
/// (checked on every access, throttled to once per 5 s) so pairing or printer changes made with
/// the CLI are picked up by the service without a restart.
/// </summary>
public sealed class ConfigHolder
{
    private readonly ConfigStore _store;
    private readonly object _gate = new();
    private DeviceConfig _current;
    private DateTime? _loadedStamp;
    private DateTime _lastCheck = DateTime.MinValue;

    public event Action<DeviceConfig>? Changed;

    public ConfigHolder(ConfigStore store)
    {
        _store = store;
        _current = store.Load();
        _loadedStamp = store.LastWriteUtc();
    }

    public DeviceConfig Current
    {
        get
        {
            Refresh();
            return _current;
        }
    }

    public void Update(Action<DeviceConfig> mutate)
    {
        lock (_gate)
        {
            mutate(_current);
            _store.Save(_current);
            _loadedStamp = _store.LastWriteUtc();
        }
        Changed?.Invoke(_current);
    }

    private void Refresh()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastCheck).TotalSeconds < 5)
        {
            return;
        }
        lock (_gate)
        {
            _lastCheck = now;
            DateTime? stamp = _store.LastWriteUtc();
            if (stamp == _loadedStamp)
            {
                return;
            }
            _current = _store.Load();
            _loadedStamp = stamp;
        }
        Changed?.Invoke(_current);
    }
}
