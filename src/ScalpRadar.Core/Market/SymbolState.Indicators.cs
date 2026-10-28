using ScalpRadar.Core.Indicators;
using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Market;

// Indicators that update when a 5m candle closes: swing pivots, ATR(14) and the liquidation heatmap.
public sealed partial class SymbolState
{
    public List<Pivot> Pivots5m { get; private set; } = [];
    public double? Atr5m { get; private set; }
    public LiquidationHeatmap Heatmap { get; }

    partial void On5mClosedCore(Candle five)
    {
        RecomputeSwingIndicators();
        Heatmap.ApplyCandle(five, Oi.At(five.OpenTime)?.OpenInterest, Oi.At(five.CloseTime)?.OpenInterest);
    }

    partial void OnBackfillMergedCore(BackfillEvent e)
    {
        Oi.AddRange(e.OiHistory);
        RecomputeSwingIndicators();
        SeedHeatmap();
    }

    /// <summary>Rebuilds the heatmap from the 5m candles and OI history in memory (24h at startup).</summary>
    public void SeedHeatmap()
    {
        Heatmap.Clear();
        foreach (var c in Candles5m)
            Heatmap.ApplyCandle(c, Oi.At(c.OpenTime)?.OpenInterest, Oi.At(c.CloseTime)?.OpenInterest);
    }

    private void RecomputeSwingIndicators()
    {
        Pivots5m = Pivots.Find(Candles5m, _opt.PivotLeftBars, _opt.PivotRightBars);
        Atr5m = Atr.Wilder(Candles5m, _opt.AtrPeriod);
    }
}
