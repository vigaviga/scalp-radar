using Dapper;
using Npgsql;
using NpgsqlTypes;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Rules;

namespace ScalpRadar.Worker.Database;

public sealed class AlertRepository(DbConnectionFactory db)
{
    public async Task<long> InsertAsync(AlertCandidate a, string delivery, CancellationToken ct)
    {
        var e = a.Evaluation;
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO alerts (uid, created_at, symbol, direction, tier, price, breakout_level, invalidation, target,
                                must_haves, confirmations, vetoes, metrics, config_hash, delivery)
            VALUES (@Uid, @CreatedAt, @Symbol, @Direction, @Tier, @Price, @Breakout, @Invalidation, @Target,
                    @MustHaves::jsonb, @Confirmations::jsonb, @Vetoes::jsonb, @Metrics::jsonb, @ConfigHash, @Delivery)
            RETURNING id
            """,
            new
            {
                a.Uid,
                CreatedAt = a.CreatedUtc,
                e.Symbol,
                Direction = e.Direction.Label(),
                Tier = (e.Tier ?? Tier.Alert).Label(),
                Price = a.Metrics.Price ?? 0,
                Breakout = e.BreakoutLevel ?? 0,
                Invalidation = e.Invalidation ?? 0,
                e.Target,
                MustHaves = Json.Serialize(e.MustHaves),
                Confirmations = Json.Serialize(e.Confirmations),
                Vetoes = Json.Serialize(e.Vetoes),
                Metrics = Json.Serialize(a.Metrics),
                a.ConfigHash,
                Delivery = delivery,
            }, cancellationToken: ct));
    }
}

public sealed class NearMissRepository(DbConnectionFactory db)
{
    public async Task InsertAsync(NearMissRecord n, CancellationToken ct)
    {
        var e = n.Evaluation;
        var passed = e.MustHaves.Concat(e.Confirmations).Where(c => c.Passed).Select(c => new { c.Code, c.Name, c.Detail })
            .Concat(e.Vetoes.Where(v => !v.Triggered).Select(v => new { v.Code, v.Name, v.Detail }));
        var failed = e.MustHaves.Concat(e.Confirmations).Where(c => !c.Passed).Select(c => new { c.Code, c.Name, c.Detail })
            .Concat(e.Vetoes.Where(v => v.Triggered).Select(v => new { v.Code, v.Name, v.Detail }));

        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO near_misses (created_at, symbol, direction, passed, failed, metrics, config_hash)
            VALUES (@CreatedAt, @Symbol, @Direction, @Passed::jsonb, @Failed::jsonb, @Metrics::jsonb, @ConfigHash)
            """,
            new
            {
                CreatedAt = n.CreatedUtc,
                e.Symbol,
                Direction = e.Direction.Label(),
                Passed = Json.Serialize(passed),
                Failed = Json.Serialize(failed),
                Metrics = Json.Serialize(n.Metrics),
                n.ConfigHash,
            }, cancellationToken: ct));
    }
}

public sealed class LiquidationRepository(DbConnectionFactory db)
{
    /// <summary>Bulk insert with binary COPY.</summary>
    public async Task InsertAsync(IReadOnlyList<ForceOrderEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await using var conn = await db.OpenAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY liquidations (time, symbol, side, price, qty, notional) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var r in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(r.Time, NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync(r.Symbol, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(r.Side, NpgsqlDbType.Text, ct);
            await writer.WriteAsync((decimal)r.Price, NpgsqlDbType.Numeric, ct);
            await writer.WriteAsync((decimal)r.Qty, NpgsqlDbType.Numeric, ct);
            await writer.WriteAsync((decimal)(r.Price * r.Qty), NpgsqlDbType.Numeric, ct);
        }
        await writer.CompleteAsync(ct);
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM liquidations WHERE time < @Cutoff", new { Cutoff = cutoffUtc }, cancellationToken: ct));
    }
}
