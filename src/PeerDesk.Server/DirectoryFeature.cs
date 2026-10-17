using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PeerDesk.Core;

namespace PeerDesk.Server;

internal sealed record DirectoryChallengeRequest(string Grant);

/// <summary>
/// Optional public directory: alias lookup, grant-bound challenges and owner-signed publication.
/// Never performs outbound requests; publication relies solely on the pre-approved grant binding
/// (alias, profile and certificate pin) plus a signature from the pinned certificate's key.
/// </summary>
public sealed class DirectoryFeature
{
    public const int MaxPendingChallenges = 128;
    public const int ChallengeLifetimeSeconds = 120;
    public const int MaxRequestBytes = 16_384;
    public const int MinGrantLength = 16;
    public const int MaxGrantLength = 256;

    private sealed record PendingChallenge(DirectoryChallenge Challenge, string GrantHash, byte[] Payload);

    private readonly DirectoryFeatureOptions _options;
    private readonly IClock _clock;
    private readonly DirectoryStateStore _store;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, PendingChallenge> _challenges = new(StringComparer.Ordinal);
    private DirectoryState _state;

    // Same-process owner-room overlay (guarded by _stateLock). Aliases become visible only after the
    // owner state that names them was durably saved; a reservation blocks foreign challenge/publish
    // for the alias while that save is in flight. Never persisted here; derived from owner state.
    private ImmutableHashSet<string> _selfAliases = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private string? _selfProfileJson;
    private string? _selfReservation;

    public string RegistryIdentity => _options.RegistryIdentity;

    private readonly FixedWindowLimiter _lookupLimiter;
    private readonly FixedWindowLimiter _challengeLimiter;
    private readonly FixedWindowLimiter _publishLimiter;

    public DirectoryFeature(DirectoryFeatureOptions options, IClock clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options.Validate();
        _store = new DirectoryStateStore(options);
        _state = _store.Load();
        _lookupLimiter = new FixedWindowLimiter(clock, 100, TimeSpan.FromMinutes(1));
        _challengeLimiter = new FixedWindowLimiter(clock, 10, TimeSpan.FromMinutes(1));
        _publishLimiter = new FixedWindowLimiter(clock, 10, TimeSpan.FromMinutes(1));
    }

    public void MapEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/directory/{alias}", (HttpContext ctx, string alias) =>
        {
            if (!_lookupLimiter.TryAcquire(IpKey(ctx))) return Results.StatusCode(429);
            if (!DirectoryProtocol.TryNormalizeAlias(alias, out string canonical)) return Results.NotFound();
            DirectoryStateEntry? entry;
            string? selfProfile = null;
            lock (_stateLock)
            {
                if (!_state.ByAlias.TryGetValue(canonical, out entry) && _selfAliases.Contains(canonical))
                    selfProfile = _selfProfileJson;
            }
            if (entry is not null)
                return Results.Json(new { alias = entry.Alias, profileJson = entry.ProfileJson, version = 1 });
            if (selfProfile is not null)
                return Results.Json(new { alias = canonical, profileJson = selfProfile, version = 1 });
            return Results.NotFound();
        });

        app.MapPost("/api/v1/directory/challenge", async (HttpContext ctx) =>
        {
            if (!_challengeLimiter.TryAcquire(IpKey(ctx))) return Results.StatusCode(429);
            var req = await ReadStrictJsonAsync<DirectoryChallengeRequest>(ctx);
            if (req is null || req.Grant is null || req.Grant.Length < MinGrantLength || req.Grant.Length > MaxGrantLength)
                return Results.BadRequest("Invalid request.");
            var challenge = IssueChallenge(req.Grant, out int status);
            return challenge is null ? Results.StatusCode(status) : Results.Json(challenge);
        });

        app.MapPost("/api/v1/directory/publish", async (HttpContext ctx) =>
        {
            if (!_publishLimiter.TryAcquire(IpKey(ctx))) return Results.StatusCode(429);
            var proof = await ReadStrictJsonAsync<DirectoryProof>(ctx);
            if (proof is null) return Results.BadRequest("Invalid request.");
            var entry = Publish(proof, out int status);
            return entry is null
                ? Results.StatusCode(status)
                : Results.Json(new { alias = entry.Alias, profileJson = entry.ProfileJson, version = 1 });
        });
    }

    /// <summary>Returns a fresh challenge for an approved, unconsumed, unexpired grant; null with an HTTP status otherwise.</summary>
    public DirectoryChallenge? IssueChallenge(string grant, out int status)
    {
        status = 403;
        if (grant is null || grant.Length < MinGrantLength || grant.Length > MaxGrantLength) { status = 400; return null; }

        byte[] candidate = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(grant));
        DirectoryGrantOptions? match = null;
        foreach (var g in _options.Grants)
        {
            // Compare every configured hash in fixed time; no early exit on the secret-derived value.
            if (CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(g.GrantHashSha256)))
                match = g;
        }
        if (match is null) return null;

        long now = _clock.UtcNow.ToUnixTimeSeconds();
        if (match.ExpiresAtUnixSeconds != 0 && now >= match.ExpiresAtUnixSeconds) return null;

        lock (_stateLock)
        {
            if (_state.ConsumedGrantHashes.Contains(match.GrantHashSha256) || _state.ByAlias.ContainsKey(match.Alias)
                || IsSelfClaimed(match.Alias))
            {
                status = 409;
                return null;
            }
            PruneExpiredChallenges(now);
            if (_challenges.Count >= MaxPendingChallenges)
            {
                status = 503;
                return null;
            }

            long expires = now + ChallengeLifetimeSeconds;
            if (match.ExpiresAtUnixSeconds != 0 && match.ExpiresAtUnixSeconds < expires)
                expires = match.ExpiresAtUnixSeconds;
            var challenge = new DirectoryChallenge(
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                Base64Url(RandomNumberGenerator.GetBytes(32)),
                match.Alias,
                _options.RegistryIdentity,
                expires,
                match.ProfileJson);
            _challenges[challenge.ChallengeId] = new PendingChallenge(challenge, match.GrantHashSha256, DirectoryProtocol.CanonicalPayload(challenge));
            status = 200;
            return challenge;
        }
    }

    /// <summary>Verifies and commits a proof. Challenge is single-use (consumed on any attempt); grant is consumed only on durable commit.</summary>
    public DirectoryStateEntry? Publish(DirectoryProof proof, out int status)
    {
        status = 400;
        if (proof?.Challenge is null || proof.CertificateDerBase64 is null || proof.SignatureBase64 is null) return null;
        if (proof.Challenge.ChallengeId is null || proof.Challenge.ChallengeId.Length != 32) return null;

        byte[]? der = DecodeBase64Bounded(proof.CertificateDerBase64, DirectoryProtocol.MaxCertificateDerBytes);
        byte[]? signature = DecodeBase64Bounded(proof.SignatureBase64, 1024);
        if (der is null || der.Length == 0 || signature is null || signature.Length == 0) return null;

        long now = _clock.UtcNow.ToUnixTimeSeconds();
        lock (_stateLock)
        {
            PruneExpiredChallenges(now);
            if (!_challenges.Remove(proof.Challenge.ChallengeId, out var pending)) { status = 403; return null; }

            status = 403;
            if (pending.Challenge != proof.Challenge) return null;
            byte[] payload;
            try { payload = DirectoryProtocol.CanonicalPayload(proof.Challenge); }
            catch (ArgumentException) { return null; }
            if (!CryptographicOperations.FixedTimeEquals(payload, pending.Payload)) return null;
            if (now >= pending.Challenge.ExpiresAtUnixSeconds) return null;
            if (!string.Equals(pending.Challenge.RegistryIdentity, _options.RegistryIdentity, StringComparison.Ordinal)) return null;

            var grant = _options.Grants.FirstOrDefault(g => string.Equals(g.GrantHashSha256, pending.GrantHash, StringComparison.Ordinal));
            if (grant is null ||
                !string.Equals(grant.Alias, pending.Challenge.Alias, StringComparison.Ordinal) ||
                !string.Equals(grant.ProfileJson, pending.Challenge.ProfileJson, StringComparison.Ordinal))
                return null;
            if (grant.ExpiresAtUnixSeconds != 0 && now >= grant.ExpiresAtUnixSeconds) return null;
            if (_state.ConsumedGrantHashes.Contains(grant.GrantHashSha256) || _state.ByAlias.ContainsKey(grant.Alias)
                || IsSelfClaimed(grant.Alias))
            {
                status = 409;
                return null;
            }

            ServerProfile profile;
            try { profile = ServerProfile.ParseJson(grant.ProfileJson); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
            if (!VerifyCertificateAndSignature(der, signature, profile.CertificateSha256, payload)) return null;

            var entry = new DirectoryStateEntry(grant.Alias, grant.ProfileJson, grant.GrantHashSha256, now);
            var next = _state.With(entry);
            try
            {
                _store.Save(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                status = 503;
                return null; // memory state unchanged
            }
            _state = next;
            status = 200;
            return entry;
        }
    }

    // ── Same-process owner-room coordination (lock order: owner gates -> _stateLock) ──

    // Must be called under _stateLock.
    private bool IsSelfClaimed(string alias) =>
        _selfAliases.Contains(alias) || string.Equals(_selfReservation, alias, StringComparison.Ordinal);

    // Must be called under _stateLock. A stored entry or configured grant for the alias is acceptable
    // only when it is bound to exactly the same fixed profile JSON; anything else is foreign.
    private bool IsAliasBindableToSelf(string alias, string profileJson)
    {
        if (_state.ByAlias.TryGetValue(alias, out var stored)
            && !string.Equals(stored.ProfileJson, profileJson, StringComparison.Ordinal))
            return false;
        foreach (var g in _options.Grants)
        {
            if (string.Equals(g.Alias, alias, StringComparison.Ordinal)
                && !string.Equals(g.ProfileJson, profileJson, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>Startup cross-check: throws when any persisted owner alias collides with a foreign stored entry or grant.</summary>
    internal void ValidateSelfAliases(IReadOnlyList<string> aliases, string profileJson)
    {
        lock (_stateLock)
        {
            foreach (var alias in aliases)
            {
                if (!IsAliasBindableToSelf(alias, profileJson))
                    throw new InvalidOperationException("Owner room server ID collides with a foreign directory binding.");
            }
        }
    }

    /// <summary>Reserves a new owner alias before the owner state write; fails on foreign binding or in-flight reservation.</summary>
    internal bool TryReserveSelfAlias(string alias, string profileJson)
    {
        lock (_stateLock)
        {
            PruneExpiredChallenges(_clock.UtcNow.ToUnixTimeSeconds());
            if (_selfReservation is not null) return false;
            if (_selfProfileJson is not null && !string.Equals(_selfProfileJson, profileJson, StringComparison.Ordinal)) return false;
            if (!IsAliasBindableToSelf(alias, profileJson)) return false;
            // A pending challenge (necessarily same-profile, given the binding check) still must not race the owner.
            foreach (var pending in _challenges.Values)
            {
                if (string.Equals(pending.Challenge.Alias, alias, StringComparison.Ordinal)) return false;
            }
            _selfReservation = alias;
            return true;
        }
    }

    internal void ReleaseSelfReservation(string alias)
    {
        lock (_stateLock)
        {
            if (string.Equals(_selfReservation, alias, StringComparison.Ordinal)) _selfReservation = null;
        }
    }

    /// <summary>Activates the durably saved owner alias set (replaces the overlay) and clears any reservation.</summary>
    internal void ActivateSelfAliases(IReadOnlyList<string> aliases, string profileJson)
    {
        lock (_stateLock)
        {
            _selfAliases = aliases.ToImmutableHashSet(StringComparer.Ordinal);
            _selfProfileJson = profileJson;
            _selfReservation = null;
        }
    }

    private static bool VerifyCertificateAndSignature(byte[] der, byte[] signature, string pinHex, byte[] payload)
    {
        byte[] pin;
        try { pin = Convert.FromHexString(pinHex); }
        catch (FormatException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(der), pin)) return false;

        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(der);
            using RSA? rsa = cert.GetRSAPublicKey();
            if (rsa is null || rsa.KeySize < 2048) return false;
            if (signature.Length != rsa.KeySize / 8) return false;
            return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private void PruneExpiredChallenges(long now)
    {
        List<string>? expired = null;
        foreach (var (id, pending) in _challenges)
        {
            if (now >= pending.Challenge.ExpiresAtUnixSeconds) (expired ??= []).Add(id);
        }
        if (expired is null) return;
        foreach (var id in expired) _challenges.Remove(id);
    }

    private static byte[]? DecodeBase64Bounded(string value, int maxBytes)
    {
        if (value.Length == 0 || value.Length > ((maxBytes + 2) / 3) * 4) return null;
        byte[] buffer = new byte[maxBytes];
        return Convert.TryFromBase64String(value, buffer, out int written) ? buffer.AsSpan(0, written).ToArray() : null;
    }

    private static async Task<T?> ReadStrictJsonAsync<T>(HttpContext ctx) where T : class
    {
        if (ctx.Request.ContentLength is > MaxRequestBytes) return null;
        if (ctx.Request.ContentType is null ||
            !ctx.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var ms = new MemoryStream();
            byte[] buffer = new byte[4096];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                if (ms.Length + read > MaxRequestBytes) return null;
                ms.Write(buffer, 0, read);
            }
            if (ms.Length == 0) return null;
            return JsonSerializer.Deserialize<T>(ms.ToArray(), DirectoryFeatureOptions.StrictJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or IOException
                                       or BadHttpRequestException)
        {
            return null;
        }
    }

    private static string IpKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Per-key fixed window limiter with a bounded key table; when full, unknown keys are rejected.</summary>
    private sealed class FixedWindowLimiter
    {
        private const int MaxKeys = 1024;
        private readonly IClock _clock;
        private readonly int _limit;
        private readonly TimeSpan _window;
        private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _entries = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public FixedWindowLimiter(IClock clock, int limit, TimeSpan window)
        {
            _clock = clock;
            _limit = limit;
            _window = window;
        }

        public bool TryAcquire(string key)
        {
            var now = _clock.UtcNow;
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var e) && now - e.Start < _window)
                {
                    if (e.Count >= _limit) return false;
                    _entries[key] = (e.Start, e.Count + 1);
                    return true;
                }
                if (!_entries.ContainsKey(key) && _entries.Count >= MaxKeys)
                {
                    List<string> stale = [];
                    foreach (var (k, v) in _entries)
                        if (now - v.Start >= _window) stale.Add(k);
                    foreach (var k in stale) _entries.Remove(k);
                    if (_entries.Count >= MaxKeys) return false;
                }
                _entries[key] = (now, 1);
                return true;
            }
        }
    }
}
