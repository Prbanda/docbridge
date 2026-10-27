// Demo seed data (TDD 1.2): users plus two teams with study-style binders and nested folders. Folders start empty;
// every document you see was delivered by a job. Talks only to public APIs (auth register/login/token + platform
// admin routes), so it works locally and on AWS:
//
//   dotnet run --project tools/seed -- --api http://localhost:8080 [--perf]
//
// Env: SEED_PASSWORD (demo password), PLATFORM_ADMIN_CLIENT_SECRET (platform-admin service client),
//      SEED_EMAIL_DOMAIN (default demo.docbridge.test; another domain seeds a fresh, separate set of demo users).
// --perf also creates perf@<domain> with 50 teams for the <1 s destination-browser check.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

var api = ArgValue("--api") ?? Environment.GetEnvironmentVariable("DOCBRIDGE_API_URL") ?? "http://localhost:8080";
var password = Environment.GetEnvironmentVariable("SEED_PASSWORD") ?? "Demo-Password-2026";
var domain = Environment.GetEnvironmentVariable("SEED_EMAIL_DOMAIN") is { Length: > 0 } d ? d : "demo.docbridge.test";
var adminSecret = Environment.GetEnvironmentVariable("PLATFORM_ADMIN_CLIENT_SECRET")
    ?? throw new InvalidOperationException("PLATFORM_ADMIN_CLIENT_SECRET is required");
using var http = new HttpClient { BaseAddress = new Uri(api.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };

// alice + pranathi: both teams. bob: Helios only (shares it with them, so he sees their Helios jobs).
// carol: no team at all, for the "nothing visible / 404" checks.
var alice = await EnsureUser($"alice@{domain}", "Alice Coordinator");
var pranathi = await EnsureUser("pranathidemo@test.com", "pranathidemo");
var bob = await EnsureUser($"bob@{domain}", "Bob Teammate");
var carol = await EnsureUser($"carol@{domain}", "Carol Outsider");

// Team → binders → folder paths ("A/B/C" creates the chain; shared prefixes are reused).
var teams = new (string Name, string Region, bool Bob, (string Binder, string[] Folders)[] Binders)[]
{
    ("Helios Therapeutics", "US", true,
    [
        ("Study HX-101", ["Protocol/Amendments", "Informed Consent/Approved Versions", "Safety Reports/SUSARs", "Regulatory/IRB-EC Correspondence"]),
        ("Study HX-204", ["Protocol/Amendments", "Informed Consent/Approved Versions", "Safety Reports/SUSARs", "Regulatory/IRB-EC Correspondence"]),
    ]),
    ("Nordlicht Clinical Sites", "DE", false,
    [
        ("Site Berlin", ["Investigator CVs", "Training Logs", "Monitoring Visits/2026/Q3"]),
        ("Site Hamburg", ["Investigator CVs", "Training Logs", "Monitoring Visits/2026/Q3"]),
    ]),
};

if ((await Get<JsonElement>("destinations/teams", alice.Token)).GetArrayLength() > 0)
{
    Console.WriteLine($"Seed data already present for alice@{domain}; nothing to do.");
}
else
{
    var admin = await AdminToken();
    foreach (var team in teams)
    {
        var teamId = await Create("admin/teams", new { name = team.Name, region = team.Region, docbridgeEnabled = true }, admin);
        await AddMember(teamId, alice.Id, admin);
        await AddMember(teamId, pranathi.Id, admin);
        if (team.Bob)
        {
            await AddMember(teamId, bob.Id, admin);
        }
        foreach (var (binderName, paths) in team.Binders)
        {
            var binderId = await Create($"admin/teams/{teamId}/binders", new { name = binderName }, admin);
            var created = new Dictionary<string, Guid>();
            foreach (var path in paths)
            {
                Guid? parent = null;
                var soFar = "";
                foreach (var part in path.Split('/'))
                {
                    soFar = soFar.Length == 0 ? part : $"{soFar}/{part}";
                    if (!created.TryGetValue(soFar, out var id))
                    {
                        id = await Create($"admin/binders/{binderId}/folders", new { name = part, parentFolderId = parent }, admin);
                        created[soFar] = id;
                    }
                    parent = id;
                }
            }
        }
        Console.WriteLine($"team  {team.Name} ({team.Region}) {teamId}");
    }
}

if (args.Contains("--perf"))
{
    var perf = await EnsureUser($"perf@{domain}", "Perf Fifty Teams");
    var existing = (await Get<JsonElement>("destinations/teams", perf.Token)).GetArrayLength();
    var admin = await AdminToken();
    for (var i = existing; i < 50; i++)
    {
        var teamId = await Create("admin/teams", new { name = $"Perf Team {i + 1:00}", region = i % 2 == 0 ? "US" : "DE" }, admin);
        await AddMember(teamId, perf.Id, admin);
        await Create($"admin/teams/{teamId}/binders", new { name = "Perf Binder" }, admin);
    }
    Console.WriteLine($"perf  perf@{domain} has 50 teams");
}

Console.WriteLine();
Console.WriteLine($"Users (password from SEED_PASSWORD): alice@{domain} and pranathidemo@test.com (both teams), " +
    $"bob@{domain} (Helios only), carol@{domain} (no team)");
return;

string? ArgValue(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

async Task<(Guid Id, string Token)> EnsureUser(string email, string name)
{
    using var register = await http.PostAsJsonAsync("auth/register", new { email, password, name });
    if (register.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
    {
        throw new InvalidOperationException($"register {email}: {(int)register.StatusCode} {await register.Content.ReadAsStringAsync()}");
    }
    using var login = await http.PostAsJsonAsync("auth/login", new { email, password });
    login.EnsureSuccessStatusCode();
    var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
    payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
    var sub = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.GetProperty("sub").GetString()!;
    Console.WriteLine($"user  {email} {sub}");
    return (Guid.Parse(sub), token);
}

async Task<string> AdminToken()
{
    using var response = await http.PostAsJsonAsync("auth/token", new { clientId = "platform-admin", clientSecret = adminSecret, scope = "platform:admin" });
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
}

async Task<Guid> Create(string path, object body, string token)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await http.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }
    return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
}

async Task AddMember(Guid teamId, Guid userId, string token)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, $"admin/teams/{teamId}/members") { Content = JsonContent.Create(new { userId }) };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
}

async Task<T> Get<T>(string path, string token)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, path);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<T>())!;
}
