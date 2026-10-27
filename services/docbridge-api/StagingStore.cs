using Amazon.S3;
using Amazon.S3.Model;
using DocBridge.Shared;
using DocBridge.Shared.Errors;

namespace DocBridge.Api;

public sealed record UploadPart(int PartNumber, string Url);

/// <summary>Either a presigned POST form (≤100 MB) or a multipart plan with one signed URL per part.</summary>
public sealed record UploadPlan(
    string Type, string? Url, IDictionary<string, string>? Fields, string? UploadId, long? PartSize, IReadOnlyList<UploadPart>? Parts,
    DateTime ExpiresAt);

public sealed record FileUploadTarget(Guid FileId, string S3Key, long SizeBytes, string ContentType, string Sha256Hex);

/// <summary>
/// ADR-002/004: the browser moves bytes straight to S3. The API only signs, and only after an authorization
/// check. Every URL covers exactly one object key and one operation and expires on ADR-004's schedule.
/// </summary>
public sealed class StagingStore(IAmazonS3 s3, IAmazonS3 presigner, string bucket)
{
    // GetPreSignedURL defaults to https; match the presigner's endpoint (plain http for LocalStack locally).
    private readonly Protocol _protocol = presigner.Config.ServiceURL?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
        ? Protocol.HTTP
        : Protocol.HTTPS;

    public const long MultipartThreshold = 100L * 1024 * 1024;
    public const long PartSize = 64L * 1024 * 1024;
    public static readonly TimeSpan UploadExpiry = TimeSpan.FromMinutes(Env.GetInt("UPLOAD_URL_EXPIRY_MINUTES", 15));
    public static readonly TimeSpan DownloadExpiry = TimeSpan.FromSeconds(Env.GetInt("DOWNLOAD_URL_EXPIRY_SECONDS", 120));

    public string Bucket => bucket;

    public async Task<UploadPlan> CreateUploadAsync(FileUploadTarget file, CancellationToken ct)
    {
        var expires = DateTime.UtcNow.Add(UploadExpiry);
        if (file.SizeBytes <= MultipartThreshold)
        {
            var checksum = FileRules.HexToBase64(file.Sha256Hex);
            var post = await presigner.CreatePresignedPostAsync(new CreatePresignedPostRequest
            {
                BucketName = bucket,
                Key = file.S3Key,
                Expires = expires,
                Fields = new Dictionary<string, string>
                {
                    ["Content-Type"] = file.ContentType,
                    ["x-amz-checksum-algorithm"] = "SHA256",
                    ["x-amz-checksum-sha256"] = checksum,
                },
                Conditions =
                [
                    // Hard ceiling at the declared size; S3 rejects anything larger.
                    S3PostCondition.ContentLengthRange(1, file.SizeBytes),
                    S3PostCondition.ExactMatch("Content-Type", file.ContentType),
                    // S3 verifies the uploaded bytes against the client-declared SHA-256 on write.
                    S3PostCondition.ExactMatch("x-amz-checksum-algorithm", "SHA256"),
                    S3PostCondition.ExactMatch("x-amz-checksum-sha256", checksum),
                ],
            });
            return new UploadPlan("post", post.Url, post.Fields, null, null, null, expires);
        }

        // G7: above 100 MB, S3 multipart with individually signed parts (within-session resume).
        // S3 can't check a whole-object SHA-256 on multipart, so the worker and the platform verify it.
        var init = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = file.S3Key,
            ContentType = file.ContentType,
        }, ct);
        return MultipartPlan(file, init.UploadId, expires);
    }

    /// <summary>Re-sign an in-flight multipart upload (expired part URLs) without restarting it.</summary>
    public UploadPlan MultipartPlan(FileUploadTarget file, string uploadId, DateTime expires)
    {
        var partCount = (int)((file.SizeBytes + PartSize - 1) / PartSize);
        var parts = Enumerable.Range(1, partCount).Select(n => new UploadPart(n, presigner.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = file.S3Key,
            Verb = HttpVerb.PUT,
            Protocol = _protocol,
            UploadId = uploadId,
            PartNumber = n,
            Expires = expires,
        }))).ToList();
        return new UploadPlan("multipart", null, null, uploadId, PartSize, parts, expires);
    }

    public async Task CompleteMultipartAsync(FileUploadTarget file, string uploadId, IReadOnlyList<PartETag> parts, CancellationToken ct)
    {
        try
        {
            await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = file.S3Key,
                UploadId = uploadId,
                PartETags = parts.OrderBy(p => p.PartNumber).ToList(),
            }, ct);
        }
        catch (AmazonS3Exception ex) when ((int)ex.StatusCode is 400 or 404)
        {
            throw ApiException.BadRequest($"Multipart upload could not be completed: {ex.ErrorCode}", "MULTIPART_INVALID");
        }

        // A PUT URL can't enforce a size ceiling, so check the assembled object against the declared size.
        var head = await s3.GetObjectMetadataAsync(bucket, file.S3Key, ct);
        if (head.ContentLength != file.SizeBytes)
        {
            await s3.DeleteObjectAsync(bucket, file.S3Key, ct);
            throw new ApiException(422, "SIZE_MISMATCH", $"Uploaded {head.ContentLength} bytes, declared {file.SizeBytes}");
        }
    }

    /// <summary>
    /// Whether the staged object exists. Missing objects answer 404 when the caller may list the bucket and 403 when
    /// it may not; both mean "not there" for a status display.
    /// </summary>
    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await s3.GetObjectMetadataAsync(bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            return false;
        }
    }

    public string CreateDownloadUrl(string key, string fileName) => presigner.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = bucket,
        Key = key,
        Verb = HttpVerb.GET,
        Protocol = _protocol,
        Expires = DateTime.UtcNow.Add(DownloadExpiry),
        ResponseHeaderOverrides = new ResponseHeaderOverrides
        {
            ContentDisposition = $"attachment; filename=\"{fileName.Replace("\"", "")}\"",
        },
    });
}
