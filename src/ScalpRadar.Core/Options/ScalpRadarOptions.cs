namespace ScalpRadar.Core.Options;

// Every threshold the bot uses lives here and is bound from appsettings.json.
// Names ending in "Pct" are percentages (1.0 means 1%). Shares and ratios are fractions (0.60 means 60%).

public sealed class UniverseOptions
{
    public const string Section = "Universe";
    public int Size { get; set; } = 100;
    public double MinQuoteVolumeUsd { get; set; } = 20_000_000;
    public int MinListingAgeDays { get; set; } = 3;
    public string QuoteAsset { get; set; } = "USDT";
    public List<string> ExcludedSymbols { get; set; } = ["USDCUSDT", "FDUSDUSDT", "TUSDUSDT", "USDPUSDT", "BUSDUSDT", "DAIUSDT", "USD1USDT", "RLUSDUSDT", "USDEUSDT"];
    public int RefreshMinutes { get; set; } = 60;
    public int DefaultFundingIntervalHours { get; set; } = 8;
    public string BenchmarkSymbol { get; set; } = "BTCUSDT";
}

public sealed class BinanceOptions
{
    public const string Section = "Binance";
    public int MarkPriceUpdateMs { get; set; } = 3000;
    public int OiPollSeconds { get; set; } = 30;
    public int Backfill1mHours { get; set; } = 4;
    public int Backfill5mHours { get; set; } = 24;
    public int BackfillSpreadSeconds { get; set; } = 60;
    public int BackfillMinDelayMs { get; set; } = 250;
    public double RestWeightBudgetFraction { get; set; } = 0.5;
    public int RestWeightCapFallback { get; set; } = 2400;
    public int Default429BackoffSeconds { get; set; } = 60;
    public int Default418BanSeconds { get; set; } = 300;
    public int OiHistMaxRequestsPer5Min { get; set; } = 500;
    public int StreamSubscribesPerSecond { get; set; } = 4;
    public int StreamSubscribeBatchSize { get; set; } = 10;
    public int SocketSubscriptionsCombineTarget { get; set; } = 50;
}

public sealed class EngineOptions
{
    public const string Section = "Engine";
    public int TickSeconds { get; set; } = 10;
    public int ChannelCapacity { get; set; } = 200_000;
    public double LagWarnSeconds { get; set; } = 2;
    public int LagLogSeconds { get; set; } = 60;
    public int CandleCloseGraceMs { get; set; } = 1500;
}

public sealed class StateOptions
{
    public const string Section = "State";
    public int Keep1mHours { get; set; } = 4;
    public int Keep5mHours { get; set; } = 24;
    public int KeepOiHours { get; set; } = 24;
    public int PivotLeftBars { get; set; } = 2;
    public int PivotRightBars { get; set; } = 2;
    public int AtrPeriod { get; set; } = 14;
}

public sealed class HeatmapOptions
{
    public const string Section = "Heatmap";
    public double BucketWidthPct { get; set; } = 0.1;
    public Dictionary<string, double> LeverageMix { get; set; } = new() { ["10"] = 0.30, ["25"] = 0.30, ["50"] = 0.25, ["100"] = 0.15 };
    public double MaintenanceMarginRate { get; set; } = 0.005;
    public double LongShareMin { get; set; } = 0.2;
    public double LongShareMax { get; set; } = 0.8;
    public double HalfLifeHours { get; set; } = 24;
    public double QueryNearPct { get; set; } = 0.3;
    public double QueryFarPct { get; set; } = 3.0;
    public double PruneBelowUsd { get; set; } = 100;
}

public sealed class OrderBookOptions
{
    public const string Section = "OrderBook";
    public int MaxBooks { get; set; } = 15;
    public int MinMustHavesToShortlist { get; set; } = 3;
    public int UnsubscribeAfterMinutes { get; set; } = 10;
    public int UpdateIntervalMs { get; set; } = 500;
    public int SnapshotLimit { get; set; } = 500;
    public int SnapshotWeight { get; set; } = 10;
    public int SampleSeconds { get; set; } = 1;
    public double ImbalanceRangePct { get; set; } = 0.5;
    public int ImbalanceWindowSeconds { get; set; } = 30;
    public double WallRangePct { get; set; } = 1.0;
    public double WallMedianMultiple { get; set; } = 5.0;
    public int WallMinHoldSeconds { get; set; } = 20;
    public int MinHistorySeconds { get; set; } = 30;
    public int MaxWaitForHistorySeconds { get; set; } = 30;
    public int StartsPerMinute { get; set; } = 20;
}

public sealed class RulesOptions
{
    public const string Section = "Rules";

    // Volume (M2)
    public int VolumeWindowMinutes { get; set; } = 5;
    public int VolumeBaselineHours { get; set; } = 4;
    public double MinVolRatio { get; set; } = 2.5;
    public int ShareWindowMinutes { get; set; } = 15;
    public double MinDirectionalShare { get; set; } = 0.60;

    // Open interest (M3)
    public int OiLongWindowMinutes { get; set; } = 15;
    public int OiShortWindowMinutes { get; set; } = 5;
    public double MinOi15Pct { get; set; } = 1.0;
    public double MinOi5Pct { get; set; } = 0.0;
    public double FlatMovePct { get; set; } = 0.2;

    // Funding (M4)
    public double MaxLongRate8hPct { get; set; } = 0.03;
    public double MinShortRate8hPct { get; set; } = -0.03;
    public double CrowdedLongsNotePct { get; set; } = 0.03;

    // CVD (C1)
    public int CvdWindowMinutes { get; set; } = 15;
    public int CvdShareWindowMinutes { get; set; } = 5;
    public double MinCvd5Share { get; set; } = 0.10;

    // Liquidity (C2)
    public double MinLiqRatio { get; set; } = 1.5;

    // Order book (C3)
    public double MinImbalance { get; set; } = 0.20;

    // Vetoes
    public double OpposingWallDistancePct { get; set; } = 0.3;
    public double MaxChaseAtr { get; set; } = 1.5;
    public double MaxSpreadPct { get; set; } = 0.05;
    public int MaxTradeAgeSeconds { get; set; } = 10;
    public int MaxOiAgeSeconds { get; set; } = 90;
    public int MaxMarkAgeSeconds { get; set; } = 15;
    public int MaxBookAgeSeconds { get; set; } = 5;
    public int CooldownMinutes { get; set; } = 30;

    // Decision
    public int MinConfirmations { get; set; } = 2;
    public int StrongConfirmations { get; set; } = 3;
    public int MustHaveScoreWeight { get; set; } = 10;
    public int NearMissIntervalMinutes { get; set; } = 5;

    // Flood guard
    public int MaxAlertsPerWindow { get; set; } = 8;
    public int FloodWindowMinutes { get; set; } = 15;
}

public sealed class TelegramOptions
{
    public const string Section = "Telegram";
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
    public long ChatId { get; set; }
    public int PollTimeoutSeconds { get; set; } = 30;
    public int FeedDownSeconds { get; set; } = 120;
    public int TopCount { get; set; } = 5;
    public int DefaultStatsDays { get; set; } = 7;
}

public sealed class OutcomeOptions
{
    public const string Section = "Outcomes";
    public int TrackMinutes { get; set; } = 60;
    public int[] Checkpoints { get; set; } = [5, 15, 60];
    public int FinalizeGraceMinutes { get; set; } = 5;
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5433;
    public string Database { get; set; } = "scalpradar";
    public string Username { get; set; } = "scalpradar";
    public string? Password { get; set; }
    public int LiquidationRetentionDays { get; set; } = 30;
    public int LiquidationBatchSize { get; set; } = 200;
    public int FlushIntervalSeconds { get; set; } = 5;
}
