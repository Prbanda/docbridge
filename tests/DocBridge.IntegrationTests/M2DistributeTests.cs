using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using static DocBridge.IntegrationTests.Harness;

namespace DocBridge.IntegrationTests;

/// <summary>M2 / Flow 2: presigned uploads, job submission, async fan-out and delivery, integrity, idempotency.</summary>
public sealed class M2DistributeTests
{
    [Fact]
    public async Task Files_endpoint_validates_type_size_checksum_and_count()
    {
        var user = await NewUser("files");
        var sha = new string('a', 64);
        async Task<HttpStatusCode> Try(object file) => (await Post("files", user.Token, new { files = new[] { file } })).Status;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(new { name = "setup.exe", sizeBytes = 10, contentType = "application/x-msdownload", sha256 = sha }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(new { name = "a.png", sizeBytes = 10, contentType = "application/pdf", sha256 = sha }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { name = "a.pdf", sizeBytes = (1L << 30) + 1, contentType = "application/pdf", sha256 = sha }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { name = "a.pdf", sizeBytes = 0, contentType = "application/pdf", sha256 = sha }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { name = "a.pdf", sizeBytes = 10, contentType = "application/pdf", sha256 = "nothex" }));
        var tooMany = Enumerable.Range(0, 101).Select(i => new { name = $"{i}.pdf", sizeBytes = 10, contentType = "application/pdf", sha256 = sha });
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("files", user.Token, new { files = tooMany })).Status);
    }

    [Fact]
    public async Task Presigned_post_carries_checksum_and_size_conditions_and_storage_enforces_them()
    {
        var user = await NewUser("presign");
        var bytes = FakePdf(4096);
        var (status, body, _) = await Post("files", user.Token, new
        {
            files = new[] { new { name = "p.pdf", sizeBytes = bytes.Length, contentType = "application/pdf", sha256 = Sha256(bytes) } },
        });
        Assert.Equal(HttpStatusCode.Created, status);
        var plan = body[0].GetProperty("upload");
        Assert.Equal("post", plan.GetProperty("type").GetString());
        var fields = plan.GetProperty("fields");
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString(Sha256(bytes))), fields.GetProperty("x-amz-checksum-sha256").GetString());
        var expires = plan.GetProperty("expiresAt").GetDateTime();
        Assert.InRange((expires - DateTime.UtcNow).TotalMinutes, 13, 16); // ADR-004

        // Larger than declared → rejected by the content-length-range condition.
        var tooBig = await PresignedPost(plan, FakePdf(8192), "application/pdf");
        Assert.False(tooBig.IsSuccessStatusCode, "storage accepted a body larger than the declared size");
        // Same size, different bytes → rejected by the checksum.
        var corrupted = (byte[])bytes.Clone();
        corrupted[^1] ^= 0xFF;
        var bad = await PresignedPost(plan, corrupted, "application/pdf");
        Assert.False(bad.IsSuccessStatusCode, "storage accepted bytes that do not match the declared SHA-256");
        // The real thing succeeds.
        Assert.True((await PresignedPost(plan, bytes, "application/pdf")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Upload_url_can_be_reissued_for_an_unsubmitted_file_only_by_its_owner()
    {
        var user = await NewUser("reissue");
        var other = await NewUser("reissue-other");
        var file = (await Stage(user, 1, upload: false))[0];
        var reissued = await Post($"files/{file.FileId}/upload-url", user.Token);
        Assert.Equal(HttpStatusCode.OK, reissued.Status);
        Assert.True((await PresignedPost(reissued.Body.GetProperty("upload"), file.Bytes, "application/pdf")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post($"files/{file.FileId}/upload-url", other.Token)).Status);
    }

    [Fact]
    public async Task Batch_is_acknowledged_fast_and_every_task_is_delivered_intact()
    {
        var ws = await NewWorkspace(teams: 2, prefix: "deliver"); // 4 destinations: 2 binder roots + 2 folders
        var files = await Stage(ws.User, 5);

        var (status, body, elapsed) = await Post("jobs", ws.User.Token, Mappings(files, ws.Destinations));
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(20, body.GetProperty("taskCount").GetInt32());
        // Acknowledged without waiting for delivery. The strict p99 < 500 ms gate runs alone in M5; here the suite
        // runs ~30 tests in parallel, so only an order-of-magnitude bound is meaningful.
        Metric($"POST /jobs (20 tasks) acknowledged in {elapsed.TotalMilliseconds:F0} ms");
        Assert.InRange(elapsed.TotalMilliseconds, 0, 2000);
        var jobId = body.GetProperty("jobId").GetGuid();

        // The full task list exists immediately (pending), before any delivery.
        var listed = await Get($"jobs/{jobId}/tasks", ws.User.Token);
        Assert.Equal(20, listed.Body.GetProperty("total").GetInt64());

        var job = await WaitForJob(ws.User, jobId);
        Assert.Equal("completed", job.GetProperty("aggregateStatus").GetString());
        Assert.Equal(20, job.GetProperty("counts").GetProperty("completed").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("completedAt").ValueKind);

        // Documents landed in the platform, at the right place, with the right checksum (verified via the platform API).
        foreach (var dest in ws.Destinations)
        {
            var query = dest.FolderId is null ? "" : $"?folderId={dest.FolderId}";
            var contents = await Get($"destinations/binders/{dest.BinderId}/contents{query}", ws.User.Token);
            var docs = contents.Body.GetProperty("documents").EnumerateArray().ToList();
            Assert.Equal(5, docs.Count);
            Assert.Equal(files.Select(f => f.Sha256).Order(), docs.Select(d => d.GetProperty("checksumSha256").GetString()).Order());
        }
        Assert.All(await AllTasks(ws.User, jobId), t => Assert.Equal(1, t.GetProperty("attemptCount").GetInt32()));
    }

    [Fact]
    public async Task Idempotency_key_replay_returns_the_same_job()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "idem", folders: false);
        var files = await Stage(ws.User, 2);
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString() };
        var first = await Post("jobs", ws.User.Token, Mappings(files, ws.Destinations), headers);
        var replay = await Post("jobs", ws.User.Token, Mappings(files, ws.Destinations), headers);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Equal(first.Body.GetProperty("jobId").GetGuid(), replay.Body.GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task Concurrent_submissions_of_the_same_files_claim_them_once()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "race", folders: false);
        var files = await Stage(ws.User, 3);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Post("jobs", ws.User.Token, Mappings(files, ws.Destinations))));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("FILES_UNAVAILABLE", ErrorCode(r.Body));
        });
    }

    [Fact]
    public async Task Submission_rejects_foreign_teams_disabled_teams_and_other_users_files()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "authz", folders: false);
        var files = await Stage(ws.User, 1);

        var foreignTeam = await CreateTeam(Unique("foreign"));
        var foreignBinder = await AdminCreate($"admin/teams/{foreignTeam}/binders", new { name = "B" });
        var foreign = await Post("jobs", ws.User.Token, Mappings(files, [new Dest(foreignTeam, foreignBinder, null, "x")]));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.Status);
        Assert.Equal("NOT_A_MEMBER", ErrorCode(foreign.Body));

        var disabledTeam = await CreateTeam(Unique("off"), enabled: false);
        await AddMember(disabledTeam, ws.User.Id);
        var disabledBinder = await AdminCreate($"admin/teams/{disabledTeam}/binders", new { name = "B" });
        var disabled = await Post("jobs", ws.User.Token, Mappings(files, [new Dest(disabledTeam, disabledBinder, null, "x")]));
        Assert.Equal(HttpStatusCode.Forbidden, disabled.Status);
        Assert.Equal("TEAM_NOT_ENABLED", ErrorCode(disabled.Body));

        var thief = await NewWorkspace(teams: 1, prefix: "thief", folders: false);
        var stolen = await Post("jobs", thief.User.Token, Mappings(files, thief.Destinations));
        Assert.Equal(HttpStatusCode.Conflict, stolen.Status);

        // Nothing above created a job or claimed the file: the owner can still submit it.
        await Submit(ws.User, files, ws.Destinations);
    }

    [Fact]
    public async Task Never_uploaded_file_fails_its_task_with_file_not_uploaded()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "missing", folders: false);
        var good = await Stage(ws.User, 1);
        var missing = await Stage(ws.User, 1, upload: false);
        var jobId = await Submit(ws.User, good.Concat(missing), ws.Destinations);

        var job = await WaitForJob(ws.User, jobId);
        Assert.Equal("completed_with_errors", job.GetProperty("aggregateStatus").GetString());
        var failed = await AllTasks(ws.User, jobId, "failed");
        var reason = Assert.Single(failed).GetProperty("failureReason").GetString()!;
        Assert.StartsWith("FILE_NOT_UPLOADED", reason);
        Assert.Contains(missing[0].Name, reason); // file name and destination are in the reason
        Assert.Contains(ws.Destinations[0].Label, reason);
        Assert.Single(await AllTasks(ws.User, jobId, "completed")); // the sibling task was unaffected
    }

    [Fact]
    public async Task Membership_revoked_after_submission_fails_only_that_destination()
    {
        var ws = await NewWorkspace(teams: 3, prefix: "revoke", folders: false);
        var files = await Stage(ws.User, 4);
        var revokedTeam = ws.Teams[2];
        var jobId = await Submit(ws.User, files, ws.Destinations);
        await RemoveMember(revokedTeam, ws.User.Id); // time-of-use check at ingest (ADR-013)

        var job = await WaitForJob(ws.User, jobId);
        var failed = await AllTasks(ws.User, jobId, "failed");
        var completed = await AllTasks(ws.User, jobId, "completed");
        // A task delivered before the revoke landed is legitimately completed; everything after it must fail.
        Assert.Equal(12, failed.Count + completed.Count);
        Assert.All(failed, t => Assert.StartsWith("NOT_AUTHORIZED_AT_DELIVERY", t.GetProperty("failureReason").GetString()));
        Assert.All(failed, t => Assert.Equal(revokedTeam, t.GetProperty("teamId").GetGuid()));
        Assert.Equal(8, completed.Count(t => t.GetProperty("teamId").GetGuid() != revokedTeam));
        Assert.True(failed.Count > 0, "expected at least one revoked-destination task to fail");
        _ = job;
    }

    [Fact]
    public async Task Platform_ingest_is_idempotent_per_task_and_rejects_checksum_mismatch()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "ingest", folders: false);
        var token = await ServiceToken("delivery-worker", DeliverySecret, "documents:ingest");
        var bytes = FakePdf(1500);
        var taskId = Guid.NewGuid();

        async Task<(HttpStatusCode Status, string Body)> Ingest(Guid task, byte[] payload, string checksum)
        {
            using var form = new MultipartFormDataContent();
            var meta = new StringContent(JsonSerializer.Serialize(new
            {
                taskId = task,
                binderId = ws.Destinations[0].BinderId,
                name = "direct.pdf",
                contentType = "application/pdf",
                checksumSha256 = checksum,
                onBehalfOf = ws.User.Id,
            }));
            meta.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(meta, "metadata");
            var file = new ByteArrayContent(payload);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "file", "direct.pdf");
            using var request = new HttpRequestMessage(HttpMethod.Post, "documents") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var first = await Ingest(taskId, bytes, Sha256(bytes));
        var again = await Ingest(taskId, bytes, Sha256(bytes));
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(JsonDocument.Parse(first.Body).RootElement.GetProperty("documentId").GetGuid(),
            JsonDocument.Parse(again.Body).RootElement.GetProperty("documentId").GetGuid());

        var mismatch = await Ingest(Guid.NewGuid(), bytes, Sha256(FakePdf(10)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.Status);
        Assert.Contains("CHECKSUM_MISMATCH", mismatch.Body);

        var contents = await Get($"destinations/binders/{ws.Destinations[0].BinderId}/contents", ws.User.Token);
        Assert.Single(contents.Body.GetProperty("documents").EnumerateArray()); // one document, mismatch never stored

        // A user token can't ingest.
        using var userAttempt = new HttpRequestMessage(HttpMethod.Post, "documents") { Content = new StringContent("x") };
        userAttempt.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ws.User.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(userAttempt)).StatusCode);
    }

    [Fact]
    public async Task Limits_are_enforced_at_submission()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "limits", folders: false);
        var files = await Stage(ws.User, 1);
        var many = Enumerable.Range(0, 21).Select(_ => new Dest(ws.Teams[0], Guid.NewGuid(), null, "x"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("jobs", ws.User.Token, Mappings(files, many))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("jobs", ws.User.Token, new { mappings = Array.Empty<object>() })).Status);
    }

    [Fact]
    public async Task Multipart_upload_above_100_mb_is_delivered_intact()
    {
        if (Env("DOCBRIDGE_SKIP_LARGE", "") == "1")
        {
            Assert.Skip("DOCBRIDGE_SKIP_LARGE=1");
        }
        var ws = await NewWorkspace(teams: 1, prefix: "large", folders: false);
        var bytes = FakePdf(110 * 1024 * 1024);
        var (status, body, _) = await Post("files", ws.User.Token, new
        {
            files = new[] { new { name = "large.pdf", sizeBytes = bytes.Length, contentType = "application/pdf", sha256 = Sha256(bytes) } },
        });
        Assert.Equal(HttpStatusCode.Created, status);
        var plan = body[0].GetProperty("upload");
        Assert.Equal("multipart", plan.GetProperty("type").GetString());
        var fileId = body[0].GetProperty("fileId").GetGuid();
        var upload = await UploadWithPlan(ws.User, fileId, plan, bytes, "application/pdf");
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());

        var jobId = await Submit(ws.User, [new StagedFile(fileId, "large.pdf", bytes, Sha256(bytes))], ws.Destinations);
        var job = await WaitForJob(ws.User, jobId, TimeSpan.FromMinutes(5));
        Assert.Equal("completed", job.GetProperty("aggregateStatus").GetString());
        var doc = (await Get($"destinations/binders/{ws.Destinations[0].BinderId}/contents", ws.User.Token)).Body.GetProperty("documents")[0];
        Assert.Equal(Sha256(bytes), doc.GetProperty("checksumSha256").GetString());
        Assert.Equal(bytes.Length, doc.GetProperty("sizeBytes").GetInt64());
    }
}
