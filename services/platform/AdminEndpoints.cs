using Dapper;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Errors;
using Npgsql;

namespace DocBridge.Platform;

public sealed record CreateTeamRequest(string? Name, string? Region, bool? DocbridgeEnabled);
public sealed record AddMemberRequest(Guid? UserId);
public sealed record CreateBinderRequest(string? Name);
public sealed record CreateFolderRequest(string? Name, Guid? ParentFolderId);

/// <summary>
/// G2: team administration. The TDD has no way to create teams or memberships, which seed data and the
/// revoked-membership test both need. Service token with scope platform:admin only.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization(AuthPolicies.Scope("platform:admin"));
        admin.MapPost("/teams", CreateTeam);
        admin.MapPost("/teams/{teamId:guid}/members", AddMember);
        admin.MapDelete("/teams/{teamId:guid}/members/{userId:guid}", RemoveMember);
        admin.MapPost("/teams/{teamId:guid}/binders", CreateBinder);
        admin.MapPost("/binders/{binderId:guid}/folders", CreateFolder);
    }

    private static async Task<IResult> CreateTeam(CreateTeamRequest body, NpgsqlDataSource db)
    {
        var name = Required(body.Name, "name");
        var region = Required(body.Region, "region").ToUpperInvariant();
        await using var conn = await db.OpenConnectionAsync();
        var id = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO teams (name, region, docbridge_enabled) VALUES (@name, @region, @enabled) RETURNING id",
            new { name, region, enabled = body.DocbridgeEnabled ?? true });
        return Results.Created($"/teams/{id}", new { id, name, region });
    }

    private static async Task<IResult> AddMember(Guid teamId, AddMemberRequest body, NpgsqlDataSource db)
    {
        var userId = body.UserId ?? throw ApiException.BadRequest("userId is required");
        await using var conn = await db.OpenConnectionAsync();
        try
        {
            await conn.ExecuteAsync(
                "INSERT INTO team_members (team_id, user_id) VALUES (@teamId, @userId) ON CONFLICT DO NOTHING", new { teamId, userId });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw ApiException.NotFound("Team not found");
        }
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMember(Guid teamId, Guid userId, NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM team_members WHERE team_id = @teamId AND user_id = @userId", new { teamId, userId });
        return Results.NoContent();
    }

    private static async Task<IResult> CreateBinder(Guid teamId, CreateBinderRequest body, NpgsqlDataSource db)
    {
        var name = Required(body.Name, "name");
        await using var conn = await db.OpenConnectionAsync();
        try
        {
            var id = await conn.ExecuteScalarAsync<Guid>(
                "INSERT INTO binders (team_id, name) VALUES (@teamId, @name) RETURNING id", new { teamId, name });
            return Results.Created($"/binders/{id}", new { id, name });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw ApiException.NotFound("Team not found");
        }
    }

    private static async Task<IResult> CreateFolder(Guid binderId, CreateFolderRequest body, NpgsqlDataSource db)
    {
        var name = Required(body.Name, "name");
        await using var conn = await db.OpenConnectionAsync();
        if (body.ParentFolderId is { } parent && !await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM folders WHERE id = @parent AND binder_id = @binderId)", new { parent, binderId }))
        {
            throw ApiException.BadRequest("parentFolderId must be a folder in the same binder");
        }
        try
        {
            var id = await conn.ExecuteScalarAsync<Guid>(
                "INSERT INTO folders (binder_id, parent_folder_id, name) VALUES (@binderId, @parent, @name) RETURNING id",
                new { binderId, parent = body.ParentFolderId, name });
            return Results.Created($"/folders/{id}", new { id, name });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw ApiException.NotFound("Binder not found");
        }
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw ApiException.BadRequest($"{field} is required") : value.Trim();
}
