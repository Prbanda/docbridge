using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DocBridge.Workers.LocalRunner;

/// <summary>
/// Local stand-in for the API Gateway WebSocket API (LocalStack Community has none). Clients connect to
/// /ws?token=…; route handling calls the same WsHandlers as the Lambdas; workers push through the real AWS SDK
/// client to POST /@connections/{id}, the management API path, so the push code path is identical to AWS.
/// </summary>
public sealed class WebSocketEmulator(WorkerServices services, ILogger<WebSocketEmulator> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, (WebSocket Socket, SemaphoreSlim SendLock)> _sockets = new();

    public void Map(WebApplication app)
    {
        app.UseWebSockets();
        app.Map("/ws", Accept);
        app.MapPost("/@connections/{connectionId}", PostToConnection);
        app.MapDelete("/@connections/{connectionId}", (string connectionId) =>
        {
            if (!_sockets.TryRemove(connectionId, out var entry))
            {
                return Results.StatusCode(410);
            }
            entry.Socket.Abort();
            return Results.NoContent();
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "workers-local" }));
    }

    private async Task Accept(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        var connectionId = Convert.ToBase64String(Guid.NewGuid().ToByteArray())[..12].Replace('/', '_').Replace('+', '-');
        var ws = await services.WsAsync();
        if (!await ws.ConnectAsync(connectionId, ctx.Request.Query["token"], ctx.RequestAborted))
        {
            ctx.Response.StatusCode = 401; // same as API Gateway rejecting $connect
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        _sockets[connectionId] = (socket, new SemaphoreSlim(1, 1));
        logger.LogInformation("WS connected {ConnectionId}", connectionId);
        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var body = JsonSerializer.Deserialize<RouteBody>(text, Json);
                if (body?.Action == "subscribe")
                {
                    await ws.SubscribeAsync(connectionId, body.JobId, ctx.RequestAborted);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException)
        {
            logger.LogInformation("WS {ConnectionId} closed: {Reason}", connectionId, ex.Message);
        }
        finally
        {
            _sockets.TryRemove(connectionId, out _);
            await ws.DisconnectAsync(connectionId, CancellationToken.None);
        }
    }

    private async Task<IResult> PostToConnection(string connectionId, HttpRequest request)
    {
        if (!_sockets.TryGetValue(connectionId, out var entry) || entry.Socket.State != WebSocketState.Open)
        {
            return Results.Json(new { message = "Gone" }, statusCode: 410);
        }
        using var reader = new StreamReader(request.Body);
        var payload = Encoding.UTF8.GetBytes(await reader.ReadToEndAsync());
        await entry.SendLock.WaitAsync();
        try
        {
            await entry.Socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            entry.SendLock.Release();
        }
        return Results.Ok();
    }

    private sealed record RouteBody(string? Action, Guid? JobId);
}
