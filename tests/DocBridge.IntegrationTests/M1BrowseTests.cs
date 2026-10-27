using System.Diagnostics;
using System.Net;
using static DocBridge.IntegrationTests.Harness;

namespace DocBridge.IntegrationTests;

/// <summary>M1 / Flow 1: authentication, token issuance, permission-accurate destination browsing.</summary>
public sealed class M1BrowseTests
{
    [Theory]
    [InlineData("destinations/teams")]
    [InlineData("jobs")]
    [InlineData("teams")]
    public async Task Unauthenticated_requests_are_rejected(string path)
    {
        var (status, body, _) = await Get(path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        _ = body;
    }

    [Fact]
    public async Task Unauthenticated_writes_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("files", null, new { files = Array.Empty<object>() })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("jobs", null, new { mappings = Array.Empty<object>() })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post($"files/{Guid.NewGuid()}/download-url", null)).Status);
    }

    [Fact]
    public async Task Forged_token_is_rejected()
    {
        var user = await NewUser("forge");
        var parts = user.Token.Split('.');
        var forged = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("destinations/teams", forged)).Status);
    }

    [Fact]
    public async Task Register_login_issues_a_15_minute_rs256_jwt()
    {
        var user = await NewUser("login");
        var claims = Claims(user.Token);
        Assert.Equal(user.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal(user.Email, claims.GetProperty("email").GetString());
        Assert.Equal("docbridge", claims.GetProperty("aud").GetString());
        var lifetime = claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64();
        Assert.Equal(900, lifetime);
        var header = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(Pad(user.Token.Split('.')[0]))).RootElement;
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.False(string.IsNullOrEmpty(header.GetProperty("kid").GetString()));
    }

    private static string Pad(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    [Fact]
    public async Task Duplicate_registration_gets_a_generic_error()
    {
        var user = await NewUser("dup");
        var (status, body, _) = await Post("auth/register", null, new { email = user.Email.ToUpperInvariant(), password = Password, name = "x" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("REGISTRATION_FAILED", ErrorCode(body));
        Assert.DoesNotContain("exist", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_look_identical()
    {
        var user = await NewUser("pw");
        var wrong = await Post("auth/login", null, new { email = user.Email, password = "not-the-password" });
        var unknown = await Post("auth/login", null, new { email = $"nobody-{Guid.NewGuid():N}@it.docbridge.test", password = "whatever-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal(wrong.Status, unknown.Status);
        Assert.Equal(ErrorCode(wrong.Body), ErrorCode(unknown.Body));
    }

    [Fact]
    public async Task Weak_password_and_bad_email_are_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("auth/register", null, new { email = "a@b.test", password = "short", name = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("auth/register", null, new { email = "nope", password = Password, name = "x" })).Status);
    }

    [Fact]
    public async Task Jwks_and_discovery_document_are_public()
    {
        var jwks = await Get(".well-known/jwks.json", null);
        Assert.Equal(HttpStatusCode.OK, jwks.Status);
        Assert.True(jwks.Body.GetProperty("keys").GetArrayLength() >= 1);
        var discovery = await Get(".well-known/openid-configuration", null);
        Assert.Equal(HttpStatusCode.OK, discovery.Status);
        Assert.EndsWith("/.well-known/jwks.json", discovery.Body.GetProperty("jwks_uri").GetString());
    }

    [Fact]
    public async Task Service_tokens_are_scoped()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("auth/token", null, new { clientId = "platform-admin", clientSecret = "wrong", scope = "platform:admin" })).Status);
        var badScope = await Post("auth/token", null, new { clientId = "platform-admin", clientSecret = AdminSecret, scope = "documents:ingest" });
        Assert.Equal(HttpStatusCode.BadRequest, badScope.Status);
        Assert.Equal("INVALID_SCOPE", ErrorCode(badScope.Body));

        var admin = await AdminToken();
        var claims = Claims(admin);
        Assert.Equal("service", claims.GetProperty("token_use").GetString());
        // A service token can't act as a user, and a user token can't call service routes.
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("destinations/teams", admin)).Status);
        var user = await NewUser("scope");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("admin/teams", user.Token, new { name = "x", region = "US" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get($"teams/{Guid.NewGuid()}/members/{user.Id}", user.Token)).Status);
    }

    [Fact]
    public async Task User_sees_only_member_teams_that_have_docbridge_enabled()
    {
        var ws = await NewWorkspace(teams: 2, prefix: "browse");
        var disabled = await CreateTeam(Unique("disabled"), enabled: false);
        await AddMember(disabled, ws.User.Id);
        var foreign = await CreateTeam(Unique("foreign"));

        var (status, body, _) = await Get("destinations/teams", ws.User.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        var ids = body.EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Equal(ws.Teams.ToHashSet(), ids);
        Assert.DoesNotContain(disabled, ids);
        Assert.DoesNotContain(foreign, ids);
        Assert.All(body.EnumerateArray(), t => Assert.False(string.IsNullOrEmpty(t.GetProperty("region").GetString())));

        Assert.Equal(HttpStatusCode.Forbidden, (await Get($"destinations/teams/{foreign}/binders", ws.User.Token)).Status);
    }

    [Fact]
    public async Task Tree_expands_one_level_at_a_time_including_nested_folders()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "tree");
        var root = ws.Destinations.First(d => d.FolderId is null);
        var level1 = ws.Destinations.First(d => d.FolderId is not null).FolderId!.Value;
        var level2 = await AdminCreate($"admin/binders/{root.BinderId}/folders", new { name = "IRB", parentFolderId = level1 });
        await AdminCreate($"admin/binders/{root.BinderId}/folders", new { name = "2026", parentFolderId = level2 });

        var binders = await Get($"destinations/teams/{root.TeamId}/binders", ws.User.Token);
        Assert.Single(binders.Body.EnumerateArray());

        var top = await Get($"destinations/binders/{root.BinderId}/contents", ws.User.Token);
        Assert.Equal(new[] { level1 }, top.Body.GetProperty("folders").EnumerateArray().Select(f => f.GetProperty("id").GetGuid()));
        var mid = await Get($"destinations/binders/{root.BinderId}/contents?folderId={level1}", ws.User.Token);
        Assert.Equal(new[] { level2 }, mid.Body.GetProperty("folders").EnumerateArray().Select(f => f.GetProperty("id").GetGuid()));
        var deep = await Get($"destinations/binders/{root.BinderId}/contents?folderId={level2}", ws.User.Token);
        Assert.Equal("2026", deep.Body.GetProperty("folders")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Contents_of_another_teams_binder_are_forbidden_and_foreign_folders_not_found()
    {
        var mine = await NewWorkspace(teams: 1, prefix: "own");
        var theirs = await NewWorkspace(teams: 1, prefix: "other");
        var theirBinder = theirs.Destinations[0].BinderId;
        Assert.Equal(HttpStatusCode.Forbidden, (await Get($"destinations/binders/{theirBinder}/contents", mine.User.Token)).Status);
        var theirFolder = theirs.Destinations.First(d => d.FolderId is not null).FolderId;
        Assert.Equal(HttpStatusCode.NotFound,
            (await Get($"destinations/binders/{mine.Destinations[0].BinderId}/contents?folderId={theirFolder}", mine.User.Token)).Status);
    }

    [Fact]
    public async Task Destination_browser_loads_50_teams_well_under_one_second()
    {
        var user = await NewUser("fifty");
        var teams = new List<Guid>();
        foreach (var batch in Enumerable.Range(0, 50).Chunk(10))
        {
            teams.AddRange(await Task.WhenAll(batch.Select(async i =>
            {
                var team = await CreateTeam(Unique($"t{i}"));
                await AddMember(team, user.Id);
                await AdminCreate($"admin/teams/{team}/binders", new { name = "B" });
                return team;
            })));
        }
        await Get("destinations/teams", user.Token); // warm caches (JWKS, service token)
        var samples = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            var (status, body, _) = await Get("destinations/teams", user.Token);
            sw.Stop();
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(50, body.GetArrayLength());
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        // First visible level of the tree; each expand is a separate one-level call with the same budget.
        var expand = await Get($"destinations/teams/{teams[0]}/binders", user.Token);
        Assert.InRange(expand.Elapsed.TotalMilliseconds, 0, 1000);
        Assert.InRange(samples.Max(), 0, 1000);
        Metric($"50-team browse: max {samples.Max():F0} ms, median {samples.Order().ElementAt(5):F0} ms");
    }
}
