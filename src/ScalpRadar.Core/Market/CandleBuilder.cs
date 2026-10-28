using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Market;

/// <summary>
/// Builds candles of one interval from trades. A trade is a taker buy when isBuyerMaker == false.
/// Candles close when a trade from a later interval arrives, or when the exchange clock passes the close time.
/// Intervals with no trades between two trades become flat zero-volume candles, so the series has no holes.
/// </summary>
public sealed class CandleBuilder(TimeSpan interval)
{
    private Candle? _lastClosed;
    private bool _interrupted;

    public Candle? Current { get; private set; }
    public long LateTrades { get; private set; }

    /// <summary>The next candle this builder opens is flagged partial. True at start: the stream begins mid-minute.</summary>
    public bool NextIsPartial { get; private set; } = true;

    public void AddTrade(DateTime time, double price, double qty, bool isBuyerMaker, List<Candle> closed)
    {
        var open = TimeUtil.Floor(time, interval);

        if (Current is not null && open == Current.OpenTime)
        {
            var c = Current;
            if (price > c.High) c.High = price;
            if (price < c.Low) c.Low = price;
            c.Close = price;
            c.Volume += qty;
            if (!isBuyerMaker) c.BuyVolume += qty;
            c.Trades++;
            return;
        }

        var isLate = Current is not null
            ? open < Current.OpenTime
            : _lastClosed is not null && open < _lastClosed.CloseTime;
        if (isLate)
        {
            LateTrades++; // belongs to a candle that is already closed; ignore
            return;
        }

        if (Current is not null)
        {
            Close(closed);
        }

        // Fill empty intervals between the last closed candle and this trade.
        if (_lastClosed is not null)
        {
            for (var t = _lastClosed.CloseTime; t < open; t += interval)
            {
                var flat = Candle.Flat(t, interval, _lastClosed.Close);
                flat.IsPartial = _interrupted; // after a disconnect an empty minute means missing data, not a quiet market
                closed.Add(flat);
                _lastClosed = flat;
            }
        }

        _interrupted = false;
        Current = Candle.FromTrade(open, interval, price, qty, isBuyerMaker);
        Current.IsPartial = NextIsPartial;
        NextIsPartial = false;
    }

    /// <summary>Closes the current candle once the exchange clock has passed its close time.</summary>
    public void CloseUpTo(DateTime exchangeNow, List<Candle> closed)
    {
        if (Current is not null && exchangeNow >= Current.CloseTime)
            Close(closed);
    }

    /// <summary>Marks the open candle, and any minutes until the next trade, as missing trades (e.g. after a disconnect).</summary>
    public void MarkCurrentPartial()
    {
        if (Current is not null) Current.IsPartial = true;
        _interrupted = true;
        NextIsPartial = true;
    }

    private void Close(List<Candle> closed)
    {
        closed.Add(Current!);
        _lastClosed = Current;
        Current = null;
    }
}

public static class CandleAggregator
{
    /// <summary>Merges consecutive smaller candles into one candle starting at <paramref name="openTime"/>.</summary>
    public static Candle Aggregate(IReadOnlyList<Candle> parts, DateTime openTime, TimeSpan interval)
    {
        if (parts.Count == 0) throw new ArgumentException("No candles to aggregate", nameof(parts));
        var c = new Candle
        {
            OpenTime = openTime,
            Interval = interval,
            Open = parts[0].Open,
            High = double.MinValue,
            Low = double.MaxValue,
            Close = parts[^1].Close,
        };
        foreach (var p in parts)
        {
            if (p.High > c.High) c.High = p.High;
            if (p.Low < c.Low) c.Low = p.Low;
            c.Volume += p.Volume;
            c.BuyVolume += p.BuyVolume;
            c.Trades += p.Trades;
            c.IsPartial |= p.IsPartial;
        }
        return c;
    }
}
