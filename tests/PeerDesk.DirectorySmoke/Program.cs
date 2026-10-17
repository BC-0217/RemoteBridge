using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PeerDesk.Core;
using PeerDesk.Server;

int failures = 0;
void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    if (!condition) failures++;
}
void Throws(Action action, string name)
{
    try { action(); Check(false, name); }
    catch (InvalidOperationException) { Check(true, name); }
}

string root = Path.Combine(Path.GetTempPath(), "peerdesk-directory-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    // Protocol
    Check(DirectoryProtocol.IsValidAlias("abc") && DirectoryProtocol.IsValidAlias("my-host-01"), "alias valid");
    Check(!DirectoryProtocol.IsValidAlias("ab") && !DirectoryProtocol.IsValidAlias("ABC") &&
          !DirectoryProtocol.IsValidAlias("-abc") && !DirectoryProtocol.IsValidAlias(new string('a', 33)) &&
          !DirectoryProtocol.IsValidAlias("ab c") && !DirectoryProtocol.IsValidAlias(null), "alias invalid");
    var c1 = new DirectoryChallenge("id", "ab", "c", "reg", 5, "{}");
    var c2 = new DirectoryChallenge("id", "a", "bc", "reg", 5, "{}");
    Check(!DirectoryProtocol.CanonicalPayload(c1).AsSpan().SequenceEqual(DirectoryProtocol.CanonicalPayload(c2)), "canonical length-prefixed unambiguous");
    Check(DirectoryProtocol.CanonicalPayload(c1).AsSpan().SequenceEqual(DirectoryProtocol.CanonicalPayload(c1 with { })), "canonical deterministic");

    // Owner key/certificate (test-only, in memory)
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=peerdesk-smoke", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    string pin = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
    string profileJson = $$"""{"Version":1,"Name":"Smoke","RendezvousUrl":"https://host.example/","RelayUrl":"https://host.example/","CertificateSha256":"{{pin}}"}""";

    string grant = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    string grantHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(grant)));
    string statePath = Path.Combine(root, "state", "directory-state.json");
    string configJson = JsonSerializer.Serialize(new
    {
        version = 1,
        registryIdentity = "registry.smoke",
        statePath,
        grants = new[] { new { grantHashSha256 = grantHash, alias = "smoke-host", profileJson, expiresAtUnixSeconds = 0L } }
    });
    string configPath = Path.Combine(root, "directory.json");
    File.WriteAllText(configPath, configJson);

    // Options strictness
    var options = DirectoryFeatureOptions.Load(configPath);
    Check(options.Grants.Count == 1 && !File.ReadAllText(configPath).Contains(grant), "config loads, no plaintext grant");
    Throws(() => DirectoryFeatureOptions.Parse(Encoding.UTF8.GetBytes(configJson.Replace("\"version\":1", "\"version\":1,\"extra\":1"))), "config unknown field rejected");
    Throws(() => DirectoryFeatureOptions.Parse(Encoding.UTF8.GetBytes(configJson.Replace("\"version\":1", "\"version\":1,\"version\":1"))), "config duplicate field rejected");
    Throws(() => DirectoryFeatureOptions.Parse(Encoding.UTF8.GetBytes(configJson.Replace("\"version\":1", "\"Version\":1"))), "config case-sensitive");
    Throws(() => DirectoryFeatureOptions.Parse(Encoding.UTF8.GetBytes(configJson.Replace(grantHash, grantHash.ToUpperInvariant()))), "config uppercase hash rejected");
    Throws(() => DirectoryFeatureOptions.Parse(Encoding.UTF8.GetBytes(configJson.Replace("smoke-host", "Smoke"))), "config bad alias rejected");

    var clock = new TestClock { CurrentTime = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000) };
    var feature = new DirectoryFeature(options, clock);

    Check(feature.IssueChallenge("wrong-grant-value-123", out int st) is null && st == 403, "wrong grant rejected");
    var challenge = feature.IssueChallenge(grant, out st);
    Check(challenge is not null && st == 200 && challenge.Alias == "smoke-host" && challenge.ProfileJson == profileJson &&
          challenge.ExpiresAtUnixSeconds - clock.UtcNow.ToUnixTimeSeconds() <= 120, "challenge issued");
    string Json(object o) => JsonSerializer.Serialize(o);
    Check(!Json(challenge!).Contains(grant) && !Json(challenge!).Contains(grantHash), "challenge leaks no grant");

    DirectoryProof Sign(DirectoryChallenge ch, RSA key, byte[] der) => new(
        ch, Convert.ToBase64String(der),
        Convert.ToBase64String(key.SignData(DirectoryProtocol.CanonicalPayload(ch), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));

    // Tampered challenge -> rejected and consumed
    var tampered = Sign(challenge! with { Alias = "other-host" }, rsa, cert.RawData);
    Check(feature.Publish(tampered, out st) is null && st == 403, "tampered challenge rejected");
    Check(feature.Publish(Sign(challenge!, rsa, cert.RawData), out st) is null && st == 403, "challenge single-use after failed attempt");

    // Wrong key with the pinned cert -> rejected
    using var otherRsa = RSA.Create(2048);
    var ch2 = feature.IssueChallenge(grant, out _)!;
    Check(feature.Publish(Sign(ch2, otherRsa, cert.RawData), out st) is null && st == 403, "wrong key signature rejected");

    // Unpinned cert -> rejected
    using var otherCert = new CertificateRequest("CN=other", otherRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    var ch3 = feature.IssueChallenge(grant, out _)!;
    Check(feature.Publish(Sign(ch3, otherRsa, otherCert.RawData), out st) is null && st == 403, "unpinned certificate rejected");

    // Expired challenge -> rejected
    var ch4 = feature.IssueChallenge(grant, out _)!;
    clock.Advance(TimeSpan.FromSeconds(121));
    Check(feature.Publish(Sign(ch4, rsa, cert.RawData), out st) is null && st == 403, "expired challenge rejected");
    Check(!File.Exists(statePath), "no state written before success");

    // Valid publish
    var ch5 = feature.IssueChallenge(grant, out _)!;
    var entry = feature.Publish(Sign(ch5, rsa, cert.RawData), out st);
    Check(entry is not null && st == 200 && entry.Alias == "smoke-host" && entry.ProfileJson == profileJson, "valid proof published");
    Check(File.Exists(statePath) && !File.ReadAllText(statePath).Contains(grant), "state persisted without plaintext grant");
    Check(feature.IssueChallenge(grant, out st) is null && st == 409, "consumed grant cannot re-challenge");

    // Restart: persisted state reloads; grant stays consumed
    var restarted = new DirectoryFeature(DirectoryFeatureOptions.Load(configPath), clock);
    Check(restarted.IssueChallenge(grant, out st) is null && st == 409, "consumed grant persists across restart");

    // Malformed / inconsistent state fails closed
    string goodState = File.ReadAllText(statePath);
    File.WriteAllText(statePath, goodState.Replace("\"version\":1", "\"version\":1,\"x\":1"));
    Throws(() => new DirectoryFeature(DirectoryFeatureOptions.Load(configPath), clock), "malformed state fails startup");
    File.WriteAllText(statePath, goodState.Replace("smoke-host", "evil-host"));
    Throws(() => new DirectoryFeature(DirectoryFeatureOptions.Load(configPath), clock), "inconsistent alias fails startup");
    File.WriteAllText(statePath, goodState.Replace("registry.smoke", "registry.other"));
    Throws(() => new DirectoryFeature(DirectoryFeatureOptions.Load(configPath), clock), "registry identity mismatch fails startup");
    File.WriteAllText(statePath, goodState);
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

Console.WriteLine(failures == 0 ? "DirectorySmoke: all checks passed" : $"DirectorySmoke: {failures} failure(s)");
return failures == 0 ? 0 : 1;
