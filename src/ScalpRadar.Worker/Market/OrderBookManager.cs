using System.Collections.Concurrent;
using System.Threading.Channels;
using Binance.Net.Interfaces.Clients;
using Binance.Net.SymbolOrderBooks;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Worker.Binance;
using ScalpRadar.Worker.Infrastructure;

namespace ScalpRadar.Worker.Market;

/// <summary>
/// Runs local order books (depth diff stream + REST snapshot, via Binance.Net) for shortlisted symbols only,
/// and posts a BookSnapshotEvent with the levels near mid once a second. The consumer decides which symbols;
/// this service only does the I/O.
/// </summary>
public sealed class OrderBookManager(
    IBinanceRestClient rest,
    IBinanceSocketClient socket,
    ILoggerFactory loggerFactory,
    RestGate gate,
    MarketChannel channel,
    IOptions<OrderBookOptions> options,
    TimeProvider time,
    ILogger<OrderBookManager> log) : BackgroundService
{
    private sealed record Command(string Symbol, bool Start);

    private readonly OrderBookOptions _o = options.Value;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>();
    private readonly ConcurrentDictionary<string, BinanceFuturesUsdtSymbolOrderBook> _books = new(StringComparer.Ordinal);

    public int ActiveBooks => _books.Count;

    // Symbols the consumer wants a book for (Start without a later Stop). A failed start is retried while wanted.
    private readonly ConcurrentDictionary<string, byte> _wanted = new(StringComparer.Ordinal);

    public void Start(string symbol)
    {
        _wanted[symbol] = 0;
        _commands.Writer.TryWrite(new Command(symbol, true));
    }

    public void Stop(string symbol)
    {
        _wanted.TryRemove(symbol, out _);
        _commands.Writer.TryWrite(new Command(symbol, false));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sampler = SampleLoopAsync(stoppingToken);
        var startSpacing = TimeSpan.FromSeconds(60.0 / Math.Max(1, _o.StartsPerMinute));

        try
        {
            await foreach (var cmd in _commands.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (cmd.Start)
                    {
                        if (await StartBookAsync(cmd.Symbol, stoppingToken))
                            await Task.Delay(startSpacing, time, stoppingToken); // throttle subscribes and snapshots
                    }
                    else
                    {
                        await StopBookAsync(cmd.Symbol);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Order book command {Command} for {Symbol} failed", cmd.Start ? "start" : "stop", cmd.Symbol);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await sampler.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var symbol in _books.Keys.ToList()) await StopBookAsync(symbol);
    }

    private async Task<bool> StartBookAsync(string symbol, CancellationToken ct)
    {
        if (_books.ContainsKey(symbol)) return false;
        if (_books.Count >= _o.MaxBooks)
        {
            log.LogWarning("Not starting order book for {Symbol}: already {Count} books", symbol, _books.Count);
            return false;
        }

        await gate.ReserveAsync(_o.SnapshotWeight, ct);
        // No Limit: setting one switches Binance.Net to the partial-depth stream (20 levels max).
        // Without it we get the <symbol>@depth@500ms diff stream plus a 1000-level REST snapshot.
        var book = new BinanceFuturesUsdtSymbolOrderBook(symbol, o => o.UpdateInterval = _o.UpdateIntervalMs,
            loggerFactory, rest, socket);

        book.OnStatusChange += (from, to) =>
        {
            if (to is OrderBookStatus.Reconnecting or OrderBookStatus.Disconnected)
                log.LogWarning("Order book {Symbol}: {From} -> {To}", symbol, from, to);
        };

        var result = await book.StartAsync(ct);
        if (!result.Success)
        {
            log.LogWarning("Order book for {Symbol} failed to start: {Error}. Retrying in {Seconds}s", symbol, result.Error, _o.StartRetrySeconds);
            book.Dispose();
            _ = RetryLaterAsync(symbol, ct);
            return true;
        }

        _books[symbol] = book;
        await channel.PostAsync(new BookStatusEvent(symbol, true), ct);
        log.LogInformation("Order book started for {Symbol} ({Count}/{Max})", symbol, _books.Count, _o.MaxBooks);
        return true;
    }

    private async Task RetryLaterAsync(string symbol, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_o.StartRetrySeconds), time, ct);
            if (_wanted.ContainsKey(symbol) && !_books.ContainsKey(symbol))
                _commands.Writer.TryWrite(new Command(symbol, true));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task StopBookAsync(string symbol)
    {
        if (!_books.TryRemove(symbol, out var book)) return;
        try
        {
            await book.StopAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Stopping order book for {Symbol} threw", symbol);
        }
        book.Dispose();
        channel.Post(new BookStatusEvent(symbol, false));
        log.LogInformation("Order book stopped for {Symbol} ({Count}/{Max})", symbol, _books.Count, _o.MaxBooks);
    }

    /// <summary>Once a second, copy the levels within WallRangePct of mid from every synced book.</summary>
    private async Task SampleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_o.SampleSeconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = time.GetUtcNow().UtcDateTime;
                foreach (var (symbol, book) in _books)
                {
                    try
                    {
                        if (book.Status != OrderBookStatus.Synced) continue;
                        var (bids, asks) = book.Book;
                        if (bids.Length == 0 || asks.Length == 0) continue;

                        var mid = ((double)bids[0].Price + (double)asks[0].Price) / 2;
                        double lo = mid * (1 - _o.WallRangePct / 100), hi = mid * (1 + _o.WallRangePct / 100);
                        var b = bids.Select(x => new BookLevel((double)x.Price, (double)x.Quantity)).TakeWhile(x => x.Price >= lo).ToArray();
                        var a = asks.Select(x => new BookLevel((double)x.Price, (double)x.Quantity)).TakeWhile(x => x.Price <= hi).ToArray();
                        channel.Post(new BookSnapshotEvent(symbol, now, b, a));
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Sampling order book for {Symbol} failed", symbol);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
