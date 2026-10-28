using System.Globalization;
using System.Text;

namespace ScalpRadar.Core.Reports;

/// <summary>One /stats line: alerts of one tier and direction. Percentages are over resolved alerts.</summary>
public sealed record StatsRow(
    string Tier,
    string Direction,
    int Count,
    int Resolved,
    int Targets,
    int Invalidations,
    double? MedianMfePct,
    double? MedianMaePct);

public sealed record FailedCount(string Code, int Count);

public sealed record DailySummary(
    DateOnly Day,
    int AlertsSent,
    int AlertsInDigest,
    IReadOnlyList<StatsRow> Rows,
    int NearMisses,
    IReadOnlyList<FailedCount> TopFailed);

/// <summary>A row of /top: the symbol's best direction right now.</summary>
public sealed record TopRow(string Symbol, string Direction, int Score, int MustHaves, int Confirmations, string Passing);

public static class ReportFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Stats(IReadOnlyList<StatsRow> rows, int days, DateTime sinceUtc)
    {
        var sb = new StringBuilder();
        sb.Append(Inv, $"📊 Stats · last {days} day{(days == 1 ? "" : "s")} (since {sinceUtc:yyyy-MM-dd HH:mm} UTC)");
        if (rows.Count == 0)
        {
            sb.Append("\nNo alerts yet.");
            return sb.ToString();
        }
        foreach (var r in Ordered(rows)) sb.Append('\n').Append(Line(r));
        var total = rows.Sum(r => r.Count);
        var open = total - rows.Sum(r => r.Resolved);
        sb.Append(Inv, $"\nTotal {total}");
        if (open > 0) sb.Append(Inv, $" · {open} still tracking");
        sb.Append("\nFirst hit = which came first within 60 min. MFE/MAE = median best/worst move.");
        return sb.ToString();
    }

    public static string Daily(DailySummary d)
    {
        var sb = new StringBuilder();
        sb.Append(Inv, $"📅 Daily summary · {d.Day:yyyy-MM-dd} (UTC)");
        var alerts = d.AlertsSent + d.AlertsInDigest;
        sb.Append(Inv, $"\nAlerts: {alerts}");
        if (d.AlertsInDigest > 0) sb.Append(Inv, $" ({d.AlertsSent} sent, {d.AlertsInDigest} in digests)");

        foreach (var r in Ordered(d.Rows)) sb.Append('\n').Append(Line(r));

        if (alerts > 0)
        {
            var resolved = d.Rows.Sum(r => r.Resolved);
            var targets = d.Rows.Sum(r => r.Targets);
            var invalid = d.Rows.Sum(r => r.Invalidations);
            sb.Append(Inv, $"\nOutcomes: target first {targets} · invalidation first {invalid} · neither {resolved - targets - invalid}");
            if (alerts - resolved > 0) sb.Append(Inv, $" · still tracking {alerts - resolved}");
        }

        sb.Append(Inv, $"\nNear misses: {d.NearMisses}");
        if (d.TopFailed.Count > 0)
            sb.Append(" · most often failed: ").Append(string.Join(", ", d.TopFailed.Select(f => $"{f.Code} ({f.Count})")));
        return sb.ToString();
    }

    public static string Top(IReadOnlyList<TopRow> rows, int count)
    {
        if (rows.Count == 0) return "🏁 Top: no scores yet (still backfilling).";
        var sb = new StringBuilder();
        sb.Append(Inv, $"🏁 Top {Math.Min(count, rows.Count)} by score (must-haves × 10 + confirmations)");
        var i = 0;
        foreach (var r in rows.Take(count))
        {
            i++;
            sb.Append(Inv, $"\n{i}. {r.Symbol} {r.Direction} · score {r.Score} ({r.MustHaves}/4 must, {r.Confirmations}/3 conf)");
            sb.Append("\n   ").Append(r.Passing);
        }
        return sb.ToString();
    }

    private static IEnumerable<StatsRow> Ordered(IEnumerable<StatsRow> rows) =>
        rows.OrderBy(r => r.Tier == "STRONG" ? 0 : 1).ThenBy(r => r.Direction, StringComparer.Ordinal);

    private static string Line(StatsRow r)
    {
        var emoji = r.Tier == "STRONG" ? "🔥" : "✅";
        var label = $"{r.Tier} {r.Direction}".PadRight(12);
        var s = string.Create(Inv, $"{emoji} {label} {r.Count}");
        if (r.Resolved == 0) return s + " · none resolved yet";
        return s + string.Create(Inv,
            $" · target {Pct(r.Targets, r.Resolved)} · inval {Pct(r.Invalidations, r.Resolved)} · MFE {Num(r.MedianMfePct)} · MAE {Num(r.MedianMaePct)}");
    }

    private static string Pct(int n, int of) => (100.0 * n / of).ToString("F0", Inv) + "%";
    private static string Num(double? v) => v is { } x ? x.ToString("F2", Inv) + "%" : "n/a";
}
