using System.Diagnostics;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Worker.Infrastructure;

namespace ScalpRadar.Worker.Market;

/// <summary>
/// The single consumer. It owns every SymbolState and applies events in channel order, so market state
/// needs no locks. Each event is handled inside its own try/catch: one bad symbol never stops the others.
/// </summary>
public sealed partial class MarketStateLoop : BackgroundService
{
    private readonly MarketChannel _channel;
    private readonly BackfillService _backfill;
    private readonly EngineStatus _status;
    private readonly StateOptions _stateOptions;
    private readonly HeatmapOptions _heatmapOptions;
    private readonly EngineOptions _engine;
    private readonly TimeProvider _time;
    private readonly ILogger<MarketStateLoop> _log;
    private readonly ILoggerFactory _loggerFactory;

    private readonly Dictionary<string, SymbolState> _states = new(StringComparer.Ordinal);
    private readonly ExchangeClock _clock = new();
    private readonly List<Candle> _closed = [];

    // Lag and throughput stats, reset every Engine:LagLogSeconds.
    private double _maxLagMs;
    private long _eventsInWindow;
    private long _tradesInWindow;
    private long _windowStartTimestamp = Stopwatch.GetTimestamp();
    private DateTime _lastLagWarn;
    private double _lastEventsPerSecond;
    private double _lastMaxLagMs;

    public MarketStateLoop(
        MarketChannel channel,
        BackfillService backfill,
        EngineStatus status,
        IOptions<StateOptions> stateOptions,
        IOptions<HeatmapOptions> heatmapOptions,
        IOptions<EngineOptions> engine,
        TimeProvider time,
        ILogger<MarketStateLoop> log,
        ILoggerFactory loggerFactory)
    {
        _channel = channel;
        _backfill = backfill;
        _status = status;
        _stateOptions = stateOptions.Value;
        _heatmapOptions = heatmapOptions.Value;
        _engine = engine.Value;
        _time = time;
        _log = log;
        _loggerFactory = loggerFactory;
    }

    private DateTime LocalNow => _time.GetUtcNow().UtcDateTime;
    private DateTime ExchangeNow => _clock.Now(LocalNow);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Market state loop started");
        try
        {
            await foreach (var e in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                TrackLag(e);
                try
                {
                    Apply(e);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to apply {Event}", e.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        _log.LogInformation("Market state loop stopped");
    }

    private void Apply(MarketEvent e)
    {
        switch (e)
        {
            case TradeEvent t: OnTrade(t); break;
            case MarkPriceEvent m: OnMarkPrice(m); break;
            case ForceOrderEvent f: OnForceOrder(f); break;
            case OiEvent o: OnOi(o); break;
            case BookSnapshotEvent b: OnBookSnapshot(b); break;
            case BookStatusEvent b: OnBookStatus(b); break;
            case Tick tick: OnTick(tick); break;
            case UniverseEvent u: OnUniverse(u); break;
            case FundingIntervalsEvent f: OnFundingIntervals(f); break;
            case BackfillEvent b: OnBackfill(b); break;
            case StreamStateEvent s: OnStreamState(s); break;
            default: _log.LogWarning("Unknown event {Type}", e.GetType().Name); break;
        }
    }

    // ------------------------------------------------------------------ events

    private void OnTrade(TradeEvent t)
    {
        _tradesInWindow++;
        _clock.Observe(t.Time, t.ReceivedUtc);
        OnAnyTrade(t);
        if (!_states.TryGetValue(t.Symbol, out var s)) return; // symbol left the universe; group still open

        _closed.Clear();
        s.ApplyTrade(t, _closed);
        if (_closed.Count > 0) OnCandlesClosed(s, _closed);
    }

    private void OnMarkPrice(MarkPriceEvent m)
    {
        if (m.Updates.Count > 0) _clock.Observe(m.Updates.Max(u => u.Time), m.ReceivedUtc);
        foreach (var u in m.Updates)
        {
            if (_states.TryGetValue(u.Symbol, out var s))
                ApplyMark(s, u, m.ReceivedUtc);
        }
        CloseDueCandles();
    }

    private void OnUniverse(UniverseEvent u)
    {
        foreach (var info in u.Added)
        {
            if (_states.TryGetValue(info.Symbol, out var existing)) existing.Info = info;
            else _states[info.Symbol] = CreateState(info);
        }
        foreach (var symbol in u.Removed)
        {
            _states.Remove(symbol);
            OnSymbolRemoved(symbol);
        }
    }

    private void OnBackfill(BackfillEvent b)
    {
        if (!_states.TryGetValue(b.Symbol, out var s)) return;
        s.ApplyBackfill(b, ExchangeNow);
        _log.LogDebug("{Symbol}: merged {Kind} backfill, {N1} x 1m and {N5} x 5m in memory",
            b.Symbol, b.IsGap ? "gap" : "full", s.Candles1m.Count, s.Candles5m.Count);
    }

    private void OnStreamState(StreamStateEvent e)
    {
        foreach (var symbol in e.Symbols)
        {
            if (!_states.TryGetValue(symbol, out var s)) continue;
            if (!e.Connected)
            {
                s.OnStreamInterrupted();
            }
            else
            {
                // Reconnected: backfill the gap from the last complete minute, then clear the stale flag.
                s.Stale = true;
                var from = s.FirstPartialCandleTime()
                           ?? (s.Candles1m.Count > 0 ? s.Candles1m[^1].CloseTime : (DateTime?)null);
                if (from is { } f && s.BackfillDone)
                {
                    s.GapFillPending = true;
                    _backfill.RequestGap(symbol, f);
                }
            }
        }
        if (e.Symbols.Count > 0)
            _log.LogInformation("{Count} symbols marked stale after a stream {State}", e.Symbols.Count, e.Connected ? "reconnect" : "disconnect");
    }

    private void OnTick(Tick tick)
    {
        CloseDueCandles();
        RequestPartialCandleFills();
        Score(tick);
        PublishStatus();
        MaybeLogStats();
    }

    // ------------------------------------------------------------------ helpers

    private void CloseDueCandles()
    {
        if (!_clock.IsSynced) return;
        var cutoff = ExchangeNow - TimeSpan.FromMilliseconds(_engine.CandleCloseGraceMs);
        foreach (var s in _states.Values)
        {
            _closed.Clear();
            s.CloseCandles(cutoff, _closed);
            if (_closed.Count > 0) OnCandlesClosed(s, _closed);
        }
    }

    /// <summary>Partial minutes (stream start, reconnects) are replaced with complete REST klines.</summary>
    private void RequestPartialCandleFills()
    {
        foreach (var s in _states.Values)
        {
            if (!s.BackfillDone || s.GapFillPending) continue;
            if (s.FirstPartialCandleTime() is { } from)
            {
                s.GapFillPending = true;
                _backfill.RequestGap(s.Symbol, from);
            }
        }
    }

    private void PublishStatus()
    {
        var now = LocalNow;
        double? oldest = null;
        string? oldestSymbol = null;
        var ready = 0;
        foreach (var s in _states.Values)
        {
            if (s.BackfillDone) ready++;
            var age = s.LastTradeReceivedUtc is { } t ? (now - t).TotalSeconds : double.PositiveInfinity;
            if (oldest is null || age > oldest)
            {
                oldest = age;
                oldestSymbol = s.Symbol;
            }
        }

        _status.Publish(new EngineSnapshot(now, _states.Count, ready, oldest, oldestSymbol,
            _lastEventsPerSecond, _lastMaxLagMs, _channel.Dropped, BookCount, TopEntries()));
    }

    private void TrackLag(MarketEvent e)
    {
        _eventsInWindow++;
        var lagMs = Stopwatch.GetElapsedTime(e.CreatedTimestamp).TotalMilliseconds;
        if (lagMs > _maxLagMs) _maxLagMs = lagMs;

        if (lagMs > _engine.LagWarnSeconds * 1000 && LocalNow - _lastLagWarn > TimeSpan.FromSeconds(10))
        {
            _lastLagWarn = LocalNow;
            _log.LogWarning("Channel lag {LagMs:F0} ms is over {Limit}s ({Queued} events queued)",
                lagMs, _engine.LagWarnSeconds, _channel.Count);
        }
    }

    private void MaybeLogStats()
    {
        var elapsed = Stopwatch.GetElapsedTime(_windowStartTimestamp);
        if (elapsed.TotalSeconds < _engine.LagLogSeconds) return;

        _lastEventsPerSecond = _eventsInWindow / elapsed.TotalSeconds;
        _lastMaxLagMs = _maxLagMs;
        _log.LogInformation(
            "Engine: {Symbols} symbols ({Ready} backfilled), {Eps:F0} events/s ({Tps:F0} trades/s), max channel lag {Lag:F0} ms, queued {Queued}, dropped {Dropped}, books {Books}",
            _states.Count, _states.Values.Count(s => s.BackfillDone), _lastEventsPerSecond, _tradesInWindow / elapsed.TotalSeconds,
            _maxLagMs, _channel.Count, _channel.Dropped, BookCount);

        _eventsInWindow = 0;
        _tradesInWindow = 0;
        _maxLagMs = 0;
        _windowStartTimestamp = Stopwatch.GetTimestamp();
    }
}
