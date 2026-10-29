using System.Security.Claims;
using System.Text.Encodings.Web;
using DocBridge.Shared.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocBridge.Shared.Auth;

/// <summary>
/// Services re-validate every token even behind the gateway authorizer (defense in depth);
/// the platform must, because workers reach it without the gateway.
/// </summary>
public sealed class BearerAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, JwtValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DocBridgeBearer";
    public const string RawTokenItem = "docbridge.raw_token";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }
        var raw = header["Bearer ".Length..].Trim();
        try
        {
            var token = await validator.ValidateAsync(raw, Context.RequestAborted);
            Context.Items[RawTokenItem] = raw;
            var claims = new List<Claim>
            {
                new("sub", token.Subject),
                new("token_use", token.IsService ? "service" : "user"),
            };
            if (token.Email is not null)
            {
                claims.Add(new Claim("email", token.Email));
            }
            claims.AddRange(token.Scopes.Select(s => new Claim("scope", s)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "sub", null));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
        }
        catch (ApiException ex)
        {
            return AuthenticateResult.Fail(ex.Message);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Response.WriteAsJsonAsync(ErrorEnvelope.Of("UNAUTHORIZED", "Valid bearer token required"));
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        return Response.WriteAsJsonAsync(ErrorEnvelope.Of("FORBIDDEN", "Token is not allowed to call this route"));
    }
}

public static class AuthPolicies
{
    /// <summary>End-user tokens only: service tokens can't browse or submit.</summary>
    public const string User = "user";

    public static string Scope(string scope) => $"scope:{scope}";

    public static IServiceCollection AddDocBridgeAuth(this IServiceCollection services, string jwksUrl,
        JwtSettings settings, params string[] serviceScopes)
    {
        services.AddSingleton(settings);
        services.AddSingleton(sp => new JwksCache(sp.GetRequiredService<IHttpClientFactory>().CreateClient("jwks"), jwksUrl));
        return services.AddDocBridgeAuthCore(serviceScopes);
    }

    /// <summary>For callers that register their own JwksCache and JwtSettings (the auth service itself).</summary>
    public static IServiceCollection AddDocBridgeAuthCore(this IServiceCollection services, params string[] serviceScopes)
    {
        services.AddHttpClient("jwks", c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<JwtValidator>();
        services.AddAuthentication(BearerAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BearerAuthHandler>(BearerAuthHandler.SchemeName, null);
        var builder = services.AddAuthorizationBuilder()
            // Every endpoint requires authentication unless it opts out with AllowAnonymous.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(User, p => p.RequireAuthenticatedUser().RequireClaim("token_use", "user"));
        foreach (var scope in serviceScopes)
        {
            builder.AddPolicy(Scope(scope), p =>
                p.RequireAuthenticatedUser().RequireClaim("token_use", "service").RequireClaim("scope", scope));
        }
        return services;
    }

    public static string UserId(this HttpContext ctx) =>
        ctx.User.FindFirst("sub")?.Value ?? throw ApiException.Unauthorized();

    public static Guid UserGuid(this HttpContext ctx) =>
        Guid.TryParse(ctx.UserId(), out var id) ? id : throw ApiException.Unauthorized("Subject is not a user id");

    public static string RawToken(this HttpContext ctx) =>
        ctx.Items[BearerAuthHandler.RawTokenItem] as string ?? throw ApiException.Unauthorized();
}
