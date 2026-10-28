namespace ScalpRadar.Core.Models;

/// <summary>An OHLC candle with taker buy/sell split. Times are exchange times (UTC).</summary>
public sealed class Candle
{
    public DateTime OpenTime { get; init; }
    public TimeSpan Interval { get; init; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Volume { get; set; }
    public double BuyVolume { get; set; }
    public int Trades { get; set; }

    /// <summary>Cumulative volume delta at this candle's close.</summary>
    public double Cvd { get; set; }

    /// <summary>True when the candle is missing trades (stream started mid-minute or reconnected). A REST backfill replaces it.</summary>
    public bool IsPartial { get; set; }

    public DateTime CloseTime => OpenTime + Interval;
    public double SellVolume => Volume - BuyVolume;
    public double Delta => BuyVolume - SellVolume;
    public bool IsGreen => Close > Open;
    public bool IsRed => Close < Open;
    public double Typical => (High + Low + Close) / 3.0;

    public static Candle FromTrade(DateTime openTime, TimeSpan interval, double price, double qty, bool isBuyerMaker) => new()
    {
        OpenTime = openTime,
        Interval = interval,
        Open = price,
        High = price,
        Low = price,
        Close = price,
        Volume = qty,
        BuyVolume = isBuyerMaker ? 0 : qty,
        Trades = 1,
    };

    /// <summary>A no-trade minute: flat at the previous close, zero volume.</summary>
    public static Candle Flat(DateTime openTime, TimeSpan interval, double price) => new()
    {
        OpenTime = openTime,
        Interval = interval,
        Open = price,
        High = price,
        Low = price,
        Close = price,
    };

    public override string ToString() => $"{OpenTime:HH:mm} O{Open} H{High} L{Low} C{Close} V{Volume} B{BuyVolume}";
}

public static class TimeUtil
{
    public static DateTime Floor(DateTime t, TimeSpan interval) =>
        new(t.Ticks - t.Ticks % interval.Ticks, DateTimeKind.Utc);

    public static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
}
