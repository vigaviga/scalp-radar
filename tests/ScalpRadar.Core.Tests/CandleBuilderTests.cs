using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Tests;

public class CandleBuilderTests
{
    private readonly CandleBuilder _b = new(TimeUtil.OneMinute);
    private readonly List<Candle> _closed = [];

    [Fact]
    public void Trades_in_one_minute_build_OHLC_and_volume()
    {
        _b.AddTrade(Fx.Min(0, 1), 100, 1, isBuyerMaker: false, _closed);
        _b.AddTrade(Fx.Min(0, 20), 103, 2, isBuyerMaker: true, _closed);
        _b.AddTrade(Fx.Min(0, 40), 99, 3, isBuyerMaker: false, _closed);
        _b.AddTrade(Fx.Min(0, 59), 101, 4, isBuyerMaker: true, _closed);

        Assert.Empty(_closed);
        var c = _b.Current!;
        Assert.Equal(Fx.Min(0), c.OpenTime);
        Assert.Equal(100, c.Open);
        Assert.Equal(103, c.High);
        Assert.Equal(99, c.Low);
        Assert.Equal(101, c.Close);
        Assert.Equal(10, c.Volume);
        Assert.Equal(4, c.Trades);
    }

    [Fact]
    public void Taker_buy_is_when_buyer_is_not_the_maker()
    {
        _b.AddTrade(Fx.Min(0, 1), 100, 3, isBuyerMaker: false, _closed); // taker buy
        _b.AddTrade(Fx.Min(0, 2), 100, 1, isBuyerMaker: true, _closed);  // taker sell

        var c = _b.Current!;
        Assert.Equal(3, c.BuyVolume);
        Assert.Equal(1, c.SellVolume);
        Assert.Equal(2, c.Delta);
    }

    [Fact]
    public void Delta_is_negative_when_sellers_dominate()
    {
        _b.AddTrade(Fx.Min(0, 1), 100, 1, isBuyerMaker: false, _closed);
        _b.AddTrade(Fx.Min(0, 2), 100, 4, isBuyerMaker: true, _closed);
        Assert.Equal(-3, _b.Current!.Delta);
    }

    [Fact]
    public void A_trade_at_exactly_the_next_minute_closes_the_candle()
    {
        _b.AddTrade(Fx.Min(0, 59).AddMilliseconds(999), 100, 1, false, _closed);
        _b.AddTrade(Fx.Min(1), 101, 1, false, _closed);

        var closed = Assert.Single(_closed);
        Assert.Equal(Fx.Min(0), closed.OpenTime);
        Assert.Equal(100, closed.Close);
        Assert.Equal(Fx.Min(1), _b.Current!.OpenTime);
    }

    [Fact]
    public void Empty_minutes_between_trades_become_flat_zero_volume_candles()
    {
        _b.AddTrade(Fx.Min(0, 10), 100, 1, false, _closed);
        _b.AddTrade(Fx.Min(3, 10), 105, 1, false, _closed);

        Assert.Equal(3, _closed.Count);
        Assert.Equal([Fx.Min(0), Fx.Min(1), Fx.Min(2)], _closed.Select(c => c.OpenTime));
        Assert.All(_closed.Skip(1), c =>
        {
            Assert.Equal(0, c.Volume);
            Assert.Equal(100, c.Close);
            Assert.False(c.IsPartial);
        });
    }

    [Fact]
    public void CloseUpTo_closes_on_exchange_time_and_not_before()
    {
        _b.AddTrade(Fx.Min(0, 10), 100, 1, false, _closed);

        _b.CloseUpTo(Fx.Min(0, 59), _closed);
        Assert.Empty(_closed);

        _b.CloseUpTo(Fx.Min(1), _closed);
        Assert.Single(_closed);
        Assert.Null(_b.Current);
    }

    [Fact]
    public void Trade_after_a_timer_close_fills_the_gap_from_the_closed_candle()
    {
        _b.AddTrade(Fx.Min(0, 10), 100, 1, false, _closed);
        _b.CloseUpTo(Fx.Min(1, 2), _closed);
        _b.AddTrade(Fx.Min(2, 5), 102, 1, false, _closed);

        Assert.Equal([Fx.Min(0), Fx.Min(1)], _closed.Select(c => c.OpenTime));
        Assert.Equal(0, _closed[1].Volume);
    }

    [Fact]
    public void Late_trade_for_a_closed_minute_is_ignored()
    {
        _b.AddTrade(Fx.Min(0, 10), 100, 1, false, _closed);
        _b.AddTrade(Fx.Min(1, 10), 101, 1, false, _closed);
        _b.AddTrade(Fx.Min(0, 50), 50, 1, false, _closed);

        Assert.Equal(1, _b.LateTrades);
        Assert.Equal(101, _b.Current!.Low);
    }

    [Fact]
    public void First_candle_is_partial_and_later_ones_are_not()
    {
        _b.AddTrade(Fx.Min(0, 30), 100, 1, false, _closed);
        _b.AddTrade(Fx.Min(1, 0), 100, 1, false, _closed);

        Assert.True(_closed[0].IsPartial);
        Assert.False(_b.Current!.IsPartial);
    }

    [Fact]
    public void After_a_disconnect_the_open_candle_and_missing_minutes_are_partial()
    {
        _b.AddTrade(Fx.Min(0, 0), 100, 1, false, _closed);
        _b.AddTrade(Fx.Min(1, 0), 100, 1, false, _closed);
        _closed.Clear();

        _b.MarkCurrentPartial();
        _b.AddTrade(Fx.Min(4, 30), 100, 1, false, _closed);

        Assert.Equal(3, _closed.Count); // minute 1 (open at disconnect), 2 and 3 (missing)
        Assert.All(_closed, c => Assert.True(c.IsPartial));
        Assert.True(_b.Current!.IsPartial);
    }

    [Fact]
    public void Aggregate_merges_five_1m_candles_into_one_5m_candle()
    {
        var ones = new List<Candle>
        {
            Fx.C1(0, 100, 101, 99, 100.5, vol: 10, buy: 6),
            Fx.C1(1, 100.5, 103, 100, 102, vol: 20, buy: 15),
            Fx.C1(2, 102, 102, 98, 99, vol: 30, buy: 5),
            Fx.C1(3, 99, 100, 97, 98, vol: 10, buy: 5),
            Fx.C1(4, 98, 99, 98, 98.5, vol: 10, buy: 9),
        };

        var five = CandleAggregator.Aggregate(ones, Fx.Min(0), TimeUtil.FiveMinutes);

        Assert.Equal(100, five.Open);
        Assert.Equal(103, five.High);
        Assert.Equal(97, five.Low);
        Assert.Equal(98.5, five.Close);
        Assert.Equal(80, five.Volume);
        Assert.Equal(40, five.BuyVolume);
        Assert.Equal(0, five.Delta);
        Assert.Equal(Fx.Min(5), five.CloseTime);
    }
}

public class SymbolStateCandleTests
{
    private static SymbolState NewState() => new(new SymbolInfo("TESTUSDT", 2, Fx.T0.AddDays(-30)), new StateOptions());

    [Fact]
    public void Closing_the_minute_that_ends_a_5m_bucket_builds_the_5m_candle()
    {
        var s = NewState();
        var closed = new List<Candle>();
        for (var m = 0; m <= 5; m++)
            s.ApplyTrade(Fx.Trade("TESTUSDT", Fx.Min(m, 1), 100 + m, 1, isBuyerMaker: m % 2 == 1), closed);

        Assert.Equal(5, s.Candles1m.Count);
        var five = Assert.Single(s.Candles5m);
        Assert.Equal(Fx.Min(0), five.OpenTime);
        Assert.Equal(104, five.Close);
        Assert.Equal(5, five.Volume);
        Assert.Equal(3, five.BuyVolume); // minutes 0, 2, 4 were taker buys
    }

    [Fact]
    public void Cvd_is_the_running_sum_of_delta_at_each_1m_close()
    {
        var s = NewState();
        var closed = new List<Candle>();
        s.ApplyTrade(Fx.Trade("TESTUSDT", Fx.Min(0, 1), 100, 3, false), closed); // +3
        s.ApplyTrade(Fx.Trade("TESTUSDT", Fx.Min(1, 1), 100, 1, true), closed);  // -1
        s.ApplyTrade(Fx.Trade("TESTUSDT", Fx.Min(2, 1), 100, 2, false), closed); // +2 (open)

        Assert.Equal([3.0, 2.0], s.Candles1m.Select(c => c.Cvd));
        Assert.Equal(4, s.CurrentCvd());
    }

    [Fact]
    public void Backfill_replaces_partial_live_candles_and_keeps_the_open_one()
    {
        var s = NewState();
        var closed = new List<Candle>();
        s.ApplyTrade(Fx.Trade("TESTUSDT", Fx.Min(9, 30), 100, 1, false), closed);   // partial minute 9
        s.ApplyTrade(Fx.Trade("TESTUSDT", Fx.Min(10, 5), 101, 1, false), closed);   // minute 10 open
        Assert.True(s.Candles1m[^1].IsPartial);

        var k1 = Fx.Flat1m(0, 11, 100, vol: 10, buy: 7); // REST includes the open minute 10 too
        var k5 = new List<Candle> { Fx.C5(0, 100, 100, 100, 100, 50, 35), Fx.C5(1, 100, 100, 100, 100, 50, 35) };
        s.ApplyBackfill(new BackfillEvent("TESTUSDT", k1, k5, [], IsGap: false), Fx.Min(10, 6));

        Assert.True(s.BackfillDone);
        Assert.Equal(10, s.Candles1m.Count);            // minutes 0..9, not the open minute 10
        Assert.Null(s.FirstPartialCandleTime());
        Assert.Equal(Fx.Min(10), s.OpenCandle!.OpenTime);
        Assert.Equal(2, s.Candles5m.Count);
        Assert.Equal(20, s.Candles5m[0].Cvd);           // 5m delta = 35 buy - 15 sell
        Assert.Equal(40, s.Candles5m[1].Cvd);
        Assert.Equal(40, s.Candles1m[^1].Cvd);          // last 1m of bucket 1 ends at the 5m CVD
    }

    [Fact]
    public void Old_candles_are_trimmed_to_the_configured_window()
    {
        var s = new SymbolState(new SymbolInfo("TESTUSDT", 2, Fx.T0), new StateOptions { Keep1mHours = 1, Keep5mHours = 2 });
        var k1 = Fx.Flat1m(0, 120, 100);
        var k5 = Enumerable.Range(0, 30).Select(i => Fx.C5(i, 100, 100, 100, 100)).ToList();
        s.ApplyBackfill(new BackfillEvent("TESTUSDT", k1, k5, [], false), Fx.Min(151));

        Assert.Equal(60, s.Candles1m.Count);
        Assert.Equal(Fx.Min(60), s.Candles1m[0].OpenTime);
        Assert.Equal(24, s.Candles5m.Count);
    }
}

public class UniverseSelectorTests
{
    private static readonly DateTime Now = Fx.T0;
    private static SymbolCandidate Cand(string s, double vol, int ageDays = 30, bool perp = true, bool trading = true, string quote = "USDT") =>
        new(s, quote, perp, trading, Now.AddDays(-ageDays), vol, 2);

    [Fact]
    public void Takes_top_by_quote_volume_after_filters()
    {
        var all = new[]
        {
            Cand("AAAUSDT", 50e6),
            Cand("BBBUSDT", 900e6),
            Cand("CCCUSDT", 10e6),                 // under $20M
            Cand("USDCUSDT", 2e9),                 // excluded stablecoin
            Cand("NEWUSDT", 500e6, ageDays: 2),    // listed less than 3 days ago
            Cand("DLVUSDT", 800e6, perp: false),   // not perpetual
            Cand("HLTUSDT", 700e6, trading: false),
            Cand("ETHBUSD", 600e6, quote: "BUSD"),
            Cand("DDDUSDT", 60e6),
        };
        var o = new UniverseOptions { Size = 2 };

        var picked = UniverseSelector.Select(all, o, Now).Select(s => s.Symbol);

        Assert.Equal(["BBBUSDT", "DDDUSDT"], picked);
    }

    [Theory]
    [InlineData("0.01", 2)]
    [InlineData("0.0100", 2)]
    [InlineData("1", 0)]
    [InlineData("0.00001", 5)]
    [InlineData("0.10", 1)]
    public void Price_decimals_come_from_tick_size(string tick, int expected) =>
        Assert.Equal(expected, UniverseSelector.DecimalsFromTickSize(decimal.Parse(tick, System.Globalization.CultureInfo.InvariantCulture)));
}
