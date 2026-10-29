using DocBridge.Auth;
using DocBridge.Shared;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Db;
using DocBridge.Shared.Errors;

var builder = ServiceHost.CreateBuilder(args, defaultPort: 3001);
var settings = JwtSettings.FromEnvironment();
var dataSource = Database.CreateDataSource(Database.ConnectionStringFromEnvironment());
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(sp => TokenIssuer.FromEnvironment(settings, sp.GetRequiredService<ILogger<TokenIssuer>>()));

var app = builder.Build();
app.UseDocBridgeErrors();

await Migrator.RunAsync(dataSource, typeof(TokenIssuer).Assembly, app.Logger);
await ServiceClientSeeder.SeedAsync(dataSource, app.Logger);
_ = app.Services.GetRequiredService<TokenIssuer>();

app.MapHealth("auth", dataSource);
app.MapAuthEndpoints();
app.Run();
