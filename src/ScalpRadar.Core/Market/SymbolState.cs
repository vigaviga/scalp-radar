using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Market;

/// <summary>
/// Everything known about one symbol. Owned by the single consumer loop, so it needs no locks.
/// </summary>
public sealed partial class SymbolState
{
    private readonly StateOptions _opt;
    private readonly CandleBuilder _builder = new(TimeUtil.OneMinute);
    private readonly List<Candle> _closedScratch = [];

    public SymbolState(SymbolInfo info, StateOptions options)
    {
        Info = info;
        _opt = options;
    }

    public SymbolInfo Info { get; set; }
    public string Symbol => Info.Symbol;

    /// <summary>Closed 1m candles, oldest first. Kept for State:Keep1mHours.</summary>
    public List<Candle> Candles1m { get; } = [];

    /// <summary>Closed 5m candles, oldest first. Kept for State:Keep5mHours.</summary>
    public List<Candle> Candles5m { get; } = [];

    public Candle? OpenCandle => _builder.Current;
    public long LateTrades => _builder.LateTrades;

    public double? LastPrice { get; private set; }
    public DateTime? LastTradeTime { get; private set; }
    public DateTime? LastTradeReceivedUtc { get; private set; }

    /// <summary>True once the initial REST backfill has been merged.</summary>
    public bool BackfillDone { get; private set; }

    /// <summary>True while the trade stream is disconnected or a gap backfill is pending.</summary>
    public bool Stale { get; set; }

    /// <summary>Set when a gap backfill has been requested and not yet merged, to avoid duplicate requests.</summary>
    public bool GapFillPending { get; set; }

    // ---------------------------------------------------------------- trades and candles

    /// <summary>Applies a trade. Any 1m candles that closed because of it are added to <paramref name="closed1m"/>.</summary>
    public void ApplyTrade(TradeEvent t, List<Candle> closed1m)
    {
        LastPrice = t.Price;
        LastTradeTime = t.Time;
        LastTradeReceivedUtc = t.ReceivedUtc;

        _closedScratch.Clear();
        _builder.AddTrade(t.Time, t.Price, t.Qty, t.IsBuyerMaker, _closedScratch);
        foreach (var c in _closedScratch) OnClosed1m(c);
        closed1m.AddRange(_closedScratch);
    }

    /// <summary>Closes the open 1m candle once the exchange clock has passed its end.</summary>
    public void CloseCandles(DateTime exchangeNow, List<Candle> closed1m)
    {
        _closedScratch.Clear();
        _builder.CloseUpTo(exchangeNow, _closedScratch);
        foreach (var c in _closedScratch) OnClosed1m(c);
        closed1m.AddRange(_closedScratch);
    }

    /// <summary>The trade stream dropped: whatever candle is open is now missing trades.</summary>
    public void OnStreamInterrupted()
    {
        Stale = true;
        _builder.MarkCurrentPartial();
    }

    /// <summary>The oldest closed 1m candle that is missing trades, if any. The consumer requests a gap backfill from it.</summary>
    public DateTime? FirstPartialCandleTime()
    {
        foreach (var c in Candles1m)
            if (c.IsPartial) return c.OpenTime;
        return null;
    }

    /// <summary>Running CVD now: CVD at the last closed 1m candle plus the open candle's delta.</summary>
    public double CurrentCvd()
    {
        var baseCvd = Candles1m.Count > 0 ? Candles1m[^1].Cvd : Candles5m.Count > 0 ? Candles5m[^1].Cvd : 0;
        return baseCvd + (OpenCandle?.Delta ?? 0);
    }

    private void OnClosed1m(Candle c)
    {
        var prev = Candles1m.Count > 0 ? Candles1m[^1] : null;
        c.Cvd = Cvd1m(c, prev);
        Candles1m.Add(c);
        Trim(Candles1m, TimeSpan.FromHours(_opt.Keep1mHours));

        // The 1m candle that ends on a 5-minute boundary completes a 5m candle.
        if (c.CloseTime.Ticks % TimeUtil.FiveMinutes.Ticks == 0)
        {
            var bucketStart = c.CloseTime - TimeUtil.FiveMinutes;
            var parts = Candles1m.Where(x => x.OpenTime >= bucketStart && x.OpenTime < c.CloseTime).ToList();
            var five = CandleAggregator.Aggregate(parts, bucketStart, TimeUtil.FiveMinutes);
            Upsert(Candles5m, five);
            Trim(Candles5m, TimeSpan.FromHours(_opt.Keep5mHours));
            RecomputeCvd5m(Candles5m.IndexOf(five));
            On5mClosed(five);
        }
    }

    // ---------------------------------------------------------------- backfill

    /// <summary>
    /// Merges REST klines. REST candles replace live ones for the same minute (they are complete);
    /// only candles that closed before the live open candle are accepted.
    /// </summary>
    public void ApplyBackfill(BackfillEvent e, DateTime exchangeNow)
    {
        var liveOpen = OpenCandle?.OpenTime ?? TimeUtil.Floor(exchangeNow, TimeUtil.OneMinute);

        foreach (var k in e.Klines1m)
            if (k.CloseTime <= liveOpen) Upsert(Candles1m, k);
        foreach (var k in e.Klines5m)
            if (k.CloseTime <= liveOpen) Upsert(Candles5m, k);

        Candles1m.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        Candles5m.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        Trim(Candles1m, TimeSpan.FromHours(_opt.Keep1mHours));
        Trim(Candles5m, TimeSpan.FromHours(_opt.Keep5mHours));

        RecomputeCvd5m(0);
        for (var i = 0; i < Candles1m.Count; i++)
            Candles1m[i].Cvd = Cvd1m(Candles1m[i], i > 0 ? Candles1m[i - 1] : null);

        OnBackfillMerged(e);

        if (!e.IsGap) BackfillDone = true;
        GapFillPending = false;
        Stale = false;
    }

    // ---------------------------------------------------------------- CVD helpers

    // CVD at a 1m close = CVD at the previous 1m close + this delta. At the first minute of a 5m bucket,
    // start from the CVD of the 5m candle that ended at the bucket start, when we have it.
    private double Cvd1m(Candle c, Candle? prev)
    {
        var bucketStart = TimeUtil.Floor(c.OpenTime, TimeUtil.FiveMinutes);
        if (prev is not null && prev.OpenTime >= bucketStart && prev.OpenTime < c.OpenTime)
            return prev.Cvd + c.Delta;
        var five = Find(Candles5m, bucketStart - TimeUtil.FiveMinutes);
        if (five is not null) return five.Cvd + c.Delta;
        return (prev?.Cvd ?? 0) + c.Delta;
    }

    private void RecomputeCvd5m(int fromIndex)
    {
        if (fromIndex < 0) return;
        for (var i = fromIndex; i < Candles5m.Count; i++)
            Candles5m[i].Cvd = (i > 0 ? Candles5m[i - 1].Cvd : 0) + Candles5m[i].Delta;
    }

    // ---------------------------------------------------------------- list helpers

    private static void Upsert(List<Candle> list, Candle c)
    {
        // Lists are sorted and almost always appended at the end, so search from the back.
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].OpenTime == c.OpenTime) { list[i] = c; return; }
            if (list[i].OpenTime < c.OpenTime) { list.Insert(i + 1, c); return; }
        }
        list.Insert(0, c);
    }

    private static Candle? Find(List<Candle> list, DateTime openTime)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].OpenTime == openTime) return list[i];
            if (list[i].OpenTime < openTime) return null;
        }
        return null;
    }

    private static void Trim(List<Candle> list, TimeSpan keep)
    {
        if (list.Count == 0) return;
        var cutoff = list[^1].CloseTime - keep;
        var remove = 0;
        while (remove < list.Count && list[remove].OpenTime < cutoff) remove++;
        if (remove > 0) list.RemoveRange(0, remove);
    }

    // Extension points filled in by the other partial files (indicators, OI, heatmap).
    partial void On5mClosedCore(Candle five);
    partial void OnBackfillMergedCore(BackfillEvent e);

    private void On5mClosed(Candle five) => On5mClosedCore(five);
    private void OnBackfillMerged(BackfillEvent e) => OnBackfillMergedCore(e);
}
