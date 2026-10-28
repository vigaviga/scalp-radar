namespace ScalpRadar.Core.Market;

/// <summary>
/// Estimates exchange time from event timestamps, so candles close on exchange time rather than the laptop clock.
/// Keeps a smoothed offset between exchange event time and local receive time.
/// </summary>
public sealed class ExchangeClock
{
    private const double Smoothing = 0.05;
    private double? _offsetMs;

    public void Observe(DateTime exchangeTime, DateTime receivedUtc)
    {
        var sample = (exchangeTime - receivedUtc).TotalMilliseconds;
        _offsetMs = _offsetMs is null ? sample : _offsetMs + Smoothing * (sample - _offsetMs);
    }

    public bool IsSynced => _offsetMs is not null;
    public TimeSpan Offset => TimeSpan.FromMilliseconds(_offsetMs ?? 0);
    public DateTime Now(DateTime localUtc) => localUtc + Offset;
}
