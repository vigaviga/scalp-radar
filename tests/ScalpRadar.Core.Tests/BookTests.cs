using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Core.Rules;

namespace ScalpRadar.Core.Tests;

public class BookTests
{
    private static BookLevel L(double p, double q) => new(p, q);

    // Mid 100. Bids 99.9..99.0 and asks 100.1..101.0, ten levels each at qty 10 unless overridden.
    private static (BookLevel[] Bids, BookLevel[] Asks) Book(Func<int, double>? bidQty = null, Func<int, double>? askQty = null)
    {
        var bids = Enumerable.Range(0, 10).Select(i => L(99.9 - i * 0.1, bidQty?.Invoke(i) ?? 10)).ToArray();
        var asks = Enumerable.Range(0, 10).Select(i => L(100.1 + i * 0.1, askQty?.Invoke(i) ?? 10)).ToArray();
        return (bids, asks);
    }

    [Fact]
    public void Spread_is_relative_to_mid()
    {
        var (b, a) = Book();
        Assert.Equal(0.2, BookMath.SpreadPct(b, a)!.Value, 9);
    }

    [Fact]
    public void Imbalance_counts_only_levels_within_half_a_percent_of_mid()
    {
        // Within ±0.45% of 100 (kept off a level so float rounding cannot matter): bids 99.9..99.6, asks 100.1..100.4.
        var (b, a) = Book(bidQty: _ => 30, askQty: _ => 10);
        double bid = b.Take(4).Sum(x => x.Price * x.Qty), ask = a.Take(4).Sum(x => x.Price * x.Qty);
        Assert.Equal((bid - ask) / (bid + ask), BookMath.Imbalance(b, a, 0.45)!.Value, 9);

        // A huge bid outside the range changes nothing.
        b[9] = L(99.0, 1_000_000);
        Assert.Equal((bid - ask) / (bid + ask), BookMath.Imbalance(b, a, 0.45)!.Value, 9);
    }

    [Fact]
    public void A_wall_is_at_least_five_times_the_median_level_in_range()
    {
        var (b, a) = Book(bidQty: i => i == 3 ? 60 : 10, askQty: i => i == 7 ? 45 : 10);
        var walls = BookMath.Walls(b, a, 1.0, 5.0);

        var wall = Assert.Single(walls); // 60 ≈ 6× median; 45 is under 5×
        Assert.True(wall.IsBid);
        Assert.Equal(99.6, wall.Price, 9);
    }

    [Fact]
    public void Imbalance_is_the_30_second_average_of_one_second_samples()
    {
        var t = new BookTracker(new OrderBookOptions());
        var (balanced, _) = (Book(), 0);
        var (bidHeavyB, bidHeavyA) = Book(bidQty: _ => 30);

        t.Apply(Fx.Min(0, 0), balanced.Bids, balanced.Asks);         // imbalance ~0 (falls out of the window)
        for (var s = 1; s <= 30; s++) t.Apply(Fx.Min(0, s), bidHeavyB, bidHeavyA);

        var expected = BookMath.Imbalance(bidHeavyB, bidHeavyA, 0.5)!.Value;
        Assert.Equal(expected, t.Imbalance(Fx.Min(0, 30))!.Value, 9);
        Assert.Equal(30, t.HistorySeconds(Fx.Min(0, 30)));
    }

    [Fact]
    public void A_wall_counts_only_after_holding_for_20_seconds()
    {
        var t = new BookTracker(new OrderBookOptions());
        var (b, a) = Book(bidQty: i => i == 3 ? 60 : 10);

        for (var s = 0; s <= 19; s++) t.Apply(Fx.Min(0, s), b, a);
        Assert.Empty(t.PersistentWalls(Fx.Min(0, 19)));

        t.Apply(Fx.Min(0, 20), b, a);
        Assert.Single(t.PersistentWalls(Fx.Min(0, 20)));
    }

    [Fact]
    public void A_wall_that_disappears_restarts_its_timer()
    {
        var t = new BookTracker(new OrderBookOptions());
        var (wb, wa) = Book(bidQty: i => i == 3 ? 60 : 10);
        var (nb, na) = Book();

        for (var s = 0; s <= 15; s++) t.Apply(Fx.Min(0, s), wb, wa);
        t.Apply(Fx.Min(0, 16), nb, na); // pulled: a spoof
        for (var s = 17; s <= 30; s++) t.Apply(Fx.Min(0, s), wb, wa);

        Assert.Empty(t.PersistentWalls(Fx.Min(0, 30)));
        t.Apply(Fx.Min(0, 37), wb, wa);
        Assert.Single(t.PersistentWalls(Fx.Min(0, 37)));
    }
}

public class ShortlistTests
{
    private static readonly OrderBookOptions Opts = new() { MaxBooks = 2, MinMustHavesToShortlist = 3, UnsubscribeAfterMinutes = 10 };

    private static ShortlistCandidate C(string s, int must, int score) => new(s, must, score);

    [Fact]
    public void Symbols_with_three_of_four_must_haves_join()
    {
        var sl = new Shortlist(Opts);
        var (add, remove) = sl.Update([C("A", 3, 30), C("B", 2, 20)], Fx.Min(0));
        Assert.Equal(["A"], add);
        Assert.Empty(remove);
    }

    [Fact]
    public void When_full_the_lowest_score_is_evicted_for_a_higher_one()
    {
        var sl = new Shortlist(Opts);
        sl.Update([C("A", 3, 30), C("B", 3, 32)], Fx.Min(0));

        var (add, remove) = sl.Update([C("A", 3, 30), C("B", 3, 32), C("C", 4, 41)], Fx.Min(1));

        Assert.Equal(["C"], add);
        Assert.Equal(["A"], remove);
        Assert.Equal(["B", "C"], sl.Active.Order());
    }

    [Fact]
    public void A_newcomer_that_does_not_beat_the_lowest_waits()
    {
        var sl = new Shortlist(Opts);
        sl.Update([C("A", 3, 30), C("B", 3, 32)], Fx.Min(0));
        var (add, remove) = sl.Update([C("A", 3, 30), C("B", 3, 32), C("C", 3, 30)], Fx.Min(1));
        Assert.Empty(add);
        Assert.Empty(remove);
    }

    [Fact]
    public void Symbols_leave_after_ten_minutes_below_the_threshold()
    {
        var sl = new Shortlist(Opts);
        sl.Update([C("A", 3, 30)], Fx.Min(0));

        Assert.Empty(sl.Update([C("A", 2, 20)], Fx.Min(10)).Remove);
        Assert.Equal(["A"], sl.Update([C("A", 2, 20)], Fx.Min(10, 1)).Remove);
    }

    [Fact]
    public void Qualifying_again_resets_the_ten_minute_timer()
    {
        var sl = new Shortlist(Opts);
        sl.Update([C("A", 3, 30)], Fx.Min(0));
        sl.Update([C("A", 3, 30)], Fx.Min(8));
        Assert.Empty(sl.Update([C("A", 1, 10)], Fx.Min(15)).Remove);
    }
}
