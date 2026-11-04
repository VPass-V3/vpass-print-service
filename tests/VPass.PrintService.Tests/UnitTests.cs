using VPass.PrintService;
using VPass.PrintService.Config;
using VPass.PrintService.Core;
using VPass.PrintService.Printing;
using Xunit;

namespace VPass.PrintService.Tests;

public sealed class AckLedgerTests
{
    [Fact]
    public void EvictsOldestBeyondCapacityAndPersists()
    {
        string path = Path.Combine(Path.GetTempPath(), "ledger-" + Guid.NewGuid().ToString("N"), "acked.json");
        var ledger = new AckLedger(path, capacity: 3);
        ledger.Add(1); ledger.Add(2); ledger.Add(3); ledger.Add(4);

        Assert.False(ledger.Contains(1));
        Assert.True(ledger.Contains(2));
        Assert.True(ledger.Contains(4));

        var reloaded = new AckLedger(path, capacity: 3);
        Assert.True(reloaded.Contains(4));
        Assert.False(reloaded.Contains(1));
        Assert.Equal(3, reloaded.Count);
    }
}

public sealed class SleepDetectorTests
{
    [Fact]
    public void NormalTickDoesNotTrigger() =>
        Assert.False(SleepDetector.ShouldTriggerResume(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7)));

    [Fact]
    public void LongGapTriggersResume() =>
        Assert.True(SleepDetector.ShouldTriggerResume(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(12)));
}

public sealed class BadgeImageTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(null, false)]
    public void LandscapeTemplatesAre3And4(int? template, bool landscape) =>
        Assert.Equal(landscape, BadgeImage.IsLandscapeTemplate(template));

    [Fact]
    public void RotationHonoursSetting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "badge-" + Guid.NewGuid().ToString("N"));
        string b64 = JobLoopTests.SampleBadge(40, 20);

        using (var rotated = SixLabors.ImageSharp.Image.Load(BadgeImage.PrepareFile(b64, 4, rotateLandscape: true, dir)))
            Assert.Equal((20, 40), (rotated.Width, rotated.Height));
        using (var kept = SixLabors.ImageSharp.Image.Load(BadgeImage.PrepareFile(b64, 4, rotateLandscape: false, dir)))
            Assert.Equal((40, 20), (kept.Width, kept.Height));
    }

    [Fact]
    public void DataUriPrefixIsAccepted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "badge-" + Guid.NewGuid().ToString("N"));
        string path = BadgeImage.PrepareFile("data:image/jpeg;base64," + JobLoopTests.SampleBadge(), 1, true, dir);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void InvalidBase64IsAPrintException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "badge-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<PrintException>(() => BadgeImage.PrepareFile("not base64 !!!", 1, true, dir));
    }
}

public sealed class RegionsAndCliTests
{
    [Theory]
    [InlineData("aus", "au")]
    [InlineData("AU", "au")]
    [InlineData("canada", "ca")]
    [InlineData("uk", "uk")]
    [InlineData(null, "au")]
    public void NormaliseMapsServerLocationToRegion(string? input, string expected) =>
        Assert.Equal(expected, Regions.Normalise(input));

    [Fact]
    public void KnownRegionsResolveToVPassHosts()
    {
        Assert.True(Regions.TryGet("uk", out var uk));
        Assert.Equal("https://visitorapi-uk.vpass.io", uk.ApiBaseUrl);
        Assert.Equal("wss://wsuk.vpass.io/v1", uk.SocketUrl);
        Assert.False(Regions.TryGet("mars", out var fallback));
        Assert.Equal("https://visitorapi.vpass.io", fallback.ApiBaseUrl);
    }

    [Fact]
    public void CliParsesPositionalsFlagsAndValues()
    {
        var cli = Cli.Parse(new[] { "7KQ2-M9XR", "--region", "uk", "--no-rotate", "--printer=Brother QL", "--copies", "2" });
        Assert.Equal("7KQ2-M9XR", cli.Positional[0]);
        Assert.Equal("uk", cli.Get("region"));
        Assert.True(cli.Has("no-rotate"));
        Assert.Equal("Brother QL", cli.Get("printer"));
        Assert.Equal(2, cli.GetInt("copies", 1));
    }
}

public sealed class ConfigStoreTests
{
    [Fact]
    public void TokenRoundTripsAndConfigIsAtomicallySaved()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "cfg-" + Guid.NewGuid().ToString("N")));
        var store = new ConfigStore(paths);
        var cfg = new DeviceConfig { PrinterId = 9 };
        ConfigStore.SetToken(cfg, "secret-token");
        store.Save(cfg);

        DeviceConfig loaded = store.Load();
        Assert.Equal(9, loaded.PrinterId);
        Assert.True(loaded.IsPaired);
        Assert.Equal("secret-token", ConfigStore.GetToken(loaded));
        Assert.False(File.Exists(paths.ConfigPath + ".tmp"));
    }
}
