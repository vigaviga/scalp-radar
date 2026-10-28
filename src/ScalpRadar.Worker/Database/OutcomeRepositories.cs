using Dapper;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Outcomes;
using ScalpRadar.Core.Reports;

namespace ScalpRadar.Worker.Database;

/// <summary>An alert with no row in alert_outcomes yet.</summary>
public sealed record UnresolvedAlert(long Id, OutcomeSpec Spec);

public sealed class OutcomeRepository(DbConnectionFactory db)
{
    /// <summary>Saves an outcome by the alert's uid. Returns false when the alert row is missing or already resolved.</summary>
    public async Task<bool> InsertAsync(Guid alertUid, OutcomeResult r, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO alert_outcomes (alert_id, price_5m, price_15m, price_60m, mfe_pct, mae_pct, first_hit, resolved_at)
            SELECT id, @P5, @P15, @P60, @Mfe, @Mae, @FirstHit, @ResolvedAt FROM alerts WHERE uid = @Uid
            ON CONFLICT (alert_id) DO NOTHING
            """,
            new
            {
                Uid = alertUid,
                P5 = (decimal?)r.Price5m,
                P15 = (decimal?)r.Price15m,
                P60 = (decimal?)r.Price60m,
                Mfe = Math.Round((decimal)r.MfePct, 4),
                Mae = Math.Round((decimal)r.MaePct, 4),
                FirstHit = r.FirstHit.ToString().ToLowerInvariant(),
                ResolvedAt = r.ResolvedUtc,
            }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<IReadOnlyList<UnresolvedAlert>> UnresolvedAsync(DateTime createdBeforeUtc, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<UnresolvedRow>(new CommandDefinition("""
            SELECT a.id, a.uid, a.created_at AS CreatedAt, a.symbol, a.direction, a.price, a.invalidation, a.target
            FROM alerts a LEFT JOIN alert_outcomes o ON o.alert_id = a.id
            WHERE o.alert_id IS NULL AND a.created_at < @Before
            ORDER BY a.created_at
            LIMIT @Limit
            """, new { Before = createdBeforeUtc, Limit = limit }, cancellationToken: ct));
        return rows.Select(r => new UnresolvedAlert(r.Id, new OutcomeSpec(
            r.Uid, r.Symbol, r.Direction == "SHORT" ? Direction.Short : Direction.Long,
            DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc), (double)r.Price, (double)r.Invalidation, (double?)r.Target))).ToList();
    }

    private sealed class UnresolvedRow
    {
        public long Id { get; init; }
        public Guid Uid { get; init; }
        public DateTime CreatedAt { get; init; }
        public string Symbol { get; init; } = "";
        public string Direction { get; init; } = "";
        public decimal Price { get; init; }
        public decimal Invalidation { get; init; }
        public decimal? Target { get; init; }
    }
}

/// <summary>Read-only queries behind /stats, /status and the daily summary.</summary>
public sealed class ReportRepository(DbConnectionFactory db)
{
    public async Task<IReadOnlyList<StatsRow>> StatsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<StatsDbRow>(new CommandDefinition("""
            SELECT a.tier, a.direction,
                   count(*)::int AS count,
                   count(o.alert_id)::int AS resolved,
                   (count(*) FILTER (WHERE o.first_hit = 'target'))::int AS targets,
                   (count(*) FILTER (WHERE o.first_hit = 'invalidation'))::int AS invalidations,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY o.mfe_pct) AS medianmfe,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY o.mae_pct) AS medianmae
            FROM alerts a LEFT JOIN alert_outcomes o ON o.alert_id = a.id
            WHERE a.created_at >= @From AND a.created_at < @To
            GROUP BY a.tier, a.direction
            """, new { From = fromUtc, To = toUtc }, cancellationToken: ct));
        return rows.Select(r => new StatsRow(r.Tier, r.Direction, r.Count, r.Resolved, r.Targets, r.Invalidations, r.MedianMfe, r.MedianMae)).ToList();
    }

    public async Task<int> AlertsSinceAsync(DateTime sinceUtc, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM alerts WHERE created_at >= @Since", new { Since = sinceUtc }, cancellationToken: ct));
    }

    public async Task<DailySummary> DailyAsync(DateOnly day, int topFailed, CancellationToken ct)
    {
        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = from.AddDays(1);
        var rows = await StatsAsync(from, to, ct);

        await using var conn = await db.OpenAsync(ct);
        var digest = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM alerts WHERE created_at >= @From AND created_at < @To AND delivery = 'digest'",
            new { From = from, To = to }, cancellationToken: ct));
        var nearMisses = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM near_misses WHERE created_at >= @From AND created_at < @To",
            new { From = from, To = to }, cancellationToken: ct));
        var failed = await conn.QueryAsync<(string Code, int Count)>(new CommandDefinition("""
            SELECT f->>'code' AS code, count(*)::int AS n
            FROM near_misses, jsonb_array_elements(failed) f
            WHERE created_at >= @From AND created_at < @To
            GROUP BY 1 ORDER BY 2 DESC, 1
            LIMIT @Top
            """, new { From = from, To = to, Top = topFailed }, cancellationToken: ct));

        var total = rows.Sum(r => r.Count);
        return new DailySummary(day, total - digest, digest, rows, nearMisses,
            failed.Select(f => new FailedCount(f.Code, f.Count)).ToList());
    }

    private sealed class StatsDbRow
    {
        public string Tier { get; init; } = "";
        public string Direction { get; init; } = "";
        public int Count { get; init; }
        public int Resolved { get; init; }
        public int Targets { get; init; }
        public int Invalidations { get; init; }
        public double? MedianMfe { get; init; }
        public double? MedianMae { get; init; }
    }
}
