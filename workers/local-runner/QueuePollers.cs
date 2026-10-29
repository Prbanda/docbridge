using Amazon.SQS.Model;
using DocBridge.Shared;

namespace DocBridge.Workers.LocalRunner;

/// <summary>
/// Stands in for the Lambda SQS event source mappings: long-polls each queue and hands every message to the same
/// processor the Lambda calls. Ack = delete; retry = shorten visibility; failure = leave the message to reappear.
/// </summary>
public sealed class QueuePollers(WorkerServices services, ILogger<QueuePollers> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Poll("fan-out", Env.Required("JOB_QUEUE_URL"), 2, stoppingToken, async (msg, url, ct) =>
        {
            await (await services.FanoutAsync()).ProcessAsync(SqsMessages.JobId(msg.Body), ct);
            return null;
        }),
        Poll("delivery", Env.Required("TASK_QUEUE_URL"), Env.GetInt("DELIVERY_CONCURRENCY", 8), stoppingToken, async (msg, url, ct) =>
        {
            var outcome = await (await services.DeliveryAsync()).ProcessAsync(SqsMessages.TaskId(msg.Body), ct);
            return outcome.Kind == DeliveryOutcomeKind.Retry ? outcome.RetryDelaySeconds : null;
        }),
        Poll("task-dlq", Env.Required("TASK_DLQ_URL"), 1, stoppingToken, async (msg, url, ct) =>
        {
            await (await services.DlqAsync()).TaskExhaustedAsync(SqsMessages.TaskId(msg.Body), ct);
            return null;
        }),
        Poll("job-dlq", Env.Required("JOB_DLQ_URL"), 1, stoppingToken, async (msg, url, ct) =>
        {
            await (await services.DlqAsync()).JobExhaustedAsync(SqsMessages.JobId(msg.Body), ct);
            return null;
        }),
        SweepLoop(stoppingToken));

    /// <param name="handle">Returns a retry delay in seconds, or null when the message is done.</param>
    private async Task Poll(string name, string queueUrl, int concurrency, CancellationToken ct,
        Func<Message, string, CancellationToken, Task<int?>> handle)
    {
        using var slots = new SemaphoreSlim(concurrency);
        logger.LogInformation("Polling {Queue} ({Url}), concurrency {Concurrency}", name, queueUrl, concurrency);
        while (!ct.IsCancellationRequested)
        {
            List<Message> messages;
            try
            {
                var response = await services.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = Math.Min(10, concurrency),
                    WaitTimeSeconds = 5,
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"],
                }, ct);
                messages = response.Messages ?? [];
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Receive from {Queue} failed; retrying", name);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            foreach (var message in messages)
            {
                await slots.WaitAsync(ct);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var retryIn = await handle(message, queueUrl, ct);
                        if (retryIn is { } delay)
                        {
                            await services.Sqs.ChangeMessageVisibilityAsync(queueUrl, message.ReceiptHandle, delay, ct);
                        }
                        else
                        {
                            await services.Sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
                        }
                    }
                    catch (FormatException ex)
                    {
                        logger.LogError("Dropping malformed {Queue} message: {Error}", name, ex.Message);
                        await services.Sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "{Queue} handler failed; message will be redelivered", name);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, ct);
            }
        }
    }

    private async Task SweepLoop(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Env.GetInt("SWEEP_INTERVAL_SECONDS", 60));
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            try
            {
                await (await services.SweeperAsync()).RunAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Sweep failed");
            }
        }
    }
}
