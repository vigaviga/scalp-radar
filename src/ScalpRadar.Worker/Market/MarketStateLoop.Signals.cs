using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;

namespace ScalpRadar.Worker.Market;

// Milestone 2 placeholders. Later milestones fill these in (OI, funding, books, scoring).
public sealed partial class MarketStateLoop
{
    private int BookCount => 0;

    private IReadOnlyDictionary<string, int> _fundingIntervals = new Dictionary<string, int>();
    private int _defaultFundingHours = 8;

    private SymbolState CreateState(SymbolInfo info) => new(info, _stateOptions, _heatmapOptions)
    {
        FundingIntervalHours = _fundingIntervals.GetValueOrDefault(info.Symbol, _defaultFundingHours),
    };

    private void OnSymbolRemoved(string symbol) { }

    private void OnAnyTrade(TradeEvent t) { }

    private static void ApplyMark(SymbolState s, MarkPriceUpdate u, DateTime receivedUtc) => s.ApplyMark(u, receivedUtc);

    private ILogger? _forceOrderLog;

    /// <summary>Liquidations are a sample (max one per symbol per second). At Debug level, log each next to the
    /// heatmap bucket it hit, to sanity-check the model. Set "ScalpRadar.Worker.Market.ForceOrderDebug" to Debug.</summary>
    private void OnForceOrder(ForceOrderEvent f)
    {
        OnLiquidation(f);
        if (!_states.TryGetValue(f.Symbol, out var s)) return;
        _forceOrderLog ??= _loggerFactory.CreateLogger("ScalpRadar.Worker.Market.ForceOrderDebug");
        if (!_forceOrderLog.IsEnabled(LogLevel.Debug)) return;

        // A SELL liquidation closes a long; a BUY liquidation closes a short.
        var longSide = f.Side == "SELL";
        var bucket = s.Heatmap.BucketIndex(f.Price);
        var est = longSide ? s.Heatmap.LongBucketAt(f.Price) : s.Heatmap.ShortBucketAt(f.Price);
        var sideTotal = longSide ? s.Heatmap.LongBuckets.Values.Sum() : s.Heatmap.ShortBuckets.Values.Sum();
        _forceOrderLog.LogDebug(
            "{Symbol} liquidation {Side} {Notional:N0} USDT @ {Price} -> {Map} bucket {Lo:G6}-{Hi:G6} holds est. {Est:N0} USDT (side total {Total:N0})",
            f.Symbol, longSide ? "LONG" : "SHORT", f.Price * f.Qty, f.Price, longSide ? "long-liq" : "short-liq",
            s.Heatmap.BucketLow(bucket), s.Heatmap.BucketHigh(bucket), est, sideTotal);
    }

    // Stored in Postgres from milestone 6.
    private void OnLiquidation(ForceOrderEvent f) { }

    private void OnOi(OiEvent o)
    {
        if (_states.TryGetValue(o.Symbol, out var s)) s.ApplyOi(o);
    }

    private void OnBookSnapshot(BookSnapshotEvent b) { }

    private void OnBookStatus(BookStatusEvent b) { }

    private void OnFundingIntervals(FundingIntervalsEvent f)
    {
        _fundingIntervals = f.IntervalHours;
        _defaultFundingHours = f.DefaultHours;
        foreach (var s in _states.Values)
            s.FundingIntervalHours = f.IntervalHours.GetValueOrDefault(s.Symbol, f.DefaultHours); // not listed -> 8h
    }

    private void OnCandlesClosed(SymbolState s, List<Candle> closed1m) { }

    private void Score(Tick tick) { }

    private IReadOnlyList<TopEntry> TopEntries() => [];
}
