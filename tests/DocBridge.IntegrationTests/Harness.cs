using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocBridge.IntegrationTests;

public sealed record User(Guid Id, string Email, string Token);

public sealed record Dest(Guid TeamId, Guid BinderId, Guid? FolderId, string Label)
{
    public object Body => new { teamId = TeamId, binderId = BinderId, folderId = FolderId, label = Label };
}

public sealed record StagedFile(Guid FileId, string Name, byte[] Bytes, string Sha256);

public sealed record Workspace(User User, IReadOnlyList<Guid> Teams, IReadOnlyList<Dest> Destinations);

/// <summary>
/// Drives a running DocBridge (local compose edge, or the deployed API Gateway) through its public APIs only.
///   DOCBRIDGE_BASE_URL   default http://localhost:8080
///   DOCBRIDGE_WS_URL     default ws://localhost:8080/ws
///   PLATFORM_ADMIN_CLIENT_SECRET, DELIVERY_WORKER_CLIENT_SECRET   default: local compose values
/// Each test provisions its own users and teams, so tests are independent and can run in parallel.
/// </summary>
public static partial class Harness
{
    public static readonly string BaseUrl = Env("DOCBRIDGE_BASE_URL", "http://localhost:8080").TrimEnd('/');
    public static readonly string WsUrl = Env("DOCBRIDGE_WS_URL", "ws://localhost:8080/ws");
    public static readonly string AdminSecret = Env("PLATFORM_ADMIN_CLIENT_SECRET", "platform-admin-local-dev");
    public static readonly string DeliverySecret = Env("DELIVERY_WORKER_CLIENT_SECRET", "delivery-worker-local-dev");
    public const string Password = "Integration-Pass-2026"; // gitleaks:allow (throwaway test-user password)

    public static readonly HttpClient Http = new() { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(60) };
    private static readonly HttpClient Storage = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string? _adminToken;
    private static DateTime _adminTokenAt;

    public static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    // ---------------- HTTP helpers ----------------

    public static async Task<(HttpStatusCode Status, JsonElement Body, TimeSpan Elapsed)> Call(
        HttpMethod method, string path, string? token = null, object? body = null, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }
        foreach (var (k, v) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(k, v);
        }
        var sw = Stopwatch.StartNew();
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        sw.Stop();
        var json = string.IsNullOrWhiteSpace(text) ? default : TryParse(text);
        return (response.StatusCode, json, sw.Elapsed);
    }

    private static JsonElement TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(new { raw = text })).RootElement.Clone();
        }
    }

    public static Task<(HttpStatusCode Status, JsonElement Body, TimeSpan Elapsed)> Get(string path, string? token) => Call(HttpMethod.Get, path, token);

    public static Task<(HttpStatusCode Status, JsonElement Body, TimeSpan Elapsed)> Post(string path, string? token, object? body = null,
        IDictionary<string, string>? headers = null) => Call(HttpMethod.Post, path, token, body ?? new { }, headers);

    public static string ErrorCode(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e) ? e.GetProperty("code").GetString()! : "";

    // ---------------- identity and platform admin ----------------

    public static async Task<User> NewUser(string prefix = "user")
    {
        var email = $"{Unique(prefix)}@it.docbridge.test";
        var (status, _, _) = await Post("auth/register", null, new { email, password = Password, name = prefix });
        Assert.Equal(HttpStatusCode.Created, status);
        var login = await Post("auth/login", null, new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var token = login.Body.GetProperty("accessToken").GetString()!;
        return new User(Guid.Parse(Claims(token).GetProperty("sub").GetString()!), email, token);
    }

    public static JsonElement Claims(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(part)).RootElement.Clone();
    }

    public static async Task<string> ServiceToken(string clientId, string secret, string scope)
    {
        var (status, body, _) = await Post("auth/token", null, new { clientId, clientSecret = secret, scope });
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("accessToken").GetString()!;
    }

    /// <summary>Tokens live 15 minutes (TDD v1); long load runs outlast that, so refresh well before expiry.</summary>
    public static async Task<string> AdminToken()
    {
        if (_adminToken is null || DateTime.UtcNow - _adminTokenAt > TimeSpan.FromMinutes(12))
        {
            _adminToken = await ServiceToken("platform-admin", AdminSecret, "platform:admin");
            _adminTokenAt = DateTime.UtcNow;
        }
        return _adminToken;
    }

    /// <summary>Log the same user in again (their 15-minute access token expired during a long wait).</summary>
    public static async Task<User> Relogin(User user)
    {
        var login = await Post("auth/login", null, new { email = user.Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        return user with { Token = login.Body.GetProperty("accessToken").GetString()! };
    }

    public static async Task<Guid> AdminCreate(string path, object body)
    {
        var (status, json, _) = await Post(path, await AdminToken(), body);
        Assert.True(status == HttpStatusCode.Created, $"POST {path} → {(int)status} {json}");
        return json.GetProperty("id").GetGuid();
    }

    public static async Task<Guid> CreateTeam(string name, string region = "US", bool enabled = true) =>
        await AdminCreate("admin/teams", new { name, region, docbridgeEnabled = enabled });

    public static async Task AddMember(Guid teamId, Guid userId)
    {
        var (status, _, _) = await Post($"admin/teams/{teamId}/members", await AdminToken(), new { userId });
        Assert.Equal(HttpStatusCode.NoContent, status);
    }

    public static async Task RemoveMember(Guid teamId, Guid userId)
    {
        var (status, _, _) = await Call(HttpMethod.Delete, $"admin/teams/{teamId}/members/{userId}", await AdminToken());
        Assert.Equal(HttpStatusCode.NoContent, status);
    }

    /// <summary>A user who belongs to <paramref name="teams"/> teams, each with one binder (root destination) and one folder.</summary>
    public static async Task<Workspace> NewWorkspace(int teams = 1, string prefix = "ws", bool folders = true)
    {
        var user = await NewUser(prefix);
        var teamIds = new List<Guid>();
        var destinations = new List<Dest>();
        for (var i = 0; i < teams; i++)
        {
            var name = Unique($"{prefix}-team{i}");
            var team = await CreateTeam(name, i % 2 == 0 ? "US" : "DE");
            await AddMember(team, user.Id);
            var binder = await AdminCreate($"admin/teams/{team}/binders", new { name = "Binder" });
            destinations.Add(new Dest(team, binder, null, $"{name} / Binder"));
            if (folders)
            {
                var folder = await AdminCreate($"admin/binders/{binder}/folders", new { name = "Regulatory" });
                destinations.Add(new Dest(team, binder, folder, $"{name} / Binder / Regulatory"));
            }
            teamIds.Add(team);
        }
        return new Workspace(user, teamIds, destinations);
    }

    // ---------------- files ----------------

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] FakePdf(int size = 2048)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        "%PDF-1.7\n"u8.ToArray().CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>POST /files then upload each file straight to storage with its presigned POST (or multipart plan).</summary>
    public static async Task<List<StagedFile>> Stage(User user, int count, int size = 2048, bool upload = true)
    {
        var files = Enumerable.Range(0, count).Select(i => (Name: $"doc-{i:000}.pdf", Bytes: FakePdf(size))).ToList();
        var staged = new List<StagedFile>();
        foreach (var wave in files.Chunk(20))
        {
            var (status, body, _) = await Post("files", user.Token, new
            {
                files = wave.Select(f => new { name = f.Name, sizeBytes = f.Bytes.Length, contentType = "application/pdf", sha256 = Sha256(f.Bytes) }),
            });
            Assert.True(status == HttpStatusCode.Created, $"POST /files → {(int)status} {body}");
            var plans = body.EnumerateArray().ToList();
            var tasks = wave.Select(async (f, i) =>
            {
                var fileId = plans[i].GetProperty("fileId").GetGuid();
                if (upload)
                {
                    var result = await UploadWithPlan(user, fileId, plans[i].GetProperty("upload"), f.Bytes, "application/pdf");
                    Assert.True(result.IsSuccessStatusCode, $"upload {f.Name} → {(int)result.StatusCode} {await result.Content.ReadAsStringAsync()}");
                }
                return new StagedFile(fileId, f.Name, f.Bytes, Sha256(f.Bytes));
            });
            staged.AddRange(await Task.WhenAll(tasks));
        }
        return staged;
    }

    public static async Task<HttpResponseMessage> UploadWithPlan(User user, Guid fileId, JsonElement plan, byte[] bytes, string contentType)
    {
        if (plan.GetProperty("type").GetString() == "post")
        {
            return await PresignedPost(plan, bytes, contentType);
        }
        var partSize = plan.GetProperty("partSize").GetInt64();
        var parts = new List<object>();
        foreach (var part in plan.GetProperty("parts").EnumerateArray())
        {
            var n = part.GetProperty("partNumber").GetInt32();
            var start = (n - 1) * partSize;
            var slice = bytes.AsMemory((int)start, (int)Math.Min(partSize, bytes.Length - start));
            using var put = await Storage.PutAsync(part.GetProperty("url").GetString(), new ReadOnlyMemoryContent(slice));
            if (!put.IsSuccessStatusCode)
            {
                return put;
            }
            parts.Add(new { partNumber = n, etag = put.Headers.ETag!.Tag });
        }
        var (status, body, _) = await Post($"files/{fileId}/multipart/complete", user.Token,
            new { uploadId = plan.GetProperty("uploadId").GetString(), parts });
        return new HttpResponseMessage(status) { Content = new StringContent(body.ToString()) };
    }

    public static async Task<HttpResponseMessage> PresignedPost(JsonElement plan, byte[] bytes, string contentType)
    {
        using var form = new MultipartFormDataContent();
        foreach (var field in plan.GetProperty("fields").EnumerateObject())
        {
            form.Add(new StringContent(field.Value.GetString()!), field.Name);
        }
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "upload.bin"); // must be the last field
        return await Storage.PostAsync(plan.GetProperty("url").GetString(), form);
    }

    // ---------------- jobs ----------------

    public static object Mappings(IEnumerable<StagedFile> files, IEnumerable<Dest> destinations) => new
    {
        mappings = files.Select(f => new { fileId = f.FileId, destinations = destinations.Select(d => d.Body).ToList() }).ToList(),
    };

    public static async Task<Guid> Submit(User user, IEnumerable<StagedFile> files, IEnumerable<Dest> destinations)
    {
        var (status, body, _) = await Post("jobs", user.Token, Mappings(files, destinations));
        Assert.True(status == HttpStatusCode.Created, $"POST /jobs → {(int)status} {body}");
        return body.GetProperty("jobId").GetGuid();
    }

    /// <summary>Polls GET /jobs/:id until every task is resolved (completed or failed).</summary>
    public static async Task<JsonElement> WaitForJob(User user, Guid jobId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        while (true)
        {
            var (status, body, _) = await Get($"jobs/{jobId}", user.Token);
            if (status == HttpStatusCode.Unauthorized)
            {
                user = await Relogin(user);
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, status);
            var job = body.GetProperty("job");
            var counts = job.GetProperty("counts");
            if (counts.GetProperty("pending").GetInt64() == 0 && counts.GetProperty("inProgress").GetInt64() == 0)
            {
                return job;
            }
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Job {jobId} did not resolve in time: {counts}");
            }
            await Task.Delay(1000);
        }
    }

    public static async Task<List<JsonElement>> AllTasks(User user, Guid jobId, string? status = null)
    {
        var all = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            var filter = status is null ? "" : $"&status={status}";
            var (code, body, _) = await Get($"jobs/{jobId}/tasks?page={page}&limit=500{filter}", user.Token);
            if (code == HttpStatusCode.Unauthorized)
            {
                user = await Relogin(user);
                page--;
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, code);
            all.AddRange(body.GetProperty("tasks").EnumerateArray());
            if (page * 500 >= body.GetProperty("total").GetInt64())
            {
                return all;
            }
        }
    }

    // ---------------- websocket ----------------

    public static async Task<ClientWebSocket> OpenSocket(string? token)
    {
        var socket = new ClientWebSocket();
        var url = token is null ? WsUrl : $"{WsUrl}?token={Uri.EscapeDataString(token)}";
        await socket.ConnectAsync(new Uri(url), CancellationToken.None);
        return socket;
    }

    public static Task SendJson(ClientWebSocket socket, object payload) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json)), WebSocketMessageType.Text, true, CancellationToken.None);

}

/// <summary>
/// Reads a socket on a background loop into a channel. (Cancelling ClientWebSocket.ReceiveAsync aborts the socket,
/// so timeouts are applied to the channel read instead.)
/// </summary>
public sealed class SocketReader
{
    private readonly System.Threading.Channels.Channel<JsonElement> _messages = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();

    public SocketReader(ClientWebSocket socket)
    {
        _ = Task.Run(async () =>
        {
            var buffer = new byte[256 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var count = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), CancellationToken.None);
                        count += result.Count;
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    _messages.Writer.TryWrite(JsonDocument.Parse(buffer.AsMemory(0, count)).RootElement.Clone());
                }
            }
            catch (WebSocketException)
            {
            }
            _messages.Writer.TryComplete();
        });
    }

    public async Task<JsonElement?> Next(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _messages.Reader.ReadAsync(cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Next message matching <paramref name="match"/>, skipping others.</summary>
    public async Task<JsonElement?> NextWhere(Func<JsonElement, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = await Next(deadline - DateTime.UtcNow);
            if (message is null)
            {
                return null;
            }
            if (match(message.Value))
            {
                return message;
            }
        }
        return null;
    }
}
