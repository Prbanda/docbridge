namespace DocBridge.IntegrationTests;

public static partial class Harness
{
    /// <summary>
    /// Measured values (latencies, counts) appended to DOCBRIDGE_METRICS_FILE (default: %TEMP%/docbridge-metrics.txt),
    /// so the numbers behind each gate are kept whatever the test runner prints.
    /// </summary>
    public static void Metric(string line)
    {
        var path = Env("DOCBRIDGE_METRICS_FILE", Path.Combine(Path.GetTempPath(), "docbridge-metrics.txt"));
        lock (MetricsLock)
        {
            File.AppendAllText(path, $"{DateTime.UtcNow:O} [{BaseUrl}] {line}{Environment.NewLine}");
        }
        TestContext.Current.SendDiagnosticMessage(line);
    }

    private static readonly object MetricsLock = new();
}
