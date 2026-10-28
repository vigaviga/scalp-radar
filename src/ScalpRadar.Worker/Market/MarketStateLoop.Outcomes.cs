using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Outcomes;
using ScalpRadar.Core.Rules;
using ScalpRadar.Worker.Notifications;

namespace ScalpRadar.Worker.Market;

// Live outcome tracking for raised alerts, and the feed-health messages.
public sealed partial class MarketStateLoop
{
    private readonly Dictionary<string, List<OutcomeTracker>> _trackers = new(StringComparer.Ordinal);
    private DateTime? _lastTradeUtc;
    private DateTime? _feedDownSince; // time of the last trade before the feed went down

    partial void OnAlertRaised(AlertCandidate alert)
    {
        var e = alert.Evaluation;
        if (alert.Metrics.Price is not { } entry || e.Invalidation is not { } invalidation)
        {
            _log.LogWarning("Alert {Symbol} {Direction} has no price or invalidation; outcome not tracked", e.Symbol, e.Direction);
            return;
        }

        // Trades carry exchange time, so the tracker starts on the exchange clock too.
        var spec = new OutcomeSpec(alert.Uid, e.Symbol, e.Direction, ExchangeNow, entry, invalidation, e.Target);
        if (!_trackers.TryGetValue(e.Symbol, out var list)) _trackers[e.Symbol] = list = [];
        list.Add(new OutcomeTracker(spec));
    }

    partial void OnAnyTradeMore(TradeEvent t)
    {
        _lastTradeUtc = LocalNow;
        if (_feedDownSince is { } since)
        {
            _feedDownSince = null;
            var down = LocalNow - since;
            _log.LogWarning("Feed recovered; no trades for {Down}", down);
            _outbox.Post(new NoticeItem($"✅ feed recovered (no trades for {Duration(down)})"));
        }

        if (!_trackers.TryGetValue(t.Symbol, out var list)) return;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].OnTrade(t.Time, t.Price, LocalNow) is not { } result) continue;
            _outbox.Post(new OutcomeItem(list[i].Spec, result));
            list.RemoveAt(i);
        }
        if (list.Count == 0) _trackers.Remove(t.Symbol);
    }

    partial void OnSymbolRemovedMore(string symbol)
    {
        if (_trackers.Remove(symbol, out var list))
            _log.LogInformation("{Symbol} left the universe; {Count} open outcome(s) will be resolved from klines", symbol, list.Count);
    }

    /// <summary>Runs on every Tick: feed-health check and hand-off of trackers that never saw their end.</summary>
    private void CheckFeedAndTrackers()
    {
        var now = LocalNow;
        if (_feedDownSince is null && _lastTradeUtc is { } last && now - last >= _signals.FeedDownAfter)
        {
            _feedDownSince = last;
            _log.LogError("Feed down: no trades for {Seconds:F0}s", (now - last).TotalSeconds);
            _outbox.Post(new NoticeItem($"⚠ feed down: no trades for {Duration(now - last)} (last at {last:HH:mm:ss} UTC)"));
        }

        if (_trackers.Count == 0) return;
        var cutoff = ExchangeNow - TimeSpan.FromMinutes(_signals.Outcomes.FinalizeGraceMinutes);
        foreach (var (symbol, list) in _trackers.ToList())
        {
            var dropped = list.RemoveAll(x => x.EndUtc < cutoff);
            if (dropped > 0)
                _log.LogWarning("{Symbol}: {Count} outcome(s) got no trade after their end; resolving from klines instead", symbol, dropped);
            if (list.Count == 0) _trackers.Remove(symbol);
        }
    }

    private static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{d.Seconds}s";
}
