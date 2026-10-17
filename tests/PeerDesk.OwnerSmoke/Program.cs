using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PeerDesk.Core;
using PeerDesk.Server;

// Minimal owner-feature smoke: config strictness, state persistence, join-password fallback.
// HTTPS endpoint checks are added once BuildApp wires ServerOptions.OwnerFeature.
var root = Path.Combine(Path.GetTempPath(), "peerdesk-owner-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

try
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=owner-smoke", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    string pin = Convert.ToHexString(SHA256.HashData(cert.RawData));
    string profileJson = JsonSerializer.Serialize(new
    {
        Version = 1,
        Name = "Smoke",
        RendezvousUrl = "https://127.0.0.1:5001/",
        RelayUrl = "https://127.0.0.1:5001/",
        CertificateSha256 = pin
    });
    string statePath = Path.Combine(root, "owner-state.json");
    string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    string WriteConfig(string body)
    {
        string p = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, body);
        return p;
    }

    string valid = JsonSerializer.Serialize(new
    {
        version = 1,
        ownerSecret = secret,
        statePath,
        publicProfileJson = profileJson,
        registryIdentity = "registry.smoke"
    });
    var options = OwnerFeatureOptions.Load(WriteConfig(valid));
    Check(options.RegistryIdentity == "registry.smoke", "valid config loads");

    bool Rejects(string body)
    {
        try { OwnerFeatureOptions.Load(WriteConfig(body)); return false; }
        catch (InvalidOperationException) { return true; }
    }
    Check(Rejects(valid.Replace("\"version\":1", "\"version\":1,\"extra\":1")), "unknown field rejected");
    Check(Rejects(valid.Replace("\"version\":1", "\"version\":1,\"version\":1")), "duplicate field rejected");
    Check(Rejects(valid.Replace(secret, "short")), "short secret rejected");
    Check(Rejects(valid.Replace("registry.smoke", "bad id!")), "bad registry identity rejected");

    var clock = new TestClock();
    var feature = new OwnerFeature(options, cert, clock);
    Check(!feature.JoinPasswordConfigured, "initially unconfigured");
    Check(!await feature.VerifyJoinPasswordAsync("legacy-code", "legacy-code", CancellationToken.None), "unconfigured owner mode rejects legacy");
    Check(!await feature.VerifyJoinPasswordAsync("wrong", "legacy-code", CancellationToken.None), "legacy fallback rejects wrong");
    Check(!await feature.VerifyJoinPasswordAsync("anything", "", CancellationToken.None), "empty legacy rejects");

    File.WriteAllText(statePath, "{not json");
    bool startupFailed;
    try { _ = new OwnerFeature(options, cert, clock); startupFailed = false; }
    catch (InvalidOperationException) { startupFailed = true; }
    Check(startupFailed, "malformed state fails startup");

    File.WriteAllText(statePath, "{\"version\":1,\"displayName\":\"X\",\"unknown\":true}");
    try { _ = new OwnerFeature(options, cert, clock); startupFailed = false; }
    catch (InvalidOperationException) { startupFailed = true; }
    Check(startupFailed, "unknown state field fails startup");

    var store = new OwnerStateStore(statePath);
    byte[] salt = RandomNumberGenerator.GetBytes(16);
    byte[] hash = Rfc2898DeriveBytes.Pbkdf2("join-password-123"u8.ToArray(), salt, OwnerFeature.Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
    await store.SaveAsync(new OwnerState
    {
        DisplayName = "Host",
        JoinSaltBase64 = Convert.ToBase64String(salt),
        JoinHashBase64 = Convert.ToBase64String(hash),
        JoinIterations = OwnerFeature.Pbkdf2Iterations
    }, CancellationToken.None);
    string persisted = File.ReadAllText(statePath);
    Check(!persisted.Contains(secret, StringComparison.Ordinal) && !persisted.Contains("join-password-123", StringComparison.Ordinal), "no plaintext persisted");

    var configured = new OwnerFeature(options, cert, clock);
    Check(configured.JoinPasswordConfigured && configured.DisplayName == "Host", "configured state reloads");
    Check(await configured.VerifyJoinPasswordAsync("join-password-123", "legacy-code", CancellationToken.None), "configured password accepts");
    Check(!await configured.VerifyJoinPasswordAsync("legacy-code", "legacy-code", CancellationToken.None), "legacy disabled after configuration");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

Console.WriteLine(failures == 0 ? "OWNER SMOKE PASSED" : $"OWNER SMOKE FAILED ({failures})");
return failures == 0 ? 0 : 1;
