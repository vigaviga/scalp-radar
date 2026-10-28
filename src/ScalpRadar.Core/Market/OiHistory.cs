using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Market;

/// <summary>Open interest samples (time, contracts, notional), oldest first. Mixes 5m history with 30 s live polls.</summary>
public sealed class OiHistory(TimeSpan keep)
{
    private readonly List<OiSample> _samples = [];

    public IReadOnlyList<OiSample> Samples => _samples;
    public OiSample? Latest => _samples.Count > 0 ? _samples[^1] : null;

    public void Add(OiSample s)
    {
        // Almost always appended; insert in order otherwise. Same timestamp replaces.
        var i = _samples.Count - 1;
        while (i >= 0 && _samples[i].Time > s.Time) i--;
        if (i >= 0 && _samples[i].Time == s.Time) _samples[i] = s;
        else _samples.Insert(i + 1, s);
        Trim();
    }

    public void AddRange(IEnumerable<OiSample> samples)
    {
        foreach (var s in samples) Add(s);
    }

    /// <summary>The latest sample at or before <paramref name="time"/>, or null if history does not reach back that far.</summary>
    public OiSample? At(DateTime time)
    {
        int lo = 0, hi = _samples.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_samples[mid].Time <= time) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 ? _samples[found] : null;
    }

    /// <summary>% change in OI contracts from <paramref name="window"/> before the latest sample to the latest sample.</summary>
    public double? ChangePct(TimeSpan window)
    {
        if (Latest is not { } now) return null;
        if (At(now.Time - window) is not { } then || then.OpenInterest <= 0) return null;
        return (now.OpenInterest / then.OpenInterest - 1) * 100;
    }

    private void Trim()
    {
        if (_samples.Count == 0) return;
        var cutoff = _samples[^1].Time - keep;
        var remove = 0;
        // Keep one sample older than the cutoff so At(cutoff) still has a value.
        while (remove + 1 < _samples.Count && _samples[remove + 1].Time < cutoff) remove++;
        if (remove > 0) _samples.RemoveRange(0, remove);
    }
}
