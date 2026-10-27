using System.Text.Json;
using Amazon.SQS;

namespace DocBridge.Api;

/// <summary>ADR-008: one fan-out message per submission. The body is the job id only: no tokens in queues.</summary>
public sealed class JobQueue(IAmazonSQS sqs, string queueUrl)
{
    public Task PublishAsync(Guid jobId, CancellationToken ct) =>
        sqs.SendMessageAsync(queueUrl, JsonSerializer.Serialize(new { jobId }), ct);
}
