using ScalpRadar.Core.Indicators;
using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Reports;
using ScalpRadar.Core.Rules;
using ScalpRadar.Worker.Notifications;

namespace ScalpRadar.Worker.Market;

// Per-symbol data events and the scoring step that runs on every Tick.
public sealed partial class MarketStateLoop
{
    private const int TopKeep = 10;

    private IReadOnlyDictionary<string, int> _fundingIntervals = new Dictionary<string, int>();
    private int _defaultFundingHours = 8;
    private IReadOnlyList<TopRow> _top = [];
    private ILogger? _forceOrderLog;
    private DateTime _lastScoreErrorLog;

    private int BookCount => _states.Values.Count(s => s.Book is not null);

    private SymbolState CreateState(SymbolInfo info) => new(info, _stateOptions, _heatmapOptions)
    {
        FundingIntervalHours = _fundingIntervals.GetValueOrDefault(info.Symbol, _defaultFundingHours),
    };

    private void OnSymbolRemoved(string symbol)
    {
        if (_signals.Shortlist.Remove(symbol)) _books.Stop(symbol);
        _signals.Decider.Forget(symbol);
        OnSymbolRemovedMore(symbol);
    }

    private static void ApplyMark(SymbolState s, MarkPriceUpdate u, DateTime receivedUtc) => s.ApplyMark(u, receivedUtc);

    /// <summary>
    /// Every forceOrder event is stored. Liquidations are a sample (max one per symbol per second).
    /// At Debug level ("ScalpRadar.Worker.Market.ForceOrderDebug"), each is logged next to the heatmap bucket it hit.
    /// </summary>
    private void OnForceOrder(ForceOrderEvent f)
    {
        _outbox.Post(new LiquidationItem(f));
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

    private void OnOi(OiEvent o)
    {
        if (_states.TryGetValue(o.Symbol, out var s)) s.ApplyOi(o);
    }

    private void OnBookSnapshot(BookSnapshotEvent b)
    {
        if (_states.TryGetValue(b.Symbol, out var s)) s.Book?.Apply(b.ReceivedUtc, b.Bids, b.Asks);
    }

    private void OnBookStatus(BookStatusEvent b)
    {
        if (!_states.TryGetValue(b.Symbol, out var s)) return;
        s.Book = b.Active ? new BookTracker(_bookOptions) : null; // a fresh book starts with no history
    }

    private void OnFundingIntervals(FundingIntervalsEvent f)
    {
        _fundingIntervals = f.IntervalHours;
        _defaultFundingHours = f.DefaultHours;
        foreach (var s in _states.Values)
            s.FundingIntervalHours = f.IntervalHours.GetValueOrDefault(s.Symbol, f.DefaultHours); // not listed -> 8h
    }

    // ------------------------------------------------------------------ scoring

    private void Score(Tick tick)
    {
        var now = LocalNow;
        var r = _signals.Rules;
        double? btc15 = _states.TryGetValue(_signals.Benchmark, out var bench)
            ? PriceChange.Pct(bench.Candles1m, r.OiLongWindowMinutes)
            : null;

        var candidates = new List<ShortlistCandidate>(_states.Count);
        var top = new List<TopRow>(_states.Count);

        foreach (var s in _states.Values)
        {
            try
            {
                if (s.Candles1m.Count == 0) continue;
                var m = MetricsCalculator.Compute(s, now, r);
                var longEval = _signals.Engine.Evaluate(m, Direction.Long, _signals.Cooldown, now);
                var shortEval = _signals.Engine.Evaluate(m, Direction.Short, _signals.Cooldown, now);
                var best = longEval.Score >= shortEval.Score ? longEval : shortEval;

                candidates.Add(new ShortlistCandidate(s.Symbol, Math.Max(longEval.MustHavesPassed, shortEval.MustHavesPassed), best.Score));
                top.Add(ToTop(best));

                Act(longEval, m, now, btc15);
                Act(shortEval, m, now, btc15);
            }
            catch (Exception ex)
            {
                if (now - _lastScoreErrorLog > TimeSpan.FromSeconds(30))
                {
                    _lastScoreErrorLog = now;
                    _log.LogError(ex, "Scoring {Symbol} failed", s.Symbol);
                }
            }
        }

        var (add, remove) = _signals.Shortlist.Update(candidates, now);
        foreach (var symbol in remove) _books.Stop(symbol);
        foreach (var symbol in add) _books.Start(symbol);
        if (add.Count > 0 || remove.Count > 0)
            _log.LogInformation("Shortlist: +[{Added}] -[{Removed}] -> {Count} books wanted",
                string.Join(",", add), string.Join(",", remove), _signals.Shortlist.Active.Count);

        _top = top.OrderByDescending(t => t.Score).ThenBy(t => t.Symbol, StringComparer.Ordinal).Take(TopKeep).ToList();
    }

    private void Act(Evaluation e, SignalMetrics m, DateTime now, double? btc15)
    {
        switch (_signals.Decider.Decide(e, m, now))
        {
            case DecisionKind.Alert:
                _signals.Cooldown.Register(e.Symbol, e.Direction, e.Tier!.Value, now);
                var alert = new AlertCandidate(Guid.NewGuid(), now, e, m, btc15, _signals.ConfigHash);
                _outbox.Post(new AlertItem(alert));
                OnAlertRaised(alert);
                break;

            case DecisionKind.NearMiss:
                _outbox.Post(new NearMissItem(new NearMissRecord(now, e, m, _signals.ConfigHash)));
                _log.LogInformation("Near miss {Direction} {Symbol}: {Failed}", e.Direction, e.Symbol,
                    string.Join("; ", e.Confirmations.Where(c => !c.Passed).Select(c => $"{c.Code} {c.Detail}")
                        .Concat(e.Vetoes.Where(v => v.Triggered).Select(v => $"{v.Code} {v.Detail}"))));
                break;

            case DecisionKind.WaitForBook:
                _log.LogDebug("{Symbol} {Direction}: all must-haves pass; waiting for order book history", e.Symbol, e.Direction);
                break;
        }
    }

    private static TopRow ToTop(Evaluation e)
    {
        var marks = string.Join(" ", e.MustHaves.Select(c => (c.Passed ? "✓" : "✗") + c.Code))
                    + " | " + string.Join(" ", e.Confirmations.Select(c => (c.Passed ? "✓" : "✗") + c.Code));
        var vetoes = e.Vetoes.Where(v => v.Triggered).Select(v => v.Code).ToList();
        if (vetoes.Count > 0) marks += " | veto " + string.Join(",", vetoes);
        return new TopRow(e.Symbol, e.Direction.Label(), e.Score, e.MustHavesPassed, e.ConfirmationsPassed, marks);
    }

    private IReadOnlyList<TopRow> TopEntries() => _top;

    // Outcome tracking (milestone 7).
    partial void OnAlertRaised(AlertCandidate alert);
    partial void OnSymbolRemovedMore(string symbol);
    partial void OnCandlesClosedMore(SymbolState s, List<Candle> closed1m);
    partial void OnAnyTradeMore(TradeEvent t);

    private void OnCandlesClosed(SymbolState s, List<Candle> closed1m) => OnCandlesClosedMore(s, closed1m);
    private void OnAnyTrade(TradeEvent t) => OnAnyTradeMore(t);
}
