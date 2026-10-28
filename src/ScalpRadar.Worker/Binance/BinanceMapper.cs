using Binance.Net.Interfaces;
using ScalpRadar.Core.Models;

namespace ScalpRadar.Worker.Binance;

public static class BinanceMapper
{
    /// <summary>REST kline → Candle. Delta comes from the taker buy base volume field.</summary>
    public static Candle ToCandle(IBinanceKline k, TimeSpan interval) => new()
    {
        OpenTime = DateTime.SpecifyKind(k.OpenTime, DateTimeKind.Utc),
        Interval = interval,
        Open = (double)k.OpenPrice,
        High = (double)k.HighPrice,
        Low = (double)k.LowPrice,
        Close = (double)k.ClosePrice,
        Volume = (double)k.Volume,
        BuyVolume = (double)k.TakerBuyBaseVolume,
        Trades = k.TradeCount,
    };

    public static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
