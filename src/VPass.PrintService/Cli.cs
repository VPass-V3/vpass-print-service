namespace VPass.PrintService;

/// <summary>Tiny argument parser: positionals plus `--key value` / `--flag`.</summary>
public sealed class Cli
{
    public List<string> Positional { get; } = new();
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Cli Parse(IEnumerable<string> args)
    {
        var cli = new Cli();
        string? pendingKey = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pendingKey != null) cli.Options[pendingKey] = null;
                string body = arg[2..];
                int eq = body.IndexOf('=');
                if (eq > 0)
                {
                    cli.Options[body[..eq]] = body[(eq + 1)..];
                    pendingKey = null;
                }
                else
                {
                    pendingKey = body;
                }
            }
            else if (pendingKey != null)
            {
                cli.Options[pendingKey] = arg;
                pendingKey = null;
            }
            else
            {
                cli.Positional.Add(arg);
            }
        }
        if (pendingKey != null) cli.Options[pendingKey] = null;
        return cli;
    }

    public string? Get(string key) => Options.TryGetValue(key, out string? v) ? v : null;
    public bool Has(string key) => Options.ContainsKey(key);
    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out int v) ? v : fallback;
}
