using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using DocBridge.Shared;
using DocBridge.Workers;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DocBridge.Worker.Delivery;

/// <summary>
/// Task queue consumer (TDD 2.4). Permanent outcomes ack the message; transient ones shorten the message's
/// visibility to the backoff delay (G6) and report it as a batch item failure, so SQS redelivers it and, after
/// maxReceiveCount 3, moves it to the DLQ. Handler: DocBridge.Worker.Delivery::DocBridge.Worker.Delivery.Function::Handle
/// </summary>
public sealed class Function
{
    private static readonly WorkerServices Services = new(SqsMessages.ConsoleLogs());
    private static readonly string QueueUrl = Env.Required("TASK_QUEUE_URL");

    public async Task<SQSBatchResponse> Handle(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        var delivery = await Services.DeliveryAsync();
        foreach (var record in sqsEvent.Records)
        {
            try
            {
                var outcome = await delivery.ProcessAsync(SqsMessages.TaskId(record.Body), CancellationToken.None);
                if (outcome.Kind == DeliveryOutcomeKind.Retry)
                {
                    await Services.Sqs.ChangeMessageVisibilityAsync(QueueUrl, record.ReceiptHandle, outcome.RetryDelaySeconds);
                    failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
                }
            }
            catch (FormatException ex)
            {
                // A malformed message can never succeed: log it and ack it.
                context.Logger.LogError($"dropping malformed message {record.MessageId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"delivery failed for message {record.MessageId}: {ex}");
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }
        return new SQSBatchResponse(failures);
    }
}
