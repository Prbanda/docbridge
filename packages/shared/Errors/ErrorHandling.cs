using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocBridge.Shared.Errors;

public static class ErrorHandling
{
    public static IServiceCollection AddDocBridgeErrors(this IServiceCollection services) =>
        // Malformed JSON / missing params throw instead of returning an empty 400,
        // so they get the standard error shape below.
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

    public static IApplicationBuilder UseDocBridgeErrors(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (ApiException ex)
            {
                await Write(ctx, ex.Status, ex.Code, ex.Message);
            }
            catch (BadHttpRequestException ex)
            {
                await Write(ctx, ex.StatusCode, ex.StatusCode == 413 ? "PAYLOAD_TOO_LARGE" : "BAD_REQUEST", ex.Message);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                // Client went away; nothing to write.
            }
            catch (Exception ex)
            {
                ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("DocBridge.Errors").LogError(ex, "Unhandled error on {Path}", ctx.Request.Path);
                await Write(ctx, 500, "INTERNAL", "Internal server error");
            }
        });

        app.UseStatusCodePages(async status =>
        {
            var code = status.HttpContext.Response.StatusCode switch
            {
                401 => "UNAUTHORIZED",
                403 => "FORBIDDEN",
                404 => "NOT_FOUND",
                405 => "METHOD_NOT_ALLOWED",
                _ => "ERROR",
            };
            await status.HttpContext.Response.WriteAsJsonAsync(ErrorEnvelope.Of(code, code.Replace('_', ' ').ToLowerInvariant()));
        });
        return app;
    }

    private static async Task Write(HttpContext ctx, int status, string code, string message)
    {
        if (ctx.Response.HasStarted)
        {
            return;
        }
        ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(ErrorEnvelope.Of(code, message));
    }
}
