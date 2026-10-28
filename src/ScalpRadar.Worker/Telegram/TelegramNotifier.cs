using Microsoft.Extensions.Options;
using ScalpRadar.Core.Options;
using ScalpRadar.Worker.Notifications;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace ScalpRadar.Worker.Telegram;

public sealed class TelegramNotifier(ITelegramBotClient bot, IOptions<TelegramOptions> options, ILogger<TelegramNotifier> log) : INotifier
{
    private const int MaxAttempts = 3;

    public async Task SendAsync(string text, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await bot.SendMessage(new ChatId(options.Value.ChatId), text,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < MaxAttempts)
            {
                log.LogWarning(ex, "Telegram send failed (attempt {Attempt}/{Max}), retrying", attempt, MaxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }
}
