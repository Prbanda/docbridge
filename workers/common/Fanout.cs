using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DocBridge.Workers;

/// <summary>
/// ADR-008/016: read one job message, load the task ids recorded at submission, batch-send one {taskId} message per
/// task straight to the task queue. A crash mid-way redelivers the job message and the resend is absorbed by
/// per-task idempotency. Only pending tasks are sent: completed ones need nothing, in-progress ones own a message.
/// </summary>
public sealed class FanoutProcessor(NpgsqlDataSource db, IAmazonSQS sqs, string taskQueueUrl, ILogger logger)
{
    private const int BatchSize = 10;
    private const int MaxBatchAttempts = 3;

    public async Task<int> ProcessAsync(Guid jobId, CancellationToken ct)
    {
        List<Guid> taskIds;
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            taskIds = (await conn.QueryAsync<Guid>(
                "SELECT id FROM tasks WHERE job_id = @jobId AND status = 'pending' ORDER BY id", new { jobId })).ToList();
        }

        var failed = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(taskIds.Chunk(BatchSize), new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
            async (chunk, token) =>
            {
                var pending = chunk.Select((id, i) => new SendMessageBatchRequestEntry(i.ToString(),
                    JsonSerializer.Serialize(new { taskId = id }))).ToList();
                for (var attempt = 1; pending.Count > 0; attempt++)
                {
                    // SendMessageBatch reports per-entry failures: resend only those.
                    var response = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest(taskQueueUrl, pending), token);
                    var failedIds = (response.Failed ?? []).Select(f => f.Id).ToHashSet();
                    pending = pending.Where(e => failedIds.Contains(e.Id)).ToList();
                    if (pending.Count > 0 && attempt >= MaxBatchAttempts)
                    {
                        foreach (var entry in pending)
                        {
                            failed.Add(entry.MessageBody);
                        }
                        return;
                    }
                    if (pending.Count > 0)
                    {
                        await Task.Delay(200 * attempt, token);
                    }
                }
            });

        if (!failed.IsEmpty)
        {
            // Throwing leaves the job message on the queue: it is redelivered and fan-out resumes.
            throw new InvalidOperationException($"Fan-out for job {jobId}: {failed.Count} task messages could not be enqueued");
        }

        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            await conn.ExecuteAsync("UPDATE jobs SET enqueued_at = now() WHERE id = @jobId AND enqueued_at IS NULL", new { jobId });
        }
        logger.LogInformation("Fan-out for job {JobId}: {Count} task messages enqueued", jobId, taskIds.Count);
        return taskIds.Count;
    }
}
