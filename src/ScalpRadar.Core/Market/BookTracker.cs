using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Market;

public sealed record Wall(bool IsBid, double Price, double Notional, DateTime FirstSeen);

public static class BookMath
{
    public static double? Mid(BookLevel[] bids, BookLevel[] asks) =>
        bids.Length > 0 && asks.Length > 0 ? (bids[0].Price + asks[0].Price) / 2 : null;

    /// <summary>Spread in percent: (best ask − best bid) / mid × 100.</summary>
    public static double? SpreadPct(BookLevel[] bids, BookLevel[] asks) =>
        Mid(bids, asks) is { } mid && mid > 0 ? (asks[0].Price - bids[0].Price) / mid * 100 : null;

    /// <summary>(bid notional − ask notional) ÷ (bid + ask notional), counting levels within ±rangePct of mid.</summary>
    public static double? Imbalance(BookLevel[] bids, BookLevel[] asks, double rangePct)
    {
        if (Mid(bids, asks) is not { } mid) return null;
        double lo = mid * (1 - rangePct / 100), hi = mid * (1 + rangePct / 100);
        double bid = 0, ask = 0;
        foreach (var b in bids) if (b.Price >= lo) bid += b.Price * b.Qty;
        foreach (var a in asks) if (a.Price <= hi) ask += a.Price * a.Qty;
        var total = bid + ask;
        return total > 0 ? (bid - ask) / total : null;
    }

    /// <summary>Levels within ±rangePct of mid whose notional is at least <paramref name="multiple"/> × the median level notional in that range.</summary>
    public static List<(bool IsBid, double Price, double Notional)> Walls(BookLevel[] bids, BookLevel[] asks, double rangePct, double multiple)
    {
        var result = new List<(bool, double, double)>();
        if (Mid(bids, asks) is not { } mid) return result;
        double lo = mid * (1 - rangePct / 100), hi = mid * (1 + rangePct / 100);

        var levels = new List<(bool IsBid, double Price, double Notional)>();
        foreach (var b in bids) if (b.Price >= lo) levels.Add((true, b.Price, b.Price * b.Qty));
        foreach (var a in asks) if (a.Price <= hi) levels.Add((false, a.Price, a.Price * a.Qty));
        if (levels.Count == 0) return result;

        var median = ScalpRadar.Core.Indicators.Stats.Median(levels.Select(l => l.Notional).ToList())!.Value;
        foreach (var l in levels)
            if (median > 0 && l.Notional >= multiple * median) result.Add(l);
        return result;
    }
}

/// <summary>
/// Order book metrics for one shortlisted symbol, fed by one snapshot per second:
/// 30 s average imbalance, spread, and walls with a hold-time spoof filter.
/// </summary>
public sealed class BookTracker(OrderBookOptions o)
{
    private readonly Queue<(DateTime Time, double Imbalance)> _samples = new();
    private readonly Dictionary<(bool IsBid, double Price), DateTime> _firstSeen = new();
    private List<Wall> _walls = [];

    public DateTime? FirstSnapshotUtc { get; private set; }
    public DateTime? LastSnapshotUtc { get; private set; }
    public double? SpreadPct { get; private set; }
    public double? Mid { get; private set; }

    public void Apply(DateTime time, BookLevel[] bids, BookLevel[] asks)
    {
        Mid = BookMath.Mid(bids, asks);
        if (Mid is null) return;

        FirstSnapshotUtc ??= time;
        LastSnapshotUtc = time;
        SpreadPct = BookMath.SpreadPct(bids, asks);

        if (BookMath.Imbalance(bids, asks, o.ImbalanceRangePct) is { } imb) _samples.Enqueue((time, imb));
        while (_samples.Count > 0 && time - _samples.Peek().Time >= TimeSpan.FromSeconds(o.ImbalanceWindowSeconds))
            _samples.Dequeue();

        // A wall keeps its first-seen time only while it is continuously present.
        var now = BookMath.Walls(bids, asks, o.WallRangePct, o.WallMedianMultiple);
        var seen = new HashSet<(bool, double)>();
        var walls = new List<Wall>(now.Count);
        foreach (var (isBid, price, notional) in now)
        {
            var key = (isBid, price);
            seen.Add(key);
            if (!_firstSeen.TryGetValue(key, out var first)) _firstSeen[key] = first = time;
            walls.Add(new Wall(isBid, price, notional, first));
        }
        foreach (var key in _firstSeen.Keys.Where(k => !seen.Contains(k)).ToList()) _firstSeen.Remove(key);
        _walls = walls;
    }

    /// <summary>Average of the imbalance samples in the last ImbalanceWindowSeconds.</summary>
    public double? Imbalance(DateTime now)
    {
        double sum = 0;
        var n = 0;
        foreach (var (t, v) in _samples)
        {
            if (now - t >= TimeSpan.FromSeconds(o.ImbalanceWindowSeconds)) continue; // the last 30 s, exclusive
            sum += v;
            n++;
        }
        return n > 0 ? sum / n : null;
    }

    public double HistorySeconds(DateTime now) => FirstSnapshotUtc is { } f ? (now - f).TotalSeconds : 0;

    public double? AgeSeconds(DateTime now) => LastSnapshotUtc is { } t ? (now - t).TotalSeconds : null;

    /// <summary>Walls that have held for at least WallMinHoldSeconds (the spoof filter).</summary>
    public IEnumerable<Wall> PersistentWalls(DateTime now) =>
        _walls.Where(w => now - w.FirstSeen >= TimeSpan.FromSeconds(o.WallMinHoldSeconds));
}
