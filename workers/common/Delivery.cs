using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using DocBridge.Shared;
using DocBridge.Shared.Clients;
using DocBridge.Shared.Errors;
using DocBridge.Shared.Jobs;
using Microsoft.Extensions.Logging;

namespace DocBridge.Workers;

public enum DeliveryOutcomeKind
{
    Completed,
    Failed,
    Retry,
    Skipped,
}

public sealed record DeliveryOutcome(DeliveryOutcomeKind Kind, string? Reason = null, int RetryDelaySeconds = 0)
{
    public static readonly DeliveryOutcome Skipped = new(DeliveryOutcomeKind.Skipped);
}

/// <summary>Raised for anything that should be retried by the queue (5xx, timeouts, network, S3/auth hiccups).</summary>
public sealed class TransientDeliveryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Raised for failures retrying can't fix; surfaced on the task with a reason code.</summary>
public sealed class PermanentDeliveryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Reads through to the inner stream, hashing every byte that passes (one pass, no buffering). The digest is
/// fixed at end of stream, so it survives the HTTP content disposing the stream after sending.
/// </summary>
public sealed class HashingReadStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _digest;

    public long BytesRead { get; private set; }

    /// <summary>Hex SHA-256 of everything read; null if the stream was not read to the end.</summary>
    public string? HexDigest() => _digest;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        return Track(buffer.Span, read);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Track(buffer.AsSpan(offset), inner.Read(buffer, offset, count));

    private int Track(ReadOnlySpan<byte> buffer, int read)
    {
        if (read > 0)
        {
            _hash.AppendData(buffer[..read]);
            BytesRead += read;
        }
        else if (buffer.Length > 0)
        {
            _digest ??= Convert.ToHexStringLower(_hash.GetHashAndReset());
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Platform POST /documents (ADR-013): the worker's own service token plus onBehalfOf for the user.</summary>
public sealed class PlatformIngestClient(HttpClient http, ServiceTokenClient tokens)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Guid> IngestAsync(ClaimedTask task, Stream content, CancellationToken ct)
    {
        string token;
        try
        {
            token = await tokens.GetTokenAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new TransientDeliveryException($"auth service unavailable: {ex.Message}", ex);
        }

        using var form = new MultipartFormDataContent();
        var metadata = new StringContent(JsonSerializer.Serialize(new
        {
            taskId = task.Id,
            binderId = task.BinderId,
            folderId = task.FolderId,
            name = task.FileName,
            contentType = task.ContentType,
            checksumSha256 = task.ChecksumSha256,
            onBehalfOf = task.SubmittedByUserId,
        }, Json));
        metadata.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(metadata, "metadata");
        var file = new StreamContent(content, 1 << 16);
        file.Headers.ContentType = new MediaTypeHeaderValue(task.ContentType);
        file.Headers.ContentLength = task.SizeBytes;
        form.Add(file, "file", task.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new TransientDeliveryException($"platform unreachable: {ex.Message}", ex);
        }

        using (response)
        {
            var (code, error) = await ReadError(response, ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created:
                case HttpStatusCode.OK:
                    var body = await response.Content.ReadFromJsonAsync<IngestResponse>(Json, ct);
                    return body?.DocumentId ?? throw new TransientDeliveryException("platform returned no document id");
                case HttpStatusCode.Forbidden:
                    // Membership revoked between submit and delivery: surfaced, not retried (TDD 2.4).
                    throw new PermanentDeliveryException("NOT_AUTHORIZED_AT_DELIVERY", error ?? "submitter is no longer a member of the destination team");
                case HttpStatusCode.UnprocessableEntity:
                    throw new PermanentDeliveryException(code ?? "CHECKSUM_MISMATCH", error ?? "platform rejected the file");
                case HttpStatusCode.NotFound:
                    throw new PermanentDeliveryException("DESTINATION_NOT_FOUND", error ?? "destination does not exist");
                case HttpStatusCode.BadRequest:
                case HttpStatusCode.RequestEntityTooLarge:
                    throw new PermanentDeliveryException("INVALID_DELIVERY", error ?? $"platform answered {(int)response.StatusCode}");
                case HttpStatusCode.Unauthorized:
                    tokens.Invalidate();
                    throw new TransientDeliveryException("platform rejected the service token");
                default:
                    throw new TransientDeliveryException($"platform answered {(int)response.StatusCode}: {error}");
            }
        }
    }

    private static async Task<(string? Code, string? Message)> ReadError(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return (null, null);
        }
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(Json, ct);
            return (envelope?.Error.Code, envelope?.Error.Message);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private sealed record IngestResponse(Guid DocumentId);
}

/// <summary>TDD 2.4: one task, one delivery attempt.</summary>
public sealed class DeliveryProcessor(
    TaskRepository repo, IAmazonS3 s3, string stagingBucket, PlatformIngestClient platform, StatusPusher pusher, ILogger logger)
{
    /// <summary>Delay before the next attempt, by attempt number (G6). The queue still enforces maxReceiveCount 3.</summary>
    public static readonly int[] RetryBackoffSeconds = Env.Get("RETRY_BACKOFF_SECONDS", "60,300")
        .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

    public async Task<DeliveryOutcome> ProcessAsync(Guid taskId, CancellationToken ct)
    {
        var task = await repo.ClaimAsync(taskId);
        if (task is null)
        {
            logger.LogInformation("Task {TaskId} already terminal or missing; acking", taskId);
            return DeliveryOutcome.Skipped;
        }
        await pusher.PushTaskUpdateAsync(task.JobId, task.Id, TaskStates.InProgress, null, ct);

        try
        {
            var documentId = await DeliverAsync(task, ct);
            await repo.CompleteAsync(task.Id, documentId);
            await Resolve(task, TaskStates.Completed, null, ct);
            logger.LogInformation("Task {TaskId} delivered as document {DocumentId}", task.Id, documentId);
            return new DeliveryOutcome(DeliveryOutcomeKind.Completed);
        }
        catch (PermanentDeliveryException ex)
        {
            var reason = Describe(task, ex.Code, ex.Message);
            await repo.FailAsync(task.Id, reason);
            await Resolve(task, TaskStates.Failed, reason, ct);
            logger.LogWarning("Task {TaskId} failed permanently: {Code}", task.Id, ex.Code);
            return new DeliveryOutcome(DeliveryOutcomeKind.Failed, reason);
        }
        catch (Exception ex)
        {
            // Anything not known to be permanent is retried: after maxReceiveCount the DLQ consumer marks the task
            // RETRIES_EXHAUSTED with this last error, so even an unexpected bug ends visible, never stuck in_progress.
            var reason = Describe(task, "DELIVERY_ERROR", $"attempt {task.AttemptCount}: {ex.Message}");
            await repo.RecordTransientAsync(task.Id, reason);
            var delay = RetryBackoffSeconds.Length == 0 ? 0 : RetryBackoffSeconds[Math.Min(task.AttemptCount, RetryBackoffSeconds.Length) - 1];
            logger.LogWarning(ex, "Task {TaskId} attempt {Attempt} failed transiently; retry in {Delay}s", task.Id, task.AttemptCount, delay);
            return new DeliveryOutcome(DeliveryOutcomeKind.Retry, reason, delay);
        }
    }

    private async Task<Guid> DeliverAsync(ClaimedTask task, CancellationToken ct)
    {
        GetObjectMetadataResponse head;
        try
        {
            head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = stagingBucket,
                Key = task.S3Key,
                ChecksumMode = ChecksumMode.ENABLED,
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Blueprint risk 3: the job referenced a file that never finished uploading.
            throw new PermanentDeliveryException("FILE_NOT_UPLOADED", "the file was never uploaded to staging");
        }
        if (head.ContentLength != task.SizeBytes)
        {
            throw new PermanentDeliveryException("SIZE_MISMATCH", $"staged object is {head.ContentLength} bytes, declared {task.SizeBytes}");
        }
        // Single-part uploads carry the S3-verified SHA-256: compare before sending anything.
        // Multipart objects carry a composite checksum ("…-N") that isn't comparable, so they rely on the streaming hash (G7).
        var expectedBase64 = FileRules.HexToBase64(task.ChecksumSha256);
        if (head.ChecksumSHA256 is { Length: > 0 } stored && !stored.Contains('-') && stored != expectedBase64)
        {
            throw new PermanentDeliveryException("CHECKSUM_MISMATCH", "staged object does not match the declared SHA-256");
        }

        using var obj = await s3.GetObjectAsync(stagingBucket, task.S3Key, ct);
        await using var hashing = new HashingReadStream(obj.ResponseStream);
        var documentId = await platform.IngestAsync(task, hashing, ct);
        if (hashing.HexDigest() is { } digest && !string.Equals(digest, task.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            // The platform verifies the same digest and would have answered 422; this is the worker's own check.
            throw new PermanentDeliveryException("CHECKSUM_MISMATCH", "bytes read from staging do not match the declared SHA-256");
        }
        return documentId;
    }

    private async Task Resolve(ClaimedTask task, string status, string? reason, CancellationToken ct)
    {
        await repo.TryCloseJobAsync(task.JobId);
        await pusher.PushTaskUpdateAsync(task.JobId, task.Id, status, reason, ct);
    }

    /// <summary>failure_reason carries file name, destination and cause (TDD 2.4).</summary>
    internal static string Describe(ClaimedTask task, string code, string detail) =>
        $"{code}: '{task.FileName}' → {task.Destination}: {detail}";
}
