using DocBridge.Api;
using DocBridge.Shared;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Clients;
using DocBridge.Shared.Db;
using DocBridge.Shared.Errors;
using DocBridge.Shared.Jobs;

var builder = ServiceHost.CreateBuilder(args, defaultPort: 3003);
var dataSource = Database.CreateDataSource(Database.ConnectionStringFromEnvironment());
var authUrl = Env.Required("AUTH_URL");
builder.Services.AddSingleton(dataSource);
builder.Services.AddDocBridgeAuth(Env.Get("AUTH_JWKS_URL", $"{authUrl.TrimEnd('/')}/.well-known/jwks.json"), JwtSettings.FromEnvironment());

builder.Services.AddHttpClient("auth", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("platform", c =>
    {
        c.BaseAddress = new Uri(Env.Required("PLATFORM_URL").TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(5); // TDD 1.5
    })
    // Keep ALB connections through lulls (ALB idle timeout is 300 s) so a submission's parallel membership
    // checks reuse them instead of opening ~11 new connections at once.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(3),
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton(sp => new ServiceTokenClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("auth"), authUrl,
    new ServiceCredentials("docbridge-api", Env.Required("DOCBRIDGE_API_CLIENT_SECRET"), "memberships:read")));
builder.Services.AddSingleton(sp => new PlatformClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("platform"),
    sp.GetRequiredService<ServiceTokenClient>()));
builder.Services.AddSingleton<TeamAccess>();

var staging = Env.Required("STAGING_BUCKET");
// Presigned URLs must name an endpoint the browser can reach (LocalStack at localhost locally).
builder.Services.AddSingleton(new StagingStore(AwsClients.S3(), AwsClients.S3(Env.Optional("S3_PUBLIC_ENDPOINT")), staging));
builder.Services.AddSingleton(new JobQueue(AwsClients.Sqs(), Env.Required("JOB_QUEUE_URL")));

var app = builder.Build();
app.UsePreflightPassthrough();
app.UseDocBridgeErrors();
app.UseAuthentication();
app.UseAuthorization();

await Migrator.RunAsync(dataSource, typeof(StagingStore).Assembly, app.Logger);

// Keep the service token fresh while idle too: after a quiet period the first submission would otherwise fetch a
// new token inline (measured ~1.2 s on AWS after a few idle hours). No network call unless a refresh is due.
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while (await timer.WaitForNextTickAsync())
    {
        await app.Services.GetRequiredService<ServiceTokenClient>().WarmAsync();
    }
});

// Everything a new task's first submission would otherwise pay for (measured on AWS: ~4 s cold before ReadyToRun,
// then ~90 ms expanding a 2,000-task mapping and ~200 ms for the first parallel platform calls). The ALB only routes
// here once this has run (MapHealth).
app.MapHealth("docbridge-api", dataSource, async () =>
{
    await app.Services.GetRequiredService<ServiceTokenClient>().WarmAsync();
    await app.Services.GetRequiredService<JwksCache>().GetKeysAsync(null, CancellationToken.None);
    var team = Guid.NewGuid();
    JobEndpoints.Expand(new SubmitJobRequest(Enumerable.Range(0, 100).Select(_ => new MappingInput(Guid.NewGuid(),
        Enumerable.Range(0, 20).Select(_ => new DestinationInput(team, Guid.NewGuid(), null, "warm-up")).ToList())).ToList()));
    var platform = app.Services.GetRequiredService<PlatformClient>();
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => platform.IsMemberAsync(Guid.NewGuid(), Guid.NewGuid())));
});
app.MapDestinationEndpoints();
app.MapFileEndpoints();
app.MapJobEndpoints();
app.Run();
