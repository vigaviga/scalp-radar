using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Worker.Binance;
using ScalpRadar.Worker.Infrastructure;

namespace ScalpRadar.Worker.Market;

/// <summary>
/// Owns the WebSocket subscriptions: aggTrade per symbol (in small groups), plus the all-symbol
/// !markPrice@arr and !forceOrder@arr streams. Handlers only post events; they never touch market state.
/// Binance.Net routes these to the /market endpoint and handles reconnects, including Binance's 24h forced one.
/// </summary>
public sealed class StreamIngestor(
    IBinanceSocketClient socket,
    MarketChannel channel,
    IOptions<BinanceOptions> options,
    ILogger<StreamIngestor> log)
{
    private sealed class TradeGroup
    {
        public required UpdateSubscription Subscription { get; init; }
        public required List<string> Symbols { get; init; }
        public HashSet<string> Active { get; } = new(StringComparer.Ordinal);
        public volatile bool Connected = true;
    }

    private readonly BinanceOptions _o = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<TradeGroup> _groups = [];
    private readonly Dictionary<string, TradeGroup> _bySymbol = new(StringComparer.Ordinal);
    private UpdateSubscription? _markSub;
    private UpdateSubscription? _liqSub;
    private volatile bool _markConnected;
    private volatile bool _liqConnected;

    public IReadOnlyCollection<string> SubscribedSymbols
    {
        get { lock (_bySymbol) return _bySymbol.Keys.ToList(); }
    }

    public int TotalStreams { get { lock (_bySymbol) return _groups.Count + 2; } }

    public int ConnectedStreams
    {
        get
        {
            lock (_bySymbol)
                return _groups.Count(g => g.Connected) + (_markConnected ? 1 : 0) + (_liqConnected ? 1 : 0);
        }
    }

    public async Task StartGlobalStreamsAsync(CancellationToken ct)
    {
        int? interval = _o.MarkPriceUpdateMs == 1000 ? 1000 : null; // 3s is the stream default
        var mark = await socket.UsdFuturesApi.ExchangeData.SubscribeToAllMarkPriceUpdatesAsync(interval, d =>
        {
            var updates = new MarkPriceUpdate[d.Data.Length];
            for (var i = 0; i < d.Data.Length; i++)
            {
                var m = d.Data[i];
                updates[i] = new MarkPriceUpdate(m.Symbol, BinanceMapper.Utc(m.EventTime), (double)m.MarkPrice,
                    m.FundingRate is { } f ? (double)f : null, BinanceMapper.Utc(m.NextFundingTime));
            }
            channel.Post(new MarkPriceEvent(updates, d.ReceiveTime));
        }, ct);
        if (!mark.Success) throw new InvalidOperationException($"Could not subscribe to !markPrice@arr: {mark.Error}");
        _markSub = mark.Data;
        _markConnected = true;
        Hook(_markSub, "!markPrice@arr", c => _markConnected = c);

        var liq = await socket.UsdFuturesApi.ExchangeData.SubscribeToAllLiquidationUpdatesAsync(d =>
        {
            var l = d.Data;
            var price = l.AveragePrice > 0 ? l.AveragePrice : l.Price;
            var qty = l.QuantityFilled > 0 ? l.QuantityFilled : l.Quantity;
            channel.Post(new ForceOrderEvent(l.Symbol, BinanceMapper.Utc(l.Timestamp),
                l.Side == OrderSide.Sell ? "SELL" : "BUY", (double)price, (double)qty, d.ReceiveTime));
        }, ct);
        if (!liq.Success) throw new InvalidOperationException($"Could not subscribe to !forceOrder@arr: {liq.Error}");
        _liqSub = liq.Data;
        _liqConnected = true;
        Hook(_liqSub, "!forceOrder@arr", c => _liqConnected = c);

        log.LogInformation("Subscribed to !markPrice@arr and !forceOrder@arr");
    }

    /// <summary>Makes the aggTrade subscriptions match <paramref name="wanted"/>. Failed subscribes are retried on the next call.</summary>
    public async Task EnsureTradeStreamsAsync(IReadOnlyCollection<string> wanted, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var wantedSet = new HashSet<string>(wanted, StringComparer.Ordinal);

            // Drop symbols that left the universe. A group is closed once none of its symbols are wanted;
            // until then, trades for removed symbols are simply ignored by the consumer.
            List<TradeGroup> toClose = [];
            lock (_bySymbol)
            {
                foreach (var (symbol, group) in _bySymbol.ToList())
                {
                    if (wantedSet.Contains(symbol)) continue;
                    group.Active.Remove(symbol);
                    _bySymbol.Remove(symbol);
                    if (group.Active.Count == 0) toClose.Add(group);
                }
                foreach (var g in toClose) _groups.Remove(g);
            }
            foreach (var g in toClose)
            {
                await g.Subscription.CloseAsync();
                log.LogInformation("Closed aggTrade stream for {Symbols}", string.Join(",", g.Symbols));
            }

            List<string> missing;
            lock (_bySymbol) missing = wanted.Where(s => !_bySymbol.ContainsKey(s)).ToList();

            var delay = TimeSpan.FromSeconds(1.0 / Math.Max(1, _o.StreamSubscribesPerSecond));
            foreach (var chunk in missing.Chunk(Math.Max(1, _o.StreamSubscribeBatchSize)))
            {
                var symbols = chunk.ToList();
                var result = await socket.UsdFuturesApi.ExchangeData.SubscribeToAggregatedTradeUpdatesAsync(symbols, d =>
                {
                    var t = d.Data;
                    channel.Post(new TradeEvent(t.Symbol, BinanceMapper.Utc(t.TradeTime), (double)t.Price, (double)t.Quantity,
                        t.BuyerIsMaker, d.ReceiveTime));
                }, ct);

                if (!result.Success)
                {
                    log.LogError("aggTrade subscribe failed for {Symbols}: {Error}. Will retry at the next universe refresh",
                        string.Join(",", symbols), result.Error);
                    await Task.Delay(delay, ct);
                    continue;
                }

                var group = new TradeGroup { Subscription = result.Data, Symbols = symbols };
                foreach (var s in symbols) group.Active.Add(s);
                lock (_bySymbol)
                {
                    _groups.Add(group);
                    foreach (var s in symbols) _bySymbol[s] = group;
                }
                Hook(group.Subscription, $"aggTrade[{string.Join(",", symbols)}]", connected =>
                {
                    group.Connected = connected;
                    List<string> affected;
                    lock (_bySymbol) affected = group.Active.ToList();
                    channel.Post(new StreamStateEvent(affected, connected));
                });
                await Task.Delay(delay, ct);
            }

            if (missing.Count > 0)
                log.LogInformation("aggTrade streams: {Count} symbols subscribed in {Groups} groups", SubscribedSymbols.Count, _groups.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync()
    {
        try
        {
            await socket.UsdFuturesApi.UnsubscribeAllAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Unsubscribe on shutdown failed");
        }
    }

    private void Hook(UpdateSubscription sub, string name, Action<bool> onConnectedChanged)
    {
        sub.ConnectionLost += () =>
        {
            log.LogWarning("Stream {Name} lost its connection", name);
            onConnectedChanged(false);
        };
        sub.ConnectionRestored += downtime =>
        {
            log.LogInformation("Stream {Name} reconnected after {Seconds:F1}s", name, downtime.TotalSeconds);
            onConnectedChanged(true);
        };
        sub.ResubscribingFailed += err => log.LogError("Stream {Name} failed to resubscribe: {Error}", name, err);
        sub.Exception += ex => log.LogError(ex, "Stream {Name} handler threw", name);
    }
}
