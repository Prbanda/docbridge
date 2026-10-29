using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Dapper;
using DocBridge.Shared;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Errors;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace DocBridge.Platform;

public sealed record IngestMetadata(
    Guid? TaskId, Guid? BinderId, Guid? FolderId, string? Name, string? ContentType, string? ChecksumSha256, Guid? OnBehalfOf);

public sealed record PlatformStorage(IAmazonS3 S3, string Bucket);

/// <summary>
/// POST /documents (TDD 1.3, ADR-013). Multipart: a JSON "metadata" part, then the "file" part.
/// The bytes stream to a temp file while SHA-256 is computed, so a 1 GB file is never in memory and a
/// mismatched file is never stored. Last link of the integrity chain: client → S3 → worker → platform.
/// </summary>
public static class IngestEndpoint
{
    private const long MaxBodyBytes = FileRules.MaxFileBytes + (1 << 20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapIngestEndpoint(this WebApplication app) =>
        app.MapPost("/documents", Ingest).RequireAuthorization(AuthPolicies.Scope("documents:ingest"));

    private static async Task<IResult> Ingest(HttpContext ctx, NpgsqlDataSource db, PlatformStorage storage, ILoggerFactory logs)
    {
        ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxBodyBytes;
        var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(ctx.Request.ContentType ?? "").Boundary).Value
            ?? throw ApiException.BadRequest("multipart/form-data body required");
        var reader = new MultipartReader(boundary, ctx.Request.Body);

        var metaSection = await reader.ReadNextSectionAsync(ctx.RequestAborted)
            ?? throw ApiException.BadRequest("metadata part missing");
        var meta = await JsonSerializer.DeserializeAsync<IngestMetadata>(metaSection.Body, Json, ctx.RequestAborted)
            ?? throw ApiException.BadRequest("metadata part missing");
        Validate(meta);
        var fileSection = await reader.ReadNextSectionAsync(ctx.RequestAborted)
            ?? throw ApiException.BadRequest("file part missing");

        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);

        // Idempotency: the same task delivered twice ingests once.
        var existing = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM documents WHERE source_task_id = @TaskId", new { meta.TaskId });
        if (existing is not null)
        {
            await Drain(fileSection.Body, ctx);
            return Results.Ok(new { documentId = existing });
        }

        // Time-of-use authorization: the submitting user must still be a member of the destination team.
        var target = await conn.QuerySingleOrDefaultAsync<IngestTarget>(
            """
            SELECT b.team_id,
                   EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = b.team_id AND m.user_id = @OnBehalfOf) AS is_member,
                   (@FolderId::uuid IS NULL OR EXISTS (SELECT 1 FROM folders f WHERE f.id = @FolderId AND f.binder_id = b.id)) AS folder_ok
            FROM binders b WHERE b.id = @BinderId
            """, new { meta.BinderId, meta.FolderId, meta.OnBehalfOf });
        if (target is null || !target.FolderOk)
        {
            await Drain(fileSection.Body, ctx);
            throw new ApiException(404, "DESTINATION_NOT_FOUND", "Destination binder or folder does not exist");
        }
        if (!target.IsMember)
        {
            await Drain(fileSection.Body, ctx);
            throw new ApiException(403, "NOT_A_MEMBER", "Submitting user is not a member of the destination team");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"ingest-{Guid.NewGuid():N}");
        try
        {
            string actualHex;
            long size;
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                (actualHex, size) = await CopyHashing(fileSection.Body, temp, ctx.RequestAborted);
            }
            if (!string.Equals(actualHex, meta.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(422, "CHECKSUM_MISMATCH",
                    $"Received bytes hash to {actualHex}, expected {meta.ChecksumSha256!.ToLowerInvariant()}");
            }

            var documentId = Guid.NewGuid();
            var key = $"documents/{target.TeamId}/{meta.BinderId}/{documentId}";
            await storage.S3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = storage.Bucket,
                Key = key,
                FilePath = tempPath,
                ContentType = meta.ContentType,
                ChecksumSHA256 = FileRules.HexToBase64(actualHex),
            }, ctx.RequestAborted);

            var inserted = await conn.ExecuteScalarAsync<Guid?>(
                """
                INSERT INTO documents (id, binder_id, folder_id, name, size_bytes, content_type, checksum_sha256,
                                       source_task_id, uploaded_by_user_id, storage_key)
                VALUES (@documentId, @BinderId, @FolderId, @Name, @size, @ContentType, @checksum, @TaskId, @OnBehalfOf, @key)
                ON CONFLICT (source_task_id) DO NOTHING
                RETURNING id
                """, new
                {
                    documentId,
                    meta.BinderId,
                    meta.FolderId,
                    meta.Name,
                    size,
                    meta.ContentType,
                    checksum = actualHex,
                    meta.TaskId,
                    meta.OnBehalfOf,
                    key
                });
            if (inserted is null)
            {
                // A concurrent duplicate won the insert: keep its row, drop our copy of the bytes.
                await storage.S3.DeleteObjectAsync(storage.Bucket, key);
                var winner = await conn.ExecuteScalarAsync<Guid>("SELECT id FROM documents WHERE source_task_id = @TaskId", new { meta.TaskId });
                return Results.Ok(new { documentId = winner });
            }
            logs.CreateLogger("Ingest").LogInformation("Ingested task {TaskId} as document {DocumentId} ({Size} bytes)", meta.TaskId, documentId, size);
            return Results.Created($"/documents/{documentId}", new { documentId });
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static void Validate(IngestMetadata meta)
    {
        if (meta.TaskId is null || meta.BinderId is null || meta.OnBehalfOf is null || string.IsNullOrWhiteSpace(meta.Name))
        {
            throw ApiException.BadRequest("taskId, binderId, name and onBehalfOf are required");
        }
        if (!FileRules.IsSha256Hex(meta.ChecksumSha256))
        {
            throw ApiException.BadRequest("checksumSha256 must be 64 hex characters");
        }
        if (!FileRules.IsAllowed(meta.ContentType, meta.Name))
        {
            throw new ApiException(422, "UNSUPPORTED_FILE_TYPE", "Only PDF, DOCX, XLSX, PNG and JPG files are accepted");
        }
    }

    internal static async Task<(string Hex, long Size)> CopyHashing(Stream source, Stream destination, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long size = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            size += read;
            if (size > FileRules.MaxFileBytes)
            {
                throw new ApiException(413, "PAYLOAD_TOO_LARGE", "File exceeds 1 GB");
            }
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), size);
    }

    /// <summary>Read the rest of the body before answering early, so the sender sees the response, not a reset.</summary>
    private static Task Drain(Stream body, HttpContext ctx) => body.CopyToAsync(Stream.Null, ctx.RequestAborted);

    private sealed record IngestTarget(Guid TeamId, bool IsMember, bool FolderOk);
}
