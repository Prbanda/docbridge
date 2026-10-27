using System.Net;
using System.Text;
using System.Text.Json;
using DocBridge.Shared.Clients;

namespace DocBridge.UnitTests;

public sealed class ServiceTokenTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingAuth : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { accessToken = $"token-{n}", expiresIn = 900 }), Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Near_expiry_returns_current_token_and_refreshes_in_the_background()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var auth = new CountingAuth();
        var client = new ServiceTokenClient(new HttpClient(auth), "http://auth", new ServiceCredentials("c", "s", "x"), clock);

        Assert.Equal("token-1", await client.GetTokenAsync());
        clock.Now += TimeSpan.FromMinutes(10);
        Assert.Equal("token-1", await client.GetTokenAsync()); // still fresh: no call
        Assert.Equal(1, auth.Calls);

        clock.Now += TimeSpan.FromMinutes(4); // 14 min in: inside the 2-minute refresh window
        Assert.Equal("token-1", await client.GetTokenAsync()); // caller is not made to wait
        for (var i = 0; i < 50 && auth.Calls < 2; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal(2, auth.Calls);
        Assert.Equal("token-2", await client.GetTokenAsync());
    }

    [Fact]
    public async Task Expired_token_is_refetched_inline()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var auth = new CountingAuth();
        var client = new ServiceTokenClient(new HttpClient(auth), "http://auth", new ServiceCredentials("c", "s", "x"), clock);
        await client.GetTokenAsync();
        clock.Now += TimeSpan.FromMinutes(20);
        Assert.Equal("token-2", await client.GetTokenAsync());
    }
}
