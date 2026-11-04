using VPass.PrintService.Api;
using VPass.PrintService.Printing;

namespace VPass.PrintService.Tests;

public sealed class FakeApi : IPrinterApiClient
{
    public Queue<List<PrintJob>> Batches { get; } = new();
    public List<(long JobId, AckRequest Ack)> Acks { get; } = new();
    public int FetchCount { get; private set; }
    public bool ThrowRevoked { get; set; }
    public bool ThrowNetwork { get; set; }

    public Task<PairResponse> PairAsync(string apiBaseUrl, PairRequest request, CancellationToken ct) => throw new NotSupportedException();
    public Task<MeResponse> MeAsync(CancellationToken ct) => Task.FromResult(new MeResponse { StatusCode = 200 });

    public Task<PendingJobsResponse> GetPendingAsync(int limit, CancellationToken ct)
    {
        FetchCount++;
        if (ThrowRevoked) throw new RevokedException("REVOKED");
        if (ThrowNetwork) throw new HttpRequestException("connection refused");
        var jobs = Batches.Count > 0 ? Batches.Dequeue() : new List<PrintJob>();
        return Task.FromResult(new PendingJobsResponse { StatusCode = 200, ServerTime = DateTime.UtcNow, Jobs = jobs });
    }

    public Task AckAsync(long jobId, AckRequest request, CancellationToken ct)
    {
        if (ThrowRevoked) throw new RevokedException("REVOKED");
        Acks.Add((jobId, request));
        return Task.CompletedTask;
    }

    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) =>
        Task.FromResult(new HeartbeatResponse { StatusCode = 200, PollIntervalSeconds = 45 });

    public Task<TokenResponse> RefreshTokenAsync(CancellationToken ct) =>
        Task.FromResult(new TokenResponse { StatusCode = 200, DeviceToken = "new", TokenExpiresOn = DateTime.UtcNow.AddYears(2) });
}

public sealed class FakeBackend : IPrinterBackend
{
    public enum Mode { Ok, Offline, Throw }
    public Mode Behaviour { get; set; } = Mode.Ok;
    public List<PrintOptions> Prints { get; } = new();
    public List<(int Width, int Height)> PrintedSizes { get; } = new();
    public string Name => "fake";
    public IReadOnlyList<string> ListPrinters() => new[] { "Fake Printer" };
    public bool IsOnline(string? printerName) => Behaviour != Mode.Offline && printerName == "Fake Printer";

    public void Print(string imagePath, PrintOptions options)
    {
        switch (Behaviour)
        {
            case Mode.Offline: throw new PrinterOfflineException("Fake Printer is offline");
            case Mode.Throw: throw new InvalidOperationException("driver exploded");
        }
        using var img = SixLabors.ImageSharp.Image.Load(imagePath);
        PrintedSizes.Add((img.Width, img.Height));
        Prints.Add(options);
    }
}
