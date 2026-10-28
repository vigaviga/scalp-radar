using ScalpRadar.Worker.Infrastructure;

namespace ScalpRadar.Worker.Telegram;

public sealed class StatusCommand(AppClock clock) : ICommandHandler
{
    public string Command => "status";

    public Task<string> HandleAsync(string[] args, CancellationToken ct)
    {
        var up = clock.Uptime;
        return Task.FromResult($"ScalpRadar is running · uptime {(int)up.TotalHours}h {up.Minutes}m");
    }
}
