using System.Diagnostics;

namespace ScalpRadar.Core.Models;

/// <summary>
/// Everything that changes market state travels as one of these through a single channel.
/// Producers (WebSocket handlers, REST pollers, timers) create them; one consumer loop applies them in order.
/// </summary>
public abstract record MarketEvent
{
    /// <summary>Stopwatch timestamp taken when the event was created, used to measure channel lag.</summary>
    public long CreatedTimestamp { get; init; } = Stopwatch.GetTimestamp();
}

public sealed record TradeEvent(string Symbol, DateTime Time, double Price, double Qty, bool IsBuyerMaker, DateTime ReceivedUtc) : MarketEvent;

public sealed record MarkPriceUpdate(string Symbol, DateTime Time, double MarkPrice, double? FundingRate, DateTime NextFundingTime);

/// <summary>One batch from !markPrice@arr (all symbols, every few seconds).</summary>
public sealed record MarkPriceEvent(IReadOnlyList<MarkPriceUpdate> Updates, DateTime ReceivedUtc) : MarketEvent;

/// <summary>A forced liquidation. Side is the liquidation order side: SELL closes a long, BUY closes a short.</summary>
public sealed record ForceOrderEvent(string Symbol, DateTime Time, string Side, double Price, double Qty, DateTime ReceivedUtc) : MarketEvent;

public sealed record OiEvent(string Symbol, DateTime Time, double OpenInterest, DateTime ReceivedUtc) : MarketEvent;

public readonly record struct BookLevel(double Price, double Qty);

/// <summary>Order book levels near mid, sampled once a second for shortlisted symbols.</summary>
public sealed record BookSnapshotEvent(string Symbol, DateTime ReceivedUtc, BookLevel[] Bids, BookLevel[] Asks) : MarketEvent;

/// <summary>Timer tick (every 10 s) that triggers scoring.</summary>
public sealed record Tick(DateTime NowUtc) : MarketEvent;

// ---- control events (also applied by the consumer so state stays single-threaded) ----

public sealed record SymbolInfo(string Symbol, int PriceDecimals, DateTime ListingDate);

public sealed record UniverseEvent(IReadOnlyList<SymbolInfo> Added, IReadOnlyList<string> Removed) : MarketEvent;

public sealed record FundingIntervalsEvent(IReadOnlyDictionary<string, int> IntervalHours, int DefaultHours) : MarketEvent;

public sealed record OiSample(DateTime Time, double OpenInterest, double Notional);

/// <summary>REST backfill result: closed klines and OI history. Gap backfills only cover the missing minutes.</summary>
public sealed record BackfillEvent(
    string Symbol,
    IReadOnlyList<Candle> Klines1m,
    IReadOnlyList<Candle> Klines5m,
    IReadOnlyList<OiSample> OiHistory,
    bool IsGap) : MarketEvent;

/// <summary>A trade stream connection dropped (Connected=false) or came back (Connected=true).</summary>
public sealed record StreamStateEvent(IReadOnlyList<string> Symbols, bool Connected) : MarketEvent;

/// <summary>The local order book for a symbol started (Active=true) or stopped.</summary>
public sealed record BookStatusEvent(string Symbol, bool Active) : MarketEvent;
