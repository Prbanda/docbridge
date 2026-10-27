using System.Security.Cryptography;
using DocBridge.Auth;
using DocBridge.Shared.Auth;
using DocBridge.Shared.Errors;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocBridge.UnitTests;

/// <summary>TDD unit list: token issuance and validation.</summary>
public sealed class TokenTests
{
    private static readonly JwtSettings Settings = new("https://issuer.test", "docbridge");

    private static (TokenIssuer Issuer, JwtValidator Validator) Pair(JwtSettings? validatorSettings = null)
    {
        var issuer = new TokenIssuer(RSA.Create(2048), Settings);
        return (issuer, new JwtValidator(new JwksCache(issuer.PublicKeySet()), validatorSettings ?? Settings));
    }

    [Fact]
    public async Task User_token_round_trips_with_claims()
    {
        var (issuer, validator) = Pair();
        var userId = Guid.NewGuid();
        var verified = await validator.ValidateAsync(issuer.IssueUserToken(userId, "a@b.test", "Ann"), TestContext.Current.CancellationToken);
        Assert.Equal(userId.ToString(), verified.Subject);
        Assert.Equal("a@b.test", verified.Email);
        Assert.False(verified.IsService);
    }

    [Fact]
    public async Task Service_token_carries_scope_and_token_use()
    {
        var (issuer, validator) = Pair();
        var verified = await validator.ValidateAsync(issuer.IssueServiceToken("delivery-worker", ["documents:ingest"]), TestContext.Current.CancellationToken);
        Assert.True(verified.IsService);
        Assert.True(verified.HasScope("documents:ingest"));
        Assert.False(verified.HasScope("memberships:read"));
        Assert.Equal("delivery-worker", verified.Subject);
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        var (issuer, validator) = Pair(new JwtSettings(Settings.Issuer, "someone-else"));
        await Assert.ThrowsAsync<ApiException>(() => validator.ValidateAsync(issuer.IssueUserToken(Guid.NewGuid(), "a@b.test", "A")));
    }

    [Fact]
    public async Task Wrong_issuer_is_rejected()
    {
        var (issuer, validator) = Pair(new JwtSettings("https://evil.test", Settings.Audience));
        await Assert.ThrowsAsync<ApiException>(() => validator.ValidateAsync(issuer.IssueUserToken(Guid.NewGuid(), "a@b.test", "A")));
    }

    [Fact]
    public async Task Token_signed_by_another_key_is_rejected()
    {
        var (issuer, _) = Pair();
        var (_, otherValidator) = Pair();
        await Assert.ThrowsAsync<ApiException>(() => otherValidator.ValidateAsync(issuer.IssueUserToken(Guid.NewGuid(), "a@b.test", "A")));
    }

    [Fact]
    public async Task Tampered_payload_is_rejected()
    {
        var (issuer, validator) = Pair();
        var parts = issuer.IssueUserToken(Guid.NewGuid(), "a@b.test", "A").Split('.');
        var forged = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(
            $$"""{"sub":"{{Guid.NewGuid()}}","token_use":"service","scope":"platform:admin","aud":"docbridge","iss":"https://issuer.test","exp":4102444800}""");
        await Assert.ThrowsAsync<ApiException>(() => validator.ValidateAsync($"{parts[0]}.{forged}.{parts[2]}"));
    }

    [Fact]
    public async Task Garbage_is_rejected_as_unauthorized()
    {
        var (_, validator) = Pair();
        var ex = await Assert.ThrowsAsync<ApiException>(() => validator.ValidateAsync("not-a-jwt"));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public void Jwks_exposes_only_public_key_with_kid()
    {
        var (issuer, _) = Pair();
        Assert.Contains($"\"kid\":\"{issuer.KeyId}\"", issuer.JwksJson);
        Assert.Contains("\"alg\":\"RS256\"", issuer.JwksJson);
        Assert.DoesNotContain("\"d\":", issuer.JwksJson); // no private exponent
    }

    [Fact]
    public void Ephemeral_key_requires_explicit_opt_in()
    {
        Environment.SetEnvironmentVariable("JWT_PRIVATE_KEY_PEM", null);
        Environment.SetEnvironmentVariable("ALLOW_EPHEMERAL_SIGNING_KEY", null);
        Assert.Throws<InvalidOperationException>(() => TokenIssuer.FromEnvironment(Settings, NullLogger.Instance));
    }
}
