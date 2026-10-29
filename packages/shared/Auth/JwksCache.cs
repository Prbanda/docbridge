using Microsoft.IdentityModel.Tokens;

namespace DocBridge.Shared.Auth;

/// <summary>
/// Caches the auth service's JWKS. Refreshes every 10 minutes, and early (rate-limited
/// to once per 30 s) when a token names an unknown kid: key rollover by kid.
/// </summary>
public sealed class JwksCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly HttpClient? _http;
    private readonly string? _url;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IList<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public JwksCache(HttpClient http, string jwksUrl)
    {
        _http = http;
        _url = jwksUrl;
    }

    /// <summary>Static key set (tests, or a signer validating its own tokens).</summary>
    public JwksCache(JsonWebKeySet keys)
    {
        _keys = keys.GetSigningKeys();
        _fetchedAt = DateTimeOffset.MaxValue;
    }

    public async Task<IList<SecurityKey>> GetKeysAsync(string? kid, CancellationToken ct)
    {
        if (_http is null)
        {
            return _keys;
        }
        var now = DateTimeOffset.UtcNow;
        var stale = now - _fetchedAt > MaxAge;
        var unknownKid = kid is not null && _keys.All(k => k.KeyId != kid) && now - _fetchedAt > MinRefreshInterval;
        if (!stale && !unknownKid)
        {
            return _keys;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _fetchedAt > MinRefreshInterval)
            {
                var json = await _http.GetStringAsync(_url, ct);
                _keys = new JsonWebKeySet(json).GetSigningKeys();
                _fetchedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception) when (_keys.Count > 0)
        {
            // Auth briefly unreachable: keep validating with the keys we already have.
        }
        finally
        {
            _lock.Release();
        }
        return _keys;
    }
}
