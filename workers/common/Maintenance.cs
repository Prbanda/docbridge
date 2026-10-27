using System.Text.Json;
using Amazon.SQS;
using Dapper;
using DocBridge.Shared;
using DocBridge.Shared.Jobs;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DocBridge.Workers;

/// <summary>
/// TDD 2.5: the DLQs turn "no silent loss" from a queue guarantee into a user-visible status. Exhausted messages
/// mark their tasks failed with RETRIES_EXHAUSTED plus the last error.
/// </summary>
public sealed class DlqProcessor(NpgsqlDataSource db, StatusPusher pusher, ILogger logger)
{
    public async Task TaskExhaustedAsync(Guid taskId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ExhaustedTask>(
            """
            UPDATE tasks
            SET status = 'failed', updated_at = now(),
                failure_reason = 'RETRIES_EXHAUSTED: ' || coalesce(failure_reason, 'delivery attempts exhausted')
            WHERE id = @taskId AND status IN ('pending', 'in_progress')
            RETURNING job_id, failure_reason
            """, new { taskId });
        if (row is null)
        {
            return; // already resolved, e.g. a racing duplicate delivered it
        }
        await new TaskRepository(db).TryCloseJobAsync(row.JobId);
        await pusher.PushTaskUpdateAsync(row.JobId, taskId, TaskStates.Failed, row.FailureReason, ct);
        logger.LogWarning("Task {TaskId} exhausted its retries", taskId);
    }

    public async Task JobExhaustedAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var count = await conn.ExecuteAsync(
            """
            UPDATE tasks SET status = 'failed', updated_at = now(),
                   failure_reason = 'RETRIES_EXHAUSTED: fan-out could not enqueue this task'
            WHERE job_id = @jobId AND status = 'pending'
            """, new { jobId });
        await new TaskRepository(db).TryCloseJobAsync(jobId);
        logger.LogWarning("Job {JobId} fan-out exhausted its retries; {Count} tasks marked failed", jobId, count);
    }

    private sealed record ExhaustedTask(Guid JobId, string FailureReason);
}

/// <summary>
/// EventBridge sweeper (TDD 2.5 + AM-3 + AM-4 + G8): republish jobs whose job message never made it, purge
/// never-submitted file rows past retention, purge WebSocket rows older than the API's 2 h connection limit.
/// </summary>
public sealed class Sweeper(NpgsqlDataSource db, IAmazonSQS sqs, string jobQueueUrl, ILogger logger)
{
    public static readonly TimeSpan RepublishAfter = TimeSpan.FromSeconds(Env.GetInt("SWEEPER_REPUBLISH_AFTER_SECONDS", 120));
    public static readonly int RetentionDays = Env.GetInt("STAGING_RETENTION_DAYS", 30);

    public async Task<SweepResult> RunAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var stale = (await conn.QueryAsync<Guid>(
            """
            SELECT id FROM jobs
            WHERE enqueued_at IS NULL AND completed_at IS NULL AND created_at < now() - make_interval(secs => @secs)
            ORDER BY created_at LIMIT 200
            """, new { secs = RepublishAfter.TotalSeconds })).ToList();
        foreach (var jobId in stale)
        {
            await sqs.SendMessageAsync(jobQueueUrl, JsonSerializer.Serialize(new { jobId }), ct);
        }
        var files = await conn.ExecuteAsync(
            "DELETE FROM files WHERE job_id IS NULL AND created_at < now() - make_interval(days => @days)", new { days = RetentionDays });
        var sockets = await conn.ExecuteAsync("DELETE FROM ws_connections WHERE connected_at < now() - interval '2 hours'");
        var result = new SweepResult(stale.Count, files, sockets);
        if (result.Any)
        {
            logger.LogInformation("Sweep: republished {Jobs} jobs, purged {Files} unsubmitted files, {Sockets} stale connections",
                result.RepublishedJobs, result.PurgedFiles, result.PurgedConnections);
        }
        return result;
    }
}

public sealed record SweepResult(int RepublishedJobs, int PurgedFiles, int PurgedConnections)
{
    public bool Any => RepublishedJobs + PurgedFiles + PurgedConnections > 0;
}
