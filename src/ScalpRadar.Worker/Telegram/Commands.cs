using System.Globalization;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Options;
using ScalpRadar.Core.Reports;
using ScalpRadar.Worker.Database;
using ScalpRadar.Worker.Market;
using ScalpRadar.Worker.Notifications;

namespace ScalpRadar.Worker.Telegram;

public sealed class TopCommand(EngineStatus engine, IOptions<TelegramOptions> options) : ICommandHandler
{
    public string Command => "top";

    public Task<string> HandleAsync(string[] args, CancellationToken ct) =>
        Task.FromResult(ReportFormatter.Top(engine.Latest?.Top ?? [], options.Value.TopCount));
}

public sealed class StatsCommand(ReportRepository reports, IOptions<TelegramOptions> options, TimeProvider time) : ICommandHandler
{
    public string Command => "stats";

    public async Task<string> HandleAsync(string[] args, CancellationToken ct)
    {
        var o = options.Value;
        var days = o.DefaultStatsDays;
        if (args.Length > 0 && (!int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out days) || days < 1 || days > o.MaxStatsDays))
            return $"Usage: /stats [days], days from 1 to {o.MaxStatsDays} (default {o.DefaultStatsDays})";

        var now = time.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-days);
        return ReportFormatter.Stats(await reports.StatsAsync(since, now.AddMinutes(1), ct), days, since);
    }
}

public sealed class MuteCommand(MuteState mute, IOptions<TelegramOptions> options) : ICommandHandler
{
    public string Command => "mute";

    public Task<string> HandleAsync(string[] args, CancellationToken ct)
    {
        var max = options.Value.MaxMuteMinutes;
        if (args.Length != 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes < 1 || minutes > max)
            return Task.FromResult($"Usage: /mute <minutes>, from 1 to {max}");

        mute.Mute(TimeSpan.FromMinutes(minutes));
        return Task.FromResult($"🔇 Muted for {minutes} min (until {mute.MutedUntil:HH:mm} UTC). Alerts are still saved and tracked. /unmute to undo.");
    }
}

public sealed class UnmuteCommand(MuteState mute) : ICommandHandler
{
    public string Command => "unmute";

    public Task<string> HandleAsync(string[] args, CancellationToken ct)
    {
        var was = mute.MutedUntil is not null;
        mute.Unmute();
        return Task.FromResult(was ? "🔔 Unmuted." : "🔔 Not muted.");
    }
}
