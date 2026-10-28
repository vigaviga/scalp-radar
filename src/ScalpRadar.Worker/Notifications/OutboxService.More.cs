using ScalpRadar.Core.Rules;

namespace ScalpRadar.Worker.Notifications;

public sealed partial class OutboxService
{
    private partial Task HandleMoreAsync(OutboxItem item, CancellationToken ct)
    {
        log.LogWarning("Unhandled outbox item {Item}", item.GetType().Name);
        return Task.CompletedTask;
    }

    private partial Task OnPeriodicAsync(DateTime now, CancellationToken ct) => Task.CompletedTask;

    partial void OnAlertSaved(AlertCandidate a, long id) { }
}
