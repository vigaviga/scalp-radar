using ScalpRadar.Worker.Binance;
using ScalpRadar.Worker.Infrastructure;
using ScalpRadar.Worker.Market;

namespace ScalpRadar.Worker.Telegram;

public sealed class StatusCommand(AppClock clock, EngineStatus engine, StreamIngestor streams, RestGate gate) : ICommandHandler
{
    public string Command => "status";

    public Task<string> HandleAsync(string[] args, CancellationToken ct)
    {
        var up = clock.Uptime;
        var snap = engine.Latest;
        var lines = new List<string>
        {
            $"ScalpRadar · uptime {(int)up.TotalDays}d {up.Hours}h {up.Minutes}m",
            snap is null
                ? "Symbols: starting…"
                : $"Symbols: {snap.SymbolsTracked} tracked · {snap.SymbolsReady} backfilled · {snap.Books} order books",
            $"Streams: {streams.ConnectedStreams}/{streams.TotalStreams} connected",
            snap?.OldestTradeAgeSeconds is { } age && !double.IsInfinity(age)
                ? $"Oldest trade: {age:F0}s ago ({snap.OldestTradeSymbol})"
                : "Oldest trade: n/a",
            $"REST weight: {gate.UsedWeight}/{gate.Cap} this minute · peak {gate.PeakWeight} · budget {gate.Budget}"
                + (gate.PausedUntil is { } p ? $" · PAUSED until {p:HH:mm:ss} UTC" : ""),
        };
        if (snap is not null)
            lines.Add($"Events: {snap.EventsPerSecond:F0}/s · max lag {snap.MaxLagMs:F0} ms · dropped {snap.DroppedEvents}");
        return Task.FromResult(string.Join('\n', lines));
    }
}
