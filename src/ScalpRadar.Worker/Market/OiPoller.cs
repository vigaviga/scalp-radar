using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Worker.Binance;
using ScalpRadar.Worker.Infrastructure;

namespace ScalpRadar.Worker.Market;

/// <summary>
/// Polls GET /fapi/v1/openInterest for every symbol once per Binance:OiPollSeconds, staggered evenly
/// across the interval so the requests never burst. Also logs REST weight usage once a minute.
/// </summary>
public sealed class OiPoller(
    IBinanceRestClient rest,
    RestGate gate,
    MarketChannel channel,
    UniverseRegistry registry,
    IOptions<BinanceOptions> options,
    TimeProvider time,
    ILogger<OiPoller> log) : BackgroundService
{
    private const int OpenInterestWeight = 1;
    private DateTime _lastWeightLog;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.OiPollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var symbols = registry.Symbols;
            if (symbols.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                continue;
            }

            var spacing = interval / symbols.Count;
            foreach (var symbol in symbols)
            {
                if (stoppingToken.IsCancellationRequested) break;
                var started = time.GetUtcNow();
                try
                {
                    var r = await gate.RunAsync(OpenInterestWeight,
                        c => rest.UsdFuturesApi.ExchangeData.GetOpenInterestAsync(symbol, c), stoppingToken);
                    if (r.Success)
                        channel.Post(new OiEvent(symbol, BinanceMapper.Utc(r.Data.Timestamp ?? time.GetUtcNow().UtcDateTime),
                            (double)r.Data.OpenInterest, time.GetUtcNow().UtcDateTime));
                    else
                        log.LogWarning("openInterest for {Symbol} failed: {Error}", symbol, r.Error);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "openInterest poll for {Symbol} threw", symbol);
                }

                LogWeight();
                var wait = spacing - (time.GetUtcNow() - started);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private void LogWeight()
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - _lastWeightLog < TimeSpan.FromMinutes(1)) return;
        _lastWeightLog = now;
        log.LogInformation("REST weight {Used}/{Cap} this minute (budget {Budget}, peak {Peak}), {Calls} calls so far",
            gate.UsedWeight, gate.Cap, gate.Budget, gate.PeakWeight, gate.Calls);
    }
}
