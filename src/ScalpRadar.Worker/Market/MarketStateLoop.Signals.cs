using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;

namespace ScalpRadar.Worker.Market;

// Milestone 2 placeholders. Later milestones fill these in (OI, funding, books, scoring).
public sealed partial class MarketStateLoop
{
    private int BookCount => 0;

    private IReadOnlyDictionary<string, int> _fundingIntervals = new Dictionary<string, int>();
    private int _defaultFundingHours = 8;

    private SymbolState CreateState(SymbolInfo info) => new(info, _stateOptions)
    {
        FundingIntervalHours = _fundingIntervals.GetValueOrDefault(info.Symbol, _defaultFundingHours),
    };

    private void OnSymbolRemoved(string symbol) { }

    private void OnAnyTrade(TradeEvent t) { }

    private static void ApplyMark(SymbolState s, MarkPriceUpdate u, DateTime receivedUtc) => s.ApplyMark(u, receivedUtc);

    private void OnForceOrder(ForceOrderEvent f) { }

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
