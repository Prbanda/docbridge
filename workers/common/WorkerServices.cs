using Amazon.S3;
using Amazon.SQS;
using DocBridge.Shared;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Clients;
using DocBridge.Shared.Jobs;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DocBridge.Workers;

/// <summary>
/// Composition root shared by the Lambda entry points and the local runner, so the same objects run in both.
/// Built lazily: a function only resolves the secrets and clients it actually uses.
/// </summary>
public sealed class WorkerServices
{
    private readonly Cached<NpgsqlDataSource> _db = new(WorkerConfig.DataSourceAsync);
    private readonly Lazy<AmazonSQSClient> _sqs = new(AwsClients.Sqs);
    private readonly Lazy<AmazonS3Client> _s3 = new(() => AwsClients.S3());
    private readonly Cached<PlatformClient> _platformForApi;
    private readonly Cached<PlatformIngestClient> _ingest;
    private readonly Lazy<JwtValidator> _validator;
    private readonly HttpClient _authHttp = new(FailFastHandler()) { Timeout = TimeSpan.FromSeconds(5) };

    public WorkerServices(ILoggerFactory logs)
    {
        Logs = logs;
        _platformForApi = new(async () =>
        {
            var tokens = new ServiceTokenClient(_authHttp, AuthUrl,
                new ServiceCredentials("docbridge-api", await WorkerConfig.SecretAsync("DOCBRIDGE_API_CLIENT_SECRET"), "memberships:read"));
            return new PlatformClient(PlatformHttp(TimeSpan.FromSeconds(5)), tokens);
        });
        _ingest = new(async () =>
        {
            var tokens = new ServiceTokenClient(_authHttp, AuthUrl,
                new ServiceCredentials("delivery-worker", await WorkerConfig.SecretAsync("DELIVERY_WORKER_CLIENT_SECRET"), "documents:ingest"));
            // Below the Lambda timeout, so a hung platform surfaces as a retryable timeout rather than a killed invocation.
            return new PlatformIngestClient(PlatformHttp(TimeSpan.FromSeconds(Env.GetInt("DELIVERY_HTTP_TIMEOUT_SECONDS", 270))), tokens);
        });
        _validator = new(() => new JwtValidator(
            new JwksCache(_authHttp, Env.Get("AUTH_JWKS_URL", $"{AuthUrl}/.well-known/jwks.json")), JwtSettings.FromEnvironment()));
    }

    public ILoggerFactory Logs { get; }

    private static string AuthUrl => Env.Required("AUTH_URL").TrimEnd('/');

    public async Task<FanoutProcessor> FanoutAsync() =>
        new(await _db.Value, _sqs.Value, Env.Required("TASK_QUEUE_URL"), Logs.CreateLogger<FanoutProcessor>());

    public async Task<DeliveryProcessor> DeliveryAsync()
    {
        var db = await _db.Value;
        return new DeliveryProcessor(new TaskRepository(db), _s3.Value, WorkerConfig.StagingBucket, await _ingest.Value,
            await PusherAsync(), Logs.CreateLogger<DeliveryProcessor>());
    }

    public async Task<DlqProcessor> DlqAsync() => new(await _db.Value, await PusherAsync(), Logs.CreateLogger<DlqProcessor>());

    public async Task<Sweeper> SweeperAsync() =>
        new(await _db.Value, _sqs.Value, Env.Required("JOB_QUEUE_URL"), Logs.CreateLogger<Sweeper>());

    public async Task<WsHandlers> WsAsync(IConnectionSender? sender = null) =>
        new(await _db.Value, _validator.Value, new TeamAccess(await _platformForApi.Value), await PusherAsync(sender),
            Logs.CreateLogger<WsHandlers>());

    public IAmazonSQS Sqs => _sqs.Value;

    public Task<NpgsqlDataSource> DataSourceAsync() => _db.Value;

    private async Task<StatusPusher> PusherAsync(IConnectionSender? sender = null) =>
        new(await _db.Value, sender ?? ApiGatewaySender.FromEnvironment(), Logs.CreateLogger<StatusPusher>());

    private static HttpClient PlatformHttp(TimeSpan timeout) =>
        new(FailFastHandler()) { BaseAddress = new Uri(Env.Required("PLATFORM_URL").TrimEnd('/') + "/"), Timeout = timeout };

    /// <summary>
    /// An unreachable host (black-holed connect) fails within 5 s and goes to the retry path, instead of holding a
    /// worker until the much longer transfer timeout.
    /// </summary>
    internal static SocketsHttpHandler FailFastHandler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };
}

/// <summary>Async lazy value that is rebuilt if building it failed (e.g. Secrets Manager briefly unreachable at cold start).</summary>
internal sealed class Cached<T>(Func<Task<T>> factory)
{
    private Task<T>? _task;

    public Task<T> Value
    {
        get
        {
            var task = _task;
            if (task is null || task.IsFaulted || task.IsCanceled)
            {
                _task = task = factory();
            }
            return task;
        }
    }
}
