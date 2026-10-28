using Microsoft.Extensions.Options;
using Npgsql;
using ScalpRadar.Core.Options;

namespace ScalpRadar.Worker.Database;

public sealed class DbConnectionFactory(IOptions<DatabaseOptions> options)
{
    public string ConnectionString { get; } = Build(options.Value);

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static string Build(DatabaseOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Password))
            throw new InvalidOperationException(
                "Database:Password is not set. Run: dotnet user-secrets set \"Database:Password\" \"<the POSTGRES_PASSWORD from .env>\" --project src/ScalpRadar.Worker");
        return new NpgsqlConnectionStringBuilder
        {
            Host = o.Host,
            Port = o.Port,
            Database = o.Database,
            Username = o.Username,
            Password = o.Password,
        }.ConnectionString;
    }
}
