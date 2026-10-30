using DocBridge.Platform;
using DocBridge.Shared;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Db;
using DocBridge.Shared.Errors;

var builder = ServiceHost.CreateBuilder(args, defaultPort: 3002);
var dataSource = Database.CreateDataSource(Database.ConnectionStringFromEnvironment());
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(new PlatformStorage(AwsClients.S3(), Env.Required("PLATFORM_BUCKET")));
builder.Services.AddDocBridgeAuth(Env.Required("AUTH_JWKS_URL"), JwtSettings.FromEnvironment(),
    "memberships:read", "documents:ingest", "platform:admin");

var app = builder.Build();
app.UsePreflightPassthrough();
app.UseDocBridgeErrors();
app.UseAuthentication();
app.UseAuthorization();

await Migrator.RunAsync(dataSource, typeof(PlatformStorage).Assembly, app.Logger);

app.MapHealth("platform", dataSource);
app.MapHierarchyEndpoints();
app.MapIngestEndpoint();
app.MapAdminEndpoints();
app.MapSearchEndpoint();
app.Run();
