using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Tests;

/// <summary>Hand-built candles and times for tests.</summary>
internal static class Fx
{
    public static readonly DateTime T0 = new(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc);

    public static DateTime Min(int minutes, int seconds = 0) => T0.AddMinutes(minutes).AddSeconds(seconds);

    public static Candle C1(int minute, double o, double h, double l, double c, double vol = 10, double buy = 5) => new()
    {
        OpenTime = Min(minute),
        Interval = TimeUtil.OneMinute,
        Open = o, High = h, Low = l, Close = c, Volume = vol, BuyVolume = buy,
    };

    public static Candle C5(int index, double o, double h, double l, double c, double vol = 100, double buy = 50) => new()
    {
        OpenTime = Min(index * 5),
        Interval = TimeUtil.FiveMinutes,
        Open = o, High = h, Low = l, Close = c, Volume = vol, BuyVolume = buy,
    };

    /// <summary>A 5m candle whose high and low are given and open/close sit in the middle.</summary>
    public static Candle Hl5(int index, double high, double low, double vol = 100) =>
        C5(index, (high + low) / 2, high, low, (high + low) / 2, vol, vol / 2);

    /// <summary>Flat 1m candles (price p) for minutes [from, to).</summary>
    public static List<Candle> Flat1m(int from, int to, double p, double vol = 10, double buy = 5)
    {
        var list = new List<Candle>();
        for (var m = from; m < to; m++) list.Add(C1(m, p, p, p, p, vol, buy));
        return list;
    }

    public static TradeEvent Trade(string symbol, DateTime t, double price, double qty, bool isBuyerMaker) =>
        new(symbol, t, price, qty, isBuyerMaker, t);
}
