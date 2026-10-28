using ScalpRadar.Worker.Binance;
using ScalpRadar.Worker.Database;
using ScalpRadar.Worker.Infrastructure;
using ScalpRadar.Worker.Market;
using ScalpRadar.Worker.Notifications;

namespace ScalpRadar.Worker.Telegram;

public sealed class StatusCommand(
    AppClock clock,
    EngineStatus engine,
    StreamIngestor streams,
    RestGate gate,
    ReportRepository reports,
    MuteState mute,
    TimeProvider time,
    ILogger<StatusCommand> log) : ICommandHandler
{
    public string Command => "status";

    public async Task<string> HandleAsync(string[] args, CancellationToken ct)
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
        lines.Add($"Alerts today: {await AlertsTodayAsync(ct)}");
        if (mute.MutedUntil is { } until) lines.Add($"🔇 Muted until {until:HH:mm} UTC");
        return string.Join('\n', lines);
    }

    private async Task<string> AlertsTodayAsync(CancellationToken ct)
    {
        try
        {
            return (await reports.AlertsSinceAsync(time.GetUtcNow().UtcDateTime.Date, ct)).ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "/status could not count today's alerts");
            return "n/a (database error)";
        }
    }
}
