using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;

namespace ScalpRadar.Worker.Market;

// Milestone 2 placeholders. Later milestones fill these in (OI, funding, books, scoring).
public sealed partial class MarketStateLoop
{
    private int BookCount => 0;

    private SymbolState CreateState(SymbolInfo info) => new(info, _stateOptions);

    private void OnSymbolRemoved(string symbol) { }

    private void OnAnyTrade(TradeEvent t) { }

    private void ApplyMark(SymbolState s, MarkPriceUpdate u, DateTime receivedUtc) { }

    private void OnForceOrder(ForceOrderEvent f) { }

    private void OnOi(OiEvent o) { }

    private void OnBookSnapshot(BookSnapshotEvent b) { }

    private void OnBookStatus(BookStatusEvent b) { }

    private void OnFundingIntervals(FundingIntervalsEvent f) { }

    private void OnCandlesClosed(SymbolState s, List<Candle> closed1m) { }

    private void Score(Tick tick) { }

    private IReadOnlyList<TopEntry> TopEntries() => [];
}
