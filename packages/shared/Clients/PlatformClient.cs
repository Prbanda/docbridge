using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocBridge.Shared.Errors;

namespace DocBridge.Shared.Clients;

public sealed record PlatformTeam(Guid Id, string Name, string Region);

/// <summary>
/// DocBridge's calls into the document platform. Membership is never joined across databases
/// (ADR-009): it is always asked of the platform over its API.
/// </summary>
public sealed class PlatformClient(HttpClient http, ServiceTokenClient? serviceTokens)
{
    /// <summary>GET /teams/:teamId/members/:userId (scope memberships:read): 200 member, 404 not.</summary>
    public async Task<bool> IsMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        if (serviceTokens is null)
        {
            throw new InvalidOperationException("PlatformClient has no service credentials");
        }
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"teams/{teamId}/members/{userId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await serviceTokens.GetTokenAsync(ct));
            using var response = await Send(request, ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return true;
                case HttpStatusCode.NotFound:
                    return false;
                case HttpStatusCode.Unauthorized when attempt == 0:
                    serviceTokens.Invalidate();
                    continue;
                default:
                    throw ApiException.BadGateway($"Platform membership check failed ({(int)response.StatusCode})");
            }
        }
    }

    /// <summary>GET /users/:userId/teams (scope memberships:read): every team the user belongs to.</summary>
    public async Task<IReadOnlySet<Guid>> GetUserTeamIdsAsync(Guid userId, CancellationToken ct = default)
    {
        if (serviceTokens is null)
        {
            throw new InvalidOperationException("PlatformClient has no service credentials");
        }
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"users/{userId}/teams");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await serviceTokens.GetTokenAsync(ct));
            using var response = await Send(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                serviceTokens.Invalidate();
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw ApiException.BadGateway($"Platform team lookup failed ({(int)response.StatusCode})");
            }
            var body = await response.Content.ReadFromJsonAsync<UserTeams>(ct);
            return (body?.TeamIds ?? []).ToHashSet();
        }
    }

    private sealed record UserTeams(List<Guid> TeamIds);

    /// <summary>GET /teams with the end user's own JWT: the platform filters to their memberships.</summary>
    public async Task<IReadOnlyList<PlatformTeam>> GetTeamsAsync(string userJwt, bool docbridgeEnabledOnly, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, docbridgeEnabledOnly ? "teams?docbridgeEnabled=true" : "teams");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userJwt);
        using var response = await Send(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw ApiException.Unauthorized();
        }
        if (!response.IsSuccessStatusCode)
        {
            throw ApiException.BadGateway($"Platform team lookup failed ({(int)response.StatusCode})");
        }
        return await response.Content.ReadFromJsonAsync<List<PlatformTeam>>(ct) ?? [];
    }

    /// <summary>Forward a user-token GET verbatim (destination browsing proxy).</summary>
    public async Task<(int Status, string Body)> ForwardGetAsync(string pathAndQuery, string userJwt, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userJwt);
        using var response = await Send(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode >= 500)
        {
            throw ApiException.BadGateway($"Platform returned {(int)response.StatusCode}");
        }
        return ((int)response.StatusCode, body);
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // TDD 1.5: 5 s timeout, platform errors mapped to 502.
            throw ApiException.BadGateway("Document platform unavailable");
        }
    }
}
