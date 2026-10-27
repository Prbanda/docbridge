using System.Net.Http.Json;

namespace DocBridge.Shared.Clients;

public sealed record ServiceCredentials(string ClientId, string ClientSecret, string Scope);

/// <summary>
/// ADR-013 client-credentials token, cached for its lifetime. In Lambda the instance lives in the execution
/// context, so the token survives across warm invocations. Within the last two minutes of a token's life, callers
/// keep getting the current token while one background refresh replaces it, so no request (in particular job
/// submission's 500 ms budget) ever waits on token issuance after the first.
/// </summary>
public sealed class ServiceTokenClient(HttpClient http, string authBaseUrl, ServiceCredentials credentials, TimeProvider? clock = null)
{
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;
    private Task? _backgroundRefresh;

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var token = _token;
        if (token is not null && now < _expiresAt - SafetyMargin)
        {
            if (now > _expiresAt - RefreshWindow && _backgroundRefresh is null or { IsCompleted: true })
            {
                _backgroundRefresh = Task.Run(() => RefreshAsync(force: true, CancellationToken.None));
            }
            return token;
        }
        return await RefreshAsync(force: false, ct);
    }

    /// <summary>Fetch a token now (service startup), so the first real request doesn't pay for it.</summary>
    public async Task WarmAsync()
    {
        try
        {
            await GetTokenAsync();
        }
        catch (Exception)
        {
            // Auth not reachable yet: the first request will fetch the token instead.
        }
    }

    /// <summary>Drop the cached token, e.g. after the platform answers 401.</summary>
    public void Invalidate() => _token = null;

    private async Task<string> RefreshAsync(bool force, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!force && _token is not null && _clock.GetUtcNow() < _expiresAt - SafetyMargin)
            {
                return _token;
            }
            using var response = await http.PostAsJsonAsync($"{authBaseUrl.TrimEnd('/')}/auth/token", new
            {
                clientId = credentials.ClientId,
                clientSecret = credentials.ClientSecret,
                scope = credentials.Scope,
            }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Service token request failed with {(int)response.StatusCode}", null, response.StatusCode);
            }
            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                ?? throw new HttpRequestException("Empty token response");
            _expiresAt = _clock.GetUtcNow().AddSeconds(body.ExpiresIn);
            _token = body.AccessToken;
            return _token;
        }
        catch (Exception) when (force && _token is not null)
        {
            return _token; // background refresh failed: keep using the still-valid token
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
}
