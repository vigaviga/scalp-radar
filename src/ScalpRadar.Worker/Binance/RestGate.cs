using System.Net;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Worker.Binance;

/// <summary>
/// Every Binance REST call goes through here. It reads X-MBX-USED-WEIGHT-1M from each response and keeps
/// usage under Binance:RestWeightBudgetFraction of the cap. On 429 it backs off; on 418 it stops all REST calls
/// until the ban expires. Calls are serialised, which keeps the accounting exact at our low request rate.
/// </summary>
public sealed class RestGate(IOptions<BinanceOptions> options, TimeProvider time, ILogger<RestGate> log)
{
    private const string WeightHeader = "X-MBX-USED-WEIGHT-1M";
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly BinanceOptions _o = options.Value;

    private int _cap = options.Value.RestWeightCapFallback;
    private int _usedWeight;
    private DateTime _usedMinute;
    private DateTime _pausedUntil;
    private int _peakWeight;
    private long _calls;

    public int Cap => _cap;
    public int Budget => (int)(_cap * _o.RestWeightBudgetFraction);
    public int UsedWeight => CurrentMinute() == _usedMinute ? _usedWeight : 0;
    public int PeakWeight => _peakWeight;
    public long Calls => Interlocked.Read(ref _calls);
    public DateTime? PausedUntil => _pausedUntil > Now ? _pausedUntil : null;

    /// <summary>Sets the cap from exchangeInfo.rateLimits (REQUEST_WEIGHT per minute).</summary>
    public void SetCap(int cap)
    {
        if (cap > 0 && cap != _cap)
        {
            log.LogInformation("REST weight cap is {Cap}/min; budget {Budget}/min", cap, (int)(cap * _o.RestWeightBudgetFraction));
            _cap = cap;
        }
    }

    public async Task<T> RunAsync<T>(int weight, Func<CancellationToken, Task<T>> call, CancellationToken ct) where T : HttpResult
    {
        await _lock.WaitAsync(ct);
        try
        {
            await WaitForCapacityAsync(weight, ct);
            var result = await call(ct);
            Interlocked.Increment(ref _calls);
            Record(result, weight);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// For REST calls made inside Binance.Net (the order book snapshot): waits for budget and counts the weight
    /// until the next response header corrects it.
    /// </summary>
    public async Task ReserveAsync(int weight, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await WaitForCapacityAsync(weight, ct);
            var minute = CurrentMinute();
            _usedWeight = (minute == _usedMinute ? _usedWeight : 0) + weight;
            _usedMinute = minute;
            if (_usedWeight > _peakWeight) _peakWeight = _usedWeight;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WaitForCapacityAsync(int weight, CancellationToken ct)
    {
        while (true)
        {
            var now = Now;
            if (_pausedUntil > now)
            {
                await Task.Delay(_pausedUntil - now, time, ct);
                continue;
            }

            if (UsedWeight + weight > Budget)
            {
                var nextMinute = CurrentMinute().AddMinutes(1).AddMilliseconds(500);
                log.LogDebug("REST weight {Used}+{Weight} would pass the budget {Budget}; waiting {Wait:F0}s",
                    UsedWeight, weight, Budget, (nextMinute - now).TotalSeconds);
                await Task.Delay(nextMinute - now, time, ct);
                continue;
            }

            return;
        }
    }

    private void Record(HttpResult result, int weight)
    {
        var minute = CurrentMinute();
        if (TryReadUsedWeight(result, out var used))
        {
            _usedWeight = used;
        }
        else
        {
            // No header (e.g. network error): count our own estimate.
            _usedWeight = (minute == _usedMinute ? _usedWeight : 0) + weight;
        }
        _usedMinute = minute;
        if (_usedWeight > _peakWeight) _peakWeight = _usedWeight;

        switch (result.ResponseStatusCode)
        {
            case HttpStatusCode.TooManyRequests:
            {
                var wait = RetryAfter(result) ?? TimeSpan.FromSeconds(_o.Default429BackoffSeconds);
                _pausedUntil = Now + wait;
                log.LogWarning("Binance returned 429 (rate limit). Pausing all REST calls for {Seconds:F0}s", wait.TotalSeconds);
                break;
            }
            case (HttpStatusCode)418:
            {
                var wait = RetryAfter(result) ?? TimeSpan.FromSeconds(_o.Default418BanSeconds);
                _pausedUntil = Now + wait;
                log.LogError("Binance returned 418: this IP is banned. Stopping all REST calls until {Until:u}", _pausedUntil);
                break;
            }
        }
    }

    private static bool TryReadUsedWeight(HttpResult result, out int used)
    {
        used = 0;
        return result.ResponseHeaders is { } headers
               && headers.TryGetValues(WeightHeader, out var values)
               && int.TryParse(values.FirstOrDefault(), out used);
    }

    private static TimeSpan? RetryAfter(HttpResult result)
    {
        var retry = result.ResponseHeaders?.RetryAfter;
        if (retry?.Delta is { } delta) return delta;
        if (retry?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;
    private DateTime CurrentMinute()
    {
        var now = Now; // read the clock once, or the two reads can straddle a tick
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }
}
