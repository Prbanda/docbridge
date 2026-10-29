using Dapper;
using DocBridge.Shared.Jobs;
using Npgsql;

namespace DocBridge.Workers;

public sealed record ClaimedTask(
    Guid Id, Guid JobId, Guid FileId, Guid TeamId, Guid BinderId, Guid? FolderId, string? DestinationLabel, int AttemptCount,
    string FileName, long SizeBytes, string ContentType, string ChecksumSha256, string S3Key, Guid SubmittedByUserId)
{
    public string Destination => DestinationLabel ?? $"binder {BinderId}" + (FolderId is null ? "" : $" / folder {FolderId}");
}

/// <summary>The SQL the pipeline's correctness rests on (ADR-018: written exactly as the TDD specifies).</summary>
public sealed class TaskRepository(NpgsqlDataSource db)
{
    public NpgsqlDataSource DataSource => db;

    /// <summary>
    /// TDD 2.4 conditional claim. Re-claiming in_progress is allowed (a worker died and SQS redelivered); a
    /// terminal task returns nothing and the message is acked. Exactly-once outcome from at-least-once delivery.
    /// </summary>
    public async Task<ClaimedTask?> ClaimAsync(Guid taskId)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<ClaimedTask>(
            """
            WITH claimed AS (
              UPDATE tasks SET status = 'in_progress', attempt_count = attempt_count + 1, updated_at = now()
              WHERE id = @taskId AND status IN ('pending', 'in_progress')
              RETURNING *
            )
            SELECT c.id, c.job_id, c.file_id, c.team_id, c.binder_id, c.folder_id, c.destination_label, c.attempt_count,
                   f.original_name AS file_name, f.size_bytes, f.content_type, f.checksum_sha256, f.s3_key, j.submitted_by_user_id
            FROM claimed c JOIN files f ON f.id = c.file_id JOIN jobs j ON j.id = c.job_id
            """, new { taskId });
    }

    public async Task CompleteAsync(Guid taskId, Guid documentId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await conn.ExecuteAsync(
            """
            UPDATE tasks SET status = 'completed', platform_document_id = @documentId, failure_reason = NULL, updated_at = now()
            WHERE id = @taskId AND status <> 'completed'
            """, new { taskId, documentId });
    }

    /// <summary>Permanent failure. Never overwrites a completed task (a racing duplicate may have delivered it).</summary>
    public async Task FailAsync(Guid taskId, string reason)
    {
        await using var conn = await db.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE tasks SET status = 'failed', failure_reason = @reason, updated_at = now() WHERE id = @taskId AND status <> 'completed'",
            new { taskId, reason });
    }

    /// <summary>Transient failure: status stays in_progress, the last error is kept for the DLQ consumer to surface.</summary>
    public async Task RecordTransientAsync(Guid taskId, string reason)
    {
        await using var conn = await db.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE tasks SET failure_reason = @reason, updated_at = now() WHERE id = @taskId AND status = 'in_progress'",
            new { taskId, reason });
    }

    /// <summary>
    /// Sets completed_at once no unresolved task remains. Runs as its own statement after the task's status write
    /// has committed, so of two tasks finishing together the later one always sees both and closes the job.
    /// </summary>
    public async Task<bool> TryCloseJobAsync(Guid jobId)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.ExecuteAsync(
            """
            UPDATE jobs SET completed_at = now()
            WHERE id = @jobId AND completed_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM tasks WHERE job_id = @jobId AND status IN ('pending', 'in_progress'))
            """, new { jobId }) > 0;
    }

    public async Task<TaskCounts> CountsAsync(Guid jobId)
    {
        await using var conn = await db.OpenConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<JobCountsRow>(TaskCounts.CountsSql, new { jobIds = new[] { jobId } });
        return row?.Counts ?? new TaskCounts(0, 0, 0, 0);
    }

    public async Task<string?> StatusAsync(Guid taskId)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<string?>("SELECT status FROM tasks WHERE id = @taskId", new { taskId });
    }
}
