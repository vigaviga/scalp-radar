namespace ScalpRadar.Worker.Infrastructure;

/// <summary>Process start time and uptime. Market logic uses exchange timestamps, not this clock.</summary>
public sealed class AppClock(TimeProvider time)
{
    public DateTime StartedUtc { get; } = time.GetUtcNow().UtcDateTime;
    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    public TimeSpan Uptime => UtcNow - StartedUtc;
}
