using System.Globalization;
using ScalpRadar.Core.Indicators;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Rules;

public sealed record CheckResult(string Code, string Name, bool Passed, string Detail);

public sealed record VetoResult(string Code, string Name, bool Triggered, string Detail);

public sealed record Evaluation(
    string Symbol,
    Direction Direction,
    IReadOnlyList<CheckResult> MustHaves,
    IReadOnlyList<CheckResult> Confirmations,
    IReadOnlyList<VetoResult> Vetoes,
    Tier? Tier,
    int Score,
    double? BreakoutLevel,
    double? Invalidation,
    double? Target)
{
    public int MustHavesPassed => MustHaves.Count(c => c.Passed);
    public int ConfirmationsPassed => Confirmations.Count(c => c.Passed);
    public bool AllMustHaves => MustHaves.All(c => c.Passed);
    public bool AnyVeto => Vetoes.Any(v => v.Triggered);
    public bool IsAlert => AllMustHaves && Tier is not null && !AnyVeto;
}

/// <summary>
/// An alert needs all 4 must-haves, at least 2 of 3 confirmations, and no veto. SHORT mirrors LONG.
/// Pure: the only state it reads is the cooldown tracker passed in.
/// </summary>
public sealed class RulesEngine(RulesOptions r)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public Evaluation Evaluate(SignalMetrics m, Direction d, CooldownTracker cooldown, DateTime now)
    {
        var isLong = d == Direction.Long;
        var must = new List<CheckResult> { M1(m, isLong), M2(m, isLong), M3(m, isLong), M4(m, isLong) };
        var conf = new List<CheckResult> { C1(m, isLong), C2(m, isLong), C3(m, isLong) };

        var confPassed = conf.Count(c => c.Passed);
        Tier? tier = confPassed >= r.StrongConfirmations ? Tier.Strong
            : confPassed >= r.MinConfirmations ? Tier.Alert
            : null;

        var breakout = isLong ? m.LastPivotHigh : m.LastPivotLow;
        var invalidation = isLong ? m.LastPivotLow : m.LastPivotHigh;
        var target = isLong ? m.ShortClusterPrice : m.LongClusterPrice;

        var vetoes = new List<VetoResult>
        {
            V1(m, isLong),
            V2(m, isLong),
            V3(m, breakout),
            V4(m),
            V5(m),
            V6(m.Symbol, d, tier, cooldown, now),
        };

        var score = must.Count(c => c.Passed) * r.MustHaveScoreWeight + confPassed;
        return new Evaluation(m.Symbol, d, must, conf, vetoes, tier, score, breakout, invalidation, target);
    }

    // ---------------------------------------------------------------- must-haves

    private static CheckResult M1(SignalMetrics m, bool isLong)
    {
        var pass = isLong ? m.LongStructure : m.ShortStructure;
        var detail = isLong
            ? $"lows {F(m.PrevPivotLow)} -> {F(m.LastPivotLow)}, close {F(m.LastClose1m)} vs high {F(m.LastPivotHigh)}"
            : $"highs {F(m.PrevPivotHigh)} -> {F(m.LastPivotHigh)}, close {F(m.LastClose1m)} vs low {F(m.LastPivotLow)}";
        return new CheckResult("M1", "Structure", pass, detail);
    }

    private CheckResult M2(SignalMetrics m, bool isLong)
    {
        var share = isLong ? m.UpShare : m.DownShare;
        var pass = m.VolRatio >= r.MinVolRatio && share >= r.MinDirectionalShare;
        return new CheckResult("M2", "Volume", pass,
            $"volRatio {F(m.VolRatio)} (min {F(r.MinVolRatio)}), {(isLong ? "upShare" : "downShare")} {F(share)} (min {F(r.MinDirectionalShare)})");
    }

    private CheckResult M3(SignalMetrics m, bool isLong)
    {
        var regime = isLong ? OiRegime.NewLongs : OiRegime.NewShorts;
        var pass = m.Regime == regime && m.Oi15Pct >= r.MinOi15Pct && m.Oi5Pct > r.MinOi5Pct;
        return new CheckResult("M3", "Open interest", pass,
            $"regime {m.Regime}, oi15 {F(m.Oi15Pct)}% (min {F(r.MinOi15Pct)}), oi5 {F(m.Oi5Pct)}% (> {F(r.MinOi5Pct)})");
    }

    private CheckResult M4(SignalMetrics m, bool isLong)
    {
        var pass = isLong ? m.Rate8hPct <= r.MaxLongRate8hPct : m.Rate8hPct >= r.MinShortRate8hPct;
        return new CheckResult("M4", "Funding", pass,
            $"rate8h {F(m.Rate8hPct, 4)}% ({(isLong ? "max " + F(r.MaxLongRate8hPct, 4) : "min " + F(r.MinShortRate8hPct, 4))})");
    }

    // ---------------------------------------------------------------- confirmations

    private CheckResult C1(SignalMetrics m, bool isLong)
    {
        var pass = isLong
            ? m.Cvd15 > 0 && m.Cvd5Share >= r.MinCvd5Share
            : m.Cvd15 < 0 && m.Cvd5Share <= -r.MinCvd5Share;
        return new CheckResult("C1", "CVD", pass, $"cvd15 {F(m.Cvd15)}, cvd5Share {F(m.Cvd5Share)}");
    }

    private CheckResult C2(SignalMetrics m, bool isLong)
    {
        var pass = isLong
            ? m.ShortLiqAbove is { } a && m.LongLiqBelow is { } b && a > 0 && a >= r.MinLiqRatio * b
            : m.LongLiqBelow is { } b2 && m.ShortLiqAbove is { } a2 && b2 > 0 && b2 >= r.MinLiqRatio * a2;
        return new CheckResult("C2", "Liquidity", pass, $"shortLiqAbove {F(m.ShortLiqAbove, 0)}, longLiqBelow {F(m.LongLiqBelow, 0)}");
    }

    private CheckResult C3(SignalMetrics m, bool isLong)
    {
        var pass = isLong ? m.Imbalance30s >= r.MinImbalance : m.Imbalance30s <= -r.MinImbalance;
        return new CheckResult("C3", "Order book", pass, $"imbalance30s {F(m.Imbalance30s)}");
    }

    // ---------------------------------------------------------------- vetoes

    private static VetoResult V1(SignalMetrics m, bool isLong)
    {
        var hit = isLong ? m.BearishDivergence : m.BullishDivergence;
        return new VetoResult("V1", "Divergence", hit, hit ? (isLong ? "bearish CVD divergence" : "bullish CVD divergence") : "none");
    }

    private VetoResult V2(SignalMetrics m, bool isLong)
    {
        if (m.Price is not { } p) return new VetoResult("V2", "Opposing wall", false, "no price");
        var dist = r.OpposingWallDistancePct / 100;
        double? wall = isLong
            ? m.AskWalls.Where(w => w >= p && w <= p * (1 + dist)).Cast<double?>().FirstOrDefault()
            : m.BidWalls.Where(w => w <= p && w >= p * (1 - dist)).Cast<double?>().FirstOrDefault();
        return new VetoResult("V2", "Opposing wall", wall is not null,
            wall is { } w ? $"{(isLong ? "ask" : "bid")} wall at {F(w, 8)}" : "none");
    }

    private VetoResult V3(SignalMetrics m, double? breakout)
    {
        if (m.Price is not { } p || breakout is not { } b) return new VetoResult("V3", "Chasing", false, "no breakout level");
        if (m.Atr is not { } atr || atr <= 0) return new VetoResult("V3", "Chasing", true, "no ATR yet");
        var distance = Math.Abs(p - b);
        var hit = distance > r.MaxChaseAtr * atr;
        return new VetoResult("V3", "Chasing", hit, $"{F(distance / atr)} ATR from breakout (max {F(r.MaxChaseAtr)})");
    }

    private VetoResult V4(SignalMetrics m)
    {
        var hit = m.SpreadPct > r.MaxSpreadPct;
        return new VetoResult("V4", "Thin market", hit, $"spread {F(m.SpreadPct, 4)}% (max {F(r.MaxSpreadPct, 4)})");
    }

    private VetoResult V5(SignalMetrics m)
    {
        var reasons = new List<string>();
        if (!m.BackfillDone) reasons.Add("backfill not finished");
        if (m.Stale) reasons.Add("stream gap being backfilled");
        if (!(m.TradeAgeSeconds <= r.MaxTradeAgeSeconds)) reasons.Add($"trade {F(m.TradeAgeSeconds, 0)}s old");
        if (!(m.OiAgeSeconds <= r.MaxOiAgeSeconds)) reasons.Add($"OI {F(m.OiAgeSeconds, 0)}s old");
        if (!(m.MarkAgeSeconds <= r.MaxMarkAgeSeconds)) reasons.Add($"mark {F(m.MarkAgeSeconds, 0)}s old");
        if (!m.HasBook) reasons.Add("no order book");
        else if (!(m.BookAgeSeconds <= r.MaxBookAgeSeconds)) reasons.Add($"book {F(m.BookAgeSeconds, 0)}s old");
        return new VetoResult("V5", "Stale data", reasons.Count > 0, reasons.Count > 0 ? string.Join(", ", reasons) : "fresh");
    }

    private static VetoResult V6(string symbol, Direction d, Tier? tier, CooldownTracker cooldown, DateTime now)
    {
        var hit = tier is { } t && cooldown.Blocks(symbol, d, t, now);
        return new VetoResult("V6", "Cooldown", hit, hit ? "alerted recently" : "clear");
    }

    private static string F(double? v, int decimals = 2) =>
        v is { } x ? x.ToString("F" + decimals, Inv) : "n/a";
}

/// <summary>
/// V6: the same symbol and direction cannot alert again within the cooldown, except one upgrade from ✅ to 🔥.
/// </summary>
public sealed class CooldownTracker(RulesOptions r)
{
    private sealed record Entry(DateTime Time, Tier Tier, bool UpgradeUsed);

    private readonly Dictionary<(string, Direction), Entry> _last = new();

    public bool Blocks(string symbol, Direction d, Tier tier, DateTime now)
    {
        if (!_last.TryGetValue((symbol, d), out var e)) return false;
        if (now - e.Time >= TimeSpan.FromMinutes(r.CooldownMinutes)) return false;
        var isUpgrade = e.Tier == Tier.Alert && tier == Tier.Strong && !e.UpgradeUsed;
        return !isUpgrade;
    }

    public void Register(string symbol, Direction d, Tier tier, DateTime now)
    {
        if (_last.TryGetValue((symbol, d), out var e) && now - e.Time < TimeSpan.FromMinutes(r.CooldownMinutes))
        {
            // The one allowed upgrade: the cooldown still runs from the first alert.
            _last[(symbol, d)] = e with { Tier = tier, UpgradeUsed = true };
            return;
        }
        _last[(symbol, d)] = new Entry(now, tier, false);
    }
}
