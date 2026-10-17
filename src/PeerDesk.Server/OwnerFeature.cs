using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PeerDesk.Core;

namespace PeerDesk.Server;

/// <summary>Thrown when join-password verification capacity is exhausted (maps to HTTP 429).</summary>
public sealed class OwnerRateLimitException : Exception
{
    public OwnerRateLimitException() : base("Join password verification is busy.") { }
}

/// <summary>
/// Optional owner feature: owner-authenticated settings, device management and
/// directory proof signing. Owner auth uses the X-PeerDesk-Owner header only.
/// </summary>
public sealed class OwnerFeature
{
    public const string OwnerHeader = "X-PeerDesk-Owner";
    public const int Pbkdf2Iterations = 100_000;
    public const int MinJoinPasswordLength = 8;
    public const int MaxJoinPasswordLength = 128;
    public const int MaxDisplayNameLength = 64;
    public const int MaxChallengeTtlSeconds = 120;
    private const int MaxBodyBytes = 32 * 1024;
    private const int MaxLegacyInvitationLength = 256;

    // Bounded per-IP owner rate limit: fixed bucket table, no unbounded dictionary.
    private const int RateBuckets = 1024;
    private const int RateLimitPerWindow = 30;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    private readonly OwnerFeatureOptions _options;
    private readonly X509Certificate2 _certificate;
    private readonly IClock _clock;
    private readonly OwnerStateStore _store;
    private readonly ServerProfile _profile;
    private readonly byte[] _ownerSecretHash;
    private readonly string _certificatePin;

    // At most two concurrent PBKDF2 computations; callers fail fast when busy.
    private readonly SemaphoreSlim _kdfGate = new(2, 2);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private volatile OwnerState _state;
    public object RegistrationCommitGate { get; } = new();
    private long _verifierGeneration;
    public long VerifierGeneration { get { lock (RegistrationCommitGate) return _verifierGeneration; } }

    private readonly object _rateLock = new();
    private readonly long[] _rateWindowStart = new long[RateBuckets];
    private readonly int[] _rateCount = new int[RateBuckets];
    private readonly byte[] _rateKey = RandomNumberGenerator.GetBytes(32);

    public OwnerFeature(OwnerFeatureOptions options, X509Certificate2 cert, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cert);
        ArgumentNullException.ThrowIfNull(clock);
        options.Validate();

        using (var rsa = cert.GetRSAPrivateKey())
        {
            if (rsa is null)
                throw new InvalidOperationException("Owner feature requires an RSA certificate with a private key.");
        }

        _options = options;
        _certificate = cert;
        _clock = clock;
        _profile = options.ParsedProfile();
        _ownerSecretHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.OwnerSecret));
        _certificatePin = Convert.ToHexString(SHA256.HashData(cert.RawData));
        _store = new OwnerStateStore(options.StatePath);
        _state = _store.Load(); // malformed state fails startup
    }

    private DirectoryFeature? _roomDirectory;
    private bool _selfPublishEnabled;

    public void ConfigureDirectory(DirectoryFeature? directory)
    {
        var aliases = _state.ServerIds ?? Array.Empty<string>();
        directory?.ValidateSelfAliases(aliases, _options.PublicProfileJson);
        if (!_options.AllowSelfPublish) return;
        if (directory is null || directory.RegistryIdentity != _options.RegistryIdentity ||
            !string.Equals(_profile.CertificateSha256, _certificatePin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Self publishing requires a matching same-process directory and current certificate pin.");
        _roomDirectory = directory;
        directory.ActivateSelfAliases(aliases, _options.PublicProfileJson);
        _selfPublishEnabled = true;
    }

    public string DisplayName => _state.DisplayName ?? _profile.Name;

    public bool JoinPasswordConfigured => _state.JoinPasswordConfigured;

    /// <summary>
    /// Verifies the configured guest join password; unconfigured owner mode fails closed.
    /// Legacy invitation is accepted only when this feature is disabled. Throws
    /// <see cref="OwnerRateLimitException"/> when verification capacity is exhausted.
    /// </summary>
    public async Task<bool> VerifyJoinPasswordAsync(string candidate, string legacyInvitation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;

        var state = _state;
        if (!state.JoinPasswordConfigured) return false;

        if (candidate.Length < MinJoinPasswordLength || candidate.Length > MaxJoinPasswordLength)
            return false;

        byte[] salt = OwnerState.DecodeExact(state.JoinSaltBase64!, OwnerState.SaltBytes)!;
        byte[] expected = OwnerState.DecodeExact(state.JoinHashBase64!, OwnerState.HashBytes)!;
        int iterations = state.JoinIterations;

        if (!_kdfGate.Wait(0))
            throw new OwnerRateLimitException();
        try
        {
            byte[] actual = await Task.Run(() => DeriveJoinHash(candidate, salt, iterations), ct).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            _kdfGate.Release();
        }
    }

    public void MapEndpoints(WebApplication app, Func<object> listDevices, Func<string, bool> revokeDevice)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(listDevices);
        ArgumentNullException.ThrowIfNull(revokeDevice);

        app.MapGet("/api/v1/owner/room", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var denied = Gate(ctx); if (denied is not null) return denied;
            lock (RegistrationCommitGate) return Results.Ok(RoomResponse(_state));
        });
        app.MapPost("/api/v1/owner/room", (Func<HttpContext, Task<IResult>>)SaveRoomAsync);

        app.MapGet("/api/v1/owner/settings", (HttpContext ctx) =>
        {
            var denied = Gate(ctx);
            if (denied is not null) return denied;
            return Results.Ok(SettingsResponse());
        });

        app.MapPost("/api/v1/owner/settings", async (HttpContext ctx) =>
        {
            var denied = Gate(ctx);
            if (denied is not null) return denied;

            var req = await ReadBodyAsync<OwnerSettingsRequest>(ctx).ConfigureAwait(false);
            if (req is null) return Results.BadRequest("Invalid request body.");
            if (req.DisplayName is null || !IsValidDisplayName(req.DisplayName))
                return Results.BadRequest("displayName must be 1..64 printable characters.");
            if (req.JoinPassword is not null && !IsValidJoinPassword(req.JoinPassword))
                return Results.BadRequest("joinPassword must be 8..128 characters.");

            OwnerState? next = null;
            if (req.JoinPassword is not null)
            {
                if (!_kdfGate.Wait(0)) return Results.StatusCode(429);
                byte[] salt = RandomNumberGenerator.GetBytes(OwnerState.SaltBytes);
                byte[] hash;
                try
                {
                    string password = req.JoinPassword;
                    hash = await Task.Run(() => DeriveJoinHash(password, salt, Pbkdf2Iterations), ctx.RequestAborted).ConfigureAwait(false);
                }
                finally
                {
                    _kdfGate.Release();
                }
                next = new OwnerState
                {
                    Version = 1,
                    DisplayName = req.DisplayName,
                    JoinSaltBase64 = Convert.ToBase64String(salt),
                    JoinHashBase64 = Convert.ToBase64String(hash),
                    JoinIterations = Pbkdf2Iterations
                };
            }

            // Persist first; only a successful atomic write activates the new state.
            if (!_saveGate.Wait(0)) return Results.StatusCode(429);
            try
            {
                // Name-only update keeps the currently active verifier.
                next ??= _state with { DisplayName = req.DisplayName };
                next = next with { PrimaryServerId = _state.PrimaryServerId, ServerIds = _state.ServerIds };
                await _store.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
                lock (RegistrationCommitGate) { _state = next; _verifierGeneration++; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Results.StatusCode(500);
            }
            finally
            {
                _saveGate.Release();
            }

            return Results.Ok(SettingsResponse());
        });

        app.MapGet("/api/v1/owner/devices", (HttpContext ctx) =>
        {
            var denied = Gate(ctx);
            if (denied is not null) return denied;
            return Results.Ok(listDevices());
        });

        app.MapPost("/api/v1/owner/devices/revoke", async (HttpContext ctx) =>
        {
            var denied = Gate(ctx);
            if (denied is not null) return denied;

            var req = await ReadBodyAsync<OwnerRevokeRequest>(ctx).ConfigureAwait(false);
            if (req is null || !IsValidDeviceId(req.DeviceId))
                return Results.BadRequest("deviceId is invalid.");
            if (!revokeDevice(req.DeviceId!))
                return Results.NotFound();
            return Results.Ok(new { revoked = true });
        });

        app.MapPost("/api/v1/owner/proof", async (HttpContext ctx) =>
        {
            var denied = Gate(ctx);
            if (denied is not null) return denied;

            var challenge = await ReadBodyAsync<DirectoryChallenge>(ctx).ConfigureAwait(false);
            if (challenge is null) return Results.BadRequest("Invalid challenge.");
            string? error = ValidateChallenge(challenge);
            if (error is not null) return Results.BadRequest(error);

            byte[] payload = DirectoryProtocol.CanonicalPayload(challenge);
            byte[] signature;
            using (var rsa = _certificate.GetRSAPrivateKey())
            {
                if (rsa is null) return Results.StatusCode(500);
                signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            return Results.Ok(new
            {
                challenge,
                certificateDerBase64 = Convert.ToBase64String(_certificate.RawData),
                signatureBase64 = Convert.ToBase64String(signature)
            });
        });
    }


    private object RoomResponse(OwnerState state) => new
    {
        serverId = state.PrimaryServerId,
        serverIds = state.ServerIds ?? Array.Empty<string>(),
        joinPasswordConfigured = state.JoinPasswordConfigured,
        selfPublishEnabled = _selfPublishEnabled,
        profileJson = _options.PublicProfileJson
    };

    private async Task<IResult> SaveRoomAsync(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var denied = Gate(ctx); if (denied is not null) return denied;
        if (!_selfPublishEnabled || _roomDirectory is null) return Results.StatusCode(403);
        var req = await ReadBodyAsync<OwnerRoomRequest>(ctx).ConfigureAwait(false);
        if (req is null || !DirectoryProtocol.TryNormalizeAlias(req.ServerId, out var alias))
            return Results.BadRequest("serverId must be 3..32 ASCII letters/digits/hyphens with alphanumeric endpoints.");
        if (req.JoinPassword is not null && !IsValidJoinPassword(req.JoinPassword))
            return Results.BadRequest("joinPassword must be 8..128 characters.");
        string? saltText = null, hashText = null;
        if (req.JoinPassword is not null)
        {
            if (!_kdfGate.Wait(0)) return Results.StatusCode(429);
            try
            {
                byte[] salt = RandomNumberGenerator.GetBytes(OwnerState.SaltBytes);
                byte[] hash = await Task.Run(() => DeriveJoinHash(req.JoinPassword, salt, Pbkdf2Iterations), ctx.RequestAborted).ConfigureAwait(false);
                saltText = Convert.ToBase64String(salt); hashText = Convert.ToBase64String(hash);
            }
            finally { _kdfGate.Release(); }
        }
        if (!_saveGate.Wait(0)) return Results.StatusCode(429);
        try
        {
            // Lock order: owner save semaphore -> registration commit gate -> directory gate.
            lock (RegistrationCommitGate)
            {
                var current = _state;
                if (req.JoinPassword is null && !current.JoinPasswordConfigured)
                    return Results.BadRequest("A new room requires a joinPassword.");
                var ids = (current.ServerIds ?? Array.Empty<string>()).ToList();
                if (!ids.Contains(alias, StringComparer.Ordinal))
                {
                    if (ids.Count >= OwnerState.MaxServerIds) return Results.Conflict();
                    ids.Add(alias);
                }
                var next = current with { PrimaryServerId = alias, ServerIds = ids.AsReadOnly() };
                if (hashText is not null)
                    next = next with { JoinSaltBase64 = saltText, JoinHashBase64 = hashText, JoinIterations = Pbkdf2Iterations };
                if (_verifierGeneration == long.MaxValue) return Results.StatusCode(503);
                if (!_roomDirectory.TryReserveSelfAlias(alias, _options.PublicProfileJson)) return Results.Conflict();
                try
                {
                    // One durable owner file commits both the IDs and the verifier; no directory file write.
                    _store.Save(next);
                    _state = next;
                    _verifierGeneration++;
                    _roomDirectory.ActivateSelfAliases(next.ServerIds!, _options.PublicProfileJson);
                    return Results.Ok(RoomResponse(next));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return Results.StatusCode(503);
                }
                finally { _roomDirectory.ReleaseSelfReservation(alias); }
            }
        }
        finally { _saveGate.Release(); }
    }
    private sealed record OwnerRoomRequest(string? ServerId, string? JoinPassword);

    // ── Validation ────────────────────────────────────────────────────────────

    private string? ValidateChallenge(DirectoryChallenge ch)
    {
        if (!string.Equals(ch.RegistryIdentity, _options.RegistryIdentity, StringComparison.Ordinal))
            return "Challenge registry identity mismatch.";
        if (ch.Alias is null || !DirectoryProtocol.IsValidAlias(ch.Alias))
            return "Challenge alias is invalid.";
        if (!IsValidToken(ch.ChallengeId) || !IsValidToken(ch.Nonce))
            return "Challenge identifiers are malformed.";

        long now = _clock.UtcNow.ToUnixTimeSeconds();
        long expires = ch.ExpiresAtUnixSeconds;
        if (expires <= now || expires - now > MaxChallengeTtlSeconds)
            return "Challenge is expired or has an invalid lifetime.";

        ServerProfile offered;
        try
        {
            offered = ServerProfile.ParseJson(ch.ProfileJson);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "Challenge profile is invalid.";
        }

        if (offered.Version != _profile.Version
            || !string.Equals(offered.Name, _profile.Name, StringComparison.Ordinal)
            || !string.Equals(offered.RendezvousUrl, _profile.RendezvousUrl, StringComparison.Ordinal)
            || !string.Equals(offered.RelayUrl, _profile.RelayUrl, StringComparison.Ordinal)
            || !string.Equals(offered.CertificateSha256, _profile.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            return "Challenge profile does not match the configured profile.";

        if (!string.Equals(offered.CertificateSha256, _certificatePin, StringComparison.OrdinalIgnoreCase))
            return "Challenge profile pin does not match the current certificate.";

        return null;
    }

    internal static bool IsValidDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxDisplayNameLength)
            return false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsControl(c))
                return false;
            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                i++;
                continue;
            }
            if (char.IsSurrogate(c))
                return false;
        }
        return true;
    }

    private static bool IsValidJoinPassword(string password)
    {
        if (password.Length < MinJoinPasswordLength || password.Length > MaxJoinPasswordLength)
            return false;
        foreach (char c in password)
        {
            if (char.IsControl(c)) return false;
        }
        return true;
    }

    private static bool IsValidDeviceId(string? id)
    {
        if (id is null || id.Length != 10) return false;
        foreach (char c in id)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    // Challenge IDs and nonces: 16..128 base64url/hex characters.
    private static bool IsValidToken(string? value)
    {
        if (value is null || value.Length < 16 || value.Length > 128) return false;
        foreach (char c in value)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) return false;
        }
        return true;
    }

    // ── Auth / rate limiting ──────────────────────────────────────────────────

    private IResult? Gate(HttpContext ctx)
    {
        if (!ctx.Request.IsHttps)
            return Results.StatusCode(403);
        if (!TryConsumeRate(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
            return Results.StatusCode(429);
        if (!IsOwner(ctx))
            return Results.Unauthorized();
        return null;
    }

    private bool IsOwner(HttpContext ctx)
    {
        var values = ctx.Request.Headers[OwnerHeader];
        if (values.Count != 1) return false;
        string? presented = values[0];
        if (string.IsNullOrEmpty(presented) || presented.Length > OwnerFeatureOptions.MaxOwnerSecretLength)
            return false;
        byte[] presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(presentedHash, _ownerSecretHash);
    }

    private bool TryConsumeRate(string ip)
    {
        byte[] mac = HMACSHA256.HashData(_rateKey, Encoding.UTF8.GetBytes(ip));
        int bucket = (int)(BitConverter.ToUInt32(mac, 0) % RateBuckets);
        long now = _clock.UtcNow.UtcTicks;
        lock (_rateLock)
        {
            if (now - _rateWindowStart[bucket] >= RateWindow.Ticks || now < _rateWindowStart[bucket])
            {
                _rateWindowStart[bucket] = now;
                _rateCount[bucket] = 0;
            }
            if (_rateCount[bucket] >= RateLimitPerWindow)
                return false;
            _rateCount[bucket]++;
            return true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private object SettingsResponse() => new
    {
        displayName = DisplayName,
        joinPasswordConfigured = JoinPasswordConfigured
    };

    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx) where T : class
    {
        if (ctx.Request.ContentLength is > MaxBodyBytes)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) return null;
        try
        {
            StrictJson.RejectDuplicateKeys(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), BodyOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[] DeriveJoinHash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, OwnerState.HashBytes);

    private static bool FixedTimeEqualsUtf8(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    private sealed record OwnerSettingsRequest(string? DisplayName, string? JoinPassword);

    private sealed record OwnerRevokeRequest(string? DeviceId);
}
