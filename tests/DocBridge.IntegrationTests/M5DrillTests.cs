using System.Diagnostics;
using Amazon.SQS;
using static DocBridge.IntegrationTests.Harness;

namespace DocBridge.IntegrationTests;

/// <summary>
/// M5 failure drills (TDD M5 / failure-modes table). They take services down, so they only run when asked:
///   DOCBRIDGE_DRILLS=local   docker compose (scripts/drill-local.sh)
///   DOCBRIDGE_DRILLS=aws     ECS in the deployed env (DOCBRIDGE_ECS_CLUSTER, default docbridge-dev)
/// Outage lengths follow the retry backoff: RETRY_BACKOFF_SECONDS is 10,20 locally and 60,300 on AWS.
/// </summary>
[Collection("drills")]
public sealed class M5DrillTests
{
    private static readonly string Mode = Env("DOCBRIDGE_DRILLS", "");
    private static readonly bool Aws = Mode == "aws";
    private static readonly string RepoRoot = Env("DOCBRIDGE_REPO_ROOT", FindRepoRoot());

    private static void RequireDrills()
    {
        if (Mode is not ("local" or "aws"))
        {
            Assert.Skip("set DOCBRIDGE_DRILLS=local|aws to run failure drills");
        }
    }

    [Fact]
    public async Task Platform_outage_shorter_than_the_retry_window_drains_with_an_empty_dlq()
    {
        RequireDrills();
        // Submission itself validates membership with the platform (TDD failure modes: 502 while it's down), so the
        // platform is killed right after a large job is accepted, while most of its 400 deliveries are still queued.
        var ws = await NewWorkspace(teams: 2, prefix: "outage", folders: true);
        var files = await Stage(ws.User, 100, size: 64 * 1024);
        var dlqBefore = await DlqDepth();

        var jobId = await Submit(ws.User, files, ws.Destinations);
        try
        {
            await StopPlatform();
            await Task.Delay(TimeSpan.FromSeconds(Aws ? 45 : 8));
        }
        finally
        {
            await StartPlatform();
        }

        var job = await WaitForJob(ws.User, jobId, TimeSpan.FromMinutes(Aws ? 20 : 3));
        Assert.Equal(400, job.GetProperty("counts").GetProperty("completed").GetInt64());
        Assert.Equal(0, job.GetProperty("counts").GetProperty("failed").GetInt64());
        var tasks = await AllTasks(ws.User, jobId);
        Assert.Contains(tasks, t => t.GetProperty("attemptCount").GetInt32() > 1); // retries really happened
        Assert.Equal(dlqBefore, await DlqDepth());
    }

    [Fact]
    public async Task Platform_outage_longer_than_all_retries_surfaces_every_task_as_retries_exhausted()
    {
        RequireDrills();
        var ws = await NewWorkspace(teams: 2, prefix: "exhaust", folders: true);
        var files = await Stage(ws.User, 30, size: 64 * 1024);

        // On AWS delivery is fast enough to finish before ECS drains the platform, so the delivery trigger is paused
        // while the job is accepted (submission needs the platform up) and resumed once the platform is gone.
        if (Aws)
        {
            await SetDeliveryTrigger(enabled: false);
        }
        var jobId = await Submit(ws.User, files, ws.Destinations);
        try
        {
            await StopPlatform();
            if (Aws)
            {
                await SetDeliveryTrigger(enabled: true);
            }
            // Attempts at t≈0, +backoff1, +backoff2, then the DLQ consumer: every undelivered task ends failed.
            var job = await WaitForJob(ws.User, jobId, TimeSpan.FromMinutes(Aws ? 20 : 4));
            Assert.Equal("completed_with_errors", job.GetProperty("aggregateStatus").GetString());
            var failed = await AllTasks(ws.User, jobId, "failed");
            Assert.NotEmpty(failed);
            Assert.All(failed, t =>
            {
                var reason = t.GetProperty("failureReason").GetString()!;
                Assert.StartsWith("RETRIES_EXHAUSTED", reason);
                Assert.Contains("attempt 3", reason); // three delivery attempts before giving up
                Assert.Equal(3, t.GetProperty("attemptCount").GetInt32());
            });
            // Nothing silently lost: every one of the 120 tasks is accounted for as completed or failed.
            var counts = job.GetProperty("counts");
            Assert.Equal(120, counts.GetProperty("completed").GetInt64() + counts.GetProperty("failed").GetInt64());
        }
        finally
        {
            if (Aws)
            {
                await SetDeliveryTrigger(enabled: true);
            }
            await StartPlatform();
        }
    }

    /// <summary>Enable/disable the delivery Lambda's SQS event source mapping and wait until the change took effect.</summary>
    private static async Task SetDeliveryTrigger(bool enabled)
    {
        var function = $"{Env("DOCBRIDGE_ECS_CLUSTER", "docbridge-dev")}-delivery";
        var uuid = (await Run("aws", $"lambda list-event-source-mappings --function-name {function} --query EventSourceMappings[0].UUID --output text")).Trim();
        await Run("aws", $"lambda update-event-source-mapping --uuid {uuid} {(enabled ? "--enabled" : "--no-enabled")} --query State --output text");
        var target = enabled ? "Enabled" : "Disabled";
        for (var i = 0; i < 60; i++)
        {
            var state = (await Run("aws", $"lambda get-event-source-mapping --uuid {uuid} --query State --output text")).Trim();
            if (state == target)
            {
                return;
            }
            await Task.Delay(5000);
        }
        Assert.Fail($"event source mapping did not become {target}");
    }

    [Fact]
    public async Task Job_committed_without_its_queue_message_is_republished_by_the_sweeper()
    {
        RequireDrills();
        if (Aws)
        {
            Assert.Skip("needs a broken job-queue URL on the API; local drill only (see M5-TESTING §3)");
        }
        var ws = await NewWorkspace(teams: 1, prefix: "sweep", folders: false);
        var files = await Stage(ws.User, 2);
        await Compose("up -d --no-deps docbridge-api", ("DOCBRIDGE_API_JOB_QUEUE_URL", "http://localstack:4566/000000000000/no-such-queue"));
        await WaitHealthy("jobs");
        Guid jobId;
        try
        {
            jobId = await Submit(ws.User, files, ws.Destinations); // 201 although the publish fails
        }
        finally
        {
            await Compose("up -d --no-deps docbridge-api");
            await WaitHealthy("jobs");
        }
        var job = await WaitForJob(ws.User, jobId, TimeSpan.FromMinutes(4)); // sweeper: every 30 s, after 60 s
        Assert.Equal("completed", job.GetProperty("aggregateStatus").GetString());
    }

    // ---------------------------------------------------------------- controls

    private static Task StopPlatform() => Aws ? ScalePlatform(0) : Compose("stop platform");

    private static async Task StartPlatform()
    {
        if (Aws)
        {
            await ScalePlatform(2);
            return;
        }
        await Compose("start platform");
        await WaitHealthy("teams");
    }

    private static async Task ScalePlatform(int count)
    {
        var cluster = Env("DOCBRIDGE_ECS_CLUSTER", "docbridge-dev");
        await Run("aws", $"ecs update-service --cluster {cluster} --service platform --desired-count {count} --query service.serviceName --output text");
        if (count > 0)
        {
            await Run("aws", $"ecs wait services-stable --cluster {cluster} --services platform");
        }
        else
        {
            // Wait until no task is left running, so the outage is real.
            for (var i = 0; i < 60; i++)
            {
                var running = await Run("aws", $"ecs describe-services --cluster {cluster} --services platform --query services[0].runningCount --output text");
                if (running.Trim() == "0")
                {
                    return;
                }
                await Task.Delay(5000);
            }
        }
    }

    private static async Task WaitHealthy(string path)
    {
        for (var i = 0; i < 60; i++)
        {
            var (status, _, _) = await Get(path, null);
            if (status == System.Net.HttpStatusCode.Unauthorized)
            {
                await Task.Delay(2000); // the edge answers; give the restarted service a moment to finish starting
                return;
            }
            await Task.Delay(1000);
        }
    }

    private static Task<string> Compose(string args, params (string Key, string Value)[] env) =>
        Run("docker", $"compose -f \"{Path.Combine(RepoRoot, "docker-compose.yml")}\" {args}", env);

    private static async Task<string> Run(string file, string args, params (string Key, string Value)[] env)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = RepoRoot };
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{file} {args} failed: {stderr}");
        return stdout;
    }

    private static async Task<int> DlqDepth()
    {
        var url = Env("DOCBRIDGE_TASK_DLQ_URL", "http://localhost:4566/000000000000/docbridge-task-queue-dlq");
        using var sqs = url.StartsWith("http://localhost", StringComparison.Ordinal)
            ? new AmazonSQSClient(new Amazon.Runtime.BasicAWSCredentials("test", "test"), new AmazonSQSConfig { ServiceURL = "http://localhost:4566", AuthenticationRegion = "us-east-1" })
            : new AmazonSQSClient(Amazon.RegionEndpoint.USEast1);
        var attrs = await sqs.GetQueueAttributesAsync(url, ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"]);
        return attrs.ApproximateNumberOfMessages + attrs.ApproximateNumberOfMessagesNotVisible;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

[CollectionDefinition("drills", DisableParallelization = true)]
public sealed class DrillCollection;
