using System.Globalization;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Indicators;

public sealed record LiqCluster(double LowPrice, double HighPrice, double Notional)
{
    public double Center => (LowPrice + HighPrice) / 2;
}

/// <summary>
/// DIY estimate of where leveraged positions would be liquidated (Binance has no heatmap API).
/// Two maps per symbol, long-liquidation and short-liquidation notional, in log-price buckets 0.1% wide:
/// index = floor(ln(price) / ln(1 + width)).
/// </summary>
public sealed class LiquidationHeatmap
{
    private readonly HeatmapOptions _o;
    private readonly double _logStep;
    private readonly (double Leverage, double Weight)[] _mix;
    private readonly SortedDictionary<int, double> _long = new();
    private readonly SortedDictionary<int, double> _short = new();
    private DateTime? _lastDecay;

    public LiquidationHeatmap(HeatmapOptions options)
    {
        _o = options;
        _logStep = Math.Log(1 + options.BucketWidthPct / 100.0);
        _mix = options.LeverageMix
            .Select(kv => (double.Parse(kv.Key.TrimEnd('x', 'X'), CultureInfo.InvariantCulture), kv.Value))
            .ToArray();
    }

    public IReadOnlyDictionary<int, double> LongBuckets => _long;
    public IReadOnlyDictionary<int, double> ShortBuckets => _short;

    public int BucketIndex(double price) => (int)Math.Floor(Math.Log(price) / _logStep);
    public double BucketLow(int index) => Math.Exp(index * _logStep);
    public double BucketHigh(int index) => Math.Exp((index + 1) * _logStep);

    public void Clear()
    {
        _long.Clear();
        _short.Clear();
        _lastDecay = null;
    }

    /// <summary>
    /// Applies one closed 5m candle: decay to its close, clear buckets the candle crossed, then add or remove
    /// positions from the OI change over the candle (<paramref name="oiBefore"/> → <paramref name="oiAfter"/>, in contracts).
    /// </summary>
    public void ApplyCandle(Candle c, double? oiBefore, double? oiAfter)
    {
        DecayTo(c.CloseTime);
        ClearCrossed(c.Low, c.High);

        if (oiBefore is not { } before || oiAfter is not { } after) return;
        var delta = after - before;

        if (delta > 0)
        {
            var p = c.Typical;
            var notional = delta * p;
            var longShare = c.Volume > 0 ? c.BuyVolume / c.Volume : 0.5;
            longShare = Math.Clamp(longShare, _o.LongShareMin, _o.LongShareMax);
            var mmr = _o.MaintenanceMarginRate;
            foreach (var (lev, w) in _mix)
            {
                Add(_long, p * (1 - 1 / lev + mmr), notional * longShare * w);
                Add(_short, p * (1 + 1 / lev - mmr), notional * (1 - longShare) * w);
            }
        }
        else if (delta < 0 && before > 0)
        {
            var factor = Math.Clamp(1 - Math.Abs(delta) / before, 0, 1);
            Scale(_long, factor);
            Scale(_short, factor);
        }
    }

    /// <summary>Exponential decay with the configured half-life, measured on candle (exchange) time.</summary>
    public void DecayTo(DateTime time)
    {
        if (_lastDecay is { } last && time > last && _o.HalfLifeHours > 0)
        {
            var factor = Math.Pow(0.5, (time - last).TotalHours / _o.HalfLifeHours);
            Scale(_long, factor);
            Scale(_short, factor);
        }
        if (_lastDecay is null || time > _lastDecay) _lastDecay = time;
    }

    /// <summary>A candle's low reaching a long-liq bucket, or its high reaching a short-liq bucket, zeroes it.</summary>
    public void ClearCrossed(double low, double high)
    {
        var lowIdx = BucketIndex(low);
        foreach (var k in _long.Keys.Where(k => k >= lowIdx).ToList()) _long.Remove(k);
        var highIdx = BucketIndex(high);
        foreach (var k in _short.Keys.Where(k => k <= highIdx).ToList()) _short.Remove(k);
    }

    /// <summary>Short-liq notional in [price × (1 + near), price × (1 + far)].</summary>
    public double ShortLiqAbove(double price) =>
        Sum(_short, price * (1 + _o.QueryNearPct / 100), price * (1 + _o.QueryFarPct / 100));

    /// <summary>Long-liq notional in [price × (1 − far), price × (1 − near)].</summary>
    public double LongLiqBelow(double price) =>
        Sum(_long, price * (1 - _o.QueryFarPct / 100), price * (1 - _o.QueryNearPct / 100));

    public LiqCluster? LargestShortClusterAbove(double price) =>
        LargestCluster(_short, price * (1 + _o.QueryNearPct / 100), price * (1 + _o.QueryFarPct / 100));

    public LiqCluster? LargestLongClusterBelow(double price) =>
        LargestCluster(_long, price * (1 - _o.QueryFarPct / 100), price * (1 - _o.QueryNearPct / 100));

    /// <summary>Adds estimated liquidation notional at a price (long-liq or short-liq map).</summary>
    public void AddNotional(bool longLiquidations, double price, double notional) =>
        Add(longLiquidations ? _long : _short, price, notional);

    public double LongBucketAt(double price) => _long.GetValueOrDefault(BucketIndex(price));
    public double ShortBucketAt(double price) => _short.GetValueOrDefault(BucketIndex(price));

    // ---------------------------------------------------------------- helpers

    private void Add(SortedDictionary<int, double> map, double price, double notional)
    {
        if (price <= 0 || notional <= 0) return;
        var idx = BucketIndex(price);
        map[idx] = map.GetValueOrDefault(idx) + notional;
    }

    private void Scale(SortedDictionary<int, double> map, double factor)
    {
        foreach (var k in map.Keys.ToList())
        {
            var v = map[k] * factor;
            if (v < _o.PruneBelowUsd) map.Remove(k);
            else map[k] = v;
        }
    }

    private double Sum(SortedDictionary<int, double> map, double lo, double hi)
    {
        int a = BucketIndex(lo), b = BucketIndex(hi);
        double sum = 0;
        foreach (var (k, v) in map)
        {
            if (k < a) continue;
            if (k > b) break;
            sum += v;
        }
        return sum;
    }

    /// <summary>Merges adjacent non-empty buckets inside [lo, hi] and returns the group with the most notional.</summary>
    private LiqCluster? LargestCluster(SortedDictionary<int, double> map, double lo, double hi)
    {
        int a = BucketIndex(lo), b = BucketIndex(hi);
        LiqCluster? best = null;
        int? start = null, prev = null;
        double sum = 0;

        void Close()
        {
            if (start is null) return;
            if (best is null || sum > best.Notional)
                best = new LiqCluster(BucketLow(start.Value), BucketHigh(prev!.Value), sum);
        }

        foreach (var (k, v) in map)
        {
            if (k < a || v <= 0) continue;
            if (k > b) break;
            if (prev is { } p && k == p + 1)
            {
                sum += v;
            }
            else
            {
                Close();
                start = k;
                sum = v;
            }
            prev = k;
        }
        Close();
        return best;
    }
}
