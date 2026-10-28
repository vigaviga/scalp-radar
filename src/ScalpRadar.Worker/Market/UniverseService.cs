using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Options;
using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Worker.Binance;
using ScalpRadar.Worker.Infrastructure;

namespace ScalpRadar.Worker.Market;

/// <summary>
/// Hourly: picks the top USDT perpetuals by 24h quote volume, refreshes funding intervals, subscribes new
/// symbols (backfill first, then they count as ready) and drops the ones that left.
/// </summary>
public sealed class UniverseService(
    IBinanceRestClient rest,
    RestGate gate,
    MarketChannel channel,
    StreamIngestor streams,
    BackfillService backfill,
    UniverseRegistry registry,
    IOptions<UniverseOptions> options,
    TimeProvider time,
    ILogger<UniverseService> log) : BackgroundService
{
    private const int ExchangeInfoWeight = 1;
    private const int Tickers24hWeight = 40;
    private const int FundingInfoWeight = 1;

    private readonly UniverseOptions _o = options.Value;
    private readonly Dictionary<string, SymbolInfo> _current = new(StringComparer.Ordinal);
    private bool _globalStarted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var ok = false;
            try
            {
                ok = await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Universe refresh failed");
            }

            // Retry soon if the first refresh failed; otherwise wait for the next hourly refresh.
            var wait = ok || _current.Count > 0 ? TimeSpan.FromMinutes(_o.RefreshMinutes) : TimeSpan.FromSeconds(30);
            await Task.Delay(wait, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await streams.StopAsync();
    }

    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        var info = await gate.RunAsync(ExchangeInfoWeight, c => rest.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(c), ct);
        if (!info.Success)
        {
            log.LogError("exchangeInfo failed: {Error}", info.Error);
            return false;
        }

        var weightLimit = info.Data.RateLimits.FirstOrDefault(r =>
            r.Type == RateLimitType.RequestWeight && r.Interval == RateLimitInterval.Minute && r.IntervalNumber == 1);
        if (weightLimit is not null) gate.SetCap(weightLimit.Limit);

        var tickers = await gate.RunAsync(Tickers24hWeight, c => rest.UsdFuturesApi.ExchangeData.GetTickersAsync(c), ct);
        if (!tickers.Success)
        {
            log.LogError("24h tickers failed: {Error}", tickers.Error);
            return false;
        }

        var funding = await gate.RunAsync(FundingInfoWeight, c => rest.UsdFuturesApi.ExchangeData.GetFundingInfoAsync(c), ct);
        if (funding.Success)
        {
            var intervals = funding.Data.ToDictionary(f => f.Symbol, f => f.FundingIntervalHours, StringComparer.Ordinal);
            await channel.PostAsync(new FundingIntervalsEvent(intervals, _o.DefaultFundingIntervalHours), ct);
        }
        else
        {
            log.LogWarning("fundingInfo failed: {Error}. Keeping previous funding intervals", funding.Error);
        }

        var volumeBySymbol = tickers.Data.ToDictionary(t => t.Symbol, t => (double)t.QuoteVolume, StringComparer.Ordinal);
        var candidates = info.Data.Symbols.Select(s => new SymbolCandidate(
            s.Name,
            s.QuoteAsset,
            s.ContractType == ContractType.Perpetual,
            s.Status == SymbolStatus.Trading,
            BinanceMapper.Utc(s.ListingDate),
            volumeBySymbol.GetValueOrDefault(s.Name),
            s.PriceFilter is { } pf ? UniverseSelector.DecimalsFromTickSize(pf.TickSize) : s.PricePrecision));

        var selected = UniverseSelector.Select(candidates, _o, time.GetUtcNow().UtcDateTime);
        var wanted = selected.ToDictionary(s => s.Symbol, s => new SymbolInfo(s.Symbol, s.PriceDecimals, s.ListingDate), StringComparer.Ordinal);

        var added = wanted.Values.Where(s => !_current.ContainsKey(s.Symbol)).ToList();
        var removed = _current.Keys.Where(s => !wanted.ContainsKey(s)).ToList();

        // Consumer first creates state for new symbols, so their first trades are not dropped.
        await channel.PostAsync(new UniverseEvent(added, removed), ct);
        foreach (var s in removed) _current.Remove(s);
        foreach (var s in added) _current[s.Symbol] = s;
        registry.Set(_current.Keys);

        if (!_globalStarted)
        {
            await streams.StartGlobalStreamsAsync(ct);
            _globalStarted = true;
        }

        // Subscribe the live streams first, then backfill, so no minute falls between the two.
        await streams.EnsureTradeStreamsAsync(_current.Keys.ToList(), ct);
        backfill.RequestFull(added.Select(a => a.Symbol));

        log.LogInformation("Universe: {Count} symbols ({Added} added, {Removed} removed). Smallest 24h volume ${MinVol:N0}",
            _current.Count, added.Count, removed.Count, selected.Count > 0 ? selected[^1].QuoteVolume24h : 0);
        if (added.Count is > 0 and <= 10) log.LogInformation("Added: {Symbols}", string.Join(", ", added.Select(a => a.Symbol)));
        if (removed.Count > 0) log.LogInformation("Removed: {Symbols}", string.Join(", ", removed));
        return true;
    }
}

/// <summary>Thread-safe read-only view of the current universe for pollers.</summary>
public sealed class UniverseRegistry
{
    private volatile IReadOnlyList<string> _symbols = [];
    public IReadOnlyList<string> Symbols => _symbols;
    public void Set(IEnumerable<string> symbols) => _symbols = symbols.OrderBy(s => s, StringComparer.Ordinal).ToList();
}
