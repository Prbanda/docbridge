using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using DocBridge.Shared;
using DocBridge.Shared.Db;
using Npgsql;

namespace DocBridge.Workers;

/// <summary>
/// Worker configuration from the environment. Lambda has no native secret injection, so values that ECS
/// injects directly arrive here as *_SECRET_ARN and are fetched from Secrets Manager once per cold start.
/// Locally (compose) the plain values are set instead.
/// </summary>
public static class WorkerConfig
{
    private static readonly Lazy<AmazonSecretsManagerClient> Secrets = new(() => new AmazonSecretsManagerClient());

    public static async Task<string> SecretAsync(string name)
    {
        if (Env.Optional(name) is { } value)
        {
            return value;
        }
        var arn = Env.Required($"{name}_SECRET_ARN");
        var secret = await Secrets.Value.GetSecretValueAsync(new GetSecretValueRequest { SecretId = arn });
        return secret.SecretString;
    }

    /// <summary>DB credentials: DATABASE_URL / DB_PASSWORD locally, or DB_SECRET_ARN ({username,password}) via RDS Proxy.</summary>
    public static async Task<NpgsqlDataSource> DataSourceAsync()
    {
        if (Env.Optional("DATABASE_URL") is not null || Env.Optional("DB_PASSWORD") is not null)
        {
            return Database.CreateDataSource(Database.ConnectionStringFromEnvironment());
        }
        var secret = await Secrets.Value.GetSecretValueAsync(new GetSecretValueRequest { SecretId = Env.Required("DB_SECRET_ARN") });
        using var json = JsonDocument.Parse(secret.SecretString);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Env.Required("DB_HOST"),
            Port = Env.GetInt("DB_PORT", 5432),
            Database = Env.Required("DB_NAME"),
            Username = json.RootElement.GetProperty("username").GetString(),
            Password = json.RootElement.GetProperty("password").GetString(),
            SslMode = SslMode.Require,
            // Lambda: one container handles one message at a time; RDS Proxy does the pooling (ADR-011).
            MaxPoolSize = Env.GetInt("DB_MAX_POOL_SIZE", 2),
            Timeout = 10,
        };
        return Database.CreateDataSource(builder.ConnectionString);
    }

    public static string StagingBucket => Env.Required("STAGING_BUCKET");
}
