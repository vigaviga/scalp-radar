using ScalpRadar.Core.Outcomes;
using ScalpRadar.Core.Reports;
using ScalpRadar.Core.Rules;

namespace ScalpRadar.Worker.Notifications;

// Outcomes, the daily summary at 00:00 UTC and the daily liquidation cleanup.
public sealed partial class OutboxService
{
    private DateOnly? _summaryDay;

    private async partial Task HandleMoreAsync(OutboxItem item, CancellationToken ct)
    {
        switch (item)
        {
            case OutcomeItem o:
                await SaveOutcomeAsync(o.Spec, o.Result, ct);
                break;
            default:
                log.LogWarning("Unhandled outbox item {Item}", item.GetType().Name);
                break;
        }
    }

    private async Task SaveOutcomeAsync(OutcomeSpec s, OutcomeResult r, CancellationToken ct)
    {
        var saved = await outcomes.InsertAsync(s.Uid, r, ct);
        log.LogInformation(
            "Outcome {Direction} {Symbol} (entry {Entry}): first hit {FirstHit}, MFE {Mfe:F2}%, MAE {Mae:F2}%, +5m {P5} +15m {P15} +60m {P60}{Note}",
            s.Direction, s.Symbol, s.Entry, r.FirstHit, r.MfePct, r.MaePct, r.Price5m, r.Price15m, r.Price60m,
            saved ? "" : " (not saved: alert row missing or already resolved)");
    }

    private async partial Task OnPeriodicAsync(DateTime now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now);
        if (_summaryDay is null)
        {
            _summaryDay = today; // the first summary goes out at the next 00:00 UTC
            return;
        }
        if (today <= _summaryDay) return;

        var day = _summaryDay.Value;
        _summaryDay = today;
        await SendDailySummaryAsync(day, ct);
        await CleanUpLiquidationsAsync(now, ct);
    }

    private async Task SendDailySummaryAsync(DateOnly day, CancellationToken ct)
    {
        try
        {
            var summary = await reports.DailyAsync(day, telegramOptions.Value.SummaryTopFailed, ct);
            var text = ReportFormatter.Daily(summary);
            log.LogInformation("Daily summary\n{Text}", text);
            await SendAsync(text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Daily summary for {Day} failed", day);
        }
    }

    private async Task CleanUpLiquidationsAsync(DateTime now, CancellationToken ct)
    {
        try
        {
            var cutoff = now.AddDays(-dbOptions.Value.LiquidationRetentionDays);
            var deleted = await liquidations.DeleteOlderThanAsync(cutoff, ct);
            log.LogInformation("Deleted {Count} liquidations older than {Cutoff:yyyy-MM-dd}", deleted, cutoff);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Liquidation cleanup failed");
        }
    }
}
