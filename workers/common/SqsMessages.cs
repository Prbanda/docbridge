using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DocBridge.Workers;

public static class SqsMessages
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Guid TaskId(string body) => Parse(body).TaskId ?? throw new FormatException("message has no taskId");

    public static Guid JobId(string body) => Parse(body).JobId ?? throw new FormatException("message has no jobId");

    private static Message Parse(string body) => JsonSerializer.Deserialize<Message>(body, Json) ?? throw new FormatException("empty message");

    private sealed record Message(Guid? TaskId, Guid? JobId);

    public static ILoggerFactory ConsoleLogs() =>
        LoggerFactory.Create(b => DocBridge.Shared.ServiceHost.QuietFrameworkLogs(b.AddJsonConsole(o => o.IncludeScopes = false).SetMinimumLevel(LogLevel.Information)));
}
