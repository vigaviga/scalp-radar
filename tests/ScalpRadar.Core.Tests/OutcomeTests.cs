using ScalpRadar.Core.Models;
using ScalpRadar.Core.Outcomes;
using ScalpRadar.Core.Reports;

namespace ScalpRadar.Core.Tests;

public class OutcomeTrackerTests
{
    // Alert at 00:00:30, entry 100, invalidation 98, target 104.
    private static readonly DateTime Start = Fx.Min(0, 30);

    private static OutcomeSpec Long(double? target = 104) => new(Guid.NewGuid(), "SOLUSDT", Direction.Long, Start, 100, 98, target);
    private static OutcomeSpec Short() => new(Guid.NewGuid(), "SOLUSDT", Direction.Short, Start, 100, 102, 96);

    private static OutcomeResult Run(OutcomeTracker t, params (int Min, int Sec, double Price)[] trades)
    {
        foreach (var (min, sec, price) in trades)
        {
            if (t.OnTrade(Fx.Min(min, sec), price, Fx.Min(min, sec)) is { } r) return r;
        }
        throw new InvalidOperationException("tracker did not finish");
    }

    [Fact]
    public void Target_first_with_checkpoints_and_mfe_mae()
    {
        var r = Run(new OutcomeTracker(Long()),
            (1, 0, 99.0),   // dip: MAE 1%
            (4, 0, 101.0),
            (5, 31, 101.5), // first trade after +5 min: price at +5 = 101.0
            (10, 0, 104.5), // target hit, MFE 4.5%
            (14, 0, 103.0),
            (20, 0, 97.0),  // invalidation after target: target still came first, MAE now 3%
            (60, 29, 99.5),
            (60, 31, 99.9)); // end

        Assert.Equal(FirstHit.Target, r.FirstHit);
        Assert.Equal(101.0, r.Price5m);
        Assert.Equal(103.0, r.Price15m);
        Assert.Equal(99.5, r.Price60m);
        Assert.Equal(4.5, r.MfePct, 6);
        Assert.Equal(3.0, r.MaePct, 6);
        Assert.Equal(Fx.Min(60, 31), r.ResolvedUtc);
    }

    [Fact]
    public void Both_in_the_same_minute_counts_as_invalidation()
    {
        var r = Run(new OutcomeTracker(Long()),
            (3, 5, 104.2),  // target...
            (3, 40, 97.9),  // ...and invalidation in the same 1m candle
            (61, 0, 100));
        Assert.Equal(FirstHit.Invalidation, r.FirstHit);
    }

    [Fact]
    public void Target_in_one_minute_and_invalidation_in_the_next_is_target()
    {
        var r = Run(new OutcomeTracker(Long()), (3, 59, 104.0), (4, 0, 97.0), (61, 0, 100));
        Assert.Equal(FirstHit.Target, r.FirstHit);
    }

    [Fact]
    public void Neither_hit_and_no_target()
    {
        var r = Run(new OutcomeTracker(Long(target: null)), (10, 0, 110), (61, 0, 100));
        Assert.Equal(FirstHit.None, r.FirstHit);
        Assert.Equal(10, r.MfePct, 6);
        Assert.Equal(0, r.MaePct);
    }

    [Fact]
    public void Short_mirrors_long()
    {
        var r = Run(new OutcomeTracker(Short()), (2, 0, 101), (7, 0, 95.5), (61, 0, 99));
        Assert.Equal(FirstHit.Target, r.FirstHit);
        Assert.Equal(4.5, r.MfePct, 6);
        Assert.Equal(1.0, r.MaePct, 6);

        var stopped = Run(new OutcomeTracker(Short()), (2, 0, 102.0), (61, 0, 99));
        Assert.Equal(FirstHit.Invalidation, stopped.FirstHit);
    }

    [Fact]
    public void Trades_before_the_alert_are_ignored()
    {
        var t = new OutcomeTracker(Long());
        Assert.Null(t.OnTrade(Fx.Min(0, 10), 90, Fx.Min(0, 10))); // before the alert, would be an invalidation
        var r = Run(t, (61, 0, 100));
        Assert.Equal(FirstHit.None, r.FirstHit);
        Assert.Equal(0, r.MaePct);
    }

    [Fact]
    public void A_gap_in_trades_still_sets_every_checkpoint_from_the_last_price()
    {
        var r = Run(new OutcomeTracker(Long()), (2, 0, 101), (70, 0, 102));
        Assert.Equal(101, r.Price5m);
        Assert.Equal(101, r.Price15m);
        Assert.Equal(101, r.Price60m);
    }

    [Fact]
    public void From_candles_uses_closes_at_checkpoints_and_the_conservative_rule()
    {
        var candles = Fx.Flat1m(0, 70, 100);
        candles[3] = Fx.C1(3, 100, 101, 99.5, 100.8);
        candles[4] = Fx.C1(4, 100.8, 101.2, 100.5, 101.1); // closes 00:05:00 = last close before +5 (00:05:30)
        candles[8] = Fx.C1(8, 101, 104.1, 97.5, 100);      // target and invalidation in one candle
        candles[14] = Fx.C1(14, 100, 100, 100, 102.0);     // closes 00:15:00

        var r = OutcomeTracker.FromCandles(Long(), candles, Fx.Min(90));

        Assert.Equal(FirstHit.Invalidation, r.FirstHit);
        Assert.Equal(101.1, r.Price5m);
        Assert.Equal(102.0, r.Price15m);
        Assert.Equal(100, r.Price60m);
        Assert.Equal(4.1, r.MfePct, 6);
        Assert.Equal(2.5, r.MaePct, 6);
        Assert.Equal(Fx.Min(90), r.ResolvedUtc);
    }

    [Fact]
    public void From_candles_ignores_candles_after_the_end()
    {
        var candles = Fx.Flat1m(0, 70, 100);
        candles[60] = Fx.C1(60, 100, 120, 100, 100); // 01:00-01:01 ends after 01:00:30
        var r = OutcomeTracker.FromCandles(Long(), candles, Fx.Min(90));
        Assert.Equal(FirstHit.None, r.FirstHit);
        Assert.Equal(0, r.MfePct);
    }

    [Fact]
    public void From_no_candles_gives_an_empty_result()
    {
        var r = OutcomeTracker.FromCandles(Long(), [], Fx.Min(90));
        Assert.Null(r.Price5m);
        Assert.Equal(FirstHit.None, r.FirstHit);
    }
}

public class ReportFormatterTests
{
    [Fact]
    public void Stats_lists_strong_first_with_percentages_of_resolved()
    {
        var text = ReportFormatter.Stats(
        [
            new StatsRow("ALERT", "LONG", 10, 8, 4, 2, 1.234, 0.5),
            new StatsRow("STRONG", "SHORT", 3, 0, 0, 0, null, null),
            new StatsRow("STRONG", "LONG", 4, 4, 3, 1, 2.0, 0.25),
        ], 7, new DateTime(2026, 10, 21, 17, 0, 0, DateTimeKind.Utc));

        var expected = """
            📊 Stats · last 7 days (since 2026-10-21 17:00 UTC)
            🔥 STRONG LONG  4 · target 75% · inval 25% · MFE 2.00% · MAE 0.25%
            🔥 STRONG SHORT 3 · none resolved yet
            ✅ ALERT LONG   10 · target 50% · inval 25% · MFE 1.23% · MAE 0.50%
            Total 17 · 5 still tracking
            First hit = which came first within 60 min. MFE/MAE = median best/worst move.
            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Stats_with_no_alerts() =>
        Assert.EndsWith("No alerts yet.", ReportFormatter.Stats([], 1, Fx.T0));

    [Fact]
    public void Daily_summary()
    {
        var text = ReportFormatter.Daily(new DailySummary(new DateOnly(2026, 10, 27), 5, 1,
            [new StatsRow("ALERT", "LONG", 6, 5, 2, 2, 0.8, 0.6)], 14,
            [new FailedCount("V3", 6), new FailedCount("C2", 4)]));

        var expected = """
            📅 Daily summary · 2026-10-27 (UTC)
            Alerts: 6 (5 sent, 1 in digests)
            ✅ ALERT LONG   6 · target 40% · inval 40% · MFE 0.80% · MAE 0.60%
            Outcomes: target first 2 · invalidation first 2 · neither 1 · still tracking 1
            Near misses: 14 · most often failed: V3 (6), C2 (4)
            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Quiet_day()
    {
        var text = ReportFormatter.Daily(new DailySummary(new DateOnly(2026, 10, 27), 0, 0, [], 0, []));
        Assert.Equal("📅 Daily summary · 2026-10-27 (UTC)\nAlerts: 0\nNear misses: 0", text);
    }

    [Fact]
    public void Top_shows_score_and_marks()
    {
        var text = ReportFormatter.Top(
        [
            new TopRow("SOLUSDT", "LONG", 42, 4, 2, "✓M1 ✓M2 ✓M3 ✓M4 | ✓C1 ✗C2 ✓C3"),
            new TopRow("ETHUSDT", "SHORT", 31, 3, 1, "✓M1 ✓M2 ✗M3 ✓M4 | ✗C1 ✗C2 ✓C3"),
        ], 5);
        Assert.Equal("""
            🏁 Top 2 by score (must-haves × 10 + confirmations)
            1. SOLUSDT LONG · score 42 (4/4 must, 2/3 conf)
               ✓M1 ✓M2 ✓M3 ✓M4 | ✓C1 ✗C2 ✓C3
            2. ETHUSDT SHORT · score 31 (3/4 must, 1/3 conf)
               ✓M1 ✓M2 ✗M3 ✓M4 | ✗C1 ✗C2 ✓C3
            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Top_before_any_score() => Assert.Contains("no scores yet", ReportFormatter.Top([], 5));
}
