using System.Threading.Channels;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Worker.Binance;
using ScalpRadar.Worker.Infrastructure;

namespace ScalpRadar.Worker.Market;

/// <summary>
/// Fetches REST klines (and OI history) for new symbols and for gaps after a reconnect, one symbol at a time,
/// spaced out so the startup backfill does not burst. Results are posted to the consumer as BackfillEvents.
/// </summary>
public sealed partial class BackfillService(
    IBinanceRestClient rest,
    RestGate gate,
    MarketChannel channel,
    IOptions<BinanceOptions> options,
    IOptions<UniverseOptions> universe,
    TimeProvider time,
    ILogger<BackfillService> log) : BackgroundService
{
    private sealed record Request(string Symbol, DateTime? GapFrom, int Attempt = 0);

    private const int MaxKlineLimit = 1500;
    private const int MaxAttempts = 3;

    private readonly Channel<Request> _queue = Channel.CreateUnbounded<Request>();
    private readonly BinanceOptions _o = options.Value;
    private int _pending;

    public int Pending => Volatile.Read(ref _pending);

    public void RequestFull(IEnumerable<string> symbols)
    {
        foreach (var s in symbols) Enqueue(new Request(s, null));
    }

    public void RequestGap(string symbol, DateTime from) => Enqueue(new Request(symbol, from));

    private void Enqueue(Request r)
    {
        Interlocked.Increment(ref _pending);
        _queue.Writer.TryWrite(r);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Spread a full startup backfill over Binance:BackfillSpreadSeconds.
        var spacing = TimeSpan.FromMilliseconds(Math.Max(_o.BackfillMinDelayMs,
            _o.BackfillSpreadSeconds * 1000.0 / Math.Max(1, universe.Value.Size)));

        await foreach (var req in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            Interlocked.Decrement(ref _pending);
            try
            {
                var e = await FetchAsync(req, stoppingToken);
                if (e is not null)
                {
                    await channel.PostAsync(e, stoppingToken);
                    log.LogDebug("Backfilled {Symbol} ({Kind}): {N1} x 1m, {N5} x 5m, {Oi} OI points",
                        req.Symbol, req.GapFrom is null ? "full" : "gap", e.Klines1m.Count, e.Klines5m.Count, e.OiHistory.Count);
                }
                else if (req.Attempt + 1 < MaxAttempts)
                {
                    _ = RetryLaterAsync(req with { Attempt = req.Attempt + 1 }, stoppingToken);
                }
                else
                {
                    log.LogError("Backfill for {Symbol} failed {Max} times; giving up until the next request", req.Symbol, MaxAttempts);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Backfill for {Symbol} threw", req.Symbol);
            }

            await Task.Delay(spacing, time, stoppingToken);
        }
    }

    private async Task RetryLaterAsync(Request r, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(15 * r.Attempt), time, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!ct.IsCancellationRequested) Enqueue(r);
    }

    private async Task<BackfillEvent?> FetchAsync(Request req, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        IReadOnlyList<Candle>? k1, k5;
        IReadOnlyList<OiSample> oi = [];

        if (req.GapFrom is { } from)
        {
            var minutes = (int)Math.Ceiling((now - from).TotalMinutes) + 2;
            k1 = await KlinesAsync(req.Symbol, KlineInterval.OneMinute, TimeUtil.OneMinute, from, Math.Min(MaxKlineLimit, minutes), ct);
            var from5 = TimeUtil.Floor(from, TimeUtil.FiveMinutes);
            k5 = await KlinesAsync(req.Symbol, KlineInterval.FiveMinutes, TimeUtil.FiveMinutes, from5,
                Math.Min(MaxKlineLimit, (int)Math.Ceiling((now - from5).TotalMinutes / 5) + 2), ct);
        }
        else
        {
            k1 = await KlinesAsync(req.Symbol, KlineInterval.OneMinute, TimeUtil.OneMinute, null, _o.Backfill1mHours * 60 + 1, ct);
            k5 = await KlinesAsync(req.Symbol, KlineInterval.FiveMinutes, TimeUtil.FiveMinutes, null, _o.Backfill5mHours * 12 + 1, ct);
            oi = await FetchOiHistoryAsync(req.Symbol, ct);
        }

        if (k1 is null || k5 is null) return null;
        return new BackfillEvent(req.Symbol, k1, k5, oi, IsGap: req.GapFrom is not null);
    }

    private async Task<IReadOnlyList<Candle>?> KlinesAsync(string symbol, KlineInterval interval, TimeSpan span, DateTime? from, int limit, CancellationToken ct)
    {
        var result = await gate.RunAsync(KlineWeight(limit),
            c => rest.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, startTime: from, limit: limit, ct: c), ct);
        if (!result.Success)
        {
            log.LogWarning("Klines {Interval} for {Symbol} failed: {Error}", interval, symbol, result.Error);
            return null;
        }
        return result.Data.Select(k => BinanceMapper.ToCandle(k, span)).ToList();
    }

    /// <summary>Kline request weight by limit, per the Binance docs.</summary>
    public static int KlineWeight(int limit) => limit switch
    {
        < 100 => 1,
        < 500 => 2,
        <= 1000 => 5,
        _ => 10,
    };

    // Filled in by the OI part (milestone 3).
    private partial Task<IReadOnlyList<OiSample>> FetchOiHistoryAsync(string symbol, CancellationToken ct);
}
