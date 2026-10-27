using DocBridge.Shared;
using DocBridge.Workers;
using DocBridge.Workers.LocalRunner;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Env.GetInt("PORT", 3005)}");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = false);
ServiceHost.QuietFrameworkLogs(builder.Logging);
builder.Services.AddSingleton(sp => new WorkerServices(sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<WebSocketEmulator>();
builder.Services.AddHostedService<QueuePollers>();

var app = builder.Build();
app.Services.GetRequiredService<WebSocketEmulator>().Map(app);
app.Run();
