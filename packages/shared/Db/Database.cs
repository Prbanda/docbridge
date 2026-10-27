using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DocBridge.Shared.Db;

public static class Database
{
    /// <summary>
    /// DATABASE_URL (postgres://user:pass@host:port/db, used locally) or DB_HOST / DB_PORT / DB_NAME /
    /// DB_USER / DB_PASSWORD / DB_SSLMODE (ECS injects DB_USER/DB_PASSWORD from Secrets Manager).
    /// </summary>
    public static string ConnectionStringFromEnvironment()
    {
        if (Env.Optional("DATABASE_URL") is { } url)
        {
            return FromUrl(url);
        }
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Env.Required("DB_HOST"),
            Port = Env.GetInt("DB_PORT", 5432),
            Database = Env.Required("DB_NAME"),
            Username = Env.Required("DB_USER"),
            Password = Env.Required("DB_PASSWORD"),
            SslMode = Enum.Parse<SslMode>(Env.Get("DB_SSLMODE", "Require"), ignoreCase: true),
        };
        return Tune(builder);
    }

    public static string FromUrl(string url)
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            SslMode = Enum.Parse<SslMode>(Env.Get("DB_SSLMODE", "Disable"), ignoreCase: true),
        };
        return Tune(builder);
    }

    private static string Tune(NpgsqlConnectionStringBuilder builder)
    {
        builder.MaxPoolSize = Env.GetInt("DB_MAX_POOL_SIZE", 20);
        // Keep a couple of TLS connections open through idle periods, so the first request after a lull doesn't
        // pay for connection setup. Lambdas don't use this path (WorkerConfig builds theirs; RDS Proxy pools there).
        builder.MinPoolSize = Math.Min(Env.GetInt("DB_MIN_POOL_SIZE", 2), builder.MaxPoolSize);
        builder.Timeout = 10;
        builder.CommandTimeout = 30;
        // Idle pooled connections are probed every 15 s, so connections killed by an RDS reboot or Multi-AZ failover are
        // dropped in the background. Observed without it: every stale pooled connection failed one request after a reboot.
        builder.KeepAlive = Env.GetInt("DB_KEEPALIVE_SECONDS", 15);
        return builder.ConnectionString;
    }

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        return NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>
    /// Open MinPoolSize connections at once so they exist before the first burst. A job submission fans out ~11
    /// parallel platform calls; opening that many TLS connections to RDS on demand cost ~600 ms on 0.25 vCPU tasks.
    /// </summary>
    public static async Task WarmPoolAsync(NpgsqlDataSource dataSource)
    {
        var min = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MinPoolSize;
        var connections = await Task.WhenAll(Enumerable.Range(0, min).Select(_ => dataSource.OpenConnectionAsync().AsTask()));
        foreach (var connection in connections)
        {
            await connection.DisposeAsync();
        }
    }
}

/// <summary>
/// ADR-018: plain .sql files embedded under migrations/ in the owning service's assembly, applied in
/// name order, recorded in schema_migrations. A session advisory lock makes concurrent starts (two
/// ECS tasks) safe: the second waits, then finds nothing left to apply. Forward-only for v1.
/// </summary>
public static class Migrator
{
    private const long LockKey = 0x0D0CB21D6E;

    public static async Task<IReadOnlyList<string>> RunAsync(NpgsqlDataSource dataSource, Assembly assembly, ILogger logger,
        CancellationToken ct = default)
    {
        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("migrations/", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@LockKey)", new { LockKey });
        var applied = new List<string>();
        try
        {
            await conn.ExecuteAsync(
                "CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
            var done = (await conn.QueryAsync<string>("SELECT name FROM schema_migrations")).ToHashSet();
            foreach (var name in scripts.Where(s => !done.Contains(s)))
            {
                await using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@name)", new { name }, tx);
                await tx.CommitAsync(ct);
                logger.LogInformation("Applied migration {Migration}", name);
                applied.Add(name);
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@LockKey)", new { LockKey });
        }
        return applied;
    }
}
