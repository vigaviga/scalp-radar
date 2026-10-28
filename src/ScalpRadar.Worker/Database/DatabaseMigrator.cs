using DbUp;
using Npgsql;

namespace ScalpRadar.Worker.Database;

/// <summary>Runs the embedded SQL scripts with DbUp at startup. Retries while Postgres is still starting.</summary>
public sealed class DatabaseMigrator(DbConnectionFactory db, ILogger<DatabaseMigrator> log)
{
    private const int MaxAttempts = 12;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public async Task MigrateAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using (var probe = await db.OpenAsync()) { }
                break;
            }
            catch (NpgsqlException ex) when (attempt < MaxAttempts)
            {
                log.LogWarning("Postgres not reachable yet (attempt {Attempt}/{Max}): {Message}", attempt, MaxAttempts, ex.Message);
                await Task.Delay(RetryDelay);
            }
        }

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(db.ConnectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrator).Assembly, name => name.Contains(".Database.Migrations."))
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException($"Database migration failed on script {result.ErrorScript?.Name}", result.Error);

        foreach (var script in result.Scripts)
            log.LogInformation("Applied migration {Script}", script.Name);
        log.LogInformation("Database is up to date");
    }
}
