using DocBridge.Shared.Errors;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DocBridge.Shared.Auth;

public sealed record VerifiedToken(string Subject, string? Email, string? Name, IReadOnlySet<string> Scopes, bool IsService)
{
    public bool HasScope(string scope) => IsService && Scopes.Contains(scope);
}

public sealed record JwtSettings(string Issuer, string Audience)
{
    public const string DefaultAudience = "docbridge";

    public static JwtSettings FromEnvironment() =>
        new(Env.Required("JWT_ISSUER"), Env.Get("JWT_AUDIENCE", DefaultAudience));
}

/// <summary>
/// The one JWT verification implementation (TDD 0.1): RS256 against the auth JWKS, with issuer,
/// audience and expiry enforced. Used by every service, the WS handlers and the local WS emulator.
/// </summary>
public sealed class JwtValidator(JwksCache jwks, JwtSettings settings)
{
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public async Task<VerifiedToken> ValidateAsync(string token, CancellationToken ct = default)
    {
        string? kid;
        try
        {
            kid = _handler.ReadJsonWebToken(token).Kid;
        }
        catch (Exception)
        {
            throw ApiException.Unauthorized("Malformed token");
        }

        var keys = await jwks.GetKeysAsync(kid, ct);
        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        if (!result.IsValid)
        {
            throw ApiException.Unauthorized("Invalid or expired token");
        }

        var claims = (IReadOnlyDictionary<string, object>)result.Claims.AsReadOnly();
        if (claims.GetValueOrDefault("sub") is not string { Length: > 0 } sub)
        {
            throw ApiException.Unauthorized("Token missing subject");
        }
        var isService = claims.GetValueOrDefault("token_use") as string == "service";
        var scopes = (claims.GetValueOrDefault("scope") as string ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return new VerifiedToken(sub, claims.GetValueOrDefault("email") as string,
            claims.GetValueOrDefault("name") as string, scopes, isService);
    }
}
