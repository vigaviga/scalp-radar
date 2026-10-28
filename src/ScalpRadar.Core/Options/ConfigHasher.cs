using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScalpRadar.Core.Options;

/// <summary>Short stable hash of the rule-related settings, so stats can be split by threshold version.</summary>
public static class ConfigHasher
{
    public static string Compute(RulesOptions rules, HeatmapOptions heatmap, OrderBookOptions book, StateOptions state)
    {
        var json = JsonSerializer.Serialize(new { rules, heatmap = Sorted(heatmap), book, state });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    // Dictionary order is not guaranteed, so sort the leverage mix before hashing.
    private static object Sorted(HeatmapOptions h) => new
    {
        h.BucketWidthPct,
        LeverageMix = h.LeverageMix.OrderBy(k => k.Key, StringComparer.Ordinal).ToArray(),
        h.MaintenanceMarginRate,
        h.LongShareMin,
        h.LongShareMax,
        h.HalfLifeHours,
        h.QueryNearPct,
        h.QueryFarPct,
        h.PruneBelowUsd,
    };
}
