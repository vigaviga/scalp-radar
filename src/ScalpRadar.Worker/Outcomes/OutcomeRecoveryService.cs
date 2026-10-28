using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Core.Outcomes;
using ScalpRadar.Worker.Binance;
using ScalpRadar.Worker.Database;
using ScalpRadar.Worker.Market;
using ScalpRadar.Worker.Notifications;

namespace ScalpRadar.Worker.Outcomes;

/// <summary>
/// Finishes outcomes that live tracking could not: alerts from before a restart, symbols that left the
/// universe, or trackers that saw no trade after their 60 minutes. Each is resolved from 1m klines.
/// The result goes through the outbox like a live outcome.
/// </summary>
public sealed class OutcomeRecoveryService(
    OutcomeRepository repository,
    IBinanceRestClient rest,
    RestGate gate,
    Outbox outbox,
    IOptions<OutcomeOptions> options,
    TimeProvider time,
    ILogger<OutcomeRecoveryService> log) : BackgroundService
{
    // 61 one-minute candles cover the alert minute through the end; under 100 klines cost weight 1.
    private const int KlineLimit = 62;

    // Alerts already handed to the outbox. Forgotten after the grace period, so a failed save is retried.
    private readonly Dictionary<Guid, DateTime> _posted = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(o.RecoveryIntervalSeconds), time);
        try
        {
            do
            {
                try
                {
                    await RecoverAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Outcome recovery pass failed; will retry");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var grace = TimeSpan.FromMinutes(o.FinalizeGraceMinutes);
        foreach (var (uid, at) in _posted.ToList())
            if (now - at > grace) _posted.Remove(uid);

        var before = now - OutcomeTracker.Duration - grace;
        var pending = (await repository.UnresolvedAsync(before, o.RecoveryBatchSize + _posted.Count, ct))
            .Where(a => !_posted.ContainsKey(a.Spec.Uid))
            .Take(o.RecoveryBatchSize)
            .ToList();
        if (pending.Count == 0) return;

        log.LogInformation("Resolving {Count} unfinished outcome(s) from 1m klines", pending.Count);
        foreach (var alert in pending)
        {
            var s = alert.Spec;
            var from = TimeUtil.Floor(s.StartUtc, TimeUtil.OneMinute);
            var result = await gate.RunAsync(BackfillService.KlineWeight(KlineLimit),
                c => rest.UsdFuturesApi.ExchangeData.GetKlinesAsync(s.Symbol, KlineInterval.OneMinute, startTime: from, limit: KlineLimit, ct: c), ct);
            if (!result.Success)
            {
                log.LogWarning("Klines for alert #{Id} {Symbol} failed: {Error}; will retry", alert.Id, s.Symbol, result.Error);
                continue;
            }

            var candles = result.Data.Select(k => BinanceMapper.ToCandle(k, TimeUtil.OneMinute)).ToList();
            if (candles.Count == 0)
                log.LogWarning("No klines for alert #{Id} {Symbol}; saving it with no price data", alert.Id, s.Symbol);

            outbox.Post(new OutcomeItem(s, OutcomeTracker.FromCandles(s, candles, now)));
            _posted[s.Uid] = now;
        }
    }
}
