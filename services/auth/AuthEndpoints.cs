using System.Text.RegularExpressions;
using Dapper;
using DocBridge.Shared.Errors;
using Npgsql;

namespace DocBridge.Auth;

public sealed record RegisterRequest(string? Email, string? Password, string? Name);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record TokenRequest(string? ClientId, string? ClientSecret, string? Scope);

public static partial class AuthEndpoints
{
    public const int BcryptCost = 12;

    // Login always runs one bcrypt verify, even for unknown emails, so response time
    // doesn't reveal whether an account exists.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("docbridge-timing-equalizer", BcryptCost);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/register", Register).AllowAnonymous();
        app.MapPost("/auth/login", Login).AllowAnonymous();
        app.MapPost("/auth/token", Token).AllowAnonymous();
        app.MapGet("/.well-known/jwks.json", (TokenIssuer issuer) => Results.Text(issuer.JwksJson, "application/json"))
            .AllowAnonymous();
        app.MapGet("/.well-known/openid-configuration", (TokenIssuer issuer, DocBridge.Shared.Auth.JwtSettings settings) =>
            Results.Ok(new Dictionary<string, object>
            {
                // G1: API Gateway's JWT authorizer discovers the JWKS through this document.
                ["issuer"] = settings.Issuer,
                ["jwks_uri"] = $"{settings.Issuer.TrimEnd('/')}/.well-known/jwks.json",
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["response_types_supported"] = new[] { "token" },
                ["subject_types_supported"] = new[] { "public" },
            })).AllowAnonymous();
    }

    internal static async Task<IResult> Register(RegisterRequest body, NpgsqlDataSource db)
    {
        var email = body.Email?.Trim().ToLowerInvariant() ?? "";
        var name = body.Name?.Trim() ?? "";
        if (!EmailPattern().IsMatch(email) || email.Length > 254)
        {
            throw ApiException.BadRequest("A valid email is required");
        }
        if (body.Password is not { Length: >= 10 and <= 128 })
        {
            throw ApiException.BadRequest("Password must be 10 to 128 characters");
        }
        if (name.Length is 0 or > 200)
        {
            throw ApiException.BadRequest("Name is required");
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(body.Password, BcryptCost);
        await using var conn = await db.OpenConnectionAsync();
        var id = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            INSERT INTO users (email, password_hash, name) VALUES (@email, @hash, @name)
            ON CONFLICT (email) DO NOTHING
            RETURNING id
            """, new { email, hash, name });
        if (id is null)
        {
            // Generic on purpose: no "this email is taken" detail.
            throw ApiException.Conflict("Registration could not be completed with these details", "REGISTRATION_FAILED");
        }
        return Results.Created($"/users/{id}", new { id, email, name });
    }

    internal static async Task<IResult> Login(LoginRequest body, NpgsqlDataSource db, TokenIssuer issuer)
    {
        var email = body.Email?.Trim().ToLowerInvariant() ?? "";
        await using var conn = await db.OpenConnectionAsync();
        var user = await conn.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT id, email::text AS email, password_hash, name FROM users WHERE email = @email", new { email });
        var valid = BCrypt.Net.BCrypt.Verify(body.Password ?? "", user?.PasswordHash ?? DummyHash);
        if (user is null || !valid)
        {
            throw new ApiException(401, "INVALID_CREDENTIALS", "Email or password is incorrect");
        }
        return Results.Ok(new { accessToken = issuer.IssueUserToken(user.Id, user.Email, user.Name), expiresIn = TokenIssuer.AccessTokenSeconds });
    }

    /// <summary>ADR-013 client credentials: the worker's own identity, scoped and short-lived.</summary>
    internal static async Task<IResult> Token(TokenRequest body, NpgsqlDataSource db, TokenIssuer issuer)
    {
        await using var conn = await db.OpenConnectionAsync();
        var client = await conn.QuerySingleOrDefaultAsync<ClientRow>(
            "SELECT client_id, client_secret_hash, scopes FROM service_clients WHERE client_id = @id", new { id = body.ClientId ?? "" });
        var valid = BCrypt.Net.BCrypt.Verify(body.ClientSecret ?? "", client?.ClientSecretHash ?? DummyHash);
        if (client is null || !valid)
        {
            throw new ApiException(401, "INVALID_CLIENT", "Client authentication failed");
        }
        var requested = (body.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var granted = requested.Length == 0 ? client.Scopes : requested;
        if (granted.Except(client.Scopes).Any())
        {
            throw ApiException.BadRequest("Requested scope is not allowed for this client", "INVALID_SCOPE");
        }
        return Results.Ok(new { accessToken = issuer.IssueServiceToken(client.ClientId, granted), expiresIn = TokenIssuer.AccessTokenSeconds });
    }

    private sealed record UserRow(Guid Id, string Email, string PasswordHash, string Name);

    // A property-mapped class rather than a record: Dapper's constructor matching wants exact column types,
    // and text[] is reported as System.Array.
    private sealed class ClientRow
    {
        public string ClientId { get; init; } = "";
        public string ClientSecretHash { get; init; } = "";
        public string[] Scopes { get; init; } = [];
    }
}
