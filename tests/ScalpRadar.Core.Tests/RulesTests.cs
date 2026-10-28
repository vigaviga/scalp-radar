using ScalpRadar.Core.Indicators;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Core.Rules;

namespace ScalpRadar.Core.Tests;

internal static class RuleFixtures
{
    public static readonly DateTime Now = Fx.T0;

    /// <summary>Metrics that pass every LONG rule (and match the example alert message).</summary>
    public static SignalMetrics Long() => new()
    {
        Symbol = "SOLUSDT",
        PriceDecimals = 2,
        AtUtc = Now,
        Price = 182.45,
        PriceChange15Pct = 1.4,
        LongStructure = true,
        LastClose1m = 182.40,
        LastPivotHigh = 181.90,
        PrevPivotHigh = 183.10,
        LastPivotLow = 179.80,
        PrevPivotLow = 178.50,
        Atr = 1.0,
        VolRatio = 3.08,
        UpShare = 0.68,
        DownShare = 0.25,
        Oi15Pct = 2.3,
        Oi5Pct = 0.8,
        Regime = OiRegime.NewLongs,
        Rate8hPct = 0.008,
        FundingIntervalHours = 8,
        Cvd15 = 5_000,
        Cvd5Share = 0.18,
        ShortLiqAbove = 2_100_000,
        LongLiqBelow = 1_000_000,
        ShortClusterPrice = 185.20,
        ShortClusterNotional = 900_000,
        LongClusterPrice = 178.00,
        LongClusterNotional = 400_000,
        HasBook = true,
        BookHistorySeconds = 60,
        Imbalance30s = 0.6 / 2.6, // bids 1.6× asks
        SpreadPct = 0.01,
        BidWalls = [181.50],
        AskWalls = [],
        TradeAgeSeconds = 1,
        OiAgeSeconds = 20,
        MarkAgeSeconds = 2,
        BookAgeSeconds = 1,
        BackfillDone = true,
    };

    /// <summary>Metrics that pass every SHORT rule.</summary>
    public static SignalMetrics Short() => Long() with
    {
        Price = 177.55,
        PriceChange15Pct = -1.4,
        LongStructure = false,
        ShortStructure = true,
        LastClose1m = 177.60,
        LastPivotHigh = 180.20,
        PrevPivotHigh = 181.50,
        LastPivotLow = 178.10,
        PrevPivotLow = 176.00,
        UpShare = 0.25,
        DownShare = 0.68,
        Regime = OiRegime.NewShorts,
        Rate8hPct = 0.01,
        Cvd15 = -5_000,
        Cvd5Share = -0.18,
        ShortLiqAbove = 1_000_000,
        LongLiqBelow = 2_100_000,
        Imbalance30s = -0.25,
        BidWalls = [],
        AskWalls = [178.40],
    };
}

public class RulesEngineTests
{
    private static readonly RulesOptions R = new();
    private readonly RulesEngine _engine = new(R);
    private readonly CooldownTracker _cooldown = new(R);

    private Evaluation Eval(SignalMetrics m, Direction d) => _engine.Evaluate(m, d, _cooldown, RuleFixtures.Now);

    [Fact]
    public void Baseline_long_fixture_is_a_strong_alert()
    {
        var e = Eval(RuleFixtures.Long(), Direction.Long);
        Assert.True(e.AllMustHaves);
        Assert.Equal(3, e.ConfirmationsPassed);
        Assert.False(e.AnyVeto, string.Join("; ", e.Vetoes.Where(v => v.Triggered).Select(v => v.Detail)));
        Assert.Equal(Tier.Strong, e.Tier);
        Assert.True(e.IsAlert);
        Assert.Equal(43, e.Score);
        Assert.Equal(181.90, e.BreakoutLevel);
        Assert.Equal(179.80, e.Invalidation);
        Assert.Equal(185.20, e.Target);
    }

    [Fact]
    public void Baseline_short_fixture_is_a_strong_alert()
    {
        var e = Eval(RuleFixtures.Short(), Direction.Short);
        Assert.True(e.IsAlert, string.Join("; ", e.MustHaves.Concat(e.Confirmations).Where(c => !c.Passed).Select(c => c.Code + " " + c.Detail)));
        Assert.Equal(Tier.Strong, e.Tier);
        Assert.Equal(178.10, e.BreakoutLevel);
        Assert.Equal(180.20, e.Invalidation);
        Assert.Equal(178.00, e.Target);
    }

    public static TheoryData<string, Func<SignalMetrics, SignalMetrics>, string> LongFailures => new()
    {
        { "no structure", m => m with { LongStructure = false }, "M1" },
        { "volRatio too low", m => m with { VolRatio = 2.49 }, "M2" },
        { "upShare too low", m => m with { UpShare = 0.59 }, "M2" },
        { "no volume data", m => m with { VolRatio = null }, "M2" },
        { "short covering never alerts", m => m with { Regime = OiRegime.ShortCovering }, "M3" },
        { "wrong regime", m => m with { Regime = OiRegime.NewShorts }, "M3" },
        { "oi15 under 1%", m => m with { Oi15Pct = 0.99 }, "M3" },
        { "oi5 not rising", m => m with { Oi5Pct = 0 }, "M3" },
        { "longs crowded", m => m with { Rate8hPct = 0.031 }, "M4" },
        { "no funding data", m => m with { Rate8hPct = null }, "M4" },
    };

    [Theory]
    [MemberData(nameof(LongFailures))]
    public void Each_long_must_have_can_fail_on_its_own(string _, Func<SignalMetrics, SignalMetrics> change, string code)
    {
        var e = Eval(change(RuleFixtures.Long()), Direction.Long);
        Assert.Equal([code], e.MustHaves.Where(c => !c.Passed).Select(c => c.Code));
        Assert.False(e.IsAlert);
    }

    public static TheoryData<string, Func<SignalMetrics, SignalMetrics>, string> ShortFailures => new()
    {
        { "no structure", m => m with { ShortStructure = false }, "M1" },
        { "downShare too low", m => m with { DownShare = 0.5 }, "M2" },
        { "long unwinding never alerts", m => m with { Regime = OiRegime.LongUnwinding }, "M3" },
        { "oi15 under 1%", m => m with { Oi15Pct = 0.5 }, "M3" },
        { "shorts crowded", m => m with { Rate8hPct = -0.031 }, "M4" },
    };

    [Theory]
    [MemberData(nameof(ShortFailures))]
    public void Each_short_must_have_can_fail_on_its_own(string _, Func<SignalMetrics, SignalMetrics> change, string code)
    {
        var e = Eval(change(RuleFixtures.Short()), Direction.Short);
        Assert.Equal([code], e.MustHaves.Where(c => !c.Passed).Select(c => c.Code));
    }

    [Fact]
    public void Funding_at_exactly_the_limit_passes()
    {
        Assert.True(Eval(RuleFixtures.Long() with { Rate8hPct = 0.03 }, Direction.Long).MustHaves[3].Passed);
        Assert.True(Eval(RuleFixtures.Short() with { Rate8hPct = -0.03 }, Direction.Short).MustHaves[3].Passed);
    }

    public static TheoryData<string, Func<SignalMetrics, SignalMetrics>, Direction, string> ConfirmationFailures => new()
    {
        { "cvd15 negative", m => m with { Cvd15 = -1 }, Direction.Long, "C1" },
        { "cvd5Share too small", m => m with { Cvd5Share = 0.09 }, Direction.Long, "C1" },
        { "liquidity not 1.5x", m => m with { ShortLiqAbove = 1_400_000 }, Direction.Long, "C2" },
        { "no liquidity above", m => m with { ShortLiqAbove = 0, LongLiqBelow = 0 }, Direction.Long, "C2" },
        { "imbalance too small", m => m with { Imbalance30s = 0.19 }, Direction.Long, "C3" },
        { "no book imbalance", m => m with { Imbalance30s = null }, Direction.Long, "C3" },
        { "short cvd positive", m => RuleFixtures.Short() with { Cvd15 = 10 }, Direction.Short, "C1" },
        { "short liquidity", m => RuleFixtures.Short() with { LongLiqBelow = 1_000_000 }, Direction.Short, "C2" },
        { "short imbalance", m => RuleFixtures.Short() with { Imbalance30s = -0.1 }, Direction.Short, "C3" },
    };

    [Theory]
    [MemberData(nameof(ConfirmationFailures))]
    public void One_failed_confirmation_drops_to_a_normal_alert(string _, Func<SignalMetrics, SignalMetrics> change, Direction d, string code)
    {
        var e = Eval(change(RuleFixtures.Long()), d);
        Assert.Equal([code], e.Confirmations.Where(c => !c.Passed).Select(c => c.Code));
        Assert.Equal(Tier.Alert, e.Tier);
        Assert.True(e.IsAlert);
    }

    [Fact]
    public void Two_failed_confirmations_means_no_alert()
    {
        var e = Eval(RuleFixtures.Long() with { Cvd15 = -1, Imbalance30s = 0 }, Direction.Long);
        Assert.Null(e.Tier);
        Assert.False(e.IsAlert);
        Assert.Equal(41, e.Score);
    }

    public static TheoryData<string, Func<SignalMetrics, SignalMetrics>, Direction, string> VetoCases => new()
    {
        { "bearish divergence on long", m => m with { BearishDivergence = true }, Direction.Long, "V1" },
        { "bullish divergence on short", m => RuleFixtures.Short() with { BullishDivergence = true }, Direction.Short, "V1" },
        { "ask wall within 0.3% above", m => m with { AskWalls = [182.90] }, Direction.Long, "V2" },
        { "bid wall within 0.3% below", m => RuleFixtures.Short() with { BidWalls = [177.20] }, Direction.Short, "V2" },
        { "chasing: 1.6 ATR from breakout", m => m with { Price = 181.90 + 1.6 }, Direction.Long, "V3" },
        { "no ATR", m => m with { Atr = null }, Direction.Long, "V3" },
        { "spread over 0.05%", m => m with { SpreadPct = 0.051 }, Direction.Long, "V4" },
        { "old trade", m => m with { TradeAgeSeconds = 11 }, Direction.Long, "V5" },
        { "old OI", m => m with { OiAgeSeconds = 91 }, Direction.Long, "V5" },
        { "old mark price", m => m with { MarkAgeSeconds = 16 }, Direction.Long, "V5" },
        { "old book", m => m with { BookAgeSeconds = 6 }, Direction.Long, "V5" },
        { "no book", m => m with { HasBook = false, BookAgeSeconds = null }, Direction.Long, "V5" },
        { "backfill not finished", m => m with { BackfillDone = false }, Direction.Long, "V5" },
        { "gap being backfilled", m => m with { Stale = true }, Direction.Long, "V5" },
    };

    [Theory]
    [MemberData(nameof(VetoCases))]
    public void Each_veto_blocks_the_alert(string _, Func<SignalMetrics, SignalMetrics> change, Direction d, string code)
    {
        var e = Eval(change(RuleFixtures.Long()), d);
        Assert.Equal([code], e.Vetoes.Where(v => v.Triggered).Select(v => v.Code));
        Assert.False(e.IsAlert);
    }

    [Fact]
    public void Walls_further_than_0_3_percent_or_on_the_other_side_do_not_veto()
    {
        Assert.False(Eval(RuleFixtures.Long() with { AskWalls = [183.10] }, Direction.Long).AnyVeto); // +0.36%
        Assert.False(Eval(RuleFixtures.Long() with { BidWalls = [182.40] }, Direction.Long).AnyVeto); // bid wall helps a long
    }

    [Fact]
    public void Exactly_1_5_ATR_is_not_chasing() =>
        Assert.False(Eval(RuleFixtures.Long() with { Price = 181.90 + 1.5 }, Direction.Long).Vetoes[2].Triggered);

    [Fact]
    public void Cooldown_blocks_the_same_symbol_and_direction_for_30_minutes()
    {
        _cooldown.Register("SOLUSDT", Direction.Long, Tier.Alert, RuleFixtures.Now);
        var weak = RuleFixtures.Long() with { Imbalance30s = 0 }; // ✅ tier

        Assert.True(_engine.Evaluate(weak, Direction.Long, _cooldown, RuleFixtures.Now.AddMinutes(29)).Vetoes[5].Triggered);
        Assert.False(_engine.Evaluate(weak, Direction.Long, _cooldown, RuleFixtures.Now.AddMinutes(30)).Vetoes[5].Triggered);
        Assert.False(_engine.Evaluate(RuleFixtures.Short(), Direction.Short, _cooldown, RuleFixtures.Now.AddMinutes(5)).Vetoes[5].Triggered);
    }

    [Fact]
    public void One_upgrade_from_alert_to_strong_is_allowed_inside_the_cooldown()
    {
        _cooldown.Register("SOLUSDT", Direction.Long, Tier.Alert, RuleFixtures.Now);

        var strong = _engine.Evaluate(RuleFixtures.Long(), Direction.Long, _cooldown, RuleFixtures.Now.AddMinutes(10));
        Assert.True(strong.IsAlert);
        _cooldown.Register("SOLUSDT", Direction.Long, Tier.Strong, RuleFixtures.Now.AddMinutes(10));

        var again = _engine.Evaluate(RuleFixtures.Long(), Direction.Long, _cooldown, RuleFixtures.Now.AddMinutes(20));
        Assert.True(again.Vetoes[5].Triggered); // only one upgrade
        var after = _engine.Evaluate(RuleFixtures.Long(), Direction.Long, _cooldown, RuleFixtures.Now.AddMinutes(30));
        Assert.False(after.Vetoes[5].Triggered); // cooldown runs from the first alert
    }

    [Fact]
    public void A_strong_alert_cannot_be_followed_by_another_inside_the_cooldown()
    {
        _cooldown.Register("SOLUSDT", Direction.Long, Tier.Strong, RuleFixtures.Now);
        Assert.True(_engine.Evaluate(RuleFixtures.Long(), Direction.Long, _cooldown, RuleFixtures.Now.AddMinutes(1)).Vetoes[5].Triggered);
    }
}

public class AlertDeciderTests
{
    private static readonly RulesOptions R = new();
    private readonly RulesEngine _engine = new(R);
    private readonly CooldownTracker _cooldown = new(R);
    private readonly AlertDecider _decider = new(R, new OrderBookOptions());

    private DecisionKind Decide(SignalMetrics m, DateTime now, Direction d = Direction.Long) =>
        _decider.Decide(_engine.Evaluate(m, d, _cooldown, now), m, now);

    [Fact]
    public void Alert_when_everything_passes() => Assert.Equal(DecisionKind.Alert, Decide(RuleFixtures.Long(), Fx.Min(0)));

    [Fact]
    public void Waits_up_to_30s_for_book_history_then_scores_anyway()
    {
        var young = RuleFixtures.Long() with { BookHistorySeconds = 5 };
        Assert.Equal(DecisionKind.WaitForBook, Decide(young, Fx.Min(0, 0)));
        Assert.Equal(DecisionKind.WaitForBook, Decide(young, Fx.Min(0, 29)));
        Assert.Equal(DecisionKind.Alert, Decide(young, Fx.Min(0, 30)));
    }

    [Fact]
    public void Book_history_reaching_30s_ends_the_wait() =>
        Assert.Equal(DecisionKind.Alert, Decide(RuleFixtures.Long() with { BookHistorySeconds = 30 }, Fx.Min(0)));

    [Fact]
    public void Near_miss_at_most_once_per_5_minutes_per_symbol()
    {
        var vetoed = RuleFixtures.Long() with { SpreadPct = 0.2 };
        Assert.Equal(DecisionKind.NearMiss, Decide(vetoed, Fx.Min(0)));
        Assert.Equal(DecisionKind.None, Decide(vetoed, Fx.Min(4, 59)));
        Assert.Equal(DecisionKind.None, Decide(RuleFixtures.Short() with { SpreadPct = 0.2 }, Fx.Min(2), Direction.Short));
        Assert.Equal(DecisionKind.NearMiss, Decide(vetoed, Fx.Min(5)));
    }

    [Fact]
    public void Missing_a_must_have_is_not_a_near_miss() =>
        Assert.Equal(DecisionKind.None, Decide(RuleFixtures.Long() with { VolRatio = 1 }, Fx.Min(0)));
}

public class FloodGuardTests
{
    private readonly FloodGuard<int> _guard = new(new RulesOptions { MaxAlertsPerWindow = 8, FloodWindowMinutes = 15 });

    [Fact]
    public void Eight_alerts_per_15_minutes_then_a_digest_at_the_window_end()
    {
        for (var i = 0; i < 8; i++) Assert.True(_guard.Admit(i, Fx.Min(1)));
        Assert.False(_guard.Admit(8, Fx.Min(2)));
        Assert.False(_guard.Admit(9, Fx.Min(14, 59)));

        Assert.Null(_guard.TakeDigest(Fx.Min(14, 59)));
        Assert.Equal([8, 9], _guard.TakeDigest(Fx.Min(15)));
        Assert.Null(_guard.TakeDigest(Fx.Min(16))); // only once
    }

    [Fact]
    public void A_new_window_resets_the_count()
    {
        for (var i = 0; i < 8; i++) _guard.Admit(i, Fx.Min(1));
        Assert.True(_guard.Admit(99, Fx.Min(15)));
    }
}

public class AlertFormatterTests
{
    private readonly AlertFormatter _formatter = new(new RulesOptions(), new OrderBookOptions());
    private readonly RulesEngine _engine = new(new RulesOptions());

    [Fact]
    public void Formats_the_example_message_exactly()
    {
        var m = RuleFixtures.Long();
        var e = _engine.Evaluate(m, Direction.Long, new CooldownTracker(new RulesOptions()), RuleFixtures.Now);

        var text = _formatter.Format(e, m, btc15Pct: 0.3, alertId: 1234);

        const string expected = """
            🔥 STRONG LONG · SOLUSDT · 3/3
            Price 182.45 (15m +1.4%) · broke 181.90
            ✓ Structure  higher low 179.80 → breakout 181.90
            ✓ Volume     3.1× normal · 68% on green candles
            ✓ OI         +2.3% (15m) · new longs entering
            ✓ Funding    +0.008% / 8h · healthy
            ✓ CVD        +18% of 5m volume · rising
            ✓ Liquidity  short-liq above 2.1× long-liq below · cluster 185.20 (+1.5%)
            ✓ Book       bids 1.6× asks (±0.5%) · bid wall 181.50
            Invalidation 179.80 · Target zone 185.20
            BTC 15m +0.3% · #1234
            https://www.binance.com/en/futures/SOLUSDT
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Failed_confirmation_shows_a_cross_and_tier_is_alert()
    {
        var m = RuleFixtures.Long() with { Imbalance30s = -0.1 };
        var e = _engine.Evaluate(m, Direction.Long, new CooldownTracker(new RulesOptions()), RuleFixtures.Now);
        var text = _formatter.Format(e, m, 0.3, 7);

        Assert.StartsWith("✅ ALERT LONG · SOLUSDT · 2/3", text);
        Assert.Contains("✗ Book       asks 1.2× bids (±0.5%) · bid wall 181.50", text);
    }

    [Fact]
    public void Long_with_negative_funding_notes_the_squeeze_fuel()
    {
        var m = RuleFixtures.Long() with { Rate8hPct = -0.012 };
        var e = _engine.Evaluate(m, Direction.Long, new CooldownTracker(new RulesOptions()), RuleFixtures.Now);
        Assert.Contains("✓ Funding    -0.012% / 8h · healthy · shorts are paying: squeeze fuel", _formatter.Format(e, m, 0, 1));
    }

    [Fact]
    public void Short_with_high_funding_notes_longs_are_paying()
    {
        var m = RuleFixtures.Short() with { Rate8hPct = 0.05 };
        var e = _engine.Evaluate(m, Direction.Short, new CooldownTracker(new RulesOptions()), RuleFixtures.Now);
        var text = _formatter.Format(e, m, -0.2, 2);
        Assert.Contains("+0.050% / 8h · healthy · longs are paying", text);
        Assert.Contains("✓ Structure  lower high 180.20 → breakdown 178.10", text);
        Assert.Contains("on red candles", text);
        Assert.Contains("new shorts entering", text);
        Assert.Contains("ask wall 178.40", text);
        Assert.DoesNotContain("leverage", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Digest_lists_one_line_per_held_alert()
    {
        var m = RuleFixtures.Long();
        var e = _engine.Evaluate(m, Direction.Long, new CooldownTracker(new RulesOptions()), RuleFixtures.Now);
        var text = AlertFormatter.FormatDigest([(e, m, 11), (e, m, 12)], 15);
        Assert.StartsWith("📋 Digest: 2 more alerts in the last 15 min", text);
        Assert.Contains("🔥 LONG SOLUSDT 182.45 · inv 179.80 · tgt 185.20 · #12", text);
    }
}
