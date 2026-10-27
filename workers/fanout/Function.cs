using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using DocBridge.Workers;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DocBridge.Worker.Fanout;

/// <summary>Job queue consumer (ADR-008). Handler: DocBridge.Worker.Fanout::DocBridge.Worker.Fanout.Function::Handle</summary>
public sealed class Function
{
    private static readonly WorkerServices Services = new(SqsMessages.ConsoleLogs());

    public async Task<SQSBatchResponse> Handle(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        var fanout = await Services.FanoutAsync();
        foreach (var record in sqsEvent.Records)
        {
            try
            {
                await fanout.ProcessAsync(SqsMessages.JobId(record.Body), CancellationToken.None);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"fan-out failed for message {record.MessageId}: {ex}");
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }
        return new SQSBatchResponse(failures);
    }
}
