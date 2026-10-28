using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Outcomes;

public enum FirstHit
{
    None,
    Target,
    Invalidation,
}

/// <summary>What is tracked for one alert. Entry is the alert price; start is the alert time (exchange clock).</summary>
public sealed record OutcomeSpec(
    Guid Uid,
    string Symbol,
    Direction Direction,
    DateTime StartUtc,
    double Entry,
    double Invalidation,
    double? Target);

/// <summary>MFE and MAE are magnitudes in % of the entry price (both are 0 or more).</summary>
public sealed record OutcomeResult(
    double? Price5m,
    double? Price15m,
    double? Price60m,
    double MfePct,
    double MaePct,
    FirstHit FirstHit,
    DateTime ResolvedUtc);

/// <summary>
/// Follows one alert for 60 minutes. Prices at +5/+15/+60 min are the last trade price at that moment.
/// MFE/MAE come from the highest and lowest prices seen. The first hit is decided per 1-minute bucket of
/// high/low; when target and invalidation are both inside the same minute, invalidation wins (conservative).
/// The checkpoints and the 60-minute window match the alert_outcomes columns, so they are not configurable.
/// </summary>
public sealed class OutcomeTracker
{
    public static readonly int[] CheckpointMinutes = [5, 15, 60];
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(60);

    private readonly double?[] _prices = new double?[3];
    private double _high;
    private double _low;
    private double _last;
    private FirstHit? _hit;

    private DateTime _bucketStart;
    private double _bucketHigh;
    private double _bucketLow;
    private bool _bucketHasData;

    public OutcomeTracker(OutcomeSpec spec)
    {
        Spec = spec;
        _high = _low = _last = spec.Entry;
        _bucketStart = TimeUtil.Floor(spec.StartUtc, TimeUtil.OneMinute);
    }

    public OutcomeSpec Spec { get; }
    public DateTime EndUtc => Spec.StartUtc + Duration;
    public bool IsDone { get; private set; }

    /// <summary>Feeds one live trade. Returns the result once a trade at or after the end arrives.</summary>
    public OutcomeResult? OnTrade(DateTime time, double price, DateTime nowUtc)
    {
        if (IsDone || time < Spec.StartUtc) return null;

        for (var i = 0; i < CheckpointMinutes.Length; i++)
        {
            if (_prices[i] is null && time >= Spec.StartUtc.AddMinutes(CheckpointMinutes[i]))
                _prices[i] = _last; // the price as of the checkpoint
        }

        if (time >= EndUtc)
        {
            CloseBucket();
            return Finish(nowUtc);
        }

        var minute = TimeUtil.Floor(time, TimeUtil.OneMinute);
        if (minute != _bucketStart)
        {
            CloseBucket();
            _bucketStart = minute;
        }

        if (!_bucketHasData)
        {
            _bucketHigh = _bucketLow = price;
            _bucketHasData = true;
        }
        else
        {
            _bucketHigh = Math.Max(_bucketHigh, price);
            _bucketLow = Math.Min(_bucketLow, price);
        }
        _high = Math.Max(_high, price);
        _low = Math.Min(_low, price);
        _last = price;
        return null;
    }

    /// <summary>
    /// Resolves an alert from 1m klines (restart recovery, or when live tracking missed the end).
    /// Uses the candles from the alert's minute up to the last one that closes by the end. The first candle
    /// may include a few seconds before the alert; that is the price of using klines.
    /// </summary>
    public static OutcomeResult FromCandles(OutcomeSpec spec, IEnumerable<Candle> candles1m, DateTime nowUtc)
    {
        var t = new OutcomeTracker(spec);
        var first = TimeUtil.Floor(spec.StartUtc, TimeUtil.OneMinute);
        foreach (var c in candles1m.Where(c => c.OpenTime >= first && c.CloseTime <= t.EndUtc).OrderBy(c => c.OpenTime))
            t.ApplyCandle(c);
        return t.Finish(nowUtc);
    }

    private void ApplyCandle(Candle c)
    {
        for (var i = 0; i < CheckpointMinutes.Length; i++)
        {
            if (c.CloseTime <= Spec.StartUtc.AddMinutes(CheckpointMinutes[i]))
                _prices[i] = c.Close; // keeps the last candle that closed by the checkpoint
        }
        _bucketHigh = c.High;
        _bucketLow = c.Low;
        _bucketHasData = true;
        CloseBucket();
        _high = Math.Max(_high, c.High);
        _low = Math.Min(_low, c.Low);
        _last = c.Close;
    }

    private void CloseBucket()
    {
        if (!_bucketHasData) return;
        _bucketHasData = false;
        if (_hit is not null) return;

        var s = Spec;
        var isLong = s.Direction == Direction.Long;
        var invalidated = isLong ? _bucketLow <= s.Invalidation : _bucketHigh >= s.Invalidation;
        var targetHit = s.Target is { } target && (isLong ? _bucketHigh >= target : _bucketLow <= target);

        if (invalidated) _hit = FirstHit.Invalidation;
        else if (targetHit) _hit = FirstHit.Target;
    }

    private OutcomeResult Finish(DateTime nowUtc)
    {
        IsDone = true;
        var entry = Spec.Entry;
        var up = (_high - entry) / entry * 100;
        var down = (entry - _low) / entry * 100;
        var isLong = Spec.Direction == Direction.Long;
        return new OutcomeResult(
            _prices[0], _prices[1], _prices[2],
            MfePct: Math.Max(0, isLong ? up : down),
            MaePct: Math.Max(0, isLong ? down : up),
            _hit ?? FirstHit.None,
            nowUtc);
    }
}
