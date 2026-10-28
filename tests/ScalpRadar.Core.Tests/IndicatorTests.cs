using ScalpRadar.Core.Indicators;
using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Tests;

public class StructureTests
{
    // Higher low (8 -> 9.5) with a pivot high at 13 in between.
    private static readonly (double H, double L)[] HigherLowSeries =
    [
        (10, 9), (11, 9.5), (10, 8), (11, 9), (13, 10), (12, 10.5), (11, 9.5), (12, 10), (12.5, 11),
    ];

    private static List<Candle> Build((double H, double L)[] hl) => hl.Select((x, i) => Fx.Hl5(i, x.H, x.L)).ToList();

    private static List<Candle> Mirror((double H, double L)[] hl) => hl.Select((x, i) => Fx.Hl5(i, 30 - x.L, 30 - x.H)).ToList();

    [Fact]
    public void Pivots_need_two_strictly_lower_highs_on_each_side()
    {
        var pivots = Pivots.Find(Build(HigherLowSeries), 2, 2);

        Assert.Equal(
            [(PivotKind.Low, 8.0), (PivotKind.High, 13.0), (PivotKind.Low, 9.5)],
            pivots.Select(p => (p.Kind, p.Price)));
    }

    [Fact]
    public void Equal_highs_are_not_pivots()
    {
        var candles = Build([(10, 9), (11, 9), (12, 9), (12, 9), (11, 9), (10, 9)]);
        Assert.DoesNotContain(Pivots.Find(candles, 2, 2), p => p.Kind == PivotKind.High);
    }

    [Fact]
    public void Pivot_carries_the_cvd_at_its_candle_close()
    {
        var candles = Build(HigherLowSeries);
        candles[4].Cvd = 1234;
        var high = Pivots.Find(candles, 2, 2).Single(p => p.Kind == PivotKind.High);
        Assert.Equal(1234, high.Cvd);
    }

    [Fact]
    public void Long_structure_is_a_higher_low_and_a_close_above_the_last_pivot_high()
    {
        var pivots = Pivots.Find(Build(HigherLowSeries), 2, 2);

        var r = Structure.Evaluate(pivots, lastClosed1mClose: 13.5);

        Assert.True(r.Long);
        Assert.False(r.Short);
        Assert.Equal(13, r.BreakoutLevel(Direction.Long));
        Assert.Equal(9.5, r.Invalidation(Direction.Long));
    }

    [Fact]
    public void No_long_structure_without_the_breakout_close()
    {
        var pivots = Pivots.Find(Build(HigherLowSeries), 2, 2);
        Assert.False(Structure.Evaluate(pivots, 12.9).Long);
        Assert.False(Structure.Evaluate(pivots, 13.0).Long); // must close above, not at
    }

    [Fact]
    public void Short_structure_is_a_lower_high_and_a_close_below_the_last_pivot_low()
    {
        var pivots = Pivots.Find(Mirror(HigherLowSeries), 2, 2); // pivot highs 22 -> 20.5, pivot low 17

        var r = Structure.Evaluate(pivots, lastClosed1mClose: 16.5);

        Assert.True(r.Short);
        Assert.False(r.Long);
        Assert.Equal(17, r.BreakoutLevel(Direction.Short));
        Assert.Equal(20.5, r.Invalidation(Direction.Short));
    }

    [Fact]
    public void Lower_low_is_not_long_structure_even_above_the_high()
    {
        // Lows 8 -> 7.5: a lower low.
        var candles = Build([(10, 9), (11, 9.5), (10, 8), (11, 9), (13, 10), (12, 10.5), (11, 7.5), (12, 10), (12.5, 11)]);
        var r = Structure.Evaluate(Pivots.Find(candles, 2, 2), 14);
        Assert.False(r.Long);
        Assert.False(r.Short);
    }

    [Fact]
    public void Not_enough_pivots_means_no_structure()
    {
        var r = Structure.Evaluate([], 100);
        Assert.False(r.Long);
        Assert.False(r.Short);
        Assert.Null(r.BreakoutLevel(Direction.Long));
    }
}

public class VolumeTests
{
    private static List<Candle> Fives(int count, double vol, int startIndex = 0) =>
        Enumerable.Range(startIndex, count).Select(i => Fx.C5(i, 100, 100, 100, 100, vol, vol / 2)).ToList();

    [Fact]
    public void VolRatio_is_last_five_minutes_over_the_median_5m_volume_of_the_previous_4h()
    {
        var fives = Fives(48, 100);                                    // 4h of 5m candles before minute 240
        var ones = Fx.Flat1m(240, 245, 100, vol: 50);                   // the current 5-minute window: 250

        var r = Volume.Compute(ones, fives, windowMinutes: 5, baselineHours: 4, shareMinutes: 15);

        Assert.Equal(100, r.BaselineMedian);
        Assert.Equal(2.5, r.VolRatio!.Value, 10);
    }

    [Fact]
    public void Baseline_ignores_5m_candles_inside_the_current_window()
    {
        var fives = Fives(48, 100);
        fives.Add(Fx.C5(48, 100, 100, 100, 100, vol: 100_000)); // the 5m candle covering minutes 240-244
        var ones = Fx.Flat1m(240, 245, 100, vol: 50);

        var r = Volume.Compute(ones, fives, 5, 4, 15);

        Assert.Equal(100, r.BaselineMedian);
    }

    [Fact]
    public void Median_works_with_fewer_than_48_values()
    {
        var fives = new List<Candle>
        {
            Fx.C5(0, 100, 100, 100, 100, vol: 100),
            Fx.C5(1, 100, 100, 100, 100, vol: 300),
            Fx.C5(2, 100, 100, 100, 100, vol: 200),
        };
        var ones = Fx.Flat1m(15, 20, 100, vol: 80); // 400

        var r = Volume.Compute(ones, fives, 5, 4, 15);

        Assert.Equal(200, r.BaselineMedian);
        Assert.Equal(2.0, r.VolRatio!.Value, 10);
    }

    [Fact]
    public void Median_of_an_even_count_averages_the_middle_two() =>
        Assert.Equal(2.5, Stats.Median([4, 1, 2, 3]));

    [Fact]
    public void No_baseline_means_no_ratio()
    {
        var r = Volume.Compute(Fx.Flat1m(0, 5, 100), [], 5, 4, 15);
        Assert.Null(r.VolRatio);
    }

    [Fact]
    public void UpShare_and_DownShare_split_15_minutes_of_volume_by_candle_colour()
    {
        var ones = new List<Candle>();
        for (var m = 0; m < 10; m++) ones.Add(Fx.C1(m, 100, 101, 99, 100.5, vol: 10));  // green: 100
        for (var m = 10; m < 13; m++) ones.Add(Fx.C1(m, 100, 101, 99, 99.5, vol: 20));  // red: 60
        for (var m = 13; m < 15; m++) ones.Add(Fx.C1(m, 100, 101, 99, 100, vol: 20));   // doji: 40

        var r = Volume.Compute(ones, [], 5, 4, 15);

        Assert.Equal(0.5, r.UpShare!.Value, 10);
        Assert.Equal(0.3, r.DownShare!.Value, 10);
    }
}

public class OpenInterestRegimeTests
{
    [Theory]
    [InlineData(0.5, 1.5, OiRegime.NewLongs)]
    [InlineData(-0.5, 1.5, OiRegime.NewShorts)]
    [InlineData(0.5, -1.5, OiRegime.ShortCovering)]
    [InlineData(-0.5, -1.5, OiRegime.LongUnwinding)]
    [InlineData(0.1, 1.5, OiRegime.Neutral)]   // price inside the flat zone
    [InlineData(-0.2, 1.5, OiRegime.Neutral)]  // the zone edge counts as flat
    [InlineData(0.5, 0.15, OiRegime.Neutral)]  // OI inside the flat zone
    [InlineData(0.21, 0.21, OiRegime.NewLongs)]
    public void Regime_from_price_and_oi_change(double pricePct, double oiPct, OiRegime expected) =>
        Assert.Equal(expected, OpenInterest.Classify(pricePct, oiPct, flatPct: 0.2));

    [Fact]
    public void Missing_data_is_neutral() => Assert.Equal(OiRegime.Neutral, OpenInterest.Classify(null, 2, 0.2));

    [Fact]
    public void Price_change_compares_with_the_close_n_minutes_back()
    {
        var ones = Fx.Flat1m(0, 15, 100);
        ones.Add(Fx.C1(15, 100, 102, 100, 101.5));
        Assert.Equal(1.5, PriceChange.Pct(ones, 15)!.Value, 10);
        Assert.Null(PriceChange.Pct(ones, 16));
    }
}

public class CvdTests
{
    private static List<Candle> Ones()
    {
        // 15 candles: first 10 with delta +2 (vol 10, buy 6), last 5 with delta -4 (vol 20, buy 8)
        var list = Fx.Flat1m(0, 10, 100, vol: 10, buy: 6);
        list.AddRange(Fx.Flat1m(10, 15, 100, vol: 20, buy: 8));
        return list;
    }

    [Fact]
    public void Cvd15_sums_delta_and_cvd5Share_divides_by_volume()
    {
        var r = Cvd.Compute(Ones(), 15, 5, price: 100, cvdNow: 0, lastHigh: null, lastLow: null);
        Assert.Equal(10 * 2 + 5 * -4, r.Cvd15);
        Assert.Equal(-20.0 / 100, r.Cvd5Share!.Value, 10);
    }

    [Fact]
    public void Not_enough_candles_gives_null()
    {
        var r = Cvd.Compute(Fx.Flat1m(0, 3, 100), 15, 5, 100, 0, null, null);
        Assert.Null(r.Cvd15);
        Assert.Null(r.Cvd5Share);
    }

    [Fact]
    public void Bearish_divergence_when_price_makes_the_high_but_cvd_does_not()
    {
        var high = new Pivot(PivotKind.High, Fx.T0, 105, Cvd: 500);
        Assert.True(Cvd.Compute(Ones(), 15, 5, price: 105.5, cvdNow: 400, high, null).BearishDivergence);
        Assert.True(Cvd.Compute(Ones(), 15, 5, price: 105, cvdNow: 400, high, null).BearishDivergence);   // at the high counts
        Assert.False(Cvd.Compute(Ones(), 15, 5, price: 105.5, cvdNow: 600, high, null).BearishDivergence); // CVD confirms
        Assert.False(Cvd.Compute(Ones(), 15, 5, price: 104, cvdNow: 400, high, null).BearishDivergence);   // below the high
    }

    [Fact]
    public void Bullish_divergence_when_price_makes_the_low_but_cvd_does_not()
    {
        var low = new Pivot(PivotKind.Low, Fx.T0, 95, Cvd: -500);
        Assert.True(Cvd.Compute(Ones(), 15, 5, price: 94.5, cvdNow: -400, null, low).BullishDivergence);
        Assert.False(Cvd.Compute(Ones(), 15, 5, price: 94.5, cvdNow: -600, null, low).BullishDivergence);
        Assert.False(Cvd.Compute(Ones(), 15, 5, price: 96, cvdNow: -400, null, low).BullishDivergence);
    }
}

public class AtrTests
{
    [Fact]
    public void Wilder_atr_seeds_with_the_mean_then_smooths()
    {
        var c = new List<Candle>
        {
            Fx.C5(0, 9, 10, 8, 9),
            Fx.C5(1, 9, 11, 9, 10),   // TR 2
            Fx.C5(2, 10, 12, 10, 11), // TR 2
            Fx.C5(3, 11, 13, 11, 12), // TR 2  -> seed 2
            Fx.C5(4, 12, 16, 12, 15), // TR 4  -> (2*2 + 4) / 3
        };
        Assert.Equal(8.0 / 3, Atr.Wilder(c, 3)!.Value, 10);
    }

    [Fact]
    public void True_range_includes_gaps_from_the_previous_close() =>
        Assert.Equal(5, Atr.TrueRange(Fx.C5(1, 19, 20, 19, 19.5), prevClose: 15));

    [Fact]
    public void Too_few_candles_gives_null() => Assert.Null(Atr.Wilder(Enumerable.Range(0, 14).Select(i => Fx.C5(i, 1, 2, 1, 1)).ToList(), 14));
}
