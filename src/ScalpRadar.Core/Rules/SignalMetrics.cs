using ScalpRadar.Core.Indicators;
using ScalpRadar.Core.Market;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Rules;

/// <summary>Every value computed for one symbol at one tick. Stored as the alert's / near miss's metrics JSON.</summary>
public sealed record SignalMetrics
{
    public required string Symbol { get; init; }
    public int PriceDecimals { get; init; } = 2;
    public DateTime AtUtc { get; init; }

    public double? Price { get; init; }
    public double? PriceChange15Pct { get; init; }

    // Structure
    public bool LongStructure { get; init; }
    public bool ShortStructure { get; init; }
    public double? LastClose1m { get; init; }
    public double? LastPivotHigh { get; init; }
    public double? PrevPivotHigh { get; init; }
    public double? LastPivotLow { get; init; }
    public double? PrevPivotLow { get; init; }
    public double? Atr { get; init; }

    // Volume
    public double? VolRatio { get; init; }
    public double? UpShare { get; init; }
    public double? DownShare { get; init; }

    // Open interest
    public double? Oi15Pct { get; init; }
    public double? Oi5Pct { get; init; }
    public OiRegime Regime { get; init; }

    // Funding
    public double? Rate8hPct { get; init; }
    public int FundingIntervalHours { get; init; }

    // CVD
    public double? Cvd15 { get; init; }
    public double? Cvd5Share { get; init; }
    public bool BearishDivergence { get; init; }
    public bool BullishDivergence { get; init; }

    // Liquidity (heatmap estimate)
    public double? ShortLiqAbove { get; init; }
    public double? LongLiqBelow { get; init; }
    public double? ShortClusterPrice { get; init; }
    public double? ShortClusterNotional { get; init; }
    public double? LongClusterPrice { get; init; }
    public double? LongClusterNotional { get; init; }

    // Order book
    public bool HasBook { get; init; }
    public double? BookHistorySeconds { get; init; }
    public double? Imbalance30s { get; init; }
    public double? SpreadPct { get; init; }
    public double[] BidWalls { get; init; } = [];
    public double[] AskWalls { get; init; } = [];

    // Freshness (seconds since last update, local receive time)
    public double? TradeAgeSeconds { get; init; }
    public double? OiAgeSeconds { get; init; }
    public double? MarkAgeSeconds { get; init; }
    public double? BookAgeSeconds { get; init; }
    public bool BackfillDone { get; init; }
    public bool Stale { get; init; }
}

public static class MetricsCalculator
{
    public static SignalMetrics Compute(SymbolState s, DateTime nowUtc, RulesOptions r)
    {
        var closed1m = s.Candles1m;
        var structure = Structure.Evaluate(s.Pivots5m, closed1m.Count > 0 ? closed1m[^1].Close : null);
        var volume = Volume.Compute(closed1m, s.Candles5m, r.VolumeWindowMinutes, r.VolumeBaselineHours, r.ShareWindowMinutes);
        var priceChange = PriceChange.Pct(closed1m, r.OiLongWindowMinutes);
        var oi15 = s.Oi.ChangePct(TimeSpan.FromMinutes(r.OiLongWindowMinutes));
        var oi5 = s.Oi.ChangePct(TimeSpan.FromMinutes(r.OiShortWindowMinutes));
        var price = s.LastPrice;
        var cvd = Cvd.Compute(closed1m, r.CvdWindowMinutes, r.CvdShareWindowMinutes, price, s.CurrentCvd(),
            structure.LastHigh, structure.LastLow);

        LiqCluster? shortCluster = null, longCluster = null;
        double? shortAbove = null, longBelow = null;
        if (price is { } p)
        {
            shortAbove = s.Heatmap.ShortLiqAbove(p);
            longBelow = s.Heatmap.LongLiqBelow(p);
            shortCluster = s.Heatmap.LargestShortClusterAbove(p);
            longCluster = s.Heatmap.LargestLongClusterBelow(p);
        }

        var book = s.Book;
        var walls = book?.PersistentWalls(nowUtc).ToList() ?? [];

        return new SignalMetrics
        {
            Symbol = s.Symbol,
            PriceDecimals = s.Info.PriceDecimals,
            AtUtc = nowUtc,
            Price = price,
            PriceChange15Pct = priceChange,

            LongStructure = structure.Long,
            ShortStructure = structure.Short,
            LastClose1m = structure.LastClose,
            LastPivotHigh = structure.LastHigh?.Price,
            PrevPivotHigh = structure.PrevHigh?.Price,
            LastPivotLow = structure.LastLow?.Price,
            PrevPivotLow = structure.PrevLow?.Price,
            Atr = s.Atr5m,

            VolRatio = volume.VolRatio,
            UpShare = volume.UpShare,
            DownShare = volume.DownShare,

            Oi15Pct = oi15,
            Oi5Pct = oi5,
            Regime = OpenInterest.Classify(priceChange, oi15, r.FlatMovePct),

            Rate8hPct = s.Rate8hPct,
            FundingIntervalHours = s.FundingIntervalHours,

            Cvd15 = cvd.Cvd15,
            Cvd5Share = cvd.Cvd5Share,
            BearishDivergence = cvd.BearishDivergence,
            BullishDivergence = cvd.BullishDivergence,

            ShortLiqAbove = shortAbove,
            LongLiqBelow = longBelow,
            ShortClusterPrice = shortCluster?.Center,
            ShortClusterNotional = shortCluster?.Notional,
            LongClusterPrice = longCluster?.Center,
            LongClusterNotional = longCluster?.Notional,

            HasBook = book is not null,
            BookHistorySeconds = book?.HistorySeconds(nowUtc),
            Imbalance30s = book?.Imbalance(nowUtc),
            SpreadPct = book?.SpreadPct,
            BidWalls = walls.Where(w => w.IsBid).Select(w => w.Price).OrderDescending().ToArray(),
            AskWalls = walls.Where(w => !w.IsBid).Select(w => w.Price).Order().ToArray(),

            TradeAgeSeconds = Age(s.LastTradeReceivedUtc, nowUtc),
            OiAgeSeconds = Age(s.LastOiReceivedUtc, nowUtc),
            MarkAgeSeconds = Age(s.LastMarkReceivedUtc, nowUtc),
            BookAgeSeconds = book?.AgeSeconds(nowUtc),
            BackfillDone = s.BackfillDone,
            Stale = s.Stale,
        };
    }

    private static double? Age(DateTime? t, DateTime now) => t is { } x ? Math.Max(0, (now - x).TotalSeconds) : null;
}
