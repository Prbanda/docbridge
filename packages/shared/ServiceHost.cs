using Dapper;
using DocBridge.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DocBridge.Shared;

public static class ServiceHost
{
    /// <summary>The longest a task may hold back its health check while warming up.</summary>
    private static readonly TimeSpan WarmUpLimit = TimeSpan.FromSeconds(60);

    public static WebApplicationBuilder CreateBuilder(string[] args, int defaultPort)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{Env.GetInt("PORT", defaultPort)}");
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o => o.IncludeScopes = false);
        QuietFrameworkLogs(builder.Logging);
        builder.Services.AddDocBridgeErrors();
        builder.Services.AddProblemDetails();
        return builder;
    }

    /// <summary>Framework request chatter at Information would drown the application's own logs (and cost CloudWatch money).</summary>
    public static ILoggingBuilder QuietFrameworkLogs(ILoggingBuilder logging) => logging
        .AddFilter("Microsoft", LogLevel.Warning)
        .AddFilter("System.Net.Http", LogLevel.Warning)
        .AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error) // unused (no cookies/antiforgery), warns at startup
        .AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

    /// <summary>
    /// The HTTP API forwards CORS preflights to us (its OPTIONS /{proxy+} route has an integration) and replaces our
    /// response headers with its own cors_configuration. All a preflight needs from us is a 2xx before routing or auth
    /// can turn it into a 405/401. Register first.
    /// </summary>
    public static IApplicationBuilder UsePreflightPassthrough(this IApplicationBuilder app) =>
        app.Use((ctx, next) =>
        {
            if (!HttpMethods.IsOptions(ctx.Request.Method))
            {
                return next();
            }
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

    /// <summary>
    /// /health = process up and warmed (ALB target health). /health/ready = this service's own credentials reach its
    /// own logical database (ADR-009). Both are anonymous: the gateway never routes them.
    ///
    /// /health answers 503 until <paramref name="warmUp"/> (plus the DB pool warm-up) finishes or 60 s pass, so the ALB
    /// sends no traffic to a new task before it has paid its first-request costs (measured on AWS: up to ~600 ms).
    /// </summary>
    public static void MapHealth(this WebApplication app, string service, NpgsqlDataSource dataSource, Func<Task>? warmUp = null)
    {
        var warmed = Task.Run(async () =>
        {
            try
            {
                await Db.Database.WarmPoolAsync(dataSource);
                if (warmUp is not null)
                {
                    await warmUp();
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Warm-up incomplete; serving anyway");
            }
        });
        var deadline = Task.Delay(WarmUpLimit);
        app.MapGet("/health", () => warmed.IsCompleted || deadline.IsCompleted
                ? Results.Ok(new { status = "ok", service })
                : Results.Json(new { status = "warming", service }, statusCode: 503))
            .AllowAnonymous();
        app.MapGet("/health/ready", async (CancellationToken ct) =>
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteScalarAsync<int>("SELECT 1");
            return Results.Ok(new { status = "ready", service });
        }).AllowAnonymous();
    }
}
