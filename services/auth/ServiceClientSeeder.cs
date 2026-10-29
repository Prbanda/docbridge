using Dapper;
using DocBridge.Shared;
using Npgsql;

namespace DocBridge.Auth;

/// <summary>
/// Seeds the service clients (TDD 1.1: delivery-worker; G2/G4: docbridge-api, platform-admin) from secrets
/// injected at runtime. The secret value lives only in Secrets Manager, so the row is upserted at startup
/// instead of in a migration. A client whose secret env var is absent is left untouched.
/// </summary>
public static class ServiceClientSeeder
{
    public static readonly (string ClientId, string SecretEnv, string[] Scopes)[] Clients =
    [
        ("delivery-worker", "DELIVERY_WORKER_CLIENT_SECRET", ["documents:ingest"]),
        ("docbridge-api", "DOCBRIDGE_API_CLIENT_SECRET", ["memberships:read"]),
        ("platform-admin", "PLATFORM_ADMIN_CLIENT_SECRET", ["platform:admin"]),
    ];

    public static async Task SeedAsync(NpgsqlDataSource db, ILogger logger)
    {
        await using var conn = await db.OpenConnectionAsync();
        foreach (var (clientId, secretEnv, scopes) in Clients)
        {
            if (Env.Optional(secretEnv) is not { } secret)
            {
                continue;
            }
            var existing = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT client_secret_hash FROM service_clients WHERE client_id = @clientId", new { clientId });
            if (existing is not null && BCrypt.Net.BCrypt.Verify(secret, existing))
            {
                await conn.ExecuteAsync("UPDATE service_clients SET scopes = @scopes WHERE client_id = @clientId", new { clientId, scopes });
                continue;
            }
            await conn.ExecuteAsync(
                """
                INSERT INTO service_clients (client_id, client_secret_hash, scopes) VALUES (@clientId, @hash, @scopes)
                ON CONFLICT (client_id) DO UPDATE SET client_secret_hash = EXCLUDED.client_secret_hash, scopes = EXCLUDED.scopes
                """, new { clientId, hash = BCrypt.Net.BCrypt.HashPassword(secret, AuthEndpoints.BcryptCost), scopes });
            logger.LogInformation("Seeded service client {ClientId}", clientId);
        }
    }
}
