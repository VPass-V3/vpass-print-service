namespace VPass.PrintService.Config;

/// <summary>Public endpoints per VPass region. The pair response may override both URLs.</summary>
public static class Regions
{
    public sealed record Endpoints(string ApiBaseUrl, string SocketUrl);

    private static readonly Dictionary<string, Endpoints> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["au"] = new("https://visitorapi.vpass.io", "wss://ws.vpass.io/v1"),
        ["aus"] = new("https://visitorapi.vpass.io", "wss://ws.vpass.io/v1"),
        ["prod"] = new("https://visitorapi.vpass.io", "wss://ws.vpass.io/v1"),
        ["us"] = new("https://visitorapi-us.vpass.io", "wss://wsus.vpass.io/v1"),
        ["uk"] = new("https://visitorapi-uk.vpass.io", "wss://wsuk.vpass.io/v1"),
        ["ca"] = new("https://visitorapi-ca.vpass.io", "wss://wsca.vpass.io/v1"),
        ["canada"] = new("https://visitorapi-ca.vpass.io", "wss://wsca.vpass.io/v1"),
        ["qa"] = new("https://visitorapiqa.vpass.io", "wss://wsstage.vpass.io/v1"),
        ["dev"] = new("https://visitorapidev.vpass.io", "wss://wsdev.vpass.io/v1"),
    };

    public static IEnumerable<string> Known => new[] { "au", "us", "uk", "ca", "qa", "dev" };

    public static bool TryGet(string? region, out Endpoints endpoints)
    {
        if (!string.IsNullOrWhiteSpace(region) && Map.TryGetValue(region.Trim(), out var e))
        {
            endpoints = e;
            return true;
        }
        endpoints = Map["au"];
        return false;
    }

    /// <summary>ServerLocation values from the API (qa, aus, uk, us, ca) → CLI region names.</summary>
    public static string Normalise(string? region) => region?.Trim().ToLowerInvariant() switch
    {
        "aus" or "prod" or "au" => "au",
        "canada" or "ca" => "ca",
        "us" => "us",
        "uk" => "uk",
        "qa" => "qa",
        "dev" => "dev",
        _ => "au"
    };
}
