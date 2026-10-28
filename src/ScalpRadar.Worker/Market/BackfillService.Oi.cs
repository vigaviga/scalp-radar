using Binance.Net.Enums;
using ScalpRadar.Core.Models;
using ScalpRadar.Worker.Binance;

namespace ScalpRadar.Worker.Market;

public sealed partial class BackfillService
{
    // openInterestHist has request weight 0 but its own IP limit (1000 per 5 min), tracked separately.
    private const int OiHistWeight = 0;
    private SlidingWindowLimiter? _oiHistLimiter;

    private SlidingWindowLimiter OiHistLimiter =>
        _oiHistLimiter ??= new SlidingWindowLimiter(_o.OiHistMaxRequestsPer5Min, TimeSpan.FromMinutes(5), time);

    /// <summary>Last 24h of OI at 5m resolution (288 points), one request.</summary>
    private async partial Task<IReadOnlyList<OiSample>> FetchOiHistoryAsync(string symbol, CancellationToken ct)
    {
        var points = _o.Backfill5mHours * 12;
        await OiHistLimiter.WaitAsync(ct);
        var result = await gate.RunAsync(OiHistWeight,
            c => rest.UsdFuturesApi.ExchangeData.GetOpenInterestHistoryAsync(symbol, PeriodInterval.FiveMinutes, limit: points, ct: c), ct);
        if (!result.Success)
        {
            log.LogWarning("openInterestHist for {Symbol} failed: {Error}. OI history starts from live polls", symbol, result.Error);
            return [];
        }

        return result.Data
            .Select(h => new OiSample(BinanceMapper.Utc(h.Timestamp ?? DateTime.MinValue), (double)h.SumOpenInterest, (double)h.SumOpenInterestValue))
            .Where(s => s.Time > DateTime.MinValue)
            .OrderBy(s => s.Time)
            .ToList();
    }
}
