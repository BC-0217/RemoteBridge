using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PeerDesk.Core;
using PeerDesk.Server;

namespace PeerDesk.Smoke;

class Program
{
    // ── helpers ────────────────────────────────────────────────────────────────

    static int _passed;
    static int _failed;

    static void Pass(string name)
    {
        _passed++;
        Console.WriteLine($"  [PASS] {name}");
    }

    static void Fail(string name, string reason)
    {
        _failed++;
        Console.WriteLine($"  [FAIL] {name}: {reason}");
    }

    // Start a server with the given options; return (app, baseUrl, certHashHex).
    static async Task<(Microsoft.AspNetCore.Builder.WebApplication app, string baseUrl)>
        StartServer(ServerOptions opts)
    {
        var app = PeerDeskServerApp.BuildApp(Array.Empty<string>(), opts);
        await app.StartAsync();
        var addressesFeature = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        string baseUrl = addressesFeature!.Addresses.First();
        return (app, baseUrl);
    }

    // Build a self-signed loopback cert and export it back through PFX so that
    // Windows Schannel can use it (the in-memory RSA key needs to be persisted
    // into the PFX blob for Schannel to find the private key).
    static (X509Certificate2 serverCert, string hashHex) MakeEphemeralCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var raw = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        // Export+import so private key is embedded in the X509Certificate2 object
        // (required by Kestrel / Schannel on Windows).
        byte[] pfx = raw.Export(X509ContentType.Pfx);
        var cert = X509CertificateLoader.LoadPkcs12(pfx, (string?)null,
            X509KeyStorageFlags.DefaultKeySet);
        byte[] hashBytes = cert.GetCertHash(HashAlgorithmName.SHA256);
        string hashHex = Convert.ToHexString(hashBytes);
        return (cert, hashHex);
    }

    static HttpRequestMessage Req(
        HttpMethod method, string baseUrl, string path,
        string? token, object? body = null)
    {
        var r = new HttpRequestMessage(method, $"{baseUrl}{path}");
        if (token != null) r.Headers.Add("Authorization", $"Bearer {token}");
        if (body != null) r.Content = JsonContent.Create(body);
        return r;
    }

    // ── main ───────────────────────────────────────────────────────────────────

    static async Task<int> Main()
    {
        Console.WriteLine("[PeerDesk Smoke] starting");

        // ── Suite A: ServerProfile validation ─────────────────────────────────
        Console.WriteLine("\n=== Suite A: ServerProfile ===");
        await RunSuiteA();

        // ── Suite B: PinnedHttpClientFactory ──────────────────────────────────
        Console.WriteLine("\n=== Suite B: Pin validation ===");
        await RunSuiteB();

        // ── Suite C: Full server integration ──────────────────────────────────
        Console.WriteLine("\n=== Suite C: Integration ===");
        await RunSuiteC();

        Console.WriteLine($"\n[DONE] passed={_passed} failed={_failed}");
        return _failed > 0 ? 1 : 0;
    }

    // ── Suite A ────────────────────────────────────────────────────────────────
    static Task RunSuiteA()
    {
        // Valid profile
        const string ValidJson = """{"Version":1,"Name":"Test","RendezvousUrl":"https://r.example.com/","RelayUrl":"https://relay.example.com/","CertificateSha256":"aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899"}""";

        TestThrows("A1 version!=1 rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("\"Version\":1", "\"Version\":2")));

        TestThrows("A2 unknown property rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("}", ",\"Extra\":\"val\"}") ));

        TestThrows("A3 duplicate property rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("}", ",\"Name\":\"dup\"}") ));

        TestThrows("A4 >16KB rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("Test", new string('x', 16_400)) ));

        TestThrows("A5 http URL rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("https://r.example.com/", "http://r.example.com/") ));

        TestThrows("A6 userinfo rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("https://r.example.com/", "https://u:***@r.example.com/") ));

        TestThrows("A7 query rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("https://r.example.com/", "https://r.example.com/?q=1") ));

        TestThrows("A8 fragment rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("https://r.example.com/", "https://r.example.com/#frag") ));

        TestThrows("A9 path rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("https://r.example.com/", "https://r.example.com/api") ));

        TestThrows("A10 name>64 rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace("\"Test\"", $"\"{new string('n', 65)}\"") ));

        TestThrows("A11 fingerprint not 64hex rejected",
            () => ServerProfile.ParseJson(
                ValidJson.Replace(
                    "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
                    "ZZZZ") ));

        TestOk("A12 valid profile parses ok",
            () => { _ = ServerProfile.ParseJson(ValidJson); });

        return Task.CompletedTask;
    }

    // ── Suite B ────────────────────────────────────────────────────────────────
    static async Task RunSuiteB()
    {
        // B1 – malformed pin (not 64 hex) is rejected at factory time
        TestThrows("B1 malformed pin rejected at factory",
            () => PinnedHttpClientFactory.CreateClient("not-a-hash"));

        // B2 – correct pin succeeds (integration; needs a live server)
        var (cert, hashHex) = MakeEphemeralCert();
        var opts = new ServerOptions
        {
            InvitationCode = "SMOKE_TEST_INVITATION_OK",
            EphemeralCertificate = cert,
            Port = 0
        };
        var (app, baseUrl) = await StartServer(opts);
        try
        {
            using var goodClient = PinnedHttpClientFactory.CreateClient(hashHex);
            var resp = await goodClient.SendAsync(
                Req(HttpMethod.Get, baseUrl, "/api/v1/sessions/pending", null));
            // Expect 401 (no auth) but TLS succeeded
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                Pass("B2 correct pin reaches server");
            else
                Fail("B2 correct pin reaches server", $"Unexpected status {resp.StatusCode}");

            // B3 – wrong pin is rejected by the client
            string badPin = new string('0', 64);
            using var badClient = PinnedHttpClientFactory.CreateClient(badPin);
            bool pinRejected = false;
            try { await badClient.GetAsync($"{baseUrl}/api/v1/sessions/pending"); }
            catch (HttpRequestException) { pinRejected = true; }
            if (pinRejected) Pass("B3 wrong pin rejected");
            else Fail("B3 wrong pin rejected", "Expected HttpRequestException");

            // B4 – hostname mismatch (connect via 127.0.0.1 but cert only has localhost)
            // Build a cert with only DNS localhost (no IP SAN)
            using var rsaHm = RSA.Create(2048);
            var hmReq = new CertificateRequest(
                "CN=localhost", rsaHm,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var hmSan = new SubjectAlternativeNameBuilder();
            hmSan.AddDnsName("localhost"); // no IP entry
            hmReq.CertificateExtensions.Add(hmSan.Build());
            using var hmRaw = hmReq.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            byte[] hmPfx = hmRaw.Export(X509ContentType.Pfx);
            using var hmCert = X509CertificateLoader.LoadPkcs12(hmPfx, (string?)null,
                X509KeyStorageFlags.DefaultKeySet);
            string hmHash = Convert.ToHexString(
                hmCert.GetCertHash(HashAlgorithmName.SHA256));

            var hmOpts = new ServerOptions
            {
                InvitationCode = "SMOKE_TEST_INVITATION_OK",
                EphemeralCertificate = hmCert,
                Port = 0
            };
            var (hmApp, hmBaseUrl) = await StartServer(hmOpts);
            try
            {
                // Replace "localhost" with "127.0.0.1" to trigger hostname mismatch
                string ipUrl = hmBaseUrl.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
                // The pinned client checks hostname errors; should reject
                using var hmClient = PinnedHttpClientFactory.CreateClient(hmHash);
                bool hmRejected = false;
                try { await hmClient.GetAsync($"{ipUrl}/api/v1/sessions/pending"); }
                catch (HttpRequestException) { hmRejected = true; }
                if (hmRejected) Pass("B4 hostname mismatch rejected");
                else Fail("B4 hostname mismatch rejected", "Expected rejection");
            }
            finally { await hmApp.StopAsync(); hmApp.DisposeAsync().AsTask().Wait(); }

            // B5 – expired cert is rejected
            using var rsaExp = RSA.Create(2048);
            var expReq = new CertificateRequest(
                "CN=localhost", rsaExp,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var expSan = new SubjectAlternativeNameBuilder();
            expSan.AddDnsName("localhost");
            expSan.AddIpAddress(IPAddress.Loopback);
            expReq.CertificateExtensions.Add(expSan.Build());
            // Cert valid window fully in the past
            using var expRaw = expReq.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-10),
                DateTimeOffset.UtcNow.AddDays(-1));
            byte[] expPfx = expRaw.Export(X509ContentType.Pfx);
            using var expCert = X509CertificateLoader.LoadPkcs12(expPfx, (string?)null,
                X509KeyStorageFlags.DefaultKeySet);
            string expHash = Convert.ToHexString(
                expCert.GetCertHash(HashAlgorithmName.SHA256));

            var expOpts = new ServerOptions
            {
                InvitationCode = "SMOKE_TEST_INVITATION_OK",
                EphemeralCertificate = expCert,
                Port = 0
            };
            var (expApp, expBaseUrl) = await StartServer(expOpts);
            try
            {
                using var expClient = PinnedHttpClientFactory.CreateClient(expHash);
                bool expRejected = false;
                try { await expClient.GetAsync($"{expBaseUrl}/api/v1/sessions/pending"); }
                catch (HttpRequestException) { expRejected = true; }
                if (expRejected) Pass("B5 expired cert rejected");
                else Fail("B5 expired cert rejected", "Expected rejection");
            }
            finally { await expApp.StopAsync(); expApp.DisposeAsync().AsTask().Wait(); }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            cert.Dispose();
        }
    }

    // ── Suite C ────────────────────────────────────────────────────────────────
    static async Task RunSuiteC()
    {
        var testClock = new TestClock();
        const string invite = "SMOKE_TEST_INVITATION_OK";
        var (cert, hashHex) = MakeEphemeralCert();
        var opts = new ServerOptions
        {
            InvitationCode = invite,
            EphemeralCertificate = cert,
            Clock = testClock,
            Port = 0,
            MaxDevices = 10,
            MaxSessions = 10,
            SessionTtl = TimeSpan.FromSeconds(30)
        };
        var (app, baseUrl) = await StartServer(opts);
        try
        {
            using var client = PinnedHttpClientFactory.CreateClient(hashHex);

            // ── C1: Register 3 peers (server assigns DeviceId) ─────────────────
            var rr1 = await Post<RegisterResponse>(client, baseUrl, "/api/v1/register",
                null, new RegisterRequest(invite));
            var rr2 = await Post<RegisterResponse>(client, baseUrl, "/api/v1/register",
                null, new RegisterRequest(invite));
            var rr3 = await Post<RegisterResponse>(client, baseUrl, "/api/v1/register",
                null, new RegisterRequest(invite));

            if (rr1 != null && rr2 != null && rr3 != null
                && !string.IsNullOrEmpty(rr1.DeviceId)
                && !string.IsNullOrEmpty(rr2.DeviceId)
                && !string.IsNullOrEmpty(rr3.DeviceId)
                && rr1.DeviceId != rr2.DeviceId
                && rr2.DeviceId != rr3.DeviceId)
                Pass("C1 register 3 peers with server-assigned DeviceId");
            else
                Fail("C1 register 3 peers", "DeviceId missing or duplicate");

            string t1 = rr1!.BearerToken;
            string t2 = rr2!.BearerToken;
            string t3 = rr3!.BearerToken;
            string d1 = rr1.DeviceId;
            string d2 = rr2.DeviceId;

            // ── C2: Bad token → 401 ────────────────────────────────────────────
            var badResp = await client.SendAsync(
                Req(HttpMethod.Get, baseUrl, "/api/v1/sessions/pending", "BAD_TOKEN"));
            if (badResp.StatusCode == HttpStatusCode.Unauthorized)
                Pass("C2 bad auth 401");
            else
                Fail("C2 bad auth 401", $"{badResp.StatusCode}");

            // ── C3: Wrong invitation → 401 ─────────────────────────────────────
            var badInvResp = await client.PostAsJsonAsync(
                $"{baseUrl}/api/v1/register", new RegisterRequest("WRONG_CODE_000000000"));
            if (badInvResp.StatusCode == HttpStatusCode.Unauthorized)
                Pass("C3 wrong invitation 401");
            else
                Fail("C3 wrong invitation 401", $"{badInvResp.StatusCode}");

            // ── C4: Self-request denied ────────────────────────────────────────
            var selfResp = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/request", t1,
                    new SessionRequest(d1, rr1.TemporaryPassword)));
            if (selfResp.StatusCode == HttpStatusCode.BadRequest)
                Pass("C4 self-request denied");
            else
                Fail("C4 self-request denied", $"{selfResp.StatusCode}");

            // ── C5: Connect before approval fails ─────────────────────────────
            var sess1 = await Post<SessionResponse>(client, baseUrl,
                "/api/v1/sessions/request", t1, new SessionRequest(d2, rr2.TemporaryPassword));
            var earlyConn = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/connect", t1,
                    new ConnectRequest(sess1!.SessionId)));
            if (earlyConn.StatusCode == HttpStatusCode.BadRequest)
                Pass("C5 connect before approval rejected");
            else
                Fail("C5 connect before approval", $"{earlyConn.StatusCode}");

            // ── C6: 3rd-party cannot approve ──────────────────────────────────
            var thirdApprove = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/decision", t3,
                    new DecisionRequest(sess1.SessionId, true)));
            if (thirdApprove.StatusCode == HttpStatusCode.Forbidden)
                Pass("C6 3rd-party cannot approve (403)");
            else
                Fail("C6 3rd-party approve", $"{thirdApprove.StatusCode}");

            // ── C7: Initiator cannot approve own request ───────────────────────
            var initiatorApprove = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/decision", t1,
                    new DecisionRequest(sess1.SessionId, true)));
            if (initiatorApprove.StatusCode == HttpStatusCode.Forbidden)
                Pass("C7 initiator cannot approve own request");
            else
                Fail("C7 initiator approve own", $"{initiatorApprove.StatusCode}");

            // ── C8: Target-only consent; approve, then connect ─────────────────
            var approveResp = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/decision", t2,
                    new DecisionRequest(sess1.SessionId, true)));
            if (!approveResp.IsSuccessStatusCode)
            { Fail("C8a approve", $"{approveResp.StatusCode}"); }
            else Pass("C8a target approves");

            var connResp = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/connect", t1,
                    new ConnectRequest(sess1.SessionId)));
            if (connResp.IsSuccessStatusCode)
                Pass("C8b initiator connects");
            else
                Fail("C8b initiator connects", $"{connResp.StatusCode}");

            // ── C9: Replay connect rejected ────────────────────────────────────
            var replayConn = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/connect", t1,
                    new ConnectRequest(sess1.SessionId)));
            if (replayConn.StatusCode == HttpStatusCode.BadRequest)
                Pass("C9 replay connect rejected");
            else
                Fail("C9 replay connect", $"{replayConn.StatusCode}");

            // ── C10: 3rd-party cannot connect ─────────────────────────────────
            var thirdConn = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/connect", t3,
                    new ConnectRequest(sess1.SessionId)));
            if (thirdConn.StatusCode == HttpStatusCode.Forbidden)
                Pass("C10 3rd-party connect denied");
            else
                Fail("C10 3rd-party connect", $"{thirdConn.StatusCode}");

            // A successful connect invalidates the target password; fetch its next password before new requests.
            var refreshedTarget = await Post<PasswordResponse>(client, baseUrl, "/api/v1/devices/password/rotate", t2, new { });
            if (refreshedTarget is null) throw new Exception("Target password refresh failed.");
            rr2 = rr2 with { TemporaryPassword = refreshedTarget.TemporaryPassword, PasswordExpiresAt = refreshedTarget.PasswordExpiresAt, PasswordGeneration = refreshedTarget.PasswordGeneration };

            // ── C11: Revoke and verify terminal immutability ───────────────────
            // Create a fresh Approved session to revoke
            var sess2 = await Post<SessionResponse>(client, baseUrl,
                "/api/v1/sessions/request", t1, new SessionRequest(d2, rr2.TemporaryPassword));
            await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/decision", t2,
                    new DecisionRequest(sess2!.SessionId, true)));
            var revokeResp = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/revoke", t1,
                    new RevokeRequest(sess2.SessionId)));
            if (revokeResp.IsSuccessStatusCode)
                Pass("C11a revoke succeeds");
            else
                Fail("C11a revoke", $"{revokeResp.StatusCode}");

            // Revoke again → terminal, should 400
            var revokeAgain = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/revoke", t1,
                    new RevokeRequest(sess2.SessionId)));
            if (revokeAgain.StatusCode == HttpStatusCode.BadRequest)
                Pass("C11b revoke of terminal 400");
            else
                Fail("C11b revoke terminal", $"{revokeAgain.StatusCode}");

            // Connect after revoke → 400
            var connAfterRevoke = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/connect", t1,
                    new ConnectRequest(sess2.SessionId)));
            if (connAfterRevoke.StatusCode == HttpStatusCode.BadRequest)
                Pass("C11c connect after revoke 400");
            else
                Fail("C11c connect after revoke", $"{connAfterRevoke.StatusCode}");

            // ── C12: Rejection terminal state ─────────────────────────────────
            var sess3 = await Post<SessionResponse>(client, baseUrl,
                "/api/v1/sessions/request", t1, new SessionRequest(d2, rr2.TemporaryPassword));
            await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/decision", t2,
                    new DecisionRequest(sess3!.SessionId, false)));
            // Try to approve rejected session
            var lateApprove = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/decision", t2,
                    new DecisionRequest(sess3.SessionId, true)));
            if (lateApprove.StatusCode == HttpStatusCode.BadRequest)
                Pass("C12 rejected is terminal; late approve 400");
            else
                Fail("C12 rejected terminal", $"{lateApprove.StatusCode}");

            // ── C13: Expiry via clock advance ──────────────────────────────────
            var sess4 = await Post<SessionResponse>(client, baseUrl,
                "/api/v1/sessions/request", t1, new SessionRequest(d2, rr2.TemporaryPassword));
            testClock.Advance(TimeSpan.FromSeconds(60)); // past 30s TTL
            var expiredDecision = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/decision", t2,
                    new DecisionRequest(sess4!.SessionId, true)));
            if (expiredDecision.StatusCode == HttpStatusCode.BadRequest)
                Pass("C13 expired pending session rejects decision");
            else
                Fail("C13 expiry", $"{expiredDecision.StatusCode}");

            // Also check approved-then-expired
            testClock.Advance(TimeSpan.FromSeconds(-60)); // reset clock
            var sess5 = await Post<SessionResponse>(client, baseUrl,
                "/api/v1/sessions/request", t1, new SessionRequest(d2, rr2.TemporaryPassword));
            await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/decision", t2,
                    new DecisionRequest(sess5!.SessionId, true)));
            testClock.Advance(TimeSpan.FromSeconds(60));
            var expiredConnect = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/connect", t1,
                    new ConnectRequest(sess5.SessionId)));
            if (expiredConnect.StatusCode == HttpStatusCode.BadRequest)
                Pass("C13b expired approved session rejects connect");
            else
                Fail("C13b approved expiry", $"{expiredConnect.StatusCode}");

            // ── C14: 3rd-party revoke denied ──────────────────────────────────
            testClock.Advance(TimeSpan.FromSeconds(-60));
            var sess6 = await Post<SessionResponse>(client, baseUrl,
                "/api/v1/sessions/request", t1, new SessionRequest(d2, rr2.TemporaryPassword));
            var thirdRevoke = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/revoke", t3,
                    new RevokeRequest(sess6!.SessionId)));
            if (thirdRevoke.StatusCode == HttpStatusCode.Forbidden)
                Pass("C14 3rd-party revoke denied");
            else
                Fail("C14 3rd-party revoke", $"{thirdRevoke.StatusCode}");

            // ── C15: Parallel connect exactly 1 succeeds ──────────────────────
            var sess7 = await Post<SessionResponse>(client, baseUrl,
                "/api/v1/sessions/request", t1, new SessionRequest(d2, rr2.TemporaryPassword));
            await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/decision", t2,
                    new DecisionRequest(sess7!.SessionId, true)));
            var parallelTasks = Enumerable.Range(0, 4)
                .Select(_ => client.SendAsync(
                    Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/connect", t1,
                        new ConnectRequest(sess7.SessionId))))
                .ToArray();
            var parallelResults = await Task.WhenAll(parallelTasks);
            int successes = parallelResults.Count(r => r.IsSuccessStatusCode);
            if (successes == 1)
                Pass("C15 parallel connects exactly 1 succeeds");
            else
                Fail("C15 parallel connects", $"{successes} succeeded");


            // Connected sessions must expire too: revoke would succeed if still active.
            testClock.Advance(TimeSpan.FromSeconds(30));
            using var connectedExpiry = await client.SendAsync(
                Req(HttpMethod.Post, baseUrl, "/api/v1/sessions/revoke", t1,
                    new RevokeRequest(sess7.SessionId)));
            if (connectedExpiry.StatusCode == HttpStatusCode.BadRequest)
                Pass("C18 connected session expiry is terminal at exact TTL");
            else
                Fail("C18 connected expiry", $"{connectedExpiry.StatusCode}");

            // Independent capacity-one server proves terminal cleanup restores capacity.
            var (pruneApp, pruneBase) = await StartServer(new ServerOptions {
                InvitationCode = invite, EphemeralCertificate = cert,
                Clock = testClock, Port = 0, MaxDevices = 3, MaxSessions = 1 });
            try
            {
                using var pc = PinnedHttpClientFactory.CreateClient(hashHex);
                var pa = await Post<RegisterResponse>(pc, pruneBase, "/api/v1/register", null, new RegisterRequest(invite));
                var pb = await Post<RegisterResponse>(pc, pruneBase, "/api/v1/register", null, new RegisterRequest(invite));
                if (pa is null || pb is null) throw new Exception("Prune fixture registration failed.");
                var first = await Post<SessionResponse>(pc, pruneBase, "/api/v1/sessions/request", pa.BearerToken, new SessionRequest(pb.DeviceId, pb.TemporaryPassword));
                if (first is null) throw new Exception("Prune fixture initial session failed.");
                using var full = await pc.SendAsync(Req(HttpMethod.Post, pruneBase, "/api/v1/sessions/request", pa.BearerToken, new SessionRequest(pb.DeviceId, pb.TemporaryPassword)));
                using var revoked = await pc.SendAsync(Req(HttpMethod.Post, pruneBase, "/api/v1/sessions/revoke", pa.BearerToken, new RevokeRequest(first.SessionId)));
                var second = await Post<SessionResponse>(pc, pruneBase, "/api/v1/sessions/request", pa.BearerToken, new SessionRequest(pb.DeviceId, pb.TemporaryPassword));
                using var oldConnect = await pc.SendAsync(Req(HttpMethod.Post, pruneBase, "/api/v1/sessions/connect", pa.BearerToken, new ConnectRequest(first.SessionId)));
                if (full.StatusCode == HttpStatusCode.TooManyRequests && revoked.IsSuccessStatusCode &&
                    second != null && second.SessionId != first.SessionId && oldConnect.StatusCode == HttpStatusCode.NotFound)
                    Pass("C19 terminal pruning restores capacity without old-session revival");
                else
                    Fail("C19 terminal pruning", $"full={full.StatusCode} revoke={revoked.StatusCode} new={second != null} old={oldConnect.StatusCode}");
            }
            finally { await pruneApp.StopAsync(); await pruneApp.DisposeAsync(); }

            // ── C16: Oversized body → 413 ─────────────────────────────────────
            var bigPayload = new HttpRequestMessage(
                HttpMethod.Post, $"{baseUrl}/api/v1/register");
            bigPayload.Content = JsonContent.Create(new RegisterRequest(new string('x', 20_000)));
            var bigResp = await client.SendAsync(bigPayload);
            if ((int)bigResp.StatusCode == 413)
                Pass("C16 oversized body rejected");
            else
                Fail("C16 oversized body", $"{bigResp.StatusCode}");


            // Fresh server avoids prior test traffic influencing exact rate boundaries.
            var rateClock = new TestClock();
            var (rateApp, rateBase) = await StartServer(new ServerOptions {
                InvitationCode = invite, EphemeralCertificate = cert, Clock = rateClock, Port = 0 });
            try
            {
                using var rc = PinnedHttpClientFactory.CreateClient(hashHex);
                bool registrationBoundary = true;
                for (int i = 0; i < 5; i++) {
                    using var denied = await rc.PostAsJsonAsync(rateBase + "/api/v1/register", new RegisterRequest("WRONG_TEST_INVITE"));
                    registrationBoundary &= denied.StatusCode == HttpStatusCode.Unauthorized;
                }
                using var limited = await rc.PostAsJsonAsync(rateBase + "/api/v1/register", new RegisterRequest("WRONG_TEST_INVITE"));
                rateClock.Advance(TimeSpan.FromSeconds(61));
                using var recovered = await rc.PostAsJsonAsync(rateBase + "/api/v1/register", new RegisterRequest("WRONG_TEST_INVITE"));
                if (registrationBoundary && limited.StatusCode == HttpStatusCode.TooManyRequests && recovered.StatusCode == HttpStatusCode.Unauthorized)
                    Pass("C20 registration rate 5/6 and reset verified");
                else Fail("C20 registration rate", $"boundary={registrationBoundary} sixth={limited.StatusCode} reset={recovered.StatusCode}");
                var peer = await Post<RegisterResponse>(rc, rateBase, "/api/v1/register", null, new RegisterRequest(invite));
                if (peer is null) throw new Exception("Rate fixture registration failed.");
                bool deviceBoundary = true;
                for (int i = 0; i < 60; i++) {
                    using var pending = await rc.SendAsync(Req(HttpMethod.Get, rateBase, "/api/v1/sessions/pending", peer.BearerToken));
                    deviceBoundary &= pending.StatusCode == HttpStatusCode.OK;
                }
                using var opLimited = await rc.SendAsync(Req(HttpMethod.Get, rateBase, "/api/v1/sessions/pending", peer.BearerToken));
                rateClock.Advance(TimeSpan.FromSeconds(61));
                using var opRecovered = await rc.SendAsync(Req(HttpMethod.Get, rateBase, "/api/v1/sessions/pending", peer.BearerToken));
                if (deviceBoundary && opLimited.StatusCode == HttpStatusCode.TooManyRequests && opRecovered.StatusCode == HttpStatusCode.OK)
                    Pass("C21 device rate 60/61 and reset verified");
                else Fail("C21 device rate", $"boundary={deviceBoundary} sixtyFirst={opLimited.StatusCode} reset={opRecovered.StatusCode}");
            }
            finally { await rateApp.StopAsync(); await rateApp.DisposeAsync(); }

            // ── C17: Device quota ─────────────────────────────────────────────
            // Register until quota (MaxDevices=10; 3 already registered)
            var quotaOpts = new ServerOptions
            {
                InvitationCode = invite,
                EphemeralCertificate = cert,
                Clock = testClock,
                Port = 0,
                MaxDevices = 3,
                MaxSessions = 50,
                SessionTtl = TimeSpan.FromSeconds(30)
            };
            var (quotaApp, quotaBase) = await StartServer(quotaOpts);
            try
            {
                using var qClient = PinnedHttpClientFactory.CreateClient(hashHex);
                // Register up to quota
                for (int i = 0; i < 3; i++)
                    await qClient.PostAsJsonAsync(
                        $"{quotaBase}/api/v1/register", new RegisterRequest(invite));
                var overQuota = await qClient.PostAsJsonAsync(
                    $"{quotaBase}/api/v1/register", new RegisterRequest(invite));
                if (overQuota.StatusCode == HttpStatusCode.TooManyRequests)
                    Pass("C17 device quota enforced");
                else
                    Fail("C17 device quota", $"{overQuota.StatusCode}");
            }
            finally
            {
                await quotaApp.StopAsync();
                await quotaApp.DisposeAsync();
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            cert.Dispose();
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    static void TestThrows(string name, Action act)
    {
        try { act(); Fail(name, "Expected exception but none thrown"); }
        catch (Exception) { Pass(name); }
    }

    static void TestOk(string name, Action act)
    {
        try { act(); Pass(name); }
        catch (Exception ex) { Fail(name, ex.Message); }
    }

    static async Task<T?> Post<T>(HttpClient client, string baseUrl,
        string path, string? token, object body)
    {
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, baseUrl, path, token, body));
        if (!resp.IsSuccessStatusCode) return default;
        return await resp.Content.ReadFromJsonAsync<T>();
    }
}
