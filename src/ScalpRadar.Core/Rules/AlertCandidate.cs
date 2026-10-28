namespace ScalpRadar.Core.Rules;

/// <summary>An alert decided by the consumer, on its way to the database and Telegram.</summary>
public sealed record AlertCandidate(
    Guid Uid,
    DateTime CreatedUtc,
    Evaluation Evaluation,
    SignalMetrics Metrics,
    double? Btc15Pct,
    string ConfigHash);

public sealed record NearMissRecord(DateTime CreatedUtc, Evaluation Evaluation, SignalMetrics Metrics, string ConfigHash);
