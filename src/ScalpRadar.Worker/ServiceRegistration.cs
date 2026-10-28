using ScalpRadar.Core.Options;
using ScalpRadar.Worker.Database;
using ScalpRadar.Worker.Binance;
using ScalpRadar.Worker.Infrastructure;
using ScalpRadar.Worker.Market;
using ScalpRadar.Worker.Notifications;
using ScalpRadar.Worker.Telegram;
using Telegram.Bot;

namespace ScalpRadar.Worker;

public static class ServiceRegistration
{
    public static IServiceCollection AddScalpRadar(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<UniverseOptions>(config.GetSection(UniverseOptions.Section));
        services.Configure<BinanceOptions>(config.GetSection(BinanceOptions.Section));
        services.Configure<EngineOptions>(config.GetSection(EngineOptions.Section));
        services.Configure<StateOptions>(config.GetSection(StateOptions.Section));
        services.Configure<HeatmapOptions>(config.GetSection(HeatmapOptions.Section));
        services.Configure<OrderBookOptions>(config.GetSection(OrderBookOptions.Section));
        services.Configure<RulesOptions>(config.GetSection(RulesOptions.Section));
        services.Configure<TelegramOptions>(config.GetSection(TelegramOptions.Section));
        services.Configure<OutcomeOptions>(config.GetSection(OutcomeOptions.Section));
        services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.Section));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AppClock>();
        services.AddSingleton<DbConnectionFactory>();
        services.AddSingleton<DatabaseMigrator>();

        // Binance: public market data only. No API key is configured, so no signed endpoint can be called.
        var binance = config.GetSection(BinanceOptions.Section).Get<BinanceOptions>() ?? new BinanceOptions();
        services.AddBinance(o =>
        {
            o.Socket.SocketSubscriptionsCombineTarget = binance.SocketSubscriptionsCombineTarget;
            o.Socket.ReconnectInterval = TimeSpan.FromSeconds(5);
            o.Rest.RateLimiterEnabled = true;
        });
        services.AddSingleton<RestGate>();
        services.AddSingleton<MarketChannel>();
        services.AddSingleton<EngineStatus>();
        services.AddSingleton<UniverseRegistry>();
        services.AddSingleton<StreamIngestor>();
        services.AddSingleton<BackfillService>();
        services.AddHostedService(sp => sp.GetRequiredService<BackfillService>());
        services.AddHostedService<MarketStateLoop>();
        services.AddHostedService<UniverseService>();
        services.AddHostedService<TickTimer>();

        var telegram = config.GetSection(TelegramOptions.Section).Get<TelegramOptions>() ?? new TelegramOptions();
        if (telegram.Enabled)
        {
            if (string.IsNullOrWhiteSpace(telegram.BotToken) || telegram.ChatId == 0)
                throw new InvalidOperationException(
                    "Telegram is enabled but Telegram:BotToken or Telegram:ChatId is missing. See the README (Telegram setup), or set Telegram:Enabled=false for a dry run.");
            services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(telegram.BotToken));
            services.AddSingleton<INotifier, TelegramNotifier>();
            services.AddHostedService<TelegramCommandService>();
        }
        else
        {
            services.AddSingleton<INotifier, ConsoleNotifier>();
        }

        services.AddSingleton<ICommandHandler, StatusCommand>();
        services.AddHostedService<StartupNotice>();
        return services;
    }
}

/// <summary>Sends one "started" message so I can see the bot (or the dry run) is alive.</summary>
public sealed class StartupNotice(INotifier notifier, ILogger<StartupNotice> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await notifier.SendAsync("🟢 ScalpRadar started", stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not send the startup message");
        }
    }
}
