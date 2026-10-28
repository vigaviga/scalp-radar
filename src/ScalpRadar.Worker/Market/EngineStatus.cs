namespace ScalpRadar.Worker.Market;

public sealed record TopEntry(string Symbol, string Direction, int Score, int MustHaves, int Confirmations, string Passing);

/// <summary>An immutable snapshot the consumer publishes on each tick, read by /status and /top.</summary>
public sealed record EngineSnapshot(
    DateTime AtUtc,
    int SymbolsTracked,
    int SymbolsReady,
    double? OldestTradeAgeSeconds,
    string? OldestTradeSymbol,
    double EventsPerSecond,
    double MaxLagMs,
    long DroppedEvents,
    int Books,
    IReadOnlyList<TopEntry> Top);

public sealed class EngineStatus
{
    private volatile EngineSnapshot? _latest;
    public EngineSnapshot? Latest => _latest;
    public void Publish(EngineSnapshot s) => _latest = s;
}
