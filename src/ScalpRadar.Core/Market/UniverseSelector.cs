using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Market;

public sealed record SymbolCandidate(
    string Symbol,
    string QuoteAsset,
    bool IsPerpetual,
    bool IsTrading,
    DateTime ListingDate,
    double QuoteVolume24h,
    int PriceDecimals);

/// <summary>Picks the top USDT perpetuals by 24h quote volume, after the exclusion rules.</summary>
public static class UniverseSelector
{
    public static IReadOnlyList<SymbolCandidate> Select(IEnumerable<SymbolCandidate> all, UniverseOptions o, DateTime nowUtc)
    {
        var excluded = new HashSet<string>(o.ExcludedSymbols, StringComparer.OrdinalIgnoreCase);
        var minListing = nowUtc.AddDays(-o.MinListingAgeDays);

        return all
            .Where(s => s.IsPerpetual && s.IsTrading)
            .Where(s => string.Equals(s.QuoteAsset, o.QuoteAsset, StringComparison.OrdinalIgnoreCase))
            .Where(s => !excluded.Contains(s.Symbol))
            .Where(s => s.ListingDate <= minListing)
            .Where(s => s.QuoteVolume24h >= o.MinQuoteVolumeUsd)
            .OrderByDescending(s => s.QuoteVolume24h)
            .ThenBy(s => s.Symbol, StringComparer.Ordinal)
            .Take(o.Size)
            .ToList();
    }

    /// <summary>Number of decimals in a tick size, e.g. 0.010 -> 2, 1 -> 0.</summary>
    public static int DecimalsFromTickSize(decimal tickSize)
    {
        if (tickSize <= 0) return 2;
        var normalized = tickSize / 1.000000000000000000000000000000000m; // strips trailing zeros
        var bits = decimal.GetBits(normalized);
        return (bits[3] >> 16) & 0xFF;
    }
}
