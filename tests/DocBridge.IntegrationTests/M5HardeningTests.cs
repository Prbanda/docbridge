using System.Net;
using System.Text.Json;
using Amazon.SQS;
using static DocBridge.IntegrationTests.Harness;

namespace DocBridge.IntegrationTests;

/// <summary>
/// M5: the Blueprint success metrics. Load tests run only with DOCBRIDGE_RUN_LOAD=1 (they push tens of thousands of
/// deliveries through the stack). Platform kill/recover and DLQ drills need container control: scripts/drill-local.sh.
/// </summary>
public sealed class M5HardeningTests
{
    private static void RequireLoad()
    {
        if (Env("DOCBRIDGE_RUN_LOAD", "") != "1")
        {
            Assert.Skip("set DOCBRIDGE_RUN_LOAD=1 to run load gates");
        }
    }

    private static async Task<Workspace> TwentyDestinations(string prefix) => await NewWorkspace(teams: 10, prefix: prefix, folders: true);

    [Fact]
    public async Task Submission_p99_stays_under_500ms_for_100_by_20_jobs()
    {
        RequireLoad();
        var ws = await TwentyDestinations("p99");
        Assert.Equal(20, ws.Destinations.Count);
        var runs = Env("DOCBRIDGE_P99_RUNS", "10") is var r ? int.Parse(r) : 10;
        var latencies = new List<double>();
        var jobs = new List<Guid>();
        for (var i = 0; i < runs; i++)
        {
            var files = await Stage(ws.User, 100, size: 1024);
            var (status, body, elapsed) = await Post("jobs", ws.User.Token, Mappings(files, ws.Destinations));
            Assert.Equal(HttpStatusCode.Created, status);
            Assert.Equal(2000, body.GetProperty("taskCount").GetInt32());
            latencies.Add(elapsed.TotalMilliseconds);
            jobs.Add(body.GetProperty("jobId").GetGuid());
        }
        var sorted = latencies.Order().ToList();
        var p99 = sorted[(int)Math.Ceiling(0.99 * sorted.Count) - 1];
        Metric(
            $"POST /jobs 2,000 tasks over {runs} runs: p99 {p99:F0} ms, median {sorted[sorted.Count / 2]:F0} ms, max {sorted[^1]:F0} ms");
        Assert.InRange(p99, 0, 500);

        foreach (var jobId in jobs)
        {
            var job = await WaitForJob(ws.User, jobId, TimeSpan.FromMinutes(20));
            Assert.Equal("completed", job.GetProperty("aggregateStatus").GetString());
            Assert.Equal(2000, job.GetProperty("counts").GetProperty("completed").GetInt64());
        }
    }

    [Fact]
    public async Task One_revoked_destination_fails_one_task_and_the_rest_complete()
    {
        RequireLoad();
        var ws = await TwentyDestinations("isolate");
        var files = await Stage(ws.User, 100, size: 1024);
        var isolatedTeam = await CreateTeam(Unique("isolated"));
        await AddMember(isolatedTeam, ws.User.Id);
        var isolatedBinder = await AdminCreate($"admin/teams/{isolatedTeam}/binders", new { name = "B" });
        var isolated = new Dest(isolatedTeam, isolatedBinder, null, "Isolated / B");

        // 100 files × 19 destinations, plus exactly one task to the destination we revoke.
        var body = new
        {
            mappings = files.Select((f, i) => new
            {
                fileId = f.FileId,
                destinations = ws.Destinations.Take(19).Select(d => d.Body).Concat(i == 0 ? [isolated.Body] : []).ToList(),
            }),
        };
        var (status, ack, _) = await Post("jobs", ws.User.Token, body);
        Assert.Equal(HttpStatusCode.Created, status);
        await RemoveMember(isolatedTeam, ws.User.Id);
        var jobId = ack.GetProperty("jobId").GetGuid();
        Assert.Equal(1901, ack.GetProperty("taskCount").GetInt32());

        var job = await WaitForJob(ws.User, jobId, TimeSpan.FromMinutes(15));
        var counts = job.GetProperty("counts");
        Metric($"isolation: {counts}");
        Assert.Equal(1900, counts.GetProperty("completed").GetInt64());
        Assert.Equal(1, counts.GetProperty("failed").GetInt64());
        var failed = Assert.Single(await AllTasks(ws.User, jobId, "failed"));
        Assert.Equal(isolatedTeam, failed.GetProperty("teamId").GetGuid());
        Assert.StartsWith("NOT_AUTHORIZED_AT_DELIVERY", failed.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task Duplicate_task_messages_produce_exactly_one_document_per_task()
    {
        var queueUrl = Env("DOCBRIDGE_TASK_QUEUE_URL", "http://localhost:4566/000000000000/docbridge-task-queue");
        var endpoint = Env("DOCBRIDGE_SQS_ENDPOINT", queueUrl.StartsWith("http://localhost", StringComparison.Ordinal) ? "http://localhost:4566" : "");
        using var sqs = endpoint.Length > 0
            ? new AmazonSQSClient(new Amazon.Runtime.BasicAWSCredentials("test", "test"), new AmazonSQSConfig { ServiceURL = endpoint, AuthenticationRegion = "us-east-1" })
            : new AmazonSQSClient(Amazon.RegionEndpoint.USEast1);

        var ws = await NewWorkspace(teams: 1, prefix: "dupe", folders: true);
        var files = await Stage(ws.User, 10);
        var jobId = await Submit(ws.User, files, ws.Destinations);
        var taskIds = (await AllTasks(ws.User, jobId)).Select(t => t.GetProperty("taskId").GetGuid()).ToList();

        // At-least-once: inject two extra copies of every task message while the real ones are in flight.
        foreach (var taskId in taskIds)
        {
            var message = JsonSerializer.Serialize(new { taskId });
            await sqs.SendMessageAsync(queueUrl, message);
            await sqs.SendMessageAsync(queueUrl, message);
        }
        await WaitForJob(ws.User, jobId);
        await Task.Delay(TimeSpan.FromSeconds(5)); // let the extra copies drain

        foreach (var dest in ws.Destinations)
        {
            var query = dest.FolderId is null ? "" : $"?folderId={dest.FolderId}";
            var docs = (await Get($"destinations/binders/{dest.BinderId}/contents{query}", ws.User.Token)).Body.GetProperty("documents");
            Assert.Equal(10, docs.GetArrayLength()); // not 20 or 30
        }
        var tasks = await AllTasks(ws.User, jobId);
        Assert.All(tasks, t => Assert.Equal("completed", t.GetProperty("status").GetString()));
    }
}
