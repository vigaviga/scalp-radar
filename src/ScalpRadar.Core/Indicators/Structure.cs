using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Indicators;

public enum PivotKind { High, Low }

/// <summary>A swing pivot on a closed 5m candle, with the CVD at that candle's close.</summary>
public sealed record Pivot(PivotKind Kind, DateTime Time, double Price, double Cvd);

public static class Pivots
{
    /// <summary>
    /// A pivot high is strictly higher than the <paramref name="left"/> candles before it and the
    /// <paramref name="right"/> candles after it; a pivot low is the mirror. Returned oldest first.
    /// </summary>
    public static List<Pivot> Find(IReadOnlyList<Candle> candles, int left, int right)
    {
        var result = new List<Pivot>();
        for (var i = left; i < candles.Count - right; i++)
        {
            bool isHigh = true, isLow = true;
            for (var j = i - left; j <= i + right && (isHigh || isLow); j++)
            {
                if (j == i) continue;
                if (candles[j].High >= candles[i].High) isHigh = false;
                if (candles[j].Low <= candles[i].Low) isLow = false;
            }
            if (isHigh) result.Add(new Pivot(PivotKind.High, candles[i].OpenTime, candles[i].High, candles[i].Cvd));
            if (isLow) result.Add(new Pivot(PivotKind.Low, candles[i].OpenTime, candles[i].Low, candles[i].Cvd));
        }
        return result;
    }
}

public sealed record StructureResult(
    bool Long,
    bool Short,
    Pivot? LastHigh,
    Pivot? PrevHigh,
    Pivot? LastLow,
    Pivot? PrevLow,
    double? LastClose)
{
    public static readonly StructureResult None = new(false, false, null, null, null, null, null);

    /// <summary>LONG breaks out above the last pivot high; SHORT breaks down below the last pivot low.</summary>
    public double? BreakoutLevel(Direction d) => d == Direction.Long ? LastHigh?.Price : LastLow?.Price;

    /// <summary>Invalidation: the last pivot low for LONG, the last pivot high for SHORT.</summary>
    public double? Invalidation(Direction d) => d == Direction.Long ? LastLow?.Price : LastHigh?.Price;
}

public static class Structure
{
    /// <summary>
    /// LONG: the last pivot low is above the previous one (higher low) AND the last closed 1m candle closed above
    /// the last pivot high (higher high). SHORT is the mirror.
    /// </summary>
    public static StructureResult Evaluate(IReadOnlyList<Pivot> pivots, double? lastClosed1mClose)
    {
        Pivot? lastHigh = null, prevHigh = null, lastLow = null, prevLow = null;
        for (var i = pivots.Count - 1; i >= 0 && (prevHigh is null || prevLow is null); i--)
        {
            var p = pivots[i];
            if (p.Kind == PivotKind.High)
            {
                if (lastHigh is null) lastHigh = p;
                else prevHigh ??= p;
            }
            else
            {
                if (lastLow is null) lastLow = p;
                else prevLow ??= p;
            }
        }

        var isLong = lastClosed1mClose is { } c1
                     && lastLow is not null && prevLow is not null && lastHigh is not null
                     && lastLow.Price > prevLow.Price
                     && c1 > lastHigh.Price;

        var isShort = lastClosed1mClose is { } c2
                      && lastHigh is not null && prevHigh is not null && lastLow is not null
                      && lastHigh.Price < prevHigh.Price
                      && c2 < lastLow.Price;

        return new StructureResult(isLong, isShort, lastHigh, prevHigh, lastLow, prevLow, lastClosed1mClose);
    }
}
