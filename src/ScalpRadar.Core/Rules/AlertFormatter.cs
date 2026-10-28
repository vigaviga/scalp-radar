using System.Globalization;
using System.Text;
using ScalpRadar.Core.Indicators;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Rules;

/// <summary>Builds the Telegram alert text. Plain text (no Markdown), never mentions leverage or position size.</summary>
public sealed class AlertFormatter(RulesOptions r, OrderBookOptions book)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int NameWidth = 11;

    public string Format(Evaluation e, SignalMetrics m, double? btc15Pct, long? alertId)
    {
        var isLong = e.Direction == Direction.Long;
        var tier = e.Tier ?? Tier.Alert;
        var sb = new StringBuilder();

        sb.Append(tier.Emoji()).Append(' ').Append(tier.Label()).Append(' ').Append(e.Direction.Label())
          .Append(" · ").Append(m.Symbol).Append(" · ").Append(e.ConfirmationsPassed).Append('/').Append(e.Confirmations.Count).Append('\n');

        sb.Append("Price ").Append(P(m.Price, m)).Append(" (15m ").Append(Pct1(m.PriceChange15Pct)).Append(") · ")
          .Append(isLong ? "broke " : "broke down ").Append(P(e.BreakoutLevel, m)).Append('\n');

        Line(sb, e.MustHaves[0], "Structure", isLong
            ? $"higher low {P(m.LastPivotLow, m)} → breakout {P(m.LastPivotHigh, m)}"
            : $"lower high {P(m.LastPivotHigh, m)} → breakdown {P(m.LastPivotLow, m)}");

        var share = isLong ? m.UpShare : m.DownShare;
        Line(sb, e.MustHaves[1], "Volume",
            $"{N1(m.VolRatio)}× normal · {Pct0(share)} on {(isLong ? "green" : "red")} candles");

        Line(sb, e.MustHaves[2], "OI", $"{Pct1(m.Oi15Pct)} (15m) · {RegimeText(m.Regime)}");

        Line(sb, e.MustHaves[3], "Funding", FundingText(m, e.MustHaves[3].Passed, isLong));

        Line(sb, e.Confirmations[0], "CVD", m.Cvd5Share is { } cs
            ? $"{(cs * 100).ToString("+0;-0;0", Inv)}% of 5m volume · {(m.Cvd15 > 0 ? "rising" : m.Cvd15 < 0 ? "falling" : "flat")}"
            : "n/a");

        Line(sb, e.Confirmations[1], "Liquidity", LiquidityText(m, isLong));

        Line(sb, e.Confirmations[2], "Book", BookText(m, isLong));

        sb.Append("Invalidation ").Append(P(e.Invalidation, m)).Append(" · Target zone ").Append(e.Target is null ? "n/a" : P(e.Target, m)).Append('\n');
        sb.Append("BTC 15m ").Append(Pct1(btc15Pct));
        if (alertId is { } id) sb.Append(" · #").Append(id.ToString(Inv));
        sb.Append('\n');
        sb.Append("https://www.binance.com/en/futures/").Append(m.Symbol);
        return sb.ToString();
    }

    /// <summary>One line per alert held back by the flood guard.</summary>
    public static string FormatDigest(IReadOnlyList<(Evaluation E, SignalMetrics M, long? Id)> items, int windowMinutes)
    {
        var sb = new StringBuilder();
        sb.Append("📋 Digest: ").Append(items.Count).Append(" more alert").Append(items.Count == 1 ? "" : "s")
          .Append(" in the last ").Append(windowMinutes).Append(" min (flood guard)");
        foreach (var (e, m, id) in items)
        {
            sb.Append('\n').Append((e.Tier ?? Tier.Alert).Emoji()).Append(' ').Append(e.Direction.Label()).Append(' ')
              .Append(m.Symbol).Append(' ').Append(P(m.Price, m))
              .Append(" · inv ").Append(P(e.Invalidation, m))
              .Append(" · tgt ").Append(e.Target is null ? "n/a" : P(e.Target, m));
            if (id is { } x) sb.Append(" · #").Append(x.ToString(Inv));
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- parts

    private string FundingText(SignalMetrics m, bool passed, bool isLong)
    {
        if (m.Rate8hPct is not { } rate) return "n/a";
        var text = $"{rate.ToString("+0.000;-0.000;0.000", Inv)}% / 8h · {(passed ? "healthy" : "crowded")}";
        if (isLong && rate < 0) text += " · shorts are paying: squeeze fuel";
        if (!isLong && rate > r.CrowdedLongsNotePct) text += " · longs are paying";
        return text;
    }

    private static string LiquidityText(SignalMetrics m, bool isLong)
    {
        double? mine = isLong ? m.ShortLiqAbove : m.LongLiqBelow;
        double? other = isLong ? m.LongLiqBelow : m.ShortLiqAbove;
        var label = isLong ? "short-liq above" : "long-liq below";
        var otherLabel = isLong ? "long-liq below" : "short-liq above";

        string ratio = mine is not { } a ? "n/a"
            : other is { } b && b > 0 ? $"{label} {N1(a / b)}× {otherLabel}"
            : a > 0 ? $"{label}, none {(isLong ? "below" : "above")}"
            : "no estimated liquidity nearby";

        var cluster = isLong ? m.ShortClusterPrice : m.LongClusterPrice;
        if (cluster is { } c && m.Price is { } p && p > 0)
            ratio += $" · cluster {P(c, m)} ({((c / p - 1) * 100).ToString("+0.0;-0.0;0.0", Inv)}%)";
        return ratio;
    }

    private string BookText(SignalMetrics m, bool isLong)
    {
        if (!m.HasBook || m.Imbalance30s is not { } imb) return "no order book data";
        var range = book.ImbalanceRangePct.ToString("0.###", Inv);
        // imbalance I = (B − A) / (B + A)  ⇒  B / A = (1 + I) / (1 − I)
        string side = imb >= 0
            ? $"bids {N1(Ratio(imb))}× asks (±{range}%)"
            : $"asks {N1(Ratio(-imb))}× bids (±{range}%)";

        double[] walls = isLong ? m.BidWalls : m.AskWalls;
        if (walls.Length > 0) side += $" · {(isLong ? "bid" : "ask")} wall {P(walls[0], m)}";
        return side;

        static double Ratio(double i) => i >= 1 ? double.PositiveInfinity : (1 + i) / (1 - i);
    }

    private static string RegimeText(OiRegime regime) => regime switch
    {
        OiRegime.NewLongs => "new longs entering",
        OiRegime.NewShorts => "new shorts entering",
        OiRegime.ShortCovering => "short covering (weak rally)",
        OiRegime.LongUnwinding => "long unwinding (weak drop)",
        _ => "no clear regime",
    };

    private static void Line(StringBuilder sb, CheckResult check, string name, string text) =>
        sb.Append(check.Passed ? '✓' : '✗').Append(' ').Append(name.PadRight(NameWidth)).Append(text).Append('\n');

    private static string P(double? v, SignalMetrics m) =>
        v is { } x ? x.ToString("F" + m.PriceDecimals, Inv) : "n/a";

    private static string N1(double? v) =>
        v is { } x ? (double.IsInfinity(x) ? "∞" : x.ToString("0.0", Inv)) : "n/a";

    private static string Pct1(double? v) => v is { } x ? x.ToString("+0.0;-0.0;0.0", Inv) + "%" : "n/a";

    private static string Pct0(double? share) => share is { } x ? (x * 100).ToString("0", Inv) + "%" : "n/a";
}
