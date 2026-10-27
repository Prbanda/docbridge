using Dapper;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Clients;
using DocBridge.Shared.Errors;
using DocBridge.Shared.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace DocBridge.Api;

public sealed record DestinationInput(Guid? TeamId, Guid? BinderId, Guid? FolderId, string? Label);
public sealed record MappingInput(Guid? FileId, List<DestinationInput>? Destinations);
public sealed record SubmitJobRequest(List<MappingInput>? Mappings);

public sealed record PlannedTask(Guid Id, Guid FileId, Guid TeamId, Guid BinderId, Guid? FolderId, string? Label);

public static class JobEndpoints
{
    public const int MaxFiles = 100;
    public const int MaxDestinations = 20;
    public const int MaxTasks = 2000;

    public static void MapJobEndpoints(this WebApplication app)
    {
        var jobs = app.MapGroup("/jobs").RequireAuthorization(AuthPolicies.User);
        jobs.MapPost("", Submit);
        jobs.MapGet("", ListJobs);
        jobs.MapGet("/{jobId:guid}", GetJob);
        jobs.MapGet("/{jobId:guid}/tasks", GetTasks);
    }

    /// <summary>
    /// TDD 2.2 / ADR-008: constant work per request. Authorize, then one transaction (job row + file claim + up to
    /// 2,000 task rows), then one job message, then return. Fan-out happens off the request path.
    /// </summary>
    private static async Task<IResult> Submit(SubmitJobRequest body, HttpContext ctx, NpgsqlDataSource db, PlatformClient platform,
        JobQueue queue, ILoggerFactory logs)
    {
        var userId = ctx.UserGuid();
        var idempotencyKey = ctx.Request.Headers["Idempotency-Key"].ToString() is { Length: > 0 and <= 200 } key ? key : null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var marks = new List<(string Step, long Ms)>();
        void Mark(string step) => marks.Add((step, clock.ElapsedMilliseconds));

        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        Mark("connection");
        if (idempotencyKey is not null && await FindByIdempotencyKey(conn, userId, idempotencyKey) is { } replay)
        {
            return Results.Ok(replay);
        }

        var tasks = Expand(body);
        var teams = tasks.Select(t => t.TeamId).Distinct().ToList();
        Mark("expand");

        // Membership per distinct team (service route, as the TDD specifies) plus the docbridge-enabled filter (G12),
        // all in parallel. The enabled-team list also gives each destination's region.
        var enabledTask = platform.GetTeamsAsync(ctx.RawToken(), docbridgeEnabledOnly: true, ctx.RequestAborted);
        var memberChecks = teams.Select(async team => (team, member: await platform.IsMemberAsync(team, userId, ctx.RequestAborted))).ToList();
        var enabled = (await enabledTask).ToDictionary(t => t.Id);
        Mark("enabled-teams");
        var memberships = await Task.WhenAll(memberChecks);
        Mark("memberships");
        foreach (var (team, member) in memberships)
        {
            if (!member)
            {
                throw new ApiException(403, "NOT_A_MEMBER", $"You are not a member of team {team}");
            }
            if (!enabled.ContainsKey(team))
            {
                throw new ApiException(403, "TEAM_NOT_ENABLED", $"Team {team} does not have DocBridge enabled");
            }
        }

        var jobId = Guid.CreateVersion7();
        var fileIds = tasks.Select(t => t.FileId).Distinct().ToArray();
        await using (var tx = await conn.BeginTransactionAsync(ctx.RequestAborted))
        {
            try
            {
                await conn.ExecuteAsync(
                    "INSERT INTO jobs (id, submitted_by_user_id, task_count, idempotency_key) VALUES (@jobId, @userId, @count, @idempotencyKey)",
                    new { jobId, userId, count = tasks.Count, idempotencyKey }, tx);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && idempotencyKey is not null)
            {
                await tx.RollbackAsync();
                return Results.Ok(await FindByIdempotencyKey(conn, userId, idempotencyKey));
            }

            // AM-1: claim with a conditional update, so two concurrent submissions can't both take the same files.
            var claimed = (await conn.QueryAsync<Guid>(
                "UPDATE files SET job_id = @jobId WHERE id = ANY(@fileIds) AND owner_user_id = @userId AND job_id IS NULL RETURNING id",
                new { jobId, fileIds, userId }, tx)).ToHashSet();
            if (claimed.Count != fileIds.Length)
            {
                await tx.RollbackAsync();
                var missing = fileIds.Where(f => !claimed.Contains(f)).Take(5);
                throw ApiException.Conflict($"Files not found, not yours, or already submitted: {string.Join(", ", missing)}", "FILES_UNAVAILABLE");
            }

            // Binary COPY inside the same transaction: one stream of rows, every constraint and FK still enforced.
            // Measured: the parameter-array INSERT … unnest spent more time encoding on the client than Postgres spent
            // executing it, and the insert dominated submission latency.
            await using (var copy = await conn.BeginBinaryImportAsync(
                "COPY tasks (id, job_id, file_id, team_id, binder_id, folder_id, region, destination_label) FROM STDIN (FORMAT BINARY)",
                ctx.RequestAborted))
            {
                foreach (var t in tasks)
                {
                    await copy.StartRowAsync(ctx.RequestAborted);
                    await copy.WriteAsync(t.Id, NpgsqlDbType.Uuid, ctx.RequestAborted);
                    await copy.WriteAsync(jobId, NpgsqlDbType.Uuid, ctx.RequestAborted);
                    await copy.WriteAsync(t.FileId, NpgsqlDbType.Uuid, ctx.RequestAborted);
                    await copy.WriteAsync(t.TeamId, NpgsqlDbType.Uuid, ctx.RequestAborted);
                    await copy.WriteAsync(t.BinderId, NpgsqlDbType.Uuid, ctx.RequestAborted);
                    if (t.FolderId is { } folder)
                    {
                        await copy.WriteAsync(folder, NpgsqlDbType.Uuid, ctx.RequestAborted);
                    }
                    else
                    {
                        await copy.WriteNullAsync(ctx.RequestAborted);
                    }
                    await copy.WriteAsync(enabled[t.TeamId].Region, NpgsqlDbType.Text, ctx.RequestAborted);
                    if (t.Label is { } label)
                    {
                        await copy.WriteAsync(label, NpgsqlDbType.Text, ctx.RequestAborted);
                    }
                    else
                    {
                        await copy.WriteNullAsync(ctx.RequestAborted);
                    }
                }
                await copy.CompleteAsync(ctx.RequestAborted);
            }
            Mark("insert-tasks");
            await tx.CommitAsync(ctx.RequestAborted);
            Mark("commit");
        }

        try
        {
            await queue.PublishAsync(jobId, CancellationToken.None);
            Mark("publish");
        }
        catch (Exception ex)
        {
            // The job is committed: never fail a submission the DB accepted. The sweeper republishes jobs
            // whose enqueued_at is still NULL (TDD 2.2 / G8).
            logs.CreateLogger("Submit").LogError(ex, "Job {JobId} committed but job message publish failed", jobId);
        }
        // Per-step timings: the 500 ms p99 budget (TDD 2.2) is checked against these when it regresses.
        logs.CreateLogger("Submit").LogInformation("Job {JobId} accepted: {TaskCount} tasks, {Teams} teams, {Timings}",
            jobId, tasks.Count, teams.Count, string.Join(" ", marks.Select(m => $"{m.Step}={m.Ms}ms")));
        return Results.Created($"/jobs/{jobId}", new { jobId, taskCount = tasks.Count });
    }

    /// <summary>Validate the mapping and expand it to file × destination tasks (pure: unit-tested).</summary>
    internal static List<PlannedTask> Expand(SubmitJobRequest body)
    {
        var mappings = body.Mappings ?? [];
        if (mappings.Count == 0)
        {
            throw ApiException.BadRequest("At least one file mapping is required");
        }
        var tasks = new List<PlannedTask>();
        var destinations = new HashSet<(Guid, Guid, Guid?)>();
        var files = new HashSet<Guid>();
        foreach (var (mapping, i) in mappings.Select((m, i) => (m, i)))
        {
            var fileId = mapping.FileId ?? throw ApiException.BadRequest($"mappings[{i}].fileId is required");
            if (!files.Add(fileId))
            {
                throw ApiException.BadRequest($"File {fileId} appears in more than one mapping");
            }
            if (mapping.Destinations is not { Count: > 0 })
            {
                throw ApiException.BadRequest($"mappings[{i}] needs at least one destination");
            }
            var seen = new HashSet<(Guid, Guid, Guid?)>();
            foreach (var d in mapping.Destinations)
            {
                if (d.TeamId is not { } team || d.BinderId is not { } binder)
                {
                    throw ApiException.BadRequest($"mappings[{i}]: every destination needs teamId and binderId");
                }
                var dest = (team, binder, d.FolderId);
                if (!seen.Add(dest))
                {
                    continue; // same file to the same destination twice is one task
                }
                destinations.Add(dest);
                var label = d.Label?.Trim() is { Length: > 0 } l ? l[..Math.Min(l.Length, 300)] : null;
                // UUID v7: 2,000 task rows per job append to the PK and index edges (measured: random v4 ids made
                // the insert the dominant cost of submission as the table grew).
                tasks.Add(new PlannedTask(Guid.CreateVersion7(), fileId, team, binder, d.FolderId, label));
            }
        }
        if (files.Count > MaxFiles)
        {
            throw ApiException.BadRequest($"A job can contain at most {MaxFiles} files");
        }
        if (destinations.Count > MaxDestinations)
        {
            throw ApiException.BadRequest($"A job can target at most {MaxDestinations} destinations");
        }
        if (tasks.Count > MaxTasks)
        {
            throw ApiException.BadRequest($"A job can contain at most {MaxTasks} tasks");
        }
        return tasks;
    }

    private static async Task<object?> FindByIdempotencyKey(NpgsqlConnection conn, Guid userId, string key) =>
        await conn.QuerySingleOrDefaultAsync<JobAck>(
            "SELECT id AS job_id, task_count FROM jobs WHERE submitted_by_user_id = @userId AND idempotency_key = @key",
            new { userId, key });

    /// <summary>The caller's own jobs, newest first. Counts for the whole page come from one aggregate query.</summary>
    private static async Task<IResult> ListJobs(HttpContext ctx, NpgsqlDataSource db, int? page, int? limit)
    {
        var size = Math.Clamp(limit ?? 20, 1, 100);
        var offset = (Math.Max(page ?? 1, 1) - 1) * size;
        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        var jobs = (await conn.QueryAsync<JobRow>(
            """
            SELECT id, submitted_by_user_id, task_count, created_at, completed_at FROM jobs
            WHERE submitted_by_user_id = @userId ORDER BY created_at DESC LIMIT @size OFFSET @offset
            """, new { userId = ctx.UserGuid(), size, offset })).ToList();
        var counts = (await conn.QueryAsync<JobCountsRow>(TaskCounts.CountsSql, new { jobIds = jobs.Select(j => j.Id).ToArray() }))
            .ToDictionary(c => c.JobId, c => c.Counts);
        return Results.Ok(jobs.Select(j => ToSummary(j, counts.GetValueOrDefault(j.Id) ?? new TaskCounts(0, 0, 0, 0))));
    }

    private static async Task<IResult> GetJob(Guid jobId, HttpContext ctx, NpgsqlDataSource db, TeamAccess access, StagingStore staging)
    {
        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        var job = await LoadVisibleJob(conn, jobId, ctx, access);
        var counts = (await conn.QueryAsync<JobCountsRow>(TaskCounts.CountsSql, new { jobIds = new[] { jobId } })).Single().Counts;
        var files = (await conn.QueryAsync<JobFile>(
            """
            SELECT id AS file_id, original_name AS name, size_bytes, content_type, s3_key, created_at
            FROM files WHERE job_id = @jobId ORDER BY original_name
            """, new { jobId })).ToList();

        // Staging status per file (≤ 100 files, checked in parallel): drives the job page's Download availability.
        using var slots = new SemaphoreSlim(16);
        var withStatus = await Task.WhenAll(files.Select(async f =>
        {
            await slots.WaitAsync(ctx.RequestAborted);
            try
            {
                var status = await staging.ExistsAsync(f.S3Key, ctx.RequestAborted) ? "ready"
                    : DateTime.UtcNow - f.CreatedAt > TimeSpan.FromDays(DocBridge.Shared.Env.GetInt("STAGING_RETENTION_DAYS", 30)) ? "expired"
                    : "not_uploaded";
                return new { f.FileId, f.Name, f.SizeBytes, f.ContentType, status };
            }
            finally
            {
                slots.Release();
            }
        }));
        return Results.Ok(new
        {
            job = ToSummary(job, counts),
            files = withStatus,
        });
    }

    /// <summary>Paginated (2,000 tasks never go in one response), filterable by status: the failed-tasks view.</summary>
    private static async Task<IResult> GetTasks(Guid jobId, HttpContext ctx, NpgsqlDataSource db, TeamAccess access, string? status, int? page, int? limit)
    {
        if (status is not null && status is not (TaskStates.Pending or TaskStates.InProgress or TaskStates.Completed or TaskStates.Failed))
        {
            throw ApiException.BadRequest("status must be pending, in_progress, completed or failed");
        }
        var size = Math.Clamp(limit ?? 100, 1, 500);
        var offset = (Math.Max(page ?? 1, 1) - 1) * size;
        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        await LoadVisibleJob(conn, jobId, ctx, access);
        var total = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM tasks WHERE job_id = @jobId AND (@status::text IS NULL OR status = @status)", new { jobId, status });
        var tasks = await conn.QueryAsync<TaskView>(
            """
            SELECT t.id AS task_id, t.file_id, f.original_name AS file_name, t.team_id, t.binder_id, t.folder_id, t.destination_label,
                   t.region, t.status, t.attempt_count, t.failure_reason, t.platform_document_id, t.updated_at
            FROM tasks t JOIN files f ON f.id = t.file_id
            WHERE t.job_id = @jobId AND (@status::text IS NULL OR t.status = @status)
            ORDER BY f.original_name, t.destination_label, t.id
            LIMIT @size OFFSET @offset
            """, new { jobId, status, size, offset });
        return Results.Ok(new { total, page = Math.Max(page ?? 1, 1), limit = size, tasks });
    }

    /// <summary>Visible to the submitter and to anyone sharing a team with them. Otherwise 404.</summary>
    private static async Task<JobRow> LoadVisibleJob(NpgsqlConnection conn, Guid jobId, HttpContext ctx, TeamAccess access)
    {
        var job = await conn.QuerySingleOrDefaultAsync<JobRow>(
            "SELECT id, submitted_by_user_id, task_count, created_at, completed_at FROM jobs WHERE id = @jobId", new { jobId });
        if (job is null || !await access.SharesTeamAsync(ctx.UserGuid(), job.SubmittedByUserId, ctx.RequestAborted))
        {
            throw ApiException.NotFound("Job not found");
        }
        return job;
    }

    private static object ToSummary(JobRow job, TaskCounts counts) => new
    {
        jobId = job.Id,
        submittedByUserId = job.SubmittedByUserId,
        createdAt = job.CreatedAt,
        completedAt = job.CompletedAt,
        taskCount = job.TaskCount,
        counts = new { pending = counts.Pending, inProgress = counts.InProgress, completed = counts.Completed, failed = counts.Failed },
        aggregateStatus = counts.Aggregate(),
    };

    private sealed record JobAck(Guid JobId, int TaskCount);

    private sealed record JobRow(Guid Id, Guid SubmittedByUserId, int TaskCount, DateTime CreatedAt, DateTime? CompletedAt);

    private sealed record JobFile(Guid FileId, string Name, long SizeBytes, string ContentType, string S3Key, DateTime CreatedAt);

    private sealed record TaskView(Guid TaskId, Guid FileId, string FileName, Guid TeamId, Guid BinderId, Guid? FolderId,
        string? DestinationLabel, string Region, string Status, int AttemptCount, string? FailureReason, Guid? PlatformDocumentId,
        DateTime UpdatedAt);
}
