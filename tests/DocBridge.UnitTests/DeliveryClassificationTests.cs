using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using DocBridge.Api;
using DocBridge.Shared.Clients;
using DocBridge.Shared.Jobs;
using DocBridge.Workers;

namespace DocBridge.UnitTests;

/// <summary>TDD unit list: transient-vs-permanent failure classification, presigned policy construction, share-a-team rule.</summary>
public sealed class DeliveryClassificationTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.Content is not null)
            {
                await request.Content.ReadAsByteArrayAsync(ct); // drain like a real server
            }
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static (PlatformIngestClient Client, FakeHandler Platform, FakeHandler Auth) Client(Func<HttpRequestMessage, HttpResponseMessage> platform)
    {
        var auth = new FakeHandler(_ => Json(HttpStatusCode.OK, new { accessToken = "svc-token", expiresIn = 900 }));
        var tokens = new ServiceTokenClient(new HttpClient(auth), "http://auth", new ServiceCredentials("delivery-worker", "s", "documents:ingest"));
        var handler = new FakeHandler(platform);
        return (new PlatformIngestClient(new HttpClient(handler) { BaseAddress = new Uri("http://platform/") }, tokens), handler, auth);
    }

    private static readonly ClaimedTask Task1 = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
        "Team A / Binder", 1, "protocol.pdf", 3, "application/pdf", new string('a', 64), "staging/u/f", Guid.NewGuid());

    private static Task<Guid> Ingest(PlatformIngestClient client) => client.IngestAsync(Task1, new MemoryStream([1, 2, 3]), CancellationToken.None);

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)] // already ingested: idempotent replay
    public async Task Success_returns_document_id(HttpStatusCode status)
    {
        var id = Guid.NewGuid();
        var (client, _, _) = Client(_ => Json(status, new { documentId = id }));
        Assert.Equal(id, await Ingest(client));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "NOT_AUTHORIZED_AT_DELIVERY")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "CHECKSUM_MISMATCH")]
    [InlineData(HttpStatusCode.NotFound, "DESTINATION_NOT_FOUND")]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_DELIVERY")]
    public async Task Client_errors_are_permanent_with_reason_codes(HttpStatusCode status, string code)
    {
        var (client, _, _) = Client(_ => Json(status, new { error = new { code = status == HttpStatusCode.UnprocessableEntity ? "CHECKSUM_MISMATCH" : "X", message = "m" } }));
        var ex = await Assert.ThrowsAsync<PermanentDeliveryException>(() => Ingest(client));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Unsupported_type_keeps_the_platforms_code()
    {
        var (client, _, _) = Client(_ => Json(HttpStatusCode.UnprocessableEntity, new { error = new { code = "UNSUPPORTED_FILE_TYPE", message = "no" } }));
        Assert.Equal("UNSUPPORTED_FILE_TYPE", (await Assert.ThrowsAsync<PermanentDeliveryException>(() => Ingest(client))).Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Server_errors_are_transient(HttpStatusCode status)
    {
        var (client, _, _) = Client(_ => new HttpResponseMessage(status));
        await Assert.ThrowsAsync<TransientDeliveryException>(() => Ingest(client));
    }

    [Fact]
    public async Task Unauthorized_is_transient_and_drops_the_cached_token()
    {
        var (client, _, auth) = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<TransientDeliveryException>(() => Ingest(client));
        await Assert.ThrowsAsync<TransientDeliveryException>(() => Ingest(client));
        Assert.Equal(2, auth.Calls); // token re-fetched after the 401
    }

    [Fact]
    public async Task Network_failure_is_transient()
    {
        var (client, _, _) = Client(_ => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<TransientDeliveryException>(() => Ingest(client));
    }

    [Fact]
    public async Task Service_token_is_cached_across_deliveries()
    {
        var (client, _, auth) = Client(_ => Json(HttpStatusCode.Created, new { documentId = Guid.NewGuid() }));
        await Ingest(client);
        await Ingest(client);
        Assert.Equal(1, auth.Calls);
    }

    [Fact]
    public void Failure_reason_names_file_destination_and_cause()
    {
        var reason = DeliveryProcessor.Describe(Task1, "FILE_NOT_UPLOADED", "never uploaded");
        Assert.Contains("FILE_NOT_UPLOADED", reason);
        Assert.Contains("protocol.pdf", reason);
        Assert.Contains("Team A / Binder", reason);
    }

    [Fact]
    public async Task Presigned_post_policy_pins_size_type_and_checksum()
    {
        var s3 = new AmazonS3Client(new BasicAWSCredentials("AKIDEXAMPLE", "secret"), new AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.USEast1 });
        var store = new StagingStore(s3, s3, "staging-bucket");
        var sha = new string('b', 64);
        var plan = await store.CreateUploadAsync(new FileUploadTarget(Guid.NewGuid(), "staging/u/f", 1234, "application/pdf", sha), CancellationToken.None);

        Assert.Equal("post", plan.Type);
        Assert.Equal(DocBridge.Shared.FileRules.HexToBase64(sha), plan.Fields!["x-amz-checksum-sha256"]);
        var policy = Encoding.UTF8.GetString(Convert.FromBase64String(plan.Fields!["Policy"]));
        Assert.Contains("\"content-length-range\",1,1234", policy.Replace(" ", ""));
        Assert.Contains("application/pdf", policy);
        Assert.Contains("x-amz-checksum-sha256", policy);
        Assert.Contains("staging/u/f", policy);
        Assert.InRange(plan.ExpiresAt, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16)); // ADR-004: 15 min
    }

    [Fact]
    public void Download_url_expires_in_two_minutes()
    {
        var s3 = new AmazonS3Client(new BasicAWSCredentials("AKIDEXAMPLE", "secret"), new AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.USEast1 });
        var url = new StagingStore(s3, s3, "staging-bucket").CreateDownloadUrl("staging/u/f", "a.pdf");
        Assert.Contains("X-Amz-Expires=120", url);
        Assert.Contains("staging/u/f", url);
    }

    [Fact]
    public async Task Share_a_team_uses_intersection_and_caches_for_60s()
    {
        var shared = Guid.NewGuid();
        var me = Guid.NewGuid();
        var teammate = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var teams = new Dictionary<Guid, Guid[]> { [me] = [shared, Guid.NewGuid()], [teammate] = [shared], [stranger] = [Guid.NewGuid()] };
        var platformHandler = new FakeHandler(req =>
        {
            var user = Guid.Parse(req.RequestUri!.Segments[2].TrimEnd('/'));
            return Json(HttpStatusCode.OK, new { teamIds = teams[user] });
        });
        var auth = new FakeHandler(_ => Json(HttpStatusCode.OK, new { accessToken = "t", expiresIn = 900 }));
        var platform = new PlatformClient(new HttpClient(platformHandler) { BaseAddress = new Uri("http://platform/") },
            new ServiceTokenClient(new HttpClient(auth), "http://auth", new ServiceCredentials("docbridge-api", "s", "memberships:read")));
        var access = new TeamAccess(platform);

        Assert.True(await access.SharesTeamAsync(me, me));
        Assert.True(await access.SharesTeamAsync(me, teammate));
        Assert.False(await access.SharesTeamAsync(me, stranger));
        var calls = platformHandler.Calls;
        Assert.True(await access.SharesTeamAsync(me, teammate));
        Assert.Equal(calls, platformHandler.Calls); // served from cache
    }
}
