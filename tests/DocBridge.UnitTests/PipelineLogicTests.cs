using System.Security.Cryptography;
using System.Text;
using DocBridge.Api;
using DocBridge.Shared;
using DocBridge.Shared.Errors;
using DocBridge.Shared.Jobs;
using DocBridge.Workers;

namespace DocBridge.UnitTests;

/// <summary>TDD unit list: aggregate status derivation, mapping expansion and limits, file rules.</summary>
public sealed class PipelineLogicTests
{
    [Theory]
    [InlineData(5, 0, 0, 0, "pending")]
    [InlineData(4, 1, 0, 0, "in_progress")]
    [InlineData(3, 0, 2, 0, "in_progress")]
    [InlineData(0, 1, 3, 1, "in_progress")]
    [InlineData(0, 0, 5, 0, "completed")]
    [InlineData(0, 0, 4, 1, "completed_with_errors")]
    [InlineData(0, 0, 0, 5, "completed_with_errors")]
    public void Aggregate_status_follows_the_tdd_mapping(long pending, long inProgress, long completed, long failed, string expected) =>
        Assert.Equal(expected, new TaskCounts(pending, inProgress, completed, failed).Aggregate());

    private static DestinationInput Dest(Guid? team = null, Guid? binder = null, Guid? folder = null) =>
        new(team ?? Guid.NewGuid(), binder ?? Guid.NewGuid(), folder, "label");

    [Fact]
    public void Expand_is_files_times_destinations_and_dedupes_repeats()
    {
        var a = Dest();
        var b = Dest(folder: Guid.NewGuid());
        var request = new SubmitJobRequest([
            new MappingInput(Guid.NewGuid(), [a, b, a]),
            new MappingInput(Guid.NewGuid(), [b]),
        ]);
        var tasks = JobEndpoints.Expand(request);
        Assert.Equal(3, tasks.Count);
        Assert.Equal(3, tasks.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Expand_accepts_the_full_100_by_20_job()
    {
        var destinations = Enumerable.Range(0, 20).Select(_ => Dest()).ToList();
        var request = new SubmitJobRequest(Enumerable.Range(0, 100).Select(_ => new MappingInput(Guid.NewGuid(), destinations)).ToList());
        Assert.Equal(2000, JobEndpoints.Expand(request).Count);
    }

    [Fact]
    public void Expand_rejects_more_than_20_destinations()
    {
        var destinations = Enumerable.Range(0, 21).Select(_ => Dest()).ToList();
        var ex = Assert.Throws<ApiException>(() => JobEndpoints.Expand(new SubmitJobRequest([new MappingInput(Guid.NewGuid(), destinations)])));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void Expand_rejects_more_than_100_files()
    {
        var d = Dest();
        var request = new SubmitJobRequest(Enumerable.Range(0, 101).Select(_ => new MappingInput(Guid.NewGuid(), [d])).ToList());
        Assert.Throws<ApiException>(() => JobEndpoints.Expand(request));
    }

    [Fact]
    public void Expand_rejects_empty_and_duplicate_file_mappings()
    {
        Assert.Throws<ApiException>(() => JobEndpoints.Expand(new SubmitJobRequest([])));
        Assert.Throws<ApiException>(() => JobEndpoints.Expand(new SubmitJobRequest([new MappingInput(Guid.NewGuid(), [])])));
        var file = Guid.NewGuid();
        Assert.Throws<ApiException>(() => JobEndpoints.Expand(new SubmitJobRequest([new MappingInput(file, [Dest()]), new MappingInput(file, [Dest()])])));
        Assert.Throws<ApiException>(() => JobEndpoints.Expand(new SubmitJobRequest([new MappingInput(Guid.NewGuid(), [new DestinationInput(null, Guid.NewGuid(), null, null)])])));
    }

    [Theory]
    [InlineData("application/pdf", "protocol.pdf", true)]
    [InlineData("application/pdf", "PROTOCOL.PDF", true)]
    [InlineData("image/jpeg", "scan.jpeg", true)]
    [InlineData("image/jpeg", "scan.png", false)]
    [InlineData("application/x-msdownload", "setup.exe", false)]
    [InlineData("application/pdf", "setup.exe", false)]
    [InlineData(null, "a.pdf", false)]
    public void File_type_allow_list_requires_matching_extension(string? type, string name, bool allowed) =>
        Assert.Equal(allowed, FileRules.IsAllowed(type, name));

    [Fact]
    public void Sha256_hex_converts_to_the_base64_s3_expects()
    {
        var bytes = Encoding.UTF8.GetBytes("hello");
        var hex = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.True(FileRules.IsSha256Hex(hex));
        Assert.False(FileRules.IsSha256Hex(hex[..63]));
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), FileRules.HexToBase64(hex));
    }

    [Fact]
    public async Task Hashing_stream_hashes_exactly_what_passes_through()
    {
        var data = RandomNumberGenerator.GetBytes(1_000_003);
        var hashing = new HashingReadStream(new MemoryStream(data));
        Assert.Null(hashing.HexDigest());
        await hashing.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
        await hashing.DisposeAsync(); // HTTP content disposes the stream after sending; the digest must survive that
        Assert.Equal(data.Length, hashing.BytesRead);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), hashing.HexDigest());
    }

    [Fact]
    public async Task Platform_copy_hashing_matches_and_writes_all_bytes()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        using var destination = new MemoryStream();
        var (hex, size) = await DocBridge.Platform.IngestEndpoint.CopyHashing(new MemoryStream(data), destination, TestContext.Current.CancellationToken);
        Assert.Equal(data.Length, size);
        Assert.Equal(data, destination.ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), hex);
    }
}

public sealed class SearchPatternTests
{
    [Theory]
    [InlineData("irb", "%irb%")]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData(@"c:\x", @"%c:\\x%")]
    public void Search_term_is_matched_literally(string term, string pattern) =>
        Assert.Equal(pattern, DocBridge.Platform.SearchEndpoint.ContainsPattern(term));
}
