using Dapper;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Errors;
using Npgsql;

namespace DocBridge.Platform;

public sealed record TeamDto(Guid Id, string Name, string Region);
public sealed record BinderDto(Guid Id, string Name);
public sealed record FolderDto(Guid Id, string Name);
public sealed record DocumentDto(Guid Id, string Name, long SizeBytes, string ContentType, string ChecksumSha256, Guid UploadedByUserId, DateTime CreatedAt);

/// <summary>
/// User-token reads. Membership is enforced inside each query (TDD 1.2). The access pattern is
/// expand-one-level on indexed adjacency lists: no recursive CTE on the hot path (ADR-010).
/// </summary>
public static class HierarchyEndpoints
{
    public static void MapHierarchyEndpoints(this WebApplication app)
    {
        app.MapGet("/teams", GetTeams).RequireAuthorization(AuthPolicies.User);
        app.MapGet("/teams/{teamId:guid}/binders", GetBinders).RequireAuthorization(AuthPolicies.User);
        app.MapGet("/binders/{binderId:guid}/contents", GetContents).RequireAuthorization(AuthPolicies.User);
        app.MapGet("/teams/{teamId:guid}/members/{userId:guid}", GetMembership)
            .RequireAuthorization(AuthPolicies.Scope("memberships:read"));
        app.MapGet("/users/{userId:guid}/teams", GetUserTeams)
            .RequireAuthorization(AuthPolicies.Scope("memberships:read"));
    }

    /// <summary>G13: team ids of any user, so "requester shares a team with the submitter" is one list intersection.</summary>
    private static async Task<IResult> GetUserTeams(Guid userId, NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        var teamIds = await conn.QueryAsync<Guid>("SELECT team_id FROM team_members WHERE user_id = @userId", new { userId });
        return Results.Ok(new { teamIds });
    }

    private static async Task<IResult> GetTeams(HttpContext ctx, NpgsqlDataSource db, bool? docbridgeEnabled)
    {
        await using var conn = await db.OpenConnectionAsync();
        var teams = await conn.QueryAsync<TeamDto>(
            """
            SELECT t.id, t.name, t.region
            FROM team_members m JOIN teams t ON t.id = m.team_id
            WHERE m.user_id = @userId AND (@enabled IS NULL OR t.docbridge_enabled = @enabled)
            ORDER BY t.name
            """, new { userId = ctx.UserGuid(), enabled = docbridgeEnabled });
        return Results.Ok(teams);
    }

    private static async Task<IResult> GetBinders(Guid teamId, HttpContext ctx, NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        await RequireMember(conn, teamId, ctx.UserGuid());
        var binders = await conn.QueryAsync<BinderDto>(
            "SELECT id, name FROM binders WHERE team_id = @teamId ORDER BY name", new { teamId });
        return Results.Ok(binders);
    }

    private static async Task<IResult> GetContents(Guid binderId, Guid? folderId, HttpContext ctx, NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        var binder = await conn.QuerySingleOrDefaultAsync<BinderAccess>(
            """
            SELECT b.team_id, EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = b.team_id AND m.user_id = @userId) AS is_member
            FROM binders b WHERE b.id = @binderId
            """, new { binderId, userId = ctx.UserGuid() });
        if (binder is null)
        {
            throw ApiException.NotFound("Binder not found");
        }
        if (!binder.IsMember)
        {
            throw ApiException.Forbidden("Not a member of this binder's team");
        }
        if (folderId is not null && !await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM folders WHERE id = @folderId AND binder_id = @binderId)", new { folderId, binderId }))
        {
            throw ApiException.NotFound("Folder not found in this binder");
        }

        // Separate IS NULL / = branches so both use the (binder_id, parent) indexes.
        var folderFilter = folderId is null ? "IS NULL" : "= @folderId";
        var folders = await conn.QueryAsync<FolderDto>(
            $"SELECT id, name FROM folders WHERE binder_id = @binderId AND parent_folder_id {folderFilter} ORDER BY name",
            new { binderId, folderId });
        var documents = await conn.QueryAsync<DocumentDto>(
            $"""
             SELECT id, name, size_bytes, content_type, checksum_sha256, uploaded_by_user_id, created_at
             FROM documents WHERE binder_id = @binderId AND folder_id {folderFilter} ORDER BY created_at DESC LIMIT 500
             """, new { binderId, folderId });
        return Results.Ok(new { folders, documents });
    }

    /// <summary>Service route: the status code is the answer (TDD), no isMember flag.</summary>
    private static async Task<IResult> GetMembership(Guid teamId, Guid userId, NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        var addedAt = await conn.QuerySingleOrDefaultAsync<DateTime?>(
            "SELECT added_at FROM team_members WHERE team_id = @teamId AND user_id = @userId", new { teamId, userId });
        return addedAt is null ? Results.NotFound(ErrorEnvelope.Of("NOT_A_MEMBER", "User is not a member of this team")) : Results.Ok(new { addedAt });
    }

    internal static async Task RequireMember(NpgsqlConnection conn, Guid teamId, Guid userId)
    {
        var state = await conn.QuerySingleAsync<MemberState>(
            """
            SELECT EXISTS (SELECT 1 FROM teams WHERE id = @teamId) AS team_exists,
                   EXISTS (SELECT 1 FROM team_members WHERE team_id = @teamId AND user_id = @userId) AS is_member
            """, new { teamId, userId });
        if (!state.IsMember)
        {
            throw state.TeamExists ? ApiException.Forbidden("Not a member of this team") : ApiException.NotFound("Team not found");
        }
    }

    private sealed record BinderAccess(Guid TeamId, bool IsMember);

    private sealed record MemberState(bool TeamExists, bool IsMember);
}
