using Microsoft.Extensions.Options;
using ScalpRadar.Core.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ScalpRadar.Worker.Telegram;

/// <summary>Handles one Telegram command, such as /status. Returns the reply text.</summary>
public interface ICommandHandler
{
    string Command { get; }
    Task<string> HandleAsync(string[] args, CancellationToken ct);
}

/// <summary>Long-polls Telegram for commands. Only the configured chat id is answered; everyone else is ignored.</summary>
public sealed class TelegramCommandService(
    ITelegramBotClient bot,
    IEnumerable<ICommandHandler> handlers,
    IOptions<TelegramOptions> options,
    ILogger<TelegramCommandService> log) : BackgroundService
{
    private readonly Dictionary<string, ICommandHandler> _handlers =
        handlers.ToDictionary(h => h.Command, StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        try
        {
            await bot.DeleteWebhook(cancellationToken: stoppingToken); // long polling and webhooks are exclusive
            var me = await bot.GetMe(stoppingToken);
            log.LogInformation("Telegram connected as @{Bot}; answering commands from chat {ChatId} only", me.Username, o.ChatId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Telegram startup check failed; will keep polling");
        }

        int? offset = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdates(offset, timeout: o.PollTimeoutSeconds,
                    allowedUpdates: [UpdateType.Message], cancellationToken: stoppingToken);
                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    await HandleAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Telegram polling failed; retrying in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task HandleAsync(Update update, CancellationToken ct)
    {
        var msg = update.Message;
        if (msg?.Text is not { Length: > 0 } text || !text.StartsWith('/')) return;
        if (msg.Chat.Id != options.Value.ChatId)
        {
            log.LogWarning("Ignored command from unknown chat {ChatId}", msg.Chat.Id);
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].TrimStart('/').Split('@')[0]; // "/status@MyBot" -> "status"
        string reply;
        if (_handlers.TryGetValue(command, out var handler))
        {
            try
            {
                reply = await handler.HandleAsync(parts[1..], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Command /{Command} failed", command);
                reply = $"/{command} failed: {ex.Message}";
            }
        }
        else
        {
            reply = "Commands: " + string.Join(" ", _handlers.Keys.Order().Select(k => "/" + k));
        }

        try
        {
            await bot.SendMessage(msg.Chat.Id, reply, linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not send the reply to /{Command}", command);
        }
    }
}
