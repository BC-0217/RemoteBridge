using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PeerDesk.Core;
using PeerDesk.Server;

int failures = 0;
void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    if (!condition) failures++;
}

// Loopback self-signed cert exported through PFX (DefaultKeySet) so Schannel finds the key.
using var rsa = RSA.Create(2048);
var certReq = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var san = new SubjectAlternativeNameBuilder();
san.AddDnsName("localhost");
san.AddIpAddress(IPAddress.Loopback);
certReq.CertificateExtensions.Add(san.Build());
using var raw = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
using var cert = X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.DefaultKeySet);
string pin = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));

const string invite = "SNAPSHOT_SMOKE_INVITATION_OK";
var clock = new TestClock { CurrentTime = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000) };
var app = PeerDeskServerApp.BuildApp(Array.Empty<string>(), new ServerOptions
{
    InvitationCode = invite,
    EphemeralCertificate = cert,
    Clock = clock,
    Port = 0,
    SessionTtl = TimeSpan.FromSeconds(60)
});
await app.StartAsync();
try
{
    string baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    using var client = PinnedHttpClientFactory.CreateClient(pin);

    HttpRequestMessage Req(HttpMethod method, string path, string? token, object? body = null)
    {
        var r = new HttpRequestMessage(method, baseUrl + path);
        if (token != null) r.Headers.Add("Authorization", $"Bearer {token}");
        if (body != null) r.Content = JsonContent.Create(body);
        return r;
    }
    async Task<T> Post<T>(string path, string? token, object body)
    {
        using var resp = await client.SendAsync(Req(HttpMethod.Post, path, token, body));
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{path} -> {(int)resp.StatusCode}");
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }
    async Task<(HttpStatusCode status, List<SessionResponse>? sessions, bool noStore)> Snapshot(string? token)
    {
        using var resp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/sessions/snapshot", token));
        if (resp.StatusCode != HttpStatusCode.OK) return (resp.StatusCode, null, false);
        var list = await resp.Content.ReadFromJsonAsync<SessionListResponse>();
        return (resp.StatusCode, list?.Sessions, resp.Headers.CacheControl?.NoStore == true);
    }

    var a = await Post<RegisterResponse>("/api/v1/register", null, new RegisterRequest(invite));
    var b = await Post<RegisterResponse>("/api/v1/register", null, new RegisterRequest(invite));
    var c = await Post<RegisterResponse>("/api/v1/register", null, new RegisterRequest(invite));

    // Unauthenticated
    Check((await Snapshot(null)).status == HttpStatusCode.Unauthorized, "no token -> 401");
    Check((await Snapshot(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))).status == HttpStatusCode.Unauthorized, "unknown token -> 401");

    var empty = await Snapshot(a.BearerToken);
    Check(empty.status == HttpStatusCode.OK && empty.sessions is { Count: 0 } && empty.noStore, "no sessions -> empty list, no-store");

    // Pending
    var s1 = await Post<SessionResponse>("/api/v1/sessions/request", a.BearerToken, new SessionRequest(b.DeviceId, b.TemporaryPassword));
    var sa = await Snapshot(a.BearerToken);
    var sb = await Snapshot(b.BearerToken);
    var sc = await Snapshot(c.BearerToken);
    Check(sa.sessions is [{ Status: SessionStatus.Pending }] && sa.sessions[0].SessionId == s1.SessionId &&
          sa.sessions[0].InitiatorDeviceId == a.DeviceId && sa.sessions[0].TargetDeviceId == b.DeviceId, "initiator sees Pending");
    Check(sb.sessions is [{ Status: SessionStatus.Pending }] && sb.sessions[0].SessionId == s1.SessionId, "target sees Pending");
    Check(sc.status == HttpStatusCode.OK && sc.sessions is { Count: 0 }, "third party sees 0 (Pending)");

    // Approved
    await Post<SessionResponse>("/api/v1/sessions/decision", b.BearerToken, new DecisionRequest(s1.SessionId, true));
    sa = await Snapshot(a.BearerToken);
    sb = await Snapshot(b.BearerToken);
    Check(sa.sessions is [{ Status: SessionStatus.Approved }] && sb.sessions is [{ Status: SessionStatus.Approved }], "participants see Approved");

    // Connected
    await Post<SessionResponse>("/api/v1/sessions/connect", a.BearerToken, new ConnectRequest(s1.SessionId));
    sa = await Snapshot(a.BearerToken);
    sb = await Snapshot(b.BearerToken);
    sc = await Snapshot(c.BearerToken);
    Check(sa.sessions is [{ Status: SessionStatus.Connected }] && sb.sessions is [{ Status: SessionStatus.Connected }], "participants see Connected");
    Check(sc.sessions is { Count: 0 }, "third party sees 0 (Connected)");

    // Second session C -> B (B password was invalidated on Connect; rotate for a new one), then rejected.
    clock.Advance(TimeSpan.FromSeconds(1));
    var bPw = await Post<PasswordResponse>("/api/v1/devices/password/rotate", b.BearerToken, new { });
    var s2 = await Post<SessionResponse>("/api/v1/sessions/request", c.BearerToken, new SessionRequest(b.DeviceId, bPw.TemporaryPassword));
    await Post<SessionResponse>("/api/v1/sessions/decision", b.BearerToken, new DecisionRequest(s2.SessionId, false));
    sb = await Snapshot(b.BearerToken);
    sa = await Snapshot(a.BearerToken);
    sc = await Snapshot(c.BearerToken);
    Check(sb.sessions is [{ Status: SessionStatus.Connected }, { Status: SessionStatus.Rejected }] &&
          sb.sessions[0].SessionId == s1.SessionId && sb.sessions[1].SessionId == s2.SessionId, "target sees both, terminal included, ordered by CreatedAt");
    Check(sa.sessions is [{ Status: SessionStatus.Connected }] && sa.sessions[0].SessionId == s1.SessionId, "A does not see C->B session");
    Check(sc.sessions is [{ Status: SessionStatus.Rejected }] && sc.sessions[0].SessionId == s2.SessionId, "C sees only its own Rejected session");

    // Repeated reads must not renew Connected TTL (no media activity bump).
    clock.Advance(TimeSpan.FromSeconds(30));
    for (int i = 0; i < 3; i++) await Snapshot(a.BearerToken);
    sb = await Snapshot(b.BearerToken);
    Check(sb.sessions is [{ Status: SessionStatus.Connected }, ..], "still Connected before TTL");
    clock.Advance(TimeSpan.FromSeconds(30));
    sa = await Snapshot(a.BearerToken);
    sb = await Snapshot(b.BearerToken);
    Check(sa.sessions is [{ Status: SessionStatus.Expired }] && sa.sessions[0].SessionId == s1.SessionId, "Connected expires on read at TTL (reads do not renew)");
    Check(sb.sessions is [{ Status: SessionStatus.Expired }, { Status: SessionStatus.Rejected }], "target sees Expired + Rejected");

    // Pending expiry observed on read.
    var cPw = await Post<PasswordResponse>("/api/v1/devices/password/rotate", c.BearerToken, new { });
    var s3 = await Post<SessionResponse>("/api/v1/sessions/request", a.BearerToken, new SessionRequest(c.DeviceId, cPw.TemporaryPassword));
    clock.Advance(TimeSpan.FromSeconds(59));
    sc = await Snapshot(c.BearerToken);
    Check(sc.sessions is { Count: 2 } && sc.sessions[1].SessionId == s3.SessionId && sc.sessions[1].Status == SessionStatus.Pending, "Pending before TTL");
    clock.Advance(TimeSpan.FromSeconds(1));
    sc = await Snapshot(c.BearerToken);
    Check(sc.sessions is { Count: 2 } && sc.sessions[1].Status == SessionStatus.Expired, "Pending expires on read at TTL");

    // Compatibility: /pending still answers with the legacy shape.
    using (var pend = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/sessions/pending", b.BearerToken)))
        Check(pend.StatusCode == HttpStatusCode.OK && (await pend.Content.ReadFromJsonAsync<SessionListResponse>())?.Sessions is { Count: 0 }, "/pending compatibility");

    // Per-device rate guard: 60 ops per 60 s window, then 429 (clock frozen).
    clock.Advance(TimeSpan.FromSeconds(120));
    int okCount = 0;
    HttpStatusCode last = HttpStatusCode.OK;
    for (int i = 0; i < 61; i++)
    {
        last = (await Snapshot(c.BearerToken)).status;
        if (last == HttpStatusCode.OK) okCount++; else break;
    }
    Check(okCount == 60 && last == (HttpStatusCode)429, "rate guard -> 429 after 60 reads");
    Check((await Snapshot(a.BearerToken)).status == HttpStatusCode.OK, "rate guard is per-device");
}
finally
{
    await app.StopAsync();
    await app.DisposeAsync();
}

Console.WriteLine(failures == 0 ? "SessionSnapshotSmoke: all checks passed" : $"SessionSnapshotSmoke: {failures} failure(s)");
return failures == 0 ? 0 : 1;
