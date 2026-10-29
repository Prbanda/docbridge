using System.Text;
using System.Text.Json;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Dapper;
using DocBridge.Shared;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DocBridge.Workers;

public interface IConnectionSender
{
    /// <summary>Returns false when the connection is gone (410).</summary>
    Task<bool> SendAsync(string connectionId, string json, CancellationToken ct);
}

/// <summary>PostToConnection against the WebSocket API's management endpoint (AWS, or the local emulator).</summary>
public sealed class ApiGatewaySender(IAmazonApiGatewayManagementApi client) : IConnectionSender
{
    public static ApiGatewaySender? FromEnvironment() =>
        Env.Optional("WS_CALLBACK_URL") is { } url
            ? new ApiGatewaySender(new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig
            {
                ServiceURL = url,
                AuthenticationRegion = AwsClients.Region,
            }))
            : null;

    public async Task<bool> SendAsync(string connectionId, string json, CancellationToken ct)
    {
        try
        {
            await client.PostToConnectionAsync(new PostToConnectionRequest
            {
                ConnectionId = connectionId,
                Data = new MemoryStream(Encoding.UTF8.GetBytes(json)),
            }, ct);
            return true;
        }
        catch (GoneException)
        {
            return false;
        }
    }
}

/// <summary>
/// TDD 3.3: after each status write, push to the connections subscribed to that job. Counts ride along so the
/// client updates the aggregate without refetching. Push failures are logged and swallowed: they never fail a delivery.
/// </summary>
public sealed class StatusPusher(NpgsqlDataSource db, IConnectionSender? sender, ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PushTaskUpdateAsync(Guid jobId, Guid taskId, string status, string? failureReason, CancellationToken ct = default)
    {
        if (sender is null)
        {
            return;
        }
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            var connections = (await conn.QueryAsync<string>(
                "SELECT connection_id FROM ws_connections WHERE job_id = @jobId", new { jobId })).ToList();
            if (connections.Count == 0)
            {
                return;
            }
            var counts = await new TaskRepository(db).CountsAsync(jobId);
            var message = JsonSerializer.Serialize(new
            {
                type = "task_update",
                jobId,
                taskId,
                status,
                failureReason,
                counts = new { pending = counts.Pending, inProgress = counts.InProgress, completed = counts.Completed, failed = counts.Failed },
                aggregateStatus = counts.Aggregate(),
            }, Json);
            await SendAllAsync(conn, connections, message, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Status push for task {TaskId} failed (non-fatal)", taskId);
        }
    }

    public async Task SendToConnectionAsync(string connectionId, object payload, CancellationToken ct = default)
    {
        if (sender is null)
        {
            return;
        }
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await SendAllAsync(conn, [connectionId], JsonSerializer.Serialize(payload, Json), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Send to connection {ConnectionId} failed (non-fatal)", connectionId);
        }
    }

    private async Task SendAllAsync(NpgsqlConnection conn, IReadOnlyList<string> connections, string message, CancellationToken ct)
    {
        foreach (var id in connections)
        {
            if (!await sender!.SendAsync(id, message, ct))
            {
                await conn.ExecuteAsync("DELETE FROM ws_connections WHERE connection_id = @id", new { id });
            }
        }
    }
}
