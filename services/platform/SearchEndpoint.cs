using Dapper;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Errors;
using Npgsql;

namespace DocBridge.Platform;

public sealed record SearchHit(
    string Type, Guid Id, string Name, Guid TeamId, string TeamName, string Region, Guid? BinderId, string? BinderName,
    Guid? FolderId, IReadOnlyList<string> Path);

/// <summary>
/// GET /search?q=: teams, binders and folders whose name contains q, limited to the caller's memberships (like every
/// other read). Folders come back with their full path, so the client can show and target them without expanding the
/// tree level by level. Name matching is a substring scan over the caller's own teams only, which stays small.
/// </summary>
public static class SearchEndpoint
{
    private const int MaxTeams = 10;
    private const int MaxBinders = 20;
    private const int MaxFolders = 30;

    public static void MapSearchEndpoint(this WebApplication app) =>
        app.MapGet("/search", Search).RequireAuthorization(AuthPolicies.User);

    /// <summary>ILIKE pattern for a literal substring: escapes the wildcards % and _ and the escape character itself.</summary>
    internal static string ContainsPattern(string q) =>
        "%" + q.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

    private static async Task<IResult> Search(HttpContext ctx, NpgsqlDataSource db, string? q, bool? docbridgeEnabled)
    {
        var term = q?.Trim() ?? "";
        if (term.Length is < 2 or > 100)
        {
            throw ApiException.BadRequest("q must be 2 to 100 characters");
        }
        var args = new { userId = ctx.UserGuid(), pattern = ContainsPattern(term), enabled = docbridgeEnabled };
        const string memberTeams =
            """
            SELECT t.id, t.name, t.region FROM teams t
            JOIN team_members m ON m.team_id = t.id AND m.user_id = @userId
            WHERE (@enabled::boolean IS NULL OR t.docbridge_enabled = @enabled)
            """;

        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        var teams = await conn.QueryAsync<TeamRow>(
            $"SELECT id, name, region FROM ({memberTeams}) t WHERE name ILIKE @pattern ORDER BY name LIMIT {MaxTeams}", args);
        var binders = await conn.QueryAsync<BinderRow>(
            $"""
             SELECT b.id, b.name, t.id AS team_id, t.name AS team_name, t.region
             FROM binders b JOIN ({memberTeams}) t ON t.id = b.team_id
             WHERE b.name ILIKE @pattern ORDER BY b.name LIMIT {MaxBinders}
             """, args);
        var folders = (await conn.QueryAsync<FolderRow>(
            $"""
             SELECT f.id, f.name, b.id AS binder_id, b.name AS binder_name, t.id AS team_id, t.name AS team_name, t.region
             FROM folders f JOIN binders b ON b.id = f.binder_id JOIN ({memberTeams}) t ON t.id = b.team_id
             WHERE f.name ILIKE @pattern ORDER BY f.name LIMIT {MaxFolders}
             """, args)).ToList();

        // Ancestor chain for the matched folders only (bounded by MaxFolders), root first.
        var paths = folders.Count == 0 ? [] : (await conn.QueryAsync<PathRow>(
            """
            WITH RECURSIVE up AS (
              SELECT f.id AS leaf, f.parent_folder_id, f.name, 0 AS depth FROM folders f WHERE f.id = ANY(@ids)
              UNION ALL
              SELECT up.leaf, p.parent_folder_id, p.name, up.depth + 1 FROM up JOIN folders p ON p.id = up.parent_folder_id
            )
            SELECT leaf, array_agg(name ORDER BY depth DESC) AS names FROM up GROUP BY leaf
            """, new { ids = folders.Select(f => f.Id).ToArray() })).ToDictionary(p => p.Leaf, p => p.Names);

        var hits = new List<SearchHit>();
        hits.AddRange(teams.Select(t => new SearchHit("team", t.Id, t.Name, t.Id, t.Name, t.Region, null, null, null, [t.Name])));
        hits.AddRange(binders.Select(b => new SearchHit("binder", b.Id, b.Name, b.TeamId, b.TeamName, b.Region, b.Id, b.Name, null,
            [b.TeamName, b.Name])));
        hits.AddRange(folders.Select(f => new SearchHit("folder", f.Id, f.Name, f.TeamId, f.TeamName, f.Region, f.BinderId, f.BinderName,
            f.Id, [f.TeamName, f.BinderName, .. paths.GetValueOrDefault(f.Id, [f.Name])])));
        return Results.Ok(hits);
    }

    private sealed record TeamRow(Guid Id, string Name, string Region);

    private sealed record BinderRow(Guid Id, string Name, Guid TeamId, string TeamName, string Region);

    private sealed record FolderRow(Guid Id, string Name, Guid BinderId, string BinderName, Guid TeamId, string TeamName, string Region);

    private sealed class PathRow
    {
        public Guid Leaf { get; init; }
        public string[] Names { get; init; } = [];
    }
}
