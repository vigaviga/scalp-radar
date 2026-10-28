using ScalpRadar.Core.Indicators;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Tests;

public class HeatmapTests
{
    private static HeatmapOptions Opts(Dictionary<string, double>? mix = null) => new()
    {
        LeverageMix = mix ?? new() { ["10"] = 1.0 },
        MaintenanceMarginRate = 0.005,
        PruneBelowUsd = 0.0001,
    };

    // H 101, L 99, C 100 -> typical price P = 100. Volume 10, taker buys 7 -> longShare 0.7.
    private static Candle Candle(int index = 0, double h = 101, double l = 99, double c = 100, double vol = 10, double buy = 7) =>
        Fx.C5(index, 100, h, l, c, vol, buy);

    [Fact]
    public void New_oi_lands_at_the_liquidation_prices_split_by_taker_share()
    {
        var map = new LiquidationHeatmap(Opts());
        map.ApplyCandle(Candle(), oiBefore: 100, oiAfter: 110); // ΔOI 10 contracts × P 100 = $1000

        // 10x long liq: 100 × (1 − 0.1 + 0.005) = 90.5; short liq: 100 × (1 + 0.1 − 0.005) = 109.5
        Assert.Equal(700, map.LongBucketAt(90.5), 6);
        Assert.Equal(300, map.ShortBucketAt(109.5), 6);
        Assert.Single(map.LongBuckets);
        Assert.Single(map.ShortBuckets);
    }

    [Fact]
    public void Default_leverage_mix_spreads_notional_by_weight()
    {
        var map = new LiquidationHeatmap(Opts(new HeatmapOptions().LeverageMix));
        map.ApplyCandle(Candle(), 100, 110);

        Assert.Equal(700 * 0.30, map.LongBucketAt(100 * (1 - 1 / 25.0 + 0.005)), 6);
        Assert.Equal(700 * 0.15, map.LongBucketAt(100 * (1 - 1 / 100.0 + 0.005)), 6);
        Assert.Equal(300 * 0.25, map.ShortBucketAt(100 * (1 + 1 / 50.0 - 0.005)), 6);
        Assert.Equal(700, map.LongBuckets.Values.Sum(), 6);
        Assert.Equal(300, map.ShortBuckets.Values.Sum(), 6);
    }

    [Fact]
    public void Long_share_is_clamped_to_20_80()
    {
        var map = new LiquidationHeatmap(Opts());
        map.ApplyCandle(Candle(buy: 9.9), 100, 110);
        Assert.Equal(800, map.LongBuckets.Values.Sum(), 6);
        Assert.Equal(200, map.ShortBuckets.Values.Sum(), 6);
    }

    [Fact]
    public void Oi_drop_scales_every_bucket_down()
    {
        var map = new LiquidationHeatmap(Opts());
        map.ApplyCandle(Candle(0), 100, 110);
        map.ApplyCandle(Candle(0), 110, 55); // same candle time: no decay; ΔOI −55 of 110 -> × 0.5

        Assert.Equal(350, map.LongBucketAt(90.5), 6);
        Assert.Equal(150, map.ShortBucketAt(109.5), 6);
    }

    [Fact]
    public void Buckets_clear_when_a_candle_crosses_them()
    {
        var map = new LiquidationHeatmap(Opts());
        map.ApplyCandle(Candle(0), 100, 110);

        map.ApplyCandle(Candle(0, h: 100, l: 90.4, c: 95), 110, 110);   // low reaches the 90.5 long-liq bucket
        Assert.Empty(map.LongBuckets);
        Assert.Single(map.ShortBuckets);

        map.ApplyCandle(Candle(0, h: 109.6, l: 100, c: 105), 110, 110); // high reaches the 109.5 short-liq bucket
        Assert.Empty(map.ShortBuckets);
    }

    [Fact]
    public void Buckets_decay_with_a_24h_half_life()
    {
        var map = new LiquidationHeatmap(Opts());
        map.ApplyCandle(Candle(0), 100, 110);
        map.DecayTo(Fx.Min(5).AddHours(24));

        Assert.Equal(350, map.LongBucketAt(90.5), 6);
        Assert.Equal(150, map.ShortBucketAt(109.5), 6);
    }

    [Fact]
    public void Side_sums_only_count_the_query_window()
    {
        var map = new LiquidationHeatmap(Opts());
        map.AddNotional(longLiquidations: false, 100.2, 1_000);  // +0.2%: too close
        map.AddNotional(false, 101, 2_000);                     // +1%: counts
        map.AddNotional(false, 102.9, 3_000);                   // +2.9%: counts
        map.AddNotional(false, 104, 4_000);                     // +4%: too far
        map.AddNotional(true, 99, 500);                         // −1%: counts below
        map.AddNotional(true, 96, 700);                         // −4%: too far

        Assert.Equal(5_000, map.ShortLiqAbove(100), 6);
        Assert.Equal(500, map.LongLiqBelow(100), 6);
    }

    [Fact]
    public void Largest_cluster_merges_adjacent_buckets_and_reports_its_centre()
    {
        var map = new LiquidationHeatmap(Opts());
        var i = map.BucketIndex(101);
        double Mid(int k) => (map.BucketLow(k) + map.BucketHigh(k)) / 2;

        map.AddNotional(false, Mid(i), 100);
        map.AddNotional(false, Mid(i + 1), 100);
        map.AddNotional(false, Mid(i + 2), 100);   // cluster A: 300 over three buckets
        map.AddNotional(false, Mid(i + 5), 250);   // cluster B: 250 in one bucket

        var c = map.LargestShortClusterAbove(100)!;

        Assert.Equal(300, c.Notional, 6);
        Assert.Equal((map.BucketLow(i) + map.BucketHigh(i + 2)) / 2, c.Center, 9);
    }

    [Fact]
    public void Largest_long_cluster_is_below_price()
    {
        var map = new LiquidationHeatmap(Opts());
        map.AddNotional(true, 98, 400);
        map.AddNotional(true, 99, 100);
        var c = map.LargestLongClusterBelow(100)!;
        Assert.InRange(c.Center, 97.9, 98.1);
    }

    [Fact]
    public void Bucket_index_is_log_spaced_at_0_1_percent()
    {
        var map = new LiquidationHeatmap(Opts());
        var i = map.BucketIndex(100);
        Assert.Equal(i + 1, map.BucketIndex(100 * 1.001 * 1.0000001));
        Assert.Equal(1.001, map.BucketHigh(i) / map.BucketLow(i), 9);
    }
}
