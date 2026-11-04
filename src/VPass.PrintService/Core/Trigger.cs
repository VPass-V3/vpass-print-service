using System.Threading.Channels;

namespace VPass.PrintService.Core;

/// <summary>Why the job loop should poll now. Every one of these results in the same action: fetch pending jobs.</summary>
public enum Trigger
{
    Startup,
    SocketConnected,
    SocketMessage,
    Timer,
    Resume,
    Retry,
    Manual
}

/// <summary>Unbounded, coalescing wake-up channel between the signal sources and the single job-loop consumer.</summary>
public sealed class TriggerBus
{
    private readonly Channel<Trigger> _channel = Channel.CreateUnbounded<Trigger>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ChannelReader<Trigger> Reader => _channel.Reader;

    public void Signal(Trigger trigger) => _channel.Writer.TryWrite(trigger);
}
