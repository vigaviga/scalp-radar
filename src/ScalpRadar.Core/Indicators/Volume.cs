using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Indicators;

public sealed record VolumeResult(double? VolRatio, double? UpShare, double? DownShare, double WindowVolume, double? BaselineMedian);

public static class Volume
{
    /// <summary>
    /// volRatio = volume of the last <paramref name="windowMinutes"/> closed 1m candles ÷ the median volume of the
    /// 5m candles in the previous <paramref name="baselineHours"/> (not counting the current window).
    /// upShare / downShare = volume on green / red 1m candles over the last <paramref name="shareMinutes"/> ÷ total.
    /// </summary>
    public static VolumeResult Compute(IReadOnlyList<Candle> closed1m, IReadOnlyList<Candle> closed5m,
        int windowMinutes, int baselineHours, int shareMinutes)
    {
        double? ratio = null, median = null;
        double windowVol = 0;

        if (closed1m.Count >= windowMinutes && windowMinutes > 0)
        {
            var windowStart = closed1m[^windowMinutes].OpenTime;
            for (var i = closed1m.Count - windowMinutes; i < closed1m.Count; i++) windowVol += closed1m[i].Volume;

            var baseline = new List<double>(baselineHours * 12);
            for (var i = closed5m.Count - 1; i >= 0 && baseline.Count < baselineHours * 12; i--)
                if (closed5m[i].CloseTime <= windowStart) baseline.Add(closed5m[i].Volume);

            median = Stats.Median(baseline);
            if (median is > 0) ratio = windowVol / median.Value;
        }

        double? up = null, down = null;
        if (closed1m.Count >= shareMinutes && shareMinutes > 0)
        {
            double total = 0, green = 0, red = 0;
            for (var i = closed1m.Count - shareMinutes; i < closed1m.Count; i++)
            {
                var c = closed1m[i];
                total += c.Volume;
                if (c.IsGreen) green += c.Volume;
                else if (c.IsRed) red += c.Volume;
            }
            if (total > 0)
            {
                up = green / total;
                down = red / total;
            }
        }

        return new VolumeResult(ratio, up, down, windowVol, median);
    }
}

public static class Stats
{
    public static double? Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
