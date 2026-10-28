using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Worker.Infrastructure;

/// <summary>The one bounded channel every producer posts into. A single consumer (MarketStateLoop) reads it.</summary>
public sealed class MarketChannel
{
    private readonly Channel<MarketEvent> _channel;
    private readonly ILogger<MarketChannel> _log;
    private long _dropped;
    private long _posted;
    private DateTime _lastDropLog;

    public MarketChannel(IOptions<EngineOptions> options, ILogger<MarketChannel> log)
    {
        _log = log;
        _channel = Channel.CreateBounded<MarketEvent>(new BoundedChannelOptions(options.Value.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ChannelReader<MarketEvent> Reader => _channel.Reader;
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Posted => Interlocked.Read(ref _posted);
    public int Count => _channel.Reader.Count;

    /// <summary>Non-blocking post for WebSocket callbacks. If the channel is full the event is dropped and counted.</summary>
    public void Post(MarketEvent e)
    {
        if (_channel.Writer.TryWrite(e))
        {
            Interlocked.Increment(ref _posted);
            return;
        }

        var dropped = Interlocked.Increment(ref _dropped);
        var now = DateTime.UtcNow;
        if (now - _lastDropLog > TimeSpan.FromSeconds(10))
        {
            _lastDropLog = now;
            _log.LogWarning("Market channel full: dropped {Dropped} events so far. The consumer is falling behind", dropped);
        }
    }

    /// <summary>Blocking post for control events that must not be lost (universe, backfill results).</summary>
    public async ValueTask PostAsync(MarketEvent e, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync(e, ct);
        Interlocked.Increment(ref _posted);
    }
}
