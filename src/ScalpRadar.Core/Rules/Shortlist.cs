using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Rules;

public readonly record struct ShortlistCandidate(string Symbol, int BestMustHaves, int Score);

/// <summary>
/// Decides which symbols get a local order book. A symbol joins once it passes MinMustHavesToShortlist (3 of 4)
/// in either direction, so C3 and V2 have history before all must-haves pass. At most MaxBooks books;
/// when full, the lowest-score symbol is evicted for a higher-score newcomer. A symbol leaves after
/// UnsubscribeAfterMinutes below the threshold.
/// </summary>
public sealed class Shortlist(OrderBookOptions o)
{
    private sealed class Entry
    {
        public DateTime LastQualified;
        public int Score;
    }

    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Active => _active.Keys;
    public bool Contains(string symbol) => _active.ContainsKey(symbol);

    public (List<string> Add, List<string> Remove) Update(IEnumerable<ShortlistCandidate> candidates, DateTime now)
    {
        var add = new List<string>();
        var remove = new List<string>();
        var waiting = new List<ShortlistCandidate>();

        foreach (var c in candidates)
        {
            var qualifies = c.BestMustHaves >= o.MinMustHavesToShortlist;
            if (_active.TryGetValue(c.Symbol, out var e))
            {
                e.Score = c.Score;
                if (qualifies) e.LastQualified = now;
            }
            else if (qualifies)
            {
                waiting.Add(c);
            }
        }

        var expiry = TimeSpan.FromMinutes(o.UnsubscribeAfterMinutes);
        foreach (var (symbol, e) in _active.ToList())
        {
            if (now - e.LastQualified <= expiry) continue;
            _active.Remove(symbol);
            remove.Add(symbol);
        }

        foreach (var c in waiting.OrderByDescending(c => c.Score).ThenBy(c => c.Symbol, StringComparer.Ordinal))
        {
            if (_active.Count >= o.MaxBooks)
            {
                var lowest = _active.OrderBy(kv => kv.Value.Score).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
                if (lowest.Value.Score >= c.Score) continue;
                _active.Remove(lowest.Key);
                if (!add.Remove(lowest.Key)) remove.Add(lowest.Key);
            }
            _active[c.Symbol] = new Entry { LastQualified = now, Score = c.Score };
            add.Add(c.Symbol);
        }

        return (add, remove);
    }

    /// <summary>Drops a symbol immediately (e.g. it left the universe).</summary>
    public bool Remove(string symbol) => _active.Remove(symbol);
}
