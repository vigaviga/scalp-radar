namespace ScalpRadar.Worker.Notifications;

/// <summary>Sends a plain-text message to me: Telegram normally, the console in dry-run mode.</summary>
public interface INotifier
{
    Task SendAsync(string text, CancellationToken ct = default);
}

/// <summary>Dry-run mode (Telegram:Enabled=false): messages print to the console and the log.</summary>
public sealed class ConsoleNotifier(ILogger<ConsoleNotifier> log) : INotifier
{
    public Task SendAsync(string text, CancellationToken ct = default)
    {
        log.LogInformation("[DRY RUN] Telegram message:\n{Text}", text);
        return Task.CompletedTask;
    }
}
