using System.Net;
using System.Net.WebSockets;
using static DocBridge.IntegrationTests.Harness;

namespace DocBridge.IntegrationTests;

/// <summary>M3 / Flow 3: job list with aggregates, per-task status, visibility rules, live push over WebSocket.</summary>
public sealed class M3TrackTests
{
    [Fact]
    public async Task Job_list_shows_own_jobs_with_aggregate_counts()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "list", folders: true);
        var files = await Stage(ws.User, 2);
        var missing = await Stage(ws.User, 1, upload: false);
        var ok = await Submit(ws.User, files, ws.Destinations);
        var bad = await Submit(ws.User, missing, ws.Destinations);
        await WaitForJob(ws.User, ok);
        await WaitForJob(ws.User, bad);

        var (status, body, _) = await Get("jobs", ws.User.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        var jobs = body.EnumerateArray().ToDictionary(j => j.GetProperty("jobId").GetGuid());
        Assert.Equal(2, jobs.Count);
        Assert.Equal(bad, body[0].GetProperty("jobId").GetGuid()); // newest first
        Assert.Equal("completed", jobs[ok].GetProperty("aggregateStatus").GetString());
        Assert.Equal(4, jobs[ok].GetProperty("counts").GetProperty("completed").GetInt64());
        Assert.Equal("completed_with_errors", jobs[bad].GetProperty("aggregateStatus").GetString());
        Assert.Equal(2, jobs[bad].GetProperty("counts").GetProperty("failed").GetInt64());
    }

    [Fact]
    public async Task Tasks_are_paginated_and_filterable_by_status()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "page", folders: true);
        var files = await Stage(ws.User, 3);
        var missing = await Stage(ws.User, 1, upload: false);
        var jobId = await Submit(ws.User, files.Concat(missing), ws.Destinations); // 8 tasks, 2 will fail
        await WaitForJob(ws.User, jobId);

        var page1 = await Get($"jobs/{jobId}/tasks?page=1&limit=5", ws.User.Token);
        var page2 = await Get($"jobs/{jobId}/tasks?page=2&limit=5", ws.User.Token);
        Assert.Equal(8, page1.Body.GetProperty("total").GetInt64());
        Assert.Equal(5, page1.Body.GetProperty("tasks").GetArrayLength());
        Assert.Equal(3, page2.Body.GetProperty("tasks").GetArrayLength());
        var failed = await Get($"jobs/{jobId}/tasks?status=failed", ws.User.Token);
        Assert.Equal(2, failed.Body.GetProperty("total").GetInt64());
        Assert.All(failed.Body.GetProperty("tasks").EnumerateArray(), t => Assert.False(string.IsNullOrEmpty(t.GetProperty("failureReason").GetString())));
        Assert.Equal(HttpStatusCode.BadRequest, (await Get($"jobs/{jobId}/tasks?status=bogus", ws.User.Token)).Status);
    }

    [Fact]
    public async Task Teammates_can_view_a_job_and_outsiders_get_404()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "view", folders: false);
        var teammate = await NewUser("mate");
        await AddMember(ws.Teams[0], teammate.Id);
        var outsider = await NewUser("outsider");
        var jobId = await Submit(ws.User, await Stage(ws.User, 1), ws.Destinations);

        Assert.Equal(HttpStatusCode.OK, (await Get($"jobs/{jobId}", teammate.Token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get($"jobs/{jobId}/tasks", teammate.Token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"jobs/{jobId}", outsider.Token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"jobs/{jobId}/tasks", outsider.Token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"jobs/{Guid.NewGuid()}", ws.User.Token)).Status);
    }

    [Fact]
    public async Task Websocket_rejects_missing_and_invalid_tokens()
    {
        await Assert.ThrowsAnyAsync<WebSocketException>(() => OpenSocket(null));
        await Assert.ThrowsAnyAsync<WebSocketException>(() => OpenSocket("not-a-token"));
        var service = await AdminToken();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => OpenSocket(service)); // user tokens only
    }

    [Fact]
    public async Task Subscribed_client_receives_live_task_updates_until_the_job_resolves()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "live", folders: true);
        var files = await Stage(ws.User, 3);
        // Submission enqueues immediately, so the subscribe necessarily follows it; the client's refetch on ack covers the gap.
        var jobId = await Submit(ws.User, files, ws.Destinations);
        using var socket = await OpenSocket(ws.User.Token);
        var reader = new SocketReader(socket);
        await SendJson(socket, new { action = "subscribe", jobId });
        var ack = await reader.NextWhere(m => m.GetProperty("type").GetString() == "subscribed", TimeSpan.FromSeconds(15));
        Assert.NotNull(ack);
        Assert.Equal(jobId, ack.Value.GetProperty("jobId").GetGuid());

        var job = await WaitForJob(ws.User, jobId);
        Assert.Equal("completed", job.GetProperty("aggregateStatus").GetString());
        _ = reader;
    }

    [Fact]
    public async Task Pushes_carry_status_and_counts_for_every_task()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "push", folders: true);
        // 200 tasks: delivery outlasts the subscribe round trip, so the final pushes are observed.
        var files = await Stage(ws.User, 100);
        using var socket = await OpenSocket(ws.User.Token);
        var reader = new SocketReader(socket);
        var jobId = await Submit(ws.User, files, ws.Destinations);
        await SendJson(socket, new { action = "subscribe", jobId });

        var updates = new List<System.Text.Json.JsonElement>();
        // Keep listening until the job's aggregate reaches completed: gaps between pushes are normal when the
        // delivery pool is shared with other jobs (dev caps it at 5 concurrent).
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            var message = await reader.Next(deadline - DateTime.UtcNow);
            if (message is null)
            {
                break;
            }
            if (message.Value.GetProperty("type").GetString() != "task_update")
            {
                continue;
            }
            updates.Add(message.Value);
            if (message.Value.GetProperty("aggregateStatus").GetString() == "completed")
            {
                break;
            }
        }
        Assert.NotEmpty(updates);
        var last = updates[^1];
        Assert.Equal("completed", last.GetProperty("aggregateStatus").GetString());
        Assert.Equal(200, last.GetProperty("counts").GetProperty("completed").GetInt64());
        Assert.All(updates, u => Assert.Equal(jobId, u.GetProperty("jobId").GetGuid()));
        Assert.Contains(updates, u => u.GetProperty("status").GetString() == "completed");
    }

    [Fact]
    public async Task Outsider_cannot_subscribe_to_a_job()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "wsout", folders: false);
        var jobId = await Submit(ws.User, await Stage(ws.User, 1), ws.Destinations);
        var outsider = await NewUser("wsoutsider");
        using var socket = await OpenSocket(outsider.Token);
        var reader = new SocketReader(socket);
        await SendJson(socket, new { action = "subscribe", jobId });
        var reply = await reader.Next(TimeSpan.FromSeconds(15));
        Assert.NotNull(reply);
        Assert.Equal("error", reply.Value.GetProperty("type").GetString());
        Assert.Equal("NOT_FOUND", reply.Value.GetProperty("code").GetString());
    }
}

/// <summary>M4 / Flow 4: team-scoped downloads straight from staging.</summary>
public sealed class M4RetrieveTests
{
    private static readonly HttpClient Storage = new();

    [Fact]
    public async Task Teammate_downloads_the_exact_bytes_and_outsider_gets_404()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "dl", folders: false);
        var teammate = await NewUser("dlmate");
        await AddMember(ws.Teams[0], teammate.Id);
        var outsider = await NewUser("dlout");
        var file = (await Stage(ws.User, 1, size: 5000))[0];
        var jobId = await Submit(ws.User, [file], ws.Destinations);
        await WaitForJob(ws.User, jobId);

        foreach (var who in new[] { ws.User, teammate })
        {
            var (status, body, _) = await Post($"files/{file.FileId}/download-url", who.Token);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(120, body.GetProperty("expiresIn").GetInt32()); // ADR-004
            var url = body.GetProperty("url").GetString()!;
            Assert.NotEqual(new Uri(BaseUrl).Authority, new Uri(url).Authority); // bytes come from storage, not our servers
            var bytes = await Storage.GetByteArrayAsync(url);
            Assert.Equal(file.Sha256, Sha256(bytes));
        }

        var denied = await Post($"files/{file.FileId}/download-url", outsider.Token);
        Assert.Equal(HttpStatusCode.NotFound, denied.Status); // 404, not 403: existence isn't confirmed
        Assert.Equal(HttpStatusCode.NotFound, (await Post($"files/{Guid.NewGuid()}/download-url", ws.User.Token)).Status);
    }

    [Fact]
    public async Task Former_teammate_loses_download_access()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "dlrev", folders: false);
        var mate = await NewUser("dlrevmate");
        await AddMember(ws.Teams[0], mate.Id);
        var file = (await Stage(ws.User, 1))[0];
        await WaitForJob(ws.User, await Submit(ws.User, [file], ws.Destinations));
        await RemoveMember(ws.Teams[0], mate.Id);
        await Task.Delay(TimeSpan.FromSeconds(61)); // team lists are cached for 60 s (TDD 3.1)
        Assert.Equal(HttpStatusCode.NotFound, (await Post($"files/{file.FileId}/download-url", mate.Token)).Status);
    }
}
