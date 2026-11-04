using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using VPass.PrintService.Api;
using VPass.PrintService.Config;
using VPass.PrintService.Core;
using Xunit;

namespace VPass.PrintService.Tests;

public sealed class JobLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpass-print-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private (JobLoop Loop, FakeApi Api, FakeBackend Backend, AppState State, AckLedger Ledger) Build(bool paired = true, bool withPrinter = true)
    {
        var paths = new AppPaths(_dir);
        paths.EnsureDirectories();
        var store = new ConfigStore(paths);
        var cfg = new DeviceConfig
        {
            Region = "qa", ApiBaseUrl = "http://localhost", SocketUrl = "ws://localhost/v1", Channel = "Printer-1",
            PrinterId = paired ? 1 : 0, OrgId = 1, SiteId = 1, OsPrinterName = withPrinter ? "Fake Printer" : ""
        };
        if (paired) ConfigStore.SetToken(cfg, "token");
        store.Save(cfg);
        var holder = new ConfigHolder(store);
        var api = new FakeApi();
        var backend = new FakeBackend();
        var state = new AppState();
        var ledger = new AckLedger(paths.LedgerPath);
        var loop = new JobLoop(api, backend, ledger, state, new TriggerBus(), holder, paths, NullLogger<JobLoop>.Instance);
        return (loop, api, backend, state, ledger);
    }

    internal static string SampleBadge(int w = 20, int h = 30)
    {
        using var img = new Image<Rgba32>(w, h, Color.White);
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static PrintJob Job(long id, int? template = 1, string? image = null) => new()
    {
        JobId = id, TemplateType = template, BadgeImage = image ?? SampleBadge(), CreatedOn = DateTime.UtcNow, ExpiresOn = DateTime.UtcNow.AddMinutes(10), Attempt = 1
    };

    [Fact]
    public async Task Drain_PrintsAndAcksEachJobInOrder()
    {
        var (loop, api, backend, state, _) = Build();
        api.Batches.Enqueue(new List<PrintJob> { Job(1), Job(2) });

        var result = await loop.DrainAsync(Trigger.Startup, CancellationToken.None);

        Assert.Equal(2, result.Printed);
        Assert.Null(result.StoppedBecause);
        Assert.Equal(2, backend.Prints.Count);
        Assert.Equal(new long[] { 1, 2 }, api.Acks.Select(a => a.JobId).ToArray());
        Assert.All(api.Acks, a => Assert.Equal("printed", a.Ack.Status));
        Assert.Equal(2, state.PrintedCount);
        Assert.Equal(ServiceState.Running, state.State);
        Assert.Equal(2, api.FetchCount); // second fetch returned nothing and ended the drain
    }

    [Fact]
    public async Task Drain_JobAlreadyInLedger_IsAckedWithoutPrinting()
    {
        var (loop, api, backend, _, ledger) = Build();
        ledger.Add(7);
        api.Batches.Enqueue(new List<PrintJob> { Job(7) });

        var result = await loop.DrainAsync(Trigger.SocketMessage, CancellationToken.None);

        Assert.Equal(1, result.Duplicates);
        Assert.Empty(backend.Prints);
        Assert.Single(api.Acks);
        Assert.Equal("printed", api.Acks[0].Ack.Status);
    }

    [Fact]
    public async Task Drain_PrinterOffline_AcksRetriableAndStopsCycle()
    {
        var (loop, api, backend, state, _) = Build();
        backend.Behaviour = FakeBackend.Mode.Offline;
        api.Batches.Enqueue(new List<PrintJob> { Job(1), Job(2) });

        var result = await loop.DrainAsync(Trigger.Timer, CancellationToken.None);

        Assert.Equal("printer-offline", result.StoppedBecause);
        Assert.Empty(backend.Prints);
        Assert.Single(api.Acks); // only the first job was attempted
        Assert.Equal("failed", api.Acks[0].Ack.Status);
        Assert.True(api.Acks[0].Ack.Retriable);
        Assert.Equal(ServiceState.Degraded, state.State);
    }

    [Fact]
    public async Task Drain_DriverException_AcksFailedNotRetriable()
    {
        var (loop, api, backend, state, _) = Build();
        backend.Behaviour = FakeBackend.Mode.Throw;
        api.Batches.Enqueue(new List<PrintJob> { Job(1) });

        var result = await loop.DrainAsync(Trigger.Timer, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Single(api.Acks);
        Assert.Equal("failed", api.Acks[0].Ack.Status);
        Assert.False(api.Acks[0].Ack.Retriable);
        Assert.Equal(1, state.FailedCount);
    }

    [Fact]
    public async Task Drain_EmptyBadge_FailsWithoutPrinting()
    {
        var (loop, api, backend, _, _) = Build();
        api.Batches.Enqueue(new List<PrintJob> { Job(1, 1, "") });

        var result = await loop.DrainAsync(Trigger.Timer, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Empty(backend.Prints);
        Assert.False(api.Acks[0].Ack.Retriable);
    }

    [Fact]
    public async Task Drain_Revoked_MarksNeedsPairingAndStops()
    {
        var (loop, api, backend, state, _) = Build();
        api.ThrowRevoked = true;

        var result = await loop.DrainAsync(Trigger.Startup, CancellationToken.None);

        Assert.Equal("revoked", result.StoppedBecause);
        Assert.True(state.NeedsPairing);
        Assert.Empty(backend.Prints);

        // Subsequent drains do nothing until re-paired.
        api.ThrowRevoked = false;
        api.Batches.Enqueue(new List<PrintJob> { Job(1) });
        var again = await loop.DrainAsync(Trigger.Timer, CancellationToken.None);
        Assert.Equal("needs-pairing", again.StoppedBecause);
        Assert.Equal(1, api.FetchCount);
    }

    [Fact]
    public async Task Drain_NetworkFailure_StopsAndReportsError()
    {
        var (loop, api, _, state, _) = Build();
        api.ThrowNetwork = true;

        var result = await loop.DrainAsync(Trigger.Startup, CancellationToken.None);

        Assert.Equal("api-unreachable", result.StoppedBecause);
        Assert.Contains("API unreachable", state.LastError);
        Assert.False(state.NeedsPairing);
    }

    [Fact]
    public async Task Drain_NoPrinterConfigured_DoesNotLeaseJobs()
    {
        var (loop, api, _, state, _) = Build(withPrinter: false);
        api.Batches.Enqueue(new List<PrintJob> { Job(1) });

        var result = await loop.DrainAsync(Trigger.Startup, CancellationToken.None);

        Assert.Equal("no-printer", result.StoppedBecause);
        Assert.Equal(0, api.FetchCount);
        Assert.Equal(ServiceState.Degraded, state.State);
    }

    [Fact]
    public async Task Drain_Unpaired_DoesNothing()
    {
        var (loop, api, _, state, _) = Build(paired: false);

        var result = await loop.DrainAsync(Trigger.Startup, CancellationToken.None);

        Assert.Equal("unpaired", result.StoppedBecause);
        Assert.Equal(0, api.FetchCount);
        Assert.Equal(ServiceState.Unpaired, state.State);
    }

    [Fact]
    public async Task Drain_LandscapeTemplate_IsRotatedBeforePrinting()
    {
        var (loop, api, backend, _, _) = Build();
        api.Batches.Enqueue(new List<PrintJob> { Job(1, template: 3, image: SampleBadge(40, 20)), Job(2, template: 1, image: SampleBadge(40, 20)) });

        await loop.DrainAsync(Trigger.Startup, CancellationToken.None);

        Assert.Equal((20, 40), backend.PrintedSizes[0]); // rotated 90°
        Assert.Equal((40, 20), backend.PrintedSizes[1]); // portrait template untouched
    }
}
