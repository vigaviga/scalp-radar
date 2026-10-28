using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Rules;

public enum DecisionKind
{
    None,

    /// <summary>All must-haves pass but the order book has under MinHistorySeconds of history; wait (up to MaxWaitForHistorySeconds).</summary>
    WaitForBook,

    Alert,

    /// <summary>All must-haves pass but confirmations fail or a veto hits. Logged at most once per 5 min per symbol.</summary>
    NearMiss,
}

/// <summary>Turns an evaluation into an action, holding the little per-symbol state the rules need.</summary>
public sealed class AlertDecider(RulesOptions r, OrderBookOptions book)
{
    private readonly Dictionary<(string, Direction), DateTime> _allMustHavesSince = new();
    private readonly Dictionary<string, DateTime> _lastNearMiss = new(StringComparer.Ordinal);

    public DecisionKind Decide(Evaluation e, SignalMetrics m, DateTime now)
    {
        var key = (e.Symbol, e.Direction);
        if (!e.AllMustHaves)
        {
            _allMustHavesSince.Remove(key);
            return DecisionKind.None;
        }

        if (!_allMustHavesSince.TryGetValue(key, out var since)) _allMustHavesSince[key] = since = now;

        var history = m.BookHistorySeconds ?? 0;
        if (history < book.MinHistorySeconds && now - since < TimeSpan.FromSeconds(book.MaxWaitForHistorySeconds))
            return DecisionKind.WaitForBook;

        if (e.IsAlert) return DecisionKind.Alert;

        if (_lastNearMiss.TryGetValue(e.Symbol, out var last) && now - last < TimeSpan.FromMinutes(r.NearMissIntervalMinutes))
            return DecisionKind.None;
        _lastNearMiss[e.Symbol] = now;
        return DecisionKind.NearMiss;
    }

    public void Forget(string symbol)
    {
        _allMustHavesSince.Remove((symbol, Direction.Long));
        _allMustHavesSince.Remove((symbol, Direction.Short));
        _lastNearMiss.Remove(symbol);
    }
}

/// <summary>
/// At most MaxAlertsPerWindow alerts per fixed FloodWindowMinutes window (aligned to the clock, e.g. :00, :15).
/// Anything beyond waits and goes out as one digest when the window ends.
/// </summary>
public sealed class FloodGuard<T>(RulesOptions r)
{
    private readonly List<T> _digest = [];
    private DateTime _windowStart = DateTime.MinValue;
    private int _sent;

    public int Pending => _digest.Count;

    /// <summary>True: send now. False: held for the digest.</summary>
    public bool Admit(T item, DateTime now)
    {
        if (TryTakeSlot(now)) return true;
        Hold(item);
        return false;
    }

    /// <summary>Takes one of this window's slots if any are left.</summary>
    public bool TryTakeSlot(DateTime now)
    {
        Roll(now);
        if (_sent >= r.MaxAlertsPerWindow) return false;
        _sent++;
        return true;
    }

    /// <summary>Holds an item for this window's digest.</summary>
    public void Hold(T item) => _digest.Add(item);

    /// <summary>Once the window that held items has ended, returns them (once) for the digest message.</summary>
    public IReadOnlyList<T>? TakeDigest(DateTime now)
    {
        if (_digest.Count == 0 || now < _windowStart + Window) return null;
        var items = _digest.ToList();
        _digest.Clear();
        return items;
    }

    /// <summary>Everything still held, regardless of the window (used on shutdown).</summary>
    public IReadOnlyList<T> Drain()
    {
        var items = _digest.ToList();
        _digest.Clear();
        return items;
    }

    private TimeSpan Window => TimeSpan.FromMinutes(r.FloodWindowMinutes);

    private void Roll(DateTime now)
    {
        var start = TimeUtil.Floor(now, Window);
        if (start == _windowStart) return;
        _windowStart = start;
        _sent = 0;
    }
}
