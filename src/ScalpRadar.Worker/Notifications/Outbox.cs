using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Core.Outcomes;
using ScalpRadar.Core.Rules;
using ScalpRadar.Worker.Database;

namespace ScalpRadar.Worker.Notifications;

public abstract record OutboxItem;
public sealed record AlertItem(AlertCandidate Alert) : OutboxItem;
public sealed record NearMissItem(NearMissRecord NearMiss) : OutboxItem;
public sealed record LiquidationItem(ForceOrderEvent Liquidation) : OutboxItem;
public sealed record OutcomeItem(OutcomeSpec Spec, OutcomeResult Result) : OutboxItem;

/// <summary>A plain message (feed health, daily summary). Muted messages are logged instead of sent.</summary>
public sealed record NoticeItem(string Text) : OutboxItem;

internal sealed record FlushItem : OutboxItem;

/// <summary>Non-blocking hand-off from the market loop to the outbox service.</summary>
public sealed class Outbox(ILogger<Outbox> log)
{
    private readonly Channel<OutboxItem> _channel = Channel.CreateUnbounded<OutboxItem>(new UnboundedChannelOptions { SingleReader = true });

    internal ChannelReader<OutboxItem> Reader => _channel.Reader;

    public void Post(OutboxItem item)
    {
        if (!_channel.Writer.TryWrite(item))
            log.LogError("Outbox closed; dropped {Item}", item.GetType().Name);
    }

    internal void Complete() => _channel.Writer.TryComplete();
}

/// <summary>/mute and /unmute. Muted: alerts and notices are still saved and logged, just not sent.</summary>
public sealed class MuteState(TimeProvider time)
{
    private DateTime _mutedUntil;
    public DateTime? MutedUntil => _mutedUntil > time.GetUtcNow().UtcDateTime ? _mutedUntil : null;
    public void Mute(TimeSpan duration) => _mutedUntil = time.GetUtcNow().UtcDateTime + duration;
    public void Unmute() => _mutedUntil = DateTime.MinValue;
}

/// <summary>
/// The only place that writes to Postgres and sends alerts. Processes items one at a time: alerts get an id
/// from the database, then go through the flood guard. Liquidations are batched. On shutdown, everything
/// still queued is written before the process exits.
/// </summary>
public sealed partial class OutboxService(
    Outbox outbox,
    INotifier notifier,
    MuteState mute,
    AlertRepository alerts,
    NearMissRepository nearMisses,
    LiquidationRepository liquidations,
    OutcomeRepository outcomes,
    ReportRepository reports,
    AlertFormatter formatter,
    IOptions<RulesOptions> rules,
    IOptions<DatabaseOptions> dbOptions,
    IOptions<TelegramOptions> telegramOptions,
    TimeProvider time,
    ILogger<OutboxService> log) : BackgroundService
{
    private readonly FloodGuard<(Evaluation E, SignalMetrics M, long? Id)> _flood = new(rules.Value);
    private readonly List<ForceOrderEvent> _liqBuffer = [];
    private DateTime _lastLiqFlush = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flusher = FlushTimerAsync(stoppingToken);
        try
        {
            await foreach (var item in outbox.Reader.ReadAllAsync(stoppingToken))
                await HandleSafeAsync(item, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await flusher.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Graceful shutdown: write whatever is still queued, flush liquidations, send any held digest.
        outbox.Complete();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var drained = 0;
        while (outbox.Reader.TryRead(out var item))
        {
            await HandleSafeAsync(item, cts.Token);
            drained++;
        }
        await FlushLiquidationsAsync(cts.Token);
        var held = _flood.Drain();
        if (held.Count > 0) await SendAsync(AlertFormatter.FormatDigest(held, rules.Value.FloodWindowMinutes), cts.Token);
        log.LogInformation("Outbox flushed on shutdown ({Count} queued items written)", drained);
    }

    private async Task HandleSafeAsync(OutboxItem item, CancellationToken ct)
    {
        try
        {
            await HandleAsync(item, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(ex, "Outbox failed to handle {Item}", item.GetType().Name);
        }
    }

    private async Task HandleAsync(OutboxItem item, CancellationToken ct)
    {
        switch (item)
        {
            case AlertItem a: await OnAlertAsync(a.Alert, ct); break;
            case NearMissItem n: await nearMisses.InsertAsync(n.NearMiss, ct); break;
            case LiquidationItem l:
                _liqBuffer.Add(l.Liquidation);
                if (_liqBuffer.Count >= dbOptions.Value.LiquidationBatchSize) await FlushLiquidationsAsync(ct);
                break;
            case NoticeItem n: await SendAsync(n.Text, ct); break;
            case FlushItem: await OnFlushAsync(ct); break;
            default: await HandleMoreAsync(item, ct); break;
        }
    }

    private async Task OnAlertAsync(AlertCandidate a, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var sendNow = _flood.TryTakeSlot(now);

        long? id = null;
        try
        {
            id = await alerts.InsertAsync(a, sendNow ? "sent" : "digest", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not save alert {Symbol} {Direction}; sending it anyway", a.Evaluation.Symbol, a.Evaluation.Direction);
        }

        var text = formatter.Format(a.Evaluation, a.Metrics, a.Btc15Pct, id);
        log.LogInformation("ALERT #{Id} {Tier} {Direction} {Symbol} @ {Price} ({Delivery})\n{Text}",
            id, a.Evaluation.Tier, a.Evaluation.Direction, a.Evaluation.Symbol, a.Metrics.Price, sendNow ? "sent" : "held for digest", text);

        if (sendNow) await SendAsync(text, ct);
        else _flood.Hold((a.Evaluation, a.Metrics, id));
    }

    private async Task OnFlushAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (_liqBuffer.Count > 0 && now - _lastLiqFlush >= TimeSpan.FromSeconds(dbOptions.Value.FlushIntervalSeconds))
            await FlushLiquidationsAsync(ct);

        if (_flood.TakeDigest(now) is { } digest)
            await SendAsync(AlertFormatter.FormatDigest(digest, rules.Value.FloodWindowMinutes), ct);

        await OnPeriodicAsync(now, ct);
    }

    private async Task FlushLiquidationsAsync(CancellationToken ct)
    {
        _lastLiqFlush = time.GetUtcNow().UtcDateTime;
        if (_liqBuffer.Count == 0) return;
        var batch = _liqBuffer.ToList();
        _liqBuffer.Clear();
        try
        {
            await liquidations.InsertAsync(batch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not save {Count} liquidations", batch.Count);
        }
    }

    private async Task SendAsync(string text, CancellationToken ct)
    {
        if (mute.MutedUntil is { } until)
        {
            log.LogInformation("Muted until {Until:HH:mm} UTC; not sending:\n{Text}", until, text);
            return;
        }
        try
        {
            await notifier.SendAsync(text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Sending a message failed");
        }
    }

    private async Task FlushTimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) outbox.Post(new FlushItem());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    // Outcomes, daily summary and cleanup: OutboxService.More.cs.
    private partial Task HandleMoreAsync(OutboxItem item, CancellationToken ct);
    private partial Task OnPeriodicAsync(DateTime now, CancellationToken ct);
}
