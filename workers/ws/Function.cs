using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using DocBridge.Workers;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DocBridge.Worker.Ws;

/// <summary>
/// WebSocket API route handlers (ADR-007, TDD 3.2). Handlers:
/// DocBridge.Worker.Ws::DocBridge.Worker.Ws.Function::Connect | ::Subscribe | ::Disconnect
/// </summary>
public sealed class Function
{
    private static readonly WorkerServices Services = new(SqsMessages.ConsoleLogs());
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<APIGatewayProxyResponse> Connect(APIGatewayProxyRequest request, ILambdaContext context)
    {
        string? token = null;
        request.QueryStringParameters?.TryGetValue("token", out token);
        var ws = await Services.WsAsync();
        var ok = await ws.ConnectAsync(request.RequestContext.ConnectionId, token, CancellationToken.None);
        // Rejecting $connect means API Gateway never opens the connection.
        return new APIGatewayProxyResponse { StatusCode = ok ? 200 : 401 };
    }

    public async Task<APIGatewayProxyResponse> Subscribe(APIGatewayProxyRequest request, ILambdaContext context)
    {
        Guid? jobId = null;
        try
        {
            jobId = JsonSerializer.Deserialize<SubscribeBody>(request.Body ?? "{}", Json)?.JobId;
        }
        catch (JsonException)
        {
            // falls through to the NOT_FOUND reply
        }
        var ws = await Services.WsAsync();
        await ws.SubscribeAsync(request.RequestContext.ConnectionId, jobId, CancellationToken.None);
        return new APIGatewayProxyResponse { StatusCode = 200 };
    }

    public async Task<APIGatewayProxyResponse> Disconnect(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var ws = await Services.WsAsync();
        await ws.DisconnectAsync(request.RequestContext.ConnectionId, CancellationToken.None);
        return new APIGatewayProxyResponse { StatusCode = 200 };
    }

    private sealed record SubscribeBody(string? Action, Guid? JobId);
}
