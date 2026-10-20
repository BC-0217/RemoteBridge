using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Authentication;
using PeerDesk.Server;

static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
using var rsa = RSA.Create(2048);
var req = new CertificateRequest("CN=opaque-relay-smoke", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build());
req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
var eku = new OidCollection { new("1.3.6.1.5.5.7.3.1") }; req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
using var generatedCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
using var cert = X509CertificateLoader.LoadPkcs12(generatedCert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
var pin = cert.GetCertHashString(HashAlgorithmName.SHA256);
if (args.Contains("--production") || args.Contains("--presence")) { await ProductionGate.Run(cert,args.Contains("--presence")); return; }
var builder = WebApplication.CreateBuilder();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert, o => o.SslProtocols = SslProtocols.Tls12)));
await using var app = builder.Build();
var gate = new object();
var allowRate = true;
var session = new SessionState { SessionId="fixture-session", InitiatorDeviceId="initiator", TargetDeviceId="target", Status=SessionStatus.Connected };
lock (gate) session.Media.StartSharing();
OpaqueSessionRelay.Map(app, gate, c => c.Request.Headers["X-Device"].FirstOrDefault(),
    id => id is "initiator" or "target" or "outsider", _ => session, () => { }, (_, _) => allowRate, () => DateTimeOffset.UtcNow, 10);
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    HttpClient NewClient(string device)
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
            presented is not null && presented.GetCertHashString(HashAlgorithmName.SHA256) == pin };
        var c = new HttpClient(handler) { BaseAddress = new Uri(address) };
        c.DefaultRequestHeaders.Add("X-Device", device);
        return c;
    }
    using var client = NewClient("initiator");
    using var target = NewClient("target");
    using var outsider = NewClient("outsider");

    var invalidCfg = await target.PostAsync("/api/v2/sessions/fixture-session/opaque/configure",
        new StringContent("{\"permissionBits\":1,\"permissionBits\":1,\"permissionRevision\":1,\"geometryVersion\":1}", Encoding.UTF8, "application/json"));
    Require(invalidCfg.StatusCode == HttpStatusCode.BadRequest, "duplicate JSON property accepted");
    var cfg = await target.PostAsync("/api/v2/sessions/fixture-session/opaque/configure",
        new StringContent("{\"permissionBits\":15,\"permissionRevision\":1,\"geometryVersion\":1}", Encoding.UTF8, "application/json"));
    Require(cfg.StatusCode == HttpStatusCode.OK, "configure failed: " + cfg.StatusCode);
    using var descriptor = JsonDocument.Parse(await cfg.Content.ReadAsStringAsync());
    var epoch = descriptor.RootElement.GetProperty("transportEpoch").GetString()!;
    Require(epoch.Length == 32, "epoch must be 32 hex chars");
    var denied = await outsider.GetAsync("/api/v2/sessions/fixture-session/opaque/descriptor");
    Require(denied.StatusCode == HttpStatusCode.NotFound, "participant ACL failed");

    var wrongRole = await client.PostAsync("/api/v2/sessions/fixture-session/opaque/configure",
        new StringContent("""{"permissionBits":15,"permissionRevision":1,"geometryVersion":1}""", Encoding.UTF8, "application/json"));
    Require(wrongRole.StatusCode == HttpStatusCode.Forbidden, "initiator may not configure");
    var wrongNumber = await target.PostAsync("/api/v2/sessions/fixture-session/opaque/configure",
        new StringContent("""{"permissionBits":"15","permissionRevision":1,"geometryVersion":1}""", Encoding.UTF8, "application/json"));
    Require(wrongNumber.StatusCode == HttpStatusCode.BadRequest, "string integer must reject");
    allowRate = false;
    using (var limited = await client.GetAsync("/api/v2/sessions/fixture-session/opaque/descriptor"))
        Require((int)limited.StatusCode == 429, "rate denial must remain 429");
    allowRate = true;
    var deniedScreen = new byte[85]; deniedScreen[0] = 1;
    using (var r = await client.PostAsync($"/api/v2/sessions/fixture-session/opaque/packet?epoch={epoch}", new ByteArrayContent(deniedScreen)))
        Require(r.StatusCode == HttpStatusCode.Forbidden, "initiator screen denied");
    var oversized = new byte[524288 + 86]; oversized[0] = 1;
    using (var r = await target.PostAsync($"/api/v2/sessions/fixture-session/opaque/packet?epoch={epoch}", new ByteArrayContent(oversized)))
        Require((int)r.StatusCode == 413, "oversized opaque body denied");
    using (var r = await target.PostAsync($"/api/v2/sessions/fixture-session/opaque/packet?epoch={new string('0',32)}", new ByteArrayContent(deniedScreen)))
        Require(r.StatusCode == HttpStatusCode.NotFound, "stale epoch denied");

    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    using var streamRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/sessions/fixture-session/opaque/stream?epoch={epoch}");
    var streamTask = client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
    await Task.Delay(100, cancel.Token);
    Require(session.LastMediaActivityAt is null, "empty stream renewed media activity");
    using var duplicate = await client.GetAsync($"/api/v2/sessions/fixture-session/opaque/stream?epoch={epoch}", cancel.Token);
    Require(duplicate.StatusCode == HttpStatusCode.Conflict, "duplicate participant reader accepted");
    var packet = new byte[85]; packet[0] = 1; RandomNumberGenerator.Fill(packet.AsSpan(1));
    var posted = await target.PostAsync($"/api/v2/sessions/fixture-session/opaque/packet?epoch={epoch}", new ByteArrayContent(packet), cancel.Token);
    Require(posted.StatusCode == HttpStatusCode.Accepted, "packet failed: " + posted.StatusCode);
    using var streamResponse = await streamTask;
    Require(streamResponse.StatusCode == HttpStatusCode.OK, "stream failed");
    await using var body = await streamResponse.Content.ReadAsStreamAsync(cancel.Token);
    var frameHeader = new byte[4];
    await body.ReadExactlyAsync(frameHeader, cancel.Token);
    Require(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frameHeader) == 85, "bad frame length");
    var payload = new byte[85]; await body.ReadExactlyAsync(payload, cancel.Token);
    Require(payload[0] == 1 && payload.AsSpan(1).SequenceEqual(packet.AsSpan(1)), "opaque payload changed");
    var delivered = System.Diagnostics.Stopwatch.StartNew();
    while (true) { lock (gate) { if (session.LastMediaActivityAt is not null) break; }
        if (delivered.ElapsedMilliseconds >= 1000) throw new InvalidOperationException("successful screen delivery did not mark media activity");
        await Task.Delay(5, cancel.Token);
    }
    Require(session.LastMediaActivityAt is not null, "successful screen delivery did not mark media activity");
    Console.WriteLine("OPAQUE_RELAY_PASS 18: pinned TLS, role/rate/epoch/size negatives, persistent stream");
}
finally { await app.StopAsync(); }
