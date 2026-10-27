using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using DocBridge.Workers;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DocBridge.Worker.Maintenance;

/// <summary>
/// TDD 2.5: DLQ consumers and the EventBridge sweeper. Handlers:
/// DocBridge.Worker.Maintenance::DocBridge.Worker.Maintenance.Function::TaskDlq | ::JobDlq | ::Sweep
/// </summary>
public sealed class Function
{
    private static readonly WorkerServices Services = new(SqsMessages.ConsoleLogs());

    public async Task<SQSBatchResponse> TaskDlq(SQSEvent sqsEvent, ILambdaContext context)
    {
        var dlq = await Services.DlqAsync();
        return await ForEach(sqsEvent, context, body => dlq.TaskExhaustedAsync(SqsMessages.TaskId(body), CancellationToken.None));
    }

    public async Task<SQSBatchResponse> JobDlq(SQSEvent sqsEvent, ILambdaContext context)
    {
        var dlq = await Services.DlqAsync();
        return await ForEach(sqsEvent, context, body => dlq.JobExhaustedAsync(SqsMessages.JobId(body), CancellationToken.None));
    }

    public async Task<SweepResult> Sweep(Stream input, ILambdaContext context)
    {
        var sweeper = await Services.SweeperAsync();
        return await sweeper.RunAsync(CancellationToken.None);
    }

    private static async Task<SQSBatchResponse> ForEach(SQSEvent sqsEvent, ILambdaContext context, Func<string, Task> handle)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        foreach (var record in sqsEvent.Records)
        {
            try
            {
                await handle(record.Body);
            }
            catch (FormatException ex)
            {
                context.Logger.LogError($"dropping malformed DLQ message {record.MessageId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"DLQ handling failed for {record.MessageId}: {ex}");
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }
        return new SQSBatchResponse(failures);
    }
}
