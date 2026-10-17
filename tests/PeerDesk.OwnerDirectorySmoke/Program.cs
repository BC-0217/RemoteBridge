using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using PeerDesk.Core;
using PeerDesk.Server;

// Real HTTPS integration: target server (OwnerFeature) + registry server (DirectoryFeature),
// distinct certificates, strict exact-pin clients. Secrets are synthetic, kept in memory/tempdir, never printed.
int count = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); count++; }

static X509Certificate2 MakeCert(string cn)
{
    using var rsa = RSA.Create(2048);
    var cr = new CertificateRequest("CN=" + cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); cr.CertificateExtensions.Add(san.Build());
    using var raw = cr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    // DefaultKeySet (not EphemeralKeySet): Windows Kestrel/SChannel limitation.
    return X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
}
static int ReservePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
    int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
}
static string Pin(X509Certificate2 c) => Convert.ToHexString(c.GetCertHash(HashAlgorithmName.SHA256));
static string RandomSecret(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

string temp = Path.Combine(Path.GetTempPath(), "peerdesk-ownerdir-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
using var targetCert = MakeCert("peerdesk-target");
using var registryCert = MakeCert("peerdesk-registry");
Check(!string.Equals(Pin(targetCert), Pin(registryCert), StringComparison.Ordinal), "distinct target/registry certificates");

int targetPort = ReservePort();
int registryPort = ReservePort();
Check(targetPort != registryPort, "distinct reserved loopback ports");
string targetUrl = $"https://127.0.0.1:{targetPort}";
string registryUrl = $"https://127.0.0.1:{registryPort}";
string profileJson = JsonSerializer.Serialize(new
{
    Version = 1,
    Name = "Owner Smoke Target",
    RendezvousUrl = targetUrl + "/",
    RelayUrl = targetUrl + "/",
    CertificateSha256 = Pin(targetCert)
});
ServerProfile.ParseJson(profileJson);

const string registryIdentity = "peerdesk-registry-smoke";
const string alias = "owner-smoke";
string ownerSecret = RandomSecret(32);
string legacyInvite = RandomSecret(16);
string joinPassword = RandomSecret(12);
string grant = RandomSecret(24);
string grantHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(grant)));

var targetOptions = new ServerOptions
{
    InvitationCode = legacyInvite,
    EphemeralCertificate = targetCert,
    Port = targetPort,
    OwnerFeature = new OwnerFeatureOptions
    {
        Version = 1,
        OwnerSecret = ownerSecret,
        StatePath = Path.Combine(temp, "owner-state.json"),
        PublicProfileJson = profileJson,
        RegistryIdentity = registryIdentity
    }
};
var registryOptions = new ServerOptions
{
    InvitationCode = RandomSecret(16),
    EphemeralCertificate = registryCert,
    Port = registryPort,
    DirectoryFeature = new DirectoryFeatureOptions(1, registryIdentity, Path.Combine(temp, "directory-state.json"),
        new[] { new DirectoryGrantOptions(grantHash, alias, profileJson, 0) })
};

var logArgs = new[] { "--Logging:LogLevel:Default=Warning" };
await using var target = PeerDeskServerApp.BuildApp(logArgs, targetOptions);
await using var registry = PeerDeskServerApp.BuildApp(logArgs, registryOptions);

// Counter middleware: counts requests that actually reach the HTTP pipeline after TLS.
int targetRegisterHits = 0;
target.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Equals("/api/v1/register")) Interlocked.Increment(ref targetRegisterHits);
    await next();
});
var lookupObservations = new List<(string Method, long? Length, bool HasBody, bool HasAuth, bool HasOwner, string Query)>();
registry.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/v1/directory/" + alias))
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        lock (lookupObservations)
            lookupObservations.Add((ctx.Request.Method, ctx.Request.ContentLength, ms.Length > 0,
                ctx.Request.Headers.ContainsKey("Authorization"), ctx.Request.Headers.ContainsKey(OwnerFeature.OwnerHeader),
                ctx.Request.QueryString.Value ?? string.Empty));
    }
    await next();
});

await target.StartAsync();
await registry.StartAsync();
try
{
    using var targetHttp = PinnedHttpClientFactory.CreateClient(Pin(targetCert));
    using var registryHttp = PinnedHttpClientFactory.CreateClient(Pin(registryCert));
    using var wrongPinHttp = PinnedHttpClientFactory.CreateClient(Pin(registryCert));

    async Task<HttpResponseMessage> Send(HttpClient http, string baseUrl, string method, string path,
        object? body = null, string? bearer = null, string? owner = null, HttpContent? raw = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), baseUrl + path);
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (owner != null) req.Headers.TryAddWithoutValidation(OwnerFeature.OwnerHeader, owner);
        if (raw != null) req.Content = raw;
        else if (body != null) req.Content = JsonContent.Create(body);
        return await http.SendAsync(req);
    }
    Task<HttpResponseMessage> T(string m, string p, object? b = null, string? bearer = null, string? owner = null, HttpContent? raw = null)
        => Send(targetHttp, targetUrl, m, p, b, bearer, owner, raw);
    Task<HttpResponseMessage> R(string m, string p, object? b = null, HttpContent? raw = null)
        => Send(registryHttp, registryUrl, m, p, b, raw: raw);

    // ── Owner auth ──
    Check((await T("GET", "/api/v1/owner/settings")).StatusCode == HttpStatusCode.Unauthorized, "owner settings without header 401");
    Check((await T("GET", "/api/v1/owner/settings", owner: RandomSecret(32))).StatusCode == HttpStatusCode.Unauthorized, "owner settings wrong owner 401");
    using (var s = await T("GET", "/api/v1/owner/settings", owner: ownerSecret))
    {
        Check(s.StatusCode == HttpStatusCode.OK, "owner settings correct owner 200");
        using var doc = JsonDocument.Parse(await s.Content.ReadAsStringAsync());
        Check(!doc.RootElement.GetProperty("joinPasswordConfigured").GetBoolean(), "join password initially unconfigured");
    }

    // ── Fail-closed before configuration ──
    Check((await T("POST", "/api/v1/register", new RegisterRequest(legacyInvite))).StatusCode == HttpStatusCode.Unauthorized, "register before config with legacy invite 401");
    Check((await T("POST", "/api/v1/register", new RegisterRequest(joinPassword))).StatusCode == HttpStatusCode.Unauthorized, "register before config with future join password 401");

    // ── Configure join password ──
    Check((await T("POST", "/api/v1/owner/settings", new { displayName = "Smoke", joinPassword }, owner: RandomSecret(32))).StatusCode == HttpStatusCode.Unauthorized, "configure with wrong owner 401");
    using (var s = await T("POST", "/api/v1/owner/settings", new { displayName = "Smoke", joinPassword }, owner: ownerSecret))
    {
        Check(s.StatusCode == HttpStatusCode.OK, "owner configures join password 200");
        string text = await s.Content.ReadAsStringAsync();
        Check(!text.Contains(joinPassword, StringComparison.Ordinal) && !text.Contains(ownerSecret, StringComparison.Ordinal), "settings response does not echo secrets");
        using var doc = JsonDocument.Parse(text);
        Check(doc.RootElement.GetProperty("joinPasswordConfigured").GetBoolean(), "join password configured");
    }
    Check((await T("POST", "/api/v1/register", new RegisterRequest(legacyInvite))).StatusCode == HttpStatusCode.Unauthorized, "old legacy invite after config 401");
    int hitsBeforeWrongPin = Volatile.Read(ref targetRegisterHits);
    Check(hitsBeforeWrongPin == 3, "register counter observed prior register requests");

    // ── Wrong pin never reaches the register handler ──
    HttpRequestException? pinFailure = null;
    try { await Send(wrongPinHttp, targetUrl, "POST", "/api/v1/register", new RegisterRequest(joinPassword)); }
    catch (HttpRequestException ex) { pinFailure = ex; }
    Check(pinFailure?.InnerException is AuthenticationException, "wrong-pin client fails TLS authentication");
    Check(Volatile.Read(ref targetRegisterHits) == hitsBeforeWrongPin, "wrong-pin register did not reach HTTP pipeline");

    // ── Positive registrations via correct pin ──
    async Task<RegisterResponse> Register()
    {
        using var r = await T("POST", "/api/v1/register", new RegisterRequest(joinPassword));
        Check(r.StatusCode == HttpStatusCode.OK, "register with configured join password 200");
        return (await r.Content.ReadFromJsonAsync<RegisterResponse>())!;
    }
    var a = await Register();
    Check(Volatile.Read(ref targetRegisterHits) == hitsBeforeWrongPin + 1, "correct-pin register reached HTTP pipeline");
    var b = await Register();
    Check(a.DeviceId != b.DeviceId && a.BearerToken != b.BearerToken, "two distinct devices registered");

    // ── Directory: challenge -> owner proof -> publish -> lookup ──
    Check((await R("GET", "/api/v1/directory/" + alias)).StatusCode == HttpStatusCode.NotFound, "lookup before publish 404");
    Check((await R("POST", "/api/v1/directory/challenge", new { grant = RandomSecret(24) })).StatusCode == HttpStatusCode.Forbidden, "unknown grant challenge 403");
    string challengeText;
    using (var c = await R("POST", "/api/v1/directory/challenge", new { grant }))
    {
        Check(c.StatusCode == HttpStatusCode.OK, "approved grant challenge 200");
        challengeText = await c.Content.ReadAsStringAsync();
    }
    var challenge = JsonSerializer.Deserialize<DirectoryChallenge>(challengeText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Check(challenge.Alias == alias && challenge.RegistryIdentity == registryIdentity && challenge.ProfileJson == profileJson, "challenge bound to alias/registry/profile");

    Check((await T("POST", "/api/v1/owner/proof", raw: new StringContent(challengeText, Encoding.UTF8, "application/json"), owner: RandomSecret(32))).StatusCode == HttpStatusCode.Unauthorized, "owner proof wrong owner 401");
    string proofText;
    using (var p = await T("POST", "/api/v1/owner/proof", raw: new StringContent(challengeText, Encoding.UTF8, "application/json"), owner: ownerSecret))
    {
        Check(p.StatusCode == HttpStatusCode.OK, "target owner proof 200");
        proofText = await p.Content.ReadAsStringAsync();
    }
    using (var proofDoc = JsonDocument.Parse(proofText))
    {
        byte[] der = Convert.FromBase64String(proofDoc.RootElement.GetProperty("certificateDerBase64").GetString()!);
        Check(der.AsSpan().SequenceEqual(targetCert.RawData), "proof carries target certificate");
    }
    using (var pub = await R("POST", "/api/v1/directory/publish", raw: new StringContent(proofText, Encoding.UTF8, "application/json")))
        Check(pub.StatusCode == HttpStatusCode.OK, "registry publish 200");
    using (var replay = await R("POST", "/api/v1/directory/publish", raw: new StringContent(proofText, Encoding.UTF8, "application/json")))
        Check(replay.StatusCode == HttpStatusCode.Forbidden, "proof replay rejected 403");

    int lookupsBefore; lock (lookupObservations) lookupsBefore = lookupObservations.Count;
    using (var look = await R("GET", "/api/v1/directory/" + alias))
    {
        Check(look.StatusCode == HttpStatusCode.OK, "registry lookup 200");
        string text = await look.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        Check(doc.RootElement.GetProperty("alias").GetString() == alias, "lookup alias exact");
        Check(doc.RootElement.GetProperty("profileJson").GetString() == profileJson, "lookup profileJson exact");
        Check(!text.Contains(joinPassword, StringComparison.Ordinal) && !text.Contains(grant, StringComparison.Ordinal), "lookup response has no join password or grant");
    }
    (string Method, long? Length, bool HasBody, bool HasAuth, bool HasOwner, string Query) obs;
    lock (lookupObservations) { Check(lookupObservations.Count == lookupsBefore + 1, "lookup observed once"); obs = lookupObservations[^1]; }
    Check(obs.Method == "GET" && !obs.HasBody && (obs.Length is null or 0) && !obs.HasAuth && !obs.HasOwner && obs.Query.Length == 0,
        "lookup is GET without body/credentials/query");

    // ── Existing session flow ──
    using (var r = await T("POST", "/api/v1/sessions/request", new SessionRequest(b.DeviceId, b.TemporaryPassword), bearer: a.BearerToken))
    {
        Check(r.StatusCode == HttpStatusCode.OK, "session request 200");
        var sr = (await r.Content.ReadFromJsonAsync<SessionResponse>())!;
        Check((await T("POST", "/api/v1/sessions/decision", new DecisionRequest(sr.SessionId, true), bearer: b.BearerToken)).StatusCode == HttpStatusCode.OK, "approve 200");
        using var conn = await T("POST", "/api/v1/sessions/connect", new ConnectRequest(sr.SessionId), bearer: a.BearerToken);
        Check(conn.StatusCode == HttpStatusCode.OK && (await conn.Content.ReadFromJsonAsync<SessionResponse>())!.Status == SessionStatus.Connected, "connect -> Connected");

        // Connected invalidates the target's prior temporary password (phase13).
        Check((await T("POST", "/api/v1/sessions/request", new SessionRequest(b.DeviceId, b.TemporaryPassword), bearer: a.BearerToken)).StatusCode == HttpStatusCode.Unauthorized, "connected invalidates prior target password");
        using var rotated = await T("POST", "/api/v1/devices/password/rotate", new { }, bearer: b.BearerToken);
        Check(rotated.StatusCode == HttpStatusCode.OK, "target rotates password after connect");
        var fresh = (await rotated.Content.ReadFromJsonAsync<PasswordResponse>())!;
        // Second session for explicit revoke path.
        using var r2 = await T("POST", "/api/v1/sessions/request", new SessionRequest(b.DeviceId, fresh.TemporaryPassword), bearer: a.BearerToken);
        Check(r2.StatusCode == HttpStatusCode.OK, "second session request 200");
        var sr2 = (await r2.Content.ReadFromJsonAsync<SessionResponse>())!;
        using var rv = await T("POST", "/api/v1/sessions/revoke", new RevokeRequest(sr2.SessionId), bearer: b.BearerToken);
        Check(rv.StatusCode == HttpStatusCode.OK && (await rv.Content.ReadFromJsonAsync<SessionResponse>())!.Status == SessionStatus.Revoked, "session revoke -> Revoked");

        // ── Owner device revoke on active (Connected) session ──
        Check((await T("GET", "/api/v1/devices/me", bearer: a.BearerToken)).StatusCode == HttpStatusCode.OK, "device A bearer valid before owner revoke");
        Check((await T("POST", "/api/v1/owner/devices/revoke", new { deviceId = a.DeviceId }, owner: RandomSecret(32))).StatusCode == HttpStatusCode.Unauthorized, "owner device revoke wrong owner 401");
        using (var list = await T("GET", "/api/v1/owner/devices", owner: ownerSecret))
        {
            string text = await list.Content.ReadAsStringAsync();
            Check(list.StatusCode == HttpStatusCode.OK && text.Contains(a.DeviceId) && text.Contains(b.DeviceId), "owner device list contains both");
            Check(!text.Contains(a.BearerToken) && !text.Contains(b.BearerToken), "owner device list has no bearer tokens");
        }
        Check((await T("POST", "/api/v1/owner/devices/revoke", new { deviceId = a.DeviceId }, owner: ownerSecret)).StatusCode == HttpStatusCode.OK, "owner revokes device A 200");
        Check((await T("GET", "/api/v1/devices/me", bearer: a.BearerToken)).StatusCode == HttpStatusCode.Unauthorized, "revoked device old bearer 401");
        Check((await T("POST", "/api/v1/sessions/request", new SessionRequest(b.DeviceId, b.TemporaryPassword), bearer: a.BearerToken)).StatusCode == HttpStatusCode.Unauthorized, "revoked device cannot request new session");
        using var after = await T("POST", "/api/v1/sessions/revoke", new RevokeRequest(sr.SessionId), bearer: b.BearerToken);
        Check(after.StatusCode == HttpStatusCode.BadRequest, "connected session became terminal after owner device revoke");
        Check((await T("POST", "/api/v1/owner/devices/revoke", new { deviceId = a.DeviceId }, owner: ownerSecret)).StatusCode == HttpStatusCode.NotFound, "second owner revoke 404");
        Check((await T("GET", "/api/v1/devices/me", bearer: b.BearerToken)).StatusCode == HttpStatusCode.OK, "unrevoked device B still valid");
    }
}
finally
{
    await target.StopAsync();
    await registry.StopAsync();
}

// ── Verifier generation barrier across real HTTP owner rotation (separate app hosting the feature) ──
{
    using var genCert = MakeCert("peerdesk-gen");
    int genPort = ReservePort();
    string genUrl = $"https://127.0.0.1:{genPort}";
    string genProfile = JsonSerializer.Serialize(new { Version = 1, Name = "Gen", RendezvousUrl = genUrl + "/", RelayUrl = genUrl + "/", CertificateSha256 = Pin(genCert) });
    string genOwner = RandomSecret(32);
    string oldPw = RandomSecret(12), newPw = RandomSecret(12);
    var feature = new OwnerFeature(new OwnerFeatureOptions
    {
        Version = 1, OwnerSecret = genOwner, StatePath = Path.Combine(temp, "gen-owner-state.json"),
        PublicProfileJson = genProfile, RegistryIdentity = "peerdesk-registry-smoke"
    }, genCert, SystemClock.Instance);

    var builder = WebApplication.CreateBuilder(new[] { "--Logging:LogLevel:Default=Warning" });
    builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, genPort, lo => lo.UseHttps(genCert)));
    await using var genApp = builder.Build();
    feature.MapEndpoints(genApp, () => new { devices = Array.Empty<object>() }, _ => false);
    await genApp.StartAsync();
    try
    {
        using var http = PinnedHttpClientFactory.CreateClient(Pin(genCert));
        async Task<HttpStatusCode> SetPw(string? pw)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, genUrl + "/api/v1/owner/settings") { Content = JsonContent.Create(new { displayName = "Gen", joinPassword = pw }) };
            req.Headers.TryAddWithoutValidation(OwnerFeature.OwnerHeader, genOwner);
            using var resp = await http.SendAsync(req);
            return resp.StatusCode;
        }
        Check(!await feature.VerifyJoinPasswordAsync(oldPw, string.Empty, CancellationToken.None), "unconfigured feature fails closed");
        Check(await SetPw(oldPw) == HttpStatusCode.OK, "gen: configure old password via HTTP");

        long capturedGen = feature.VerifierGeneration; // captured before KDF, as register does
        bool oldVerified = await feature.VerifyJoinPasswordAsync(oldPw, string.Empty, CancellationToken.None);
        Check(oldVerified, "gen: old password verifies before rotation");
        Check(await SetPw(newPw) == HttpStatusCode.OK, "gen: rotate to new password via HTTP");
        bool stale;
        lock (feature.RegistrationCommitGate) stale = feature.VerifierGeneration != capturedGen; // same condition as register commit
        Check(stale, "gen: old verification result is stale at commit gate after rotation");
        Check(await feature.VerifyJoinPasswordAsync(newPw, string.Empty, CancellationToken.None), "gen: new password verifies");
        Check(!await feature.VerifyJoinPasswordAsync(oldPw, string.Empty, CancellationToken.None), "gen: old password fails after rotation");

        long g2 = feature.VerifierGeneration;
        Check(await SetPw(newPw + "x") == HttpStatusCode.OK && feature.VerifierGeneration == g2 + 1, "gen: each owner save increments generation once");

        // Join-password length boundary 8..128 (random hex ASCII, values never printed).
        string pw7 = RandomSecret(4)[..7], pw8 = RandomSecret(4), pw128 = RandomSecret(64), pw129 = RandomSecret(65)[..129];
        long g3 = feature.VerifierGeneration;
        Check(await SetPw(pw7) == HttpStatusCode.BadRequest && feature.VerifierGeneration == g3, "bound: 7-char join password rejected 400 without generation change");
        Check(await feature.VerifyJoinPasswordAsync(newPw + "x", string.Empty, CancellationToken.None), "bound: verifier unchanged after 7-char rejection");
        Check(await SetPw(pw8) == HttpStatusCode.OK, "bound: 8-char join password accepted 200");
        Check(await feature.VerifyJoinPasswordAsync(pw8, string.Empty, CancellationToken.None), "bound: 8-char join password verifies");
        Check(await SetPw(null) == HttpStatusCode.OK, "bound: null join password (name-only) accepted 200");
        Check(await feature.VerifyJoinPasswordAsync(pw8, string.Empty, CancellationToken.None), "bound: null join password keeps existing verifier");
        Check(await SetPw(pw128) == HttpStatusCode.OK, "bound: 128-char join password accepted 200");
        Check(await feature.VerifyJoinPasswordAsync(pw128, string.Empty, CancellationToken.None), "bound: 128-char join password verifies");
        long g4 = feature.VerifierGeneration;
        Check(await SetPw(pw129) == HttpStatusCode.BadRequest && feature.VerifierGeneration == g4, "bound: 129-char join password rejected 400 without generation change");
        Check(await feature.VerifyJoinPasswordAsync(pw128, string.Empty, CancellationToken.None), "bound: verifier unchanged after 129-char rejection");
    }
    finally
    {
        await genApp.StopAsync();
    }
}

try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
Console.WriteLine($"OWNER DIRECTORY SMOKE PASS {count}");
