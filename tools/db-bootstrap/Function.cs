using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.SecretsManager;
using Dapper;
using DocBridge.Shared;
using Npgsql;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DocBridge.DbBootstrap;

/// <summary>
/// G11 (M0 §2.6): creates the three logical databases and per-service roles inside RDS, the cloud equivalent of
/// docker/postgres/init-databases.sh. Runs in the VPC with the master secret, invoked by Terraform's
/// aws_lambda_invocation, so no step is manual. Idempotent: re-running re-syncs passwords from Secrets Manager.
/// Handler: DocBridge.DbBootstrap::DocBridge.DbBootstrap.Function::Handle
/// </summary>
public sealed class Function
{
    private static readonly (string Database, string SecretEnv)[] Services =
    [
        ("auth", "AUTH_DB_SECRET_ARN"),
        ("platform", "PLATFORM_DB_SECRET_ARN"),
        ("docbridge", "DOCBRIDGE_DB_SECRET_ARN"),
    ];

    public async Task<object> Handle(Stream input, ILambdaContext context)
    {
        using var secrets = new AmazonSecretsManagerClient();
        var master = await ReadCredentials(secrets, Env.Required("MASTER_DB_SECRET_ARN"));
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Env.Required("DB_HOST"),
            Port = Env.GetInt("DB_PORT", 5432),
            Database = "postgres",
            Username = master.Username,
            Password = master.Password,
            SslMode = SslMode.Require,
            Pooling = false,
        };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();

        var created = new List<string>();
        foreach (var (database, secretEnv) in Services)
        {
            var creds = await ReadCredentials(secrets, Env.Required(secretEnv));
            var role = Identifier(creds.Username);
            var password = creds.Password.Replace("'", "''");
            if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @r)", new { r = creds.Username }))
            {
                await conn.ExecuteAsync($"CREATE ROLE {role} LOGIN PASSWORD '{password}'");
            }
            else
            {
                await conn.ExecuteAsync($"ALTER ROLE {role} LOGIN PASSWORD '{password}'");
            }
            // RDS: the master user must be a member of a role to create a database owned by it.
            await conn.ExecuteAsync($"GRANT {role} TO CURRENT_USER");
            if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @d)", new { d = database }))
            {
                await conn.ExecuteAsync($"CREATE DATABASE {Identifier(database)} OWNER {role}");
                created.Add(database);
            }
            // Ownership enforced by construction: only the owning role may connect (ADR-009).
            await conn.ExecuteAsync($"REVOKE CONNECT ON DATABASE {Identifier(database)} FROM PUBLIC");
            await conn.ExecuteAsync($"GRANT CONNECT ON DATABASE {Identifier(database)} TO {role}");
        }
        context.Logger.LogInformation($"db-bootstrap done; created databases: [{string.Join(", ", created)}]");
        return new { status = "ok", created };
    }

    private static async Task<(string Username, string Password)> ReadCredentials(IAmazonSecretsManager client, string arn)
    {
        var value = await client.GetSecretValueAsync(new Amazon.SecretsManager.Model.GetSecretValueRequest { SecretId = arn });
        using var json = JsonDocument.Parse(value.SecretString);
        return (json.RootElement.GetProperty("username").GetString()!, json.RootElement.GetProperty("password").GetString()!);
    }

    private static string Identifier(string name) =>
        name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
            ? name
            : throw new InvalidOperationException($"Unexpected identifier '{name}'");
}
