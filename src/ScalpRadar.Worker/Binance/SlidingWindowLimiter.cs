namespace ScalpRadar.Worker.Binance;

/// <summary>
/// Allows at most N calls per window. Used for endpoints with their own IP limit outside the weight system,
/// such as /futures/data/openInterestHist (1000 requests per 5 minutes per IP).
/// </summary>
public sealed class SlidingWindowLimiter(int maxCalls, TimeSpan window, TimeProvider time)
{
    private readonly Queue<DateTime> _calls = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WaitAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = time.GetUtcNow().UtcDateTime;
                while (_calls.Count > 0 && now - _calls.Peek() >= window) _calls.Dequeue();
                if (_calls.Count < maxCalls)
                {
                    _calls.Enqueue(now);
                    return;
                }
                await Task.Delay(_calls.Peek() + window - now, time, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
