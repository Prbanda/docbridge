using Amazon.S3.Model;
using Dapper;
using DocBridge.Shared;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Errors;
using DocBridge.Shared.Jobs;
using Npgsql;

namespace DocBridge.Api;

public sealed record FileInput(string? Name, long? SizeBytes, string? ContentType, string? Sha256);
public sealed record CreateFilesRequest(List<FileInput>? Files);
public sealed record CompletedPart(int PartNumber, string? Etag);
public sealed record CompleteMultipartRequest(string? UploadId, List<CompletedPart>? Parts);
public sealed record ReissueRequest(string? UploadId);

public static class FileEndpoints
{
    public const int MaxFilesPerRequest = 100;

    public static void MapFileEndpoints(this WebApplication app)
    {
        var files = app.MapGroup("/files").RequireAuthorization(AuthPolicies.User);
        files.MapPost("", CreateFiles);
        files.MapPost("/{fileId:guid}/upload-url", ReissueUpload);
        files.MapPost("/{fileId:guid}/multipart/complete", CompleteMultipart);
        files.MapPost("/{fileId:guid}/download-url", CreateDownloadUrl);
    }

    /// <summary>TDD 2.1: create file resources and hand back one upload plan per file.</summary>
    private static async Task<IResult> CreateFiles(CreateFilesRequest body, HttpContext ctx, NpgsqlDataSource db, StagingStore staging)
    {
        var inputs = body.Files ?? [];
        if (inputs.Count is 0 or > MaxFilesPerRequest)
        {
            throw ApiException.BadRequest($"Between 1 and {MaxFilesPerRequest} files per request");
        }
        var owner = ctx.UserGuid();
        var targets = inputs.Select((f, i) => Validate(f, i, owner)).ToList();

        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        await conn.ExecuteAsync(
            """
            INSERT INTO files (id, owner_user_id, original_name, size_bytes, content_type, checksum_sha256, s3_key)
            SELECT * FROM unnest(@ids, @owners, @names, @sizes, @types, @sums, @keys)
            """, new
            {
                ids = targets.Select(t => t.Target.FileId).ToArray(),
                owners = targets.Select(_ => owner).ToArray(),
                names = targets.Select(t => t.Name).ToArray(),
                sizes = targets.Select(t => t.Target.SizeBytes).ToArray(),
                types = targets.Select(t => t.Target.ContentType).ToArray(),
                sums = targets.Select(t => t.Target.Sha256Hex).ToArray(),
                keys = targets.Select(t => t.Target.S3Key).ToArray(),
            });

        var plans = await Task.WhenAll(targets.Select(async t =>
            new { fileId = t.Target.FileId, upload = await staging.CreateUploadAsync(t.Target, ctx.RequestAborted) }));
        return Results.Created("/files", plans);
    }

    /// <summary>AM-2: fresh URLs for an existing, not-yet-submitted file (expired wave, interrupted session).</summary>
    private static async Task<IResult> ReissueUpload(Guid fileId, ReissueRequest? body, HttpContext ctx, NpgsqlDataSource db, StagingStore staging)
    {
        var file = await LoadOwnedUnsubmitted(fileId, ctx, db);
        var target = file.ToTarget();
        var plan = body?.UploadId is { Length: > 0 } uploadId && file.SizeBytes > StagingStore.MultipartThreshold
            ? staging.MultipartPlan(target, uploadId, DateTime.UtcNow.Add(StagingStore.UploadExpiry))
            : await staging.CreateUploadAsync(target, ctx.RequestAborted);
        return Results.Ok(new { fileId, upload = plan });
    }

    private static async Task<IResult> CompleteMultipart(Guid fileId, CompleteMultipartRequest body, HttpContext ctx, NpgsqlDataSource db,
        StagingStore staging)
    {
        var file = await LoadOwnedUnsubmitted(fileId, ctx, db);
        if (string.IsNullOrEmpty(body.UploadId) || body.Parts is not { Count: > 0 } || body.Parts.Any(p => string.IsNullOrEmpty(p.Etag)))
        {
            throw ApiException.BadRequest("uploadId and parts (partNumber, etag) are required");
        }
        await staging.CompleteMultipartAsync(file.ToTarget(), body.UploadId,
            body.Parts.Select(p => new PartETag(p.PartNumber, p.Etag)).ToList(), ctx.RequestAborted);
        return Results.Ok(new { fileId, status = "uploaded" });
    }

    /// <summary>
    /// TDD 4.1: any member of the submitter's teams may download. Outside that scope the answer is 404, not 403,
    /// so a file's existence isn't confirmed. Bytes go straight from S3 to the browser (2-min URL).
    /// </summary>
    private static async Task<IResult> CreateDownloadUrl(Guid fileId, HttpContext ctx, NpgsqlDataSource db, StagingStore staging, TeamAccess access)
    {
        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        var file = await conn.QuerySingleOrDefaultAsync<DownloadRow>(
            """
            SELECT f.s3_key, f.original_name, f.owner_user_id, j.submitted_by_user_id
            FROM files f LEFT JOIN jobs j ON j.id = f.job_id
            WHERE f.id = @fileId
            """, new { fileId });
        var requester = ctx.UserGuid();
        var allowed = file is not null && (file.SubmittedByUserId is { } submitter
            ? await access.SharesTeamAsync(requester, submitter, ctx.RequestAborted)
            : file.OwnerUserId == requester);
        if (!allowed)
        {
            throw ApiException.NotFound("File not found");
        }
        return Results.Ok(new { url = staging.CreateDownloadUrl(file!.S3Key, file.OriginalName), expiresIn = (int)StagingStore.DownloadExpiry.TotalSeconds });
    }

    private static (FileUploadTarget Target, string Name) Validate(FileInput f, int index, Guid owner)
    {
        var name = f.Name?.Trim() ?? "";
        if (name.Length is 0 or > 255 || name.IndexOfAny(['/', '\\']) >= 0)
        {
            throw ApiException.BadRequest($"files[{index}].name is invalid");
        }
        if (f.SizeBytes is not { } size || size <= 0 || size > FileRules.MaxFileBytes)
        {
            throw ApiException.BadRequest($"files[{index}].sizeBytes must be between 1 byte and 1 GB");
        }
        if (!FileRules.IsAllowed(f.ContentType, name))
        {
            throw new ApiException(422, "UNSUPPORTED_FILE_TYPE", $"files[{index}]: only PDF, DOCX, XLSX, PNG and JPG are accepted");
        }
        if (!FileRules.IsSha256Hex(f.Sha256))
        {
            throw ApiException.BadRequest($"files[{index}].sha256 must be 64 hex characters");
        }
        // UUID v7 (time-ordered): new rows append to the right edge of each index instead of landing on random pages.
        var id = Guid.CreateVersion7();
        return (new FileUploadTarget(id, $"staging/{owner}/{id}", size, f.ContentType!, f.Sha256!.ToLowerInvariant()), name);
    }

    private static async Task<FileRow> LoadOwnedUnsubmitted(Guid fileId, HttpContext ctx, NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        var file = await conn.QuerySingleOrDefaultAsync<FileRow>(
            "SELECT id, s3_key, size_bytes, content_type, checksum_sha256, job_id FROM files WHERE id = @fileId AND owner_user_id = @owner",
            new { fileId, owner = ctx.UserGuid() }) ?? throw ApiException.NotFound("File not found");
        if (file.JobId is not null)
        {
            throw ApiException.Conflict("File is already part of a submitted job", "FILE_SUBMITTED");
        }
        return file;
    }

    private sealed record FileRow(Guid Id, string S3Key, long SizeBytes, string ContentType, string ChecksumSha256, Guid? JobId)
    {
        public FileUploadTarget ToTarget() => new(Id, S3Key, SizeBytes, ContentType, ChecksumSha256);
    }

    private sealed record DownloadRow(string S3Key, string OriginalName, Guid OwnerUserId, Guid? SubmittedByUserId);
}
