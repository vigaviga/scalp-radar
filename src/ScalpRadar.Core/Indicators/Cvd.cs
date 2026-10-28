using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Indicators;

public sealed record CvdResult(double? Cvd15, double? Cvd5Share, bool BearishDivergence, bool BullishDivergence);

public static class Cvd
{
    /// <summary>
    /// cvd15 = sum of delta over the last <paramref name="sumMinutes"/> closed 1m candles.
    /// cvd5Share = sum of delta over the last <paramref name="shareMinutes"/> ÷ volume over the same candles.
    /// Bearish divergence: price at or above the last pivot high while CVD now is below the CVD at that pivot.
    /// Bullish divergence: price at or below the last pivot low while CVD now is above the CVD at that pivot.
    /// </summary>
    public static CvdResult Compute(IReadOnlyList<Candle> closed1m, int sumMinutes, int shareMinutes,
        double? price, double cvdNow, Pivot? lastHigh, Pivot? lastLow)
    {
        double? cvd15 = null, share = null;
        if (closed1m.Count >= sumMinutes && sumMinutes > 0)
        {
            double sum = 0;
            for (var i = closed1m.Count - sumMinutes; i < closed1m.Count; i++) sum += closed1m[i].Delta;
            cvd15 = sum;
        }
        if (closed1m.Count >= shareMinutes && shareMinutes > 0)
        {
            double delta = 0, vol = 0;
            for (var i = closed1m.Count - shareMinutes; i < closed1m.Count; i++)
            {
                delta += closed1m[i].Delta;
                vol += closed1m[i].Volume;
            }
            if (vol > 0) share = delta / vol;
        }

        var bearish = price is { } p1 && lastHigh is not null && p1 >= lastHigh.Price && cvdNow < lastHigh.Cvd;
        var bullish = price is { } p2 && lastLow is not null && p2 <= lastLow.Price && cvdNow > lastLow.Cvd;
        return new CvdResult(cvd15, share, bearish, bullish);
    }
}

public static class Atr
{
    /// <summary>Wilder's ATR: seeded with the mean of the first <paramref name="period"/> true ranges, then smoothed.</summary>
    public static double? Wilder(IReadOnlyList<Candle> candles, int period)
    {
        if (period <= 0 || candles.Count < period + 1) return null;
        double atr = 0;
        for (var i = 1; i <= period; i++) atr += TrueRange(candles[i], candles[i - 1].Close);
        atr /= period;
        for (var i = period + 1; i < candles.Count; i++)
            atr = (atr * (period - 1) + TrueRange(candles[i], candles[i - 1].Close)) / period;
        return atr;
    }

    public static double TrueRange(Candle c, double prevClose) =>
        Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
}
