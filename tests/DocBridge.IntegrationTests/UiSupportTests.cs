using System.Net;
using static DocBridge.IntegrationTests.Harness;

namespace DocBridge.IntegrationTests;

/// <summary>Endpoints behind the browse/audit-log UI: destination search and per-file staging status.</summary>
public sealed class UiSupportTests
{
    [Fact]
    public async Task Search_finds_nested_folders_with_their_path_only_in_my_enabled_teams()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "srch", folders: false);
        var marker = Unique("Zeta");
        var binder = ws.Destinations[0].BinderId;
        var parent = await AdminCreate($"admin/binders/{binder}/folders", new { name = "Ethics" });
        var nested = await AdminCreate($"admin/binders/{binder}/folders", new { name = $"{marker} Approvals", parentFolderId = parent });

        var foreignTeam = await CreateTeam(Unique("srch-foreign"));
        var foreignBinder = await AdminCreate($"admin/teams/{foreignTeam}/binders", new { name = "B" });
        await AdminCreate($"admin/binders/{foreignBinder}/folders", new { name = $"{marker} Foreign" });

        var disabledTeam = await CreateTeam(Unique("srch-off"), enabled: false);
        await AddMember(disabledTeam, ws.User.Id);
        var disabledBinder = await AdminCreate($"admin/teams/{disabledTeam}/binders", new { name = "B" });
        await AdminCreate($"admin/binders/{disabledBinder}/folders", new { name = $"{marker} Disabled" });

        var (status, body, _) = await Get($"destinations/search?q={Uri.EscapeDataString(marker)}", ws.User.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        var hit = Assert.Single(body.EnumerateArray());
        Assert.Equal("folder", hit.GetProperty("type").GetString());
        Assert.Equal(nested, hit.GetProperty("folderId").GetGuid());
        Assert.Equal(binder, hit.GetProperty("binderId").GetGuid());
        var path = hit.GetProperty("path").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(4, path.Count); // team / binder / Ethics / <marker> Approvals
        Assert.Equal("Ethics", path[2]);

        Assert.Equal(HttpStatusCode.BadRequest, (await Get("destinations/search?q=a", ws.User.Token)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get($"destinations/search?q={marker}", null)).Status);
    }

    [Fact]
    public async Task Search_treats_wildcards_literally()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "wild", folders: false);
        await AdminCreate($"admin/binders/{ws.Destinations[0].BinderId}/folders", new { name = "plain folder" });
        var (_, body, _) = await Get("destinations/search?q=%25%25", ws.User.Token); // "%%"
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task Job_files_report_their_staging_status()
    {
        var ws = await NewWorkspace(teams: 1, prefix: "fstat", folders: false);
        var uploaded = await Stage(ws.User, 1);
        var missing = await Stage(ws.User, 1, upload: false);
        var jobId = await Submit(ws.User, uploaded.Concat(missing), ws.Destinations);
        var (_, body, _) = await Get($"jobs/{jobId}", ws.User.Token);
        var files = body.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("fileId").GetGuid(), f => f.GetProperty("status").GetString());
        Assert.Equal("ready", files[uploaded[0].FileId]);
        Assert.Equal("not_uploaded", files[missing[0].FileId]);
    }
}
