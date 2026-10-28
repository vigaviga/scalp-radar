using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Indicators;

public enum OiRegime
{
    Neutral,
    NewLongs,       // price up, OI up
    NewShorts,      // price down, OI up
    ShortCovering,  // price up, OI down: weak rally
    LongUnwinding,  // price down, OI down: weak drop
}

public static class OpenInterest
{
    /// <summary>
    /// Regime from the price change and OI change over the same window. A move within ±<paramref name="flatPct"/>
    /// (inclusive) counts as flat, and any flat leg gives Neutral.
    /// </summary>
    public static OiRegime Classify(double? priceChangePct, double? oiChangePct, double flatPct)
    {
        if (priceChangePct is not { } p || oiChangePct is not { } oi) return OiRegime.Neutral;
        var price = Sign(p, flatPct);
        var interest = Sign(oi, flatPct);
        return (price, interest) switch
        {
            (1, 1) => OiRegime.NewLongs,
            (-1, 1) => OiRegime.NewShorts,
            (1, -1) => OiRegime.ShortCovering,
            (-1, -1) => OiRegime.LongUnwinding,
            _ => OiRegime.Neutral,
        };
    }

    private static int Sign(double pct, double flat) => pct > flat ? 1 : pct < -flat ? -1 : 0;
}

public static class PriceChange
{
    /// <summary>% change from the close <paramref name="minutes"/> 1m candles ago to the last close.</summary>
    public static double? Pct(IReadOnlyList<Candle> closed1m, int minutes)
    {
        if (closed1m.Count <= minutes || minutes <= 0) return null;
        var then = closed1m[^(minutes + 1)].Close;
        return then > 0 ? (closed1m[^1].Close / then - 1) * 100 : null;
    }
}
