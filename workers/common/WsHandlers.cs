using Dapper;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Errors;
using DocBridge.Shared.Jobs;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DocBridge.Workers;

/// <summary>
/// ADR-007 lifecycle: $connect authenticates the JWT (query param: browsers can't set WS handshake headers) and
/// registers the connection; subscribe binds it to one job, using the same visibility rule as GET /jobs/:id;
/// $disconnect removes it. API Gateway only holds raw connections, so ws_connections is the registry.
/// </summary>
public sealed class WsHandlers(NpgsqlDataSource db, JwtValidator validator, TeamAccess access, StatusPusher pusher, ILogger logger)
{
    public async Task<bool> ConnectAsync(string connectionId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }
        VerifiedToken verified;
        try
        {
            verified = await validator.ValidateAsync(token, ct);
        }
        catch (ApiException)
        {
            return false;
        }
        if (verified.IsService || !Guid.TryParse(verified.Subject, out var userId))
        {
            return false;
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO ws_connections (connection_id, user_id) VALUES (@connectionId, @userId) ON CONFLICT (connection_id) DO NOTHING",
            new { connectionId, userId });
        return true;
    }

    public async Task SubscribeAsync(string connectionId, Guid? jobId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var userId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT user_id FROM ws_connections WHERE connection_id = @connectionId", new { connectionId });
        var submitter = jobId is null ? null : await conn.ExecuteScalarAsync<Guid?>(
            "SELECT submitted_by_user_id FROM jobs WHERE id = @jobId", new { jobId });
        if (userId is null || submitter is null || !await access.SharesTeamAsync(userId.Value, submitter.Value, ct))
        {
            await pusher.SendToConnectionAsync(connectionId, new { type = "error", code = "NOT_FOUND", jobId }, ct);
            return;
        }
        await conn.ExecuteAsync("UPDATE ws_connections SET job_id = @jobId WHERE connection_id = @connectionId", new { connectionId, jobId });
        // Catch-up is client-driven (TDD 3.3): on this ack the client refetches GET /jobs/:id once, then applies pushes.
        await pusher.SendToConnectionAsync(connectionId, new { type = "subscribed", jobId }, ct);
        logger.LogInformation("Connection {ConnectionId} subscribed to job {JobId}", connectionId, jobId);
    }

    public async Task DisconnectAsync(string connectionId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM ws_connections WHERE connection_id = @connectionId", new { connectionId });
    }
}
