using System.Security.Cryptography;
using System.Text.Json;
using DocBridge.Shared;
using DocBridge.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DocBridge.Auth;

/// <summary>
/// RS256 signing with the keypair from Secrets Manager (JWT_PRIVATE_KEY_PEM). The kid is derived from
/// the public key, so rotating the secret rotates the kid and validators refetch the JWKS.
/// </summary>
public sealed class TokenIssuer
{
    public const int AccessTokenSeconds = 900;

    private readonly RsaSecurityKey _signingKey;
    private readonly JwtSettings _settings;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenIssuer(RSA rsa, JwtSettings settings)
    {
        _settings = settings;
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        KeyId = Base64UrlEncoder.Encode(SHA256.HashData(publicKey))[..16];
        _signingKey = new RsaSecurityKey(rsa) { KeyId = KeyId };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = KeyId });
        JwksJson = JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = jwk.N, e = jwk.E } },
        });
    }

    public string KeyId { get; }

    public string JwksJson { get; }

    public static TokenIssuer FromEnvironment(JwtSettings settings, ILogger logger)
    {
        var rsa = RSA.Create();
        if (Env.Optional("JWT_PRIVATE_KEY_PEM") is { } pem)
        {
            rsa.ImportFromPem(pem.Replace("\\n", "\n"));
        }
        else if (Env.GetBool("ALLOW_EPHEMERAL_SIGNING_KEY"))
        {
            // Local compose only (AWS always injects the key from Secrets Manager). With JWT_PRIVATE_KEY_FILE the
            // generated key is kept in a volume, so restarts don't invalidate tokens other containers have cached.
            var file = Env.Optional("JWT_PRIVATE_KEY_FILE");
            if (file is not null && File.Exists(file))
            {
                rsa.ImportFromPem(File.ReadAllText(file));
            }
            else
            {
                rsa.KeySize = 2048;
                if (file is not null)
                {
                    File.WriteAllText(file, rsa.ExportPkcs8PrivateKeyPem());
                }
            }
            logger.LogWarning("JWT_PRIVATE_KEY_PEM not set: using a locally generated signing key (development only)");
        }
        else
        {
            throw new InvalidOperationException("JWT_PRIVATE_KEY_PEM is required");
        }
        return new TokenIssuer(rsa, settings);
    }

    public string IssueUserToken(Guid userId, string email, string name) => Issue(new Dictionary<string, object>
    {
        ["sub"] = userId.ToString(),
        ["email"] = email,
        ["name"] = name,
        ["token_use"] = "user",
    });

    public string IssueServiceToken(string clientId, IEnumerable<string> scopes) => Issue(new Dictionary<string, object>
    {
        ["sub"] = clientId,
        ["scope"] = string.Join(' ', scopes),
        ["token_use"] = "service",
    });

    public JsonWebKeySet PublicKeySet() => new(JwksJson);

    private string Issue(Dictionary<string, object> claims)
    {
        var now = DateTime.UtcNow;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddSeconds(AccessTokenSeconds),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        });
    }
}
