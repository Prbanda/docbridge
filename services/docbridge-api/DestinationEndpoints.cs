using DocBridge.Shared.Auth;
using DocBridge.Shared.Clients;

namespace DocBridge.Api;

/// <summary>
/// TDD 1.5 / ADR-006: membership enforcement lives in the platform. DocBridge forwards the user's own JWT and
/// adds the docbridge-enabled filter. One level per call (lazy tree), so the &lt;1 s target is per level.
/// </summary>
public static class DestinationEndpoints
{
    public static void MapDestinationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/destinations").RequireAuthorization(AuthPolicies.User);
        group.MapGet("/teams", (HttpContext ctx, PlatformClient platform) =>
            Forward(ctx, platform, "teams?docbridgeEnabled=true"));
        group.MapGet("/teams/{teamId:guid}/binders", (Guid teamId, HttpContext ctx, PlatformClient platform) =>
            Forward(ctx, platform, $"teams/{teamId}/binders"));
        group.MapGet("/binders/{binderId:guid}/contents", (Guid binderId, Guid? folderId, HttpContext ctx, PlatformClient platform) =>
            Forward(ctx, platform, folderId is null ? $"binders/{binderId}/contents" : $"binders/{binderId}/contents?folderId={folderId}"));
        // Name search across every team, binder and folder the caller can target (platform enforces membership).
        group.MapGet("/search", (string? q, HttpContext ctx, PlatformClient platform) =>
            Forward(ctx, platform, $"search?docbridgeEnabled=true&q={Uri.EscapeDataString(q ?? "")}"));
    }

    private static async Task<IResult> Forward(HttpContext ctx, PlatformClient platform, string path)
    {
        var (status, body) = await platform.ForwardGetAsync(path, ctx.RawToken(), ctx.RequestAborted);
        return Results.Content(body, "application/json", statusCode: status);
    }
}
