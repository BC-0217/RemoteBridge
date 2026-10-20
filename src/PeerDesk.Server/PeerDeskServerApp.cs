using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeerDesk.Core;

namespace PeerDesk.Server;

public sealed class DeviceRegistration
{
    public string DeviceId { get; init; } = string.Empty;
    // SHA-256 of the UTF-8 bearer token (32 bytes). The raw bearer is never retained.
    public byte[] TokenHash { get; init; } = Array.Empty<byte>();
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastRegistrationAttempt { get; set; }
    public int RegistrationAttempts { get; set; }
    // Password credential; required, initialized at registration. Access under stateLock.
    public PasswordCredential PasswordCredential { get; init; } = new();
    // Fixed password verifier (immutable instance, replaced under stateLock after durable commit).
    public FixedPasswordCredential FixedPassword { get; set; } = FixedPasswordCredential.None;
    // In-memory access runtime capability (never persisted). Access under stateLock.
    public string? RuntimeInstanceNonce { get; set; }
    public string? RuntimeEpoch { get; set; }
    public DateTimeOffset RuntimeExpiresAt { get; set; }
    // Bounded blank (manual) request timestamps for this target. Access under stateLock.
    public List<DateTimeOffset> BlankRequestTimes { get; } = new();

    public string? ActiveRuntimeEpoch(DateTimeOffset now) =>
        RuntimeEpoch is not null && now < RuntimeExpiresAt ? RuntimeEpoch : null;
}

public sealed class SessionState
{
    public MediaState Media { get; } = new();
    public string SessionId { get; init; } = string.Empty;
    public string InitiatorDeviceId { get; init; } = string.Empty;
    public string TargetDeviceId { get; init; } = string.Empty;
    // Status transitions are guarded by stateLock.
    public string Status { get; set; } = SessionStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastMediaActivityAt { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }
    // Server-generated authorization metadata (never request supplied). Guarded by stateLock.
    public string AuthorizationKind { get; init; } = AuthorizationKinds.Manual;
    public long AuthorizationRevision { get; init; }
    public string? RuntimeEpoch { get; set; }
    public bool AutoApprovalEligible { get; set; }
    // Set when approved through decision mode "password"; Connect rechecks proof/revision/epoch.
    public bool AutoApproved { get; set; }
}

public static class SessionStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Connected = "Connected";
    public const string Rejected = "Rejected";
    public const string Revoked = "Revoked";
    public const string Expired = "Expired";

    // Terminal states may not be overwritten.
    public static bool IsTerminal(string status) =>
        status is Rejected or Revoked or Expired;
}

public static class PeerDeskServerApp
{
    // Maximum request body size: 16 KB
    private const int MaxRequestBodyBytes = 16_384;
    // Bounded field lengths
    private const int MaxDeviceIdLength = 128;
    private const int MaxInvitationCodeLength = 256;
    private const int MaxSessionIdLength = 64;
    // Registration rate: max 5 attempts per IP per 60 s
    private const int RegRateMaxPerWindow = 5;
    private static readonly TimeSpan RegRateWindow = TimeSpan.FromSeconds(60);
    // Per-device operation rate: max 60 ops per 60 s
    private const int DevRateMaxPerWindow = 60;
    private static readonly TimeSpan DevRateWindow = TimeSpan.FromSeconds(60);
    // Blank (manual) session requests: max 10 per target per 60 s.
    private const int BlankRateMaxPerWindow = 10;
    private static readonly TimeSpan BlankRateWindow = TimeSpan.FromSeconds(60);
    // Access runtime capability lifetime.
    private static readonly TimeSpan RuntimeTtl = TimeSpan.FromSeconds(60);
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static WebApplication BuildApp(string[] args, ServerOptions? options = null)
    {
        options ??= ServerOptions.FromEnvironment();
        options.Validate();

        // Resolve invitation code: explicit option wins; otherwise require env var in production.
        if (string.IsNullOrEmpty(options.InvitationCode))
        {
            string envInvite = Environment.GetEnvironmentVariable("PEERDESK_INVITATION") ?? string.Empty;
            if (envInvite.Length < 16)
                throw new InvalidOperationException(
                    "PEERDESK_INVITATION environment variable must be set to at least 16 characters.");
            options.InvitationCode = envInvite;
        }

        var builder = WebApplication.CreateBuilder(args);

        // Suppress developer exception page so error details are not leaked.
        builder.WebHost.UseSetting("ASPNETCORE_DETAILEDERRORS", "false");

        // Resolve TLS certificate.
        X509Certificate2 cert;
        if (options.EphemeralCertificate != null)
        {
            cert = options.EphemeralCertificate;
        }
        else
        {
            string certPath = Environment.GetEnvironmentVariable("PEERDESK_CERT_PATH") ?? string.Empty;
            if (string.IsNullOrEmpty(certPath) || !File.Exists(certPath))
                throw new InvalidOperationException(
                    "HTTPS Certificate not configured and no ephemeral certificate provided.");
            string certPass = Environment.GetEnvironmentVariable("PEERDESK_CERT_PASS") ?? string.Empty;
            cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPass);
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(options.BindAddress, options.Port, listenOptions =>
            {
                listenOptions.UseHttps(cert);
            });
            // Enforce 16 KB request body limit at the Kestrel layer.
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        });

        // Suppress detailed error bodies.
        builder.Services.AddProblemDetails();

        // Persisted device registry: loaded and floor-bumped durably before the app is built/listens.
        var devices = new ConcurrentDictionary<string, DeviceRegistration>(StringComparer.Ordinal);
        var stateStore = options.DeviceStatePath is null ? null : new DeviceRegistryStateStore(options.DeviceStatePath, options.MaxDevices);
        if (stateStore is not null)
        {
            var restored = new List<DeviceRegistration>();
            foreach (var entry in stateStore.LoadRequired())
            {
                restored.Add(new DeviceRegistration
                {
                    DeviceId = entry.DeviceId,
                    TokenHash = Convert.FromHexString(entry.TokenHashSha256),
                    RegisteredAt = entry.RegisteredAt,
                    // Expired password, RotationRequired, generation = checked(floor + 1).
                    PasswordCredential = PasswordCredential.Restore(entry.PasswordGenerationFloor),
                    FixedPassword = new FixedPasswordCredential(entry.FixedPasswordRevision,
                        entry.FixedPasswordSaltHex is null ? null : Convert.FromHexString(entry.FixedPasswordSaltHex),
                        entry.FixedPasswordHashHex is null ? null : Convert.FromHexString(entry.FixedPasswordHashHex),
                        entry.FixedPasswordIterations)
                });
            }
            // Startup save failure propagates and aborts before listening.
            stateStore.Save(restored.Select(ToPersisted).OrderBy(e => e.DeviceId, StringComparer.Ordinal).ToList());
            foreach (var reg in restored)
            {
                if (!devices.TryAdd(reg.DeviceId, reg))
                    throw new InvalidOperationException("Duplicate device in restored state.");
            }
        }

        var app = builder.Build();
        // Owner revoke callback signals a failed durable commit by exception; map it to 503.
        app.Use(async (ctx, next) =>
        {
            try { await next(ctx); }
            catch (DeviceStatePersistenceException) when (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        });
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        // State
        var sessions = new ConcurrentDictionary<string, SessionState>(StringComparer.Ordinal);
        // Per-device operation timestamps (for rate limiting).
        var deviceOpTimes = new ConcurrentDictionary<string, List<DateTimeOffset>>(StringComparer.Ordinal);
        // Per-IP registration attempt timestamps.
        var regIpTimes = new ConcurrentDictionary<string, List<DateTimeOffset>>(StringComparer.Ordinal);
        // Single lock for all state mutations.
        var stateLock = new object();
        var peerPresence = new PeerPresenceState(5000, options.MaxDevices);
        var ownerFeature = options.OwnerFeature is null ? null : new OwnerFeature(options.OwnerFeature, cert, options.Clock);
        var directoryFeature = options.DirectoryFeature is null ? null : new DirectoryFeature(options.DirectoryFeature, options.Clock);
        ownerFeature?.ConfigureDirectory(directoryFeature);
        ownerFeature?.MapEndpoints(app,
            () => { lock (stateLock) { return new { devices = devices.Values.Select(d => new { deviceId = d.DeviceId, registeredAt = d.RegisteredAt }).ToArray() }; } },
            id => { lock (stateLock) {
                if (!devices.ContainsKey(id)) return false;
                // Durable commit of the snapshot without the device precedes any memory change.
                if (!TryPersist(Snapshot(d => d.DeviceId == id ? null : ToPersisted(d), null), out var persistError))
                    throw new DeviceStatePersistenceException("Device revoke could not be persisted.", persistError!);
                if (!devices.TryRemove(id, out _)) return false;
                foreach (var session in sessions.Values.Where(x => x.InitiatorDeviceId == id || x.TargetDeviceId == id)) {
                    if (!SessionStatus.IsTerminal(session.Status)) { session.Status = SessionStatus.Revoked; session.StatusChangedAt = options.Clock.UtcNow; }
                    session.Media.Stop();
                }
                deviceOpTimes.TryRemove(id, out _);
                return true;
            } });
        directoryFeature?.MapEndpoints(app);

        // ── Helpers ────────────────────────────────────────────────────────────

        bool AuthenticateDevice(HttpContext ctx, out string authenticatedDeviceId)
        {
            authenticatedDeviceId = string.Empty;
            if (!ctx.Request.Headers.TryGetValue("Authorization", out var authHeader))
                return false;
            string raw = authHeader.ToString();
            if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return false;
            string token = raw["Bearer ".Length..].Trim();
            if (token.Length != 44 || token.Any(c => c > 127)) return false;
            Span<byte> decoded = stackalloc byte[32];
            if (!Convert.TryFromBase64String(token, decoded, out int decodedLength) || decodedLength != 32 ||
                Convert.ToBase64String(decoded) != token) return false;

            // Compare SHA-256(presented bearer) against stored 32-byte hashes in constant time.
            byte[] presentedHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

            foreach (var d in devices.Values)
            {
                if (d.TokenHash.Length == presentedHash.Length &&
                    CryptographicOperations.FixedTimeEquals(d.TokenHash, presentedHash))
                {
                    authenticatedDeviceId = d.DeviceId;
                    return true;
                }
            }
            return false;
        }

        // Builds the full next registry snapshot. map returns null to drop a device.
        // Must be called under stateLock.
        List<PersistedDevice> Snapshot(Func<DeviceRegistration, PersistedDevice?> map, PersistedDevice? extra)
        {
            var list = new List<PersistedDevice>(devices.Count + 1);
            foreach (var d in devices.Values)
            {
                var mapped = map(d);
                if (mapped is not null) list.Add(mapped);
            }
            if (extra is not null) list.Add(extra);
            list.Sort((a, b) => string.CompareOrdinal(a.DeviceId, b.DeviceId));
            return list;
        }

        // Durably commits a snapshot; always true when persistence is disabled.
        // Must be called under stateLock (the single device mutation gate).
        bool TryPersist(List<PersistedDevice> snapshot, out Exception? error)
        {
            error = null;
            if (stateStore is null) return true;
            try
            {
                stateStore.Save(snapshot);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                error = ex;
                return false;
            }
        }

        PersistedDevice WithGeneration(DeviceRegistration d, string id, long generation) =>
            d.DeviceId == id ? ToPersisted(d) with { PasswordGenerationFloor = generation } : ToPersisted(d);

        // Returns false if the device has exceeded its per-minute operation quota.
        bool DeviceRateGuard(string deviceId)
        {
            var now = options.Clock.UtcNow;
            var times = deviceOpTimes.GetOrAdd(deviceId, _ => new List<DateTimeOffset>());
            lock (times)
            {
                times.RemoveAll(t => now - t > DevRateWindow);
                if (times.Count >= DevRateMaxPerWindow) return false;
                times.Add(now);
                return true;
            }
        }

        // Returns false if the source IP has exceeded the registration rate.
        bool RegistrationRateGuard(string ipKey)
        {
            var now = options.Clock.UtcNow;
            var times = regIpTimes.GetOrAdd(ipKey, _ => new List<DateTimeOffset>());
            lock (times)
            {
                times.RemoveAll(t => now - t > RegRateWindow);
                if (times.Count >= RegRateMaxPerWindow) return false;
                times.Add(now);
                return true;
            }
        }

        // Must be called under stateLock.
        // Marks Pending/Approved/Connected sessions as Expired when TTL elapsed.
        // Prunes terminal sessions when quota would be exceeded, oldest first,
        // but never reuses a session ID.
        void CleanExpiredSessions()
        {
            var now = options.Clock.UtcNow;
            foreach (var s in sessions.Values)
            {
                if (!SessionStatus.IsTerminal(s.Status) &&
                    s.Status != SessionStatus.Connected && // Connected sessions also expire
                    s.Status != SessionStatus.Rejected)
                {
                    // Pending and Approved expire after TTL
                    if ((now - s.CreatedAt) >= options.SessionTtl)
                    {
                        s.Status = SessionStatus.Expired;
                        s.Media.Stop();
                    }
                }
                else if (s.Status == SessionStatus.Connected)
                {
                    if ((now - (s.LastMediaActivityAt ?? s.CreatedAt)) >= options.SessionTtl)
                    {
                        s.Status = SessionStatus.Expired;
                        s.Media.Stop();
                    }
                }
            }
        }

        // Prune oldest terminal sessions to stay under quota.
        // Call under stateLock after CleanExpiredSessions.
        void PruneTerminalSessions()
        {
            if (sessions.Count < options.MaxSessions) return;
            // Collect terminal sessions sorted by creation time (oldest first).
            var terminal = sessions.Values
                .Where(s => SessionStatus.IsTerminal(s.Status))
                .OrderBy(s => s.CreatedAt)
                .ToList();
            foreach (var s in terminal)
            {
                if (sessions.Count < options.MaxSessions) break;
                s.Media.Stop();
                sessions.TryRemove(s.SessionId, out _);
            }
        }

        SessionResponse ToResponse(SessionState s) =>
            new(s.SessionId, s.InitiatorDeviceId, s.TargetDeviceId, s.Status, s.CreatedAt,
                s.AuthorizationKind, s.AuthorizationRevision, s.RuntimeEpoch, s.AutoApprovalEligible);

        // Creates a new Pending session. passwordVerified binds the target's active runtime epoch
        // (if any) so the target may one-shot auto approve. Must be called under stateLock.
        IResult CreateSession(string initiatorId, DeviceRegistration target, string kind, long revision, bool passwordVerified)
        {
            if (sessions.Count >= options.MaxSessions)
                return Results.StatusCode(429);
            var now = options.Clock.UtcNow;
            string? epoch = passwordVerified ? target.ActiveRuntimeEpoch(now) : null;
            string sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var session = new SessionState
            {
                SessionId = sessionId,
                InitiatorDeviceId = initiatorId,
                TargetDeviceId = target.DeviceId,
                Status = SessionStatus.Pending,
                CreatedAt = now,
                AuthorizationKind = kind,
                AuthorizationRevision = revision,
                RuntimeEpoch = epoch,
                AutoApprovalEligible = epoch is not null
            };
            sessions[sessionId] = session;
            return Results.Ok(ToResponse(session));
        }

        // True while a password-authorized session's credential revision and runtime epoch are
        // still current on the target. Must be called under stateLock.
        bool PasswordAuthorizationCurrent(SessionState s, DeviceRegistration target, DateTimeOffset now)
        {
            if (!s.AutoApprovalEligible || s.RuntimeEpoch is null) return false;
            string? active = target.ActiveRuntimeEpoch(now);
            if (active is null || !string.Equals(active, s.RuntimeEpoch, StringComparison.Ordinal)) return false;
            return s.AuthorizationKind switch
            {
                AuthorizationKinds.TemporaryPassword => !target.PasswordCredential.RotationRequired && now < target.PasswordCredential.ExpiresAt &&
                    target.PasswordCredential.Generation == s.AuthorizationRevision,
                AuthorizationKinds.FixedPassword => target.FixedPassword.Configured &&
                    target.FixedPassword.Revision == s.AuthorizationRevision,
                _ => false
            };
        }

        // Revokes fixed-authorized Pending/Approved inbound sessions; Connected ones are kept.
        // Must be called under stateLock.
        void InvalidateFixedSessions(string targetId)
        {
            var now = options.Clock.UtcNow;
            foreach (var s in sessions.Values)
            {
                if (s.TargetDeviceId == targetId && s.AuthorizationKind == AuthorizationKinds.FixedPassword &&
                    (s.Status == SessionStatus.Pending || s.Status == SessionStatus.Approved))
                {
                    s.Status = SessionStatus.Revoked;
                    s.StatusChangedAt = now;
                    s.Media.Stop();
                }
            }
        }

        // Drops auto approval eligibility from inbound Pending sessions (manual fallback).
        // Must be called under stateLock.
        void DowngradeAutoPending(string targetId)
        {
            foreach (var s in sessions.Values)
            {
                if (s.TargetDeviceId == targetId && s.Status == SessionStatus.Pending && s.AutoApprovalEligible)
                {
                    s.AutoApprovalEligible = false;
                    s.RuntimeEpoch = null;
                }
            }
        }

        static bool IsHex32(string? s)
        {
            if (s is null || s.Length != 32) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        FixedPasswordStatusResponse FixedStatus(DeviceRegistration reg) =>
            new(reg.FixedPassword.Configured, reg.FixedPassword.Revision);

        // Optional rotate body: empty, or a JSON object with at most one strict nonnegative
        // integer "expectedGeneration". expected == null means unconditional (manual) rotation.
        static bool TryParseRotateBody(byte[] body, out long? expected)
        {
            expected = null;
            if (body.Length == 0) return true;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                bool seen = false;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (seen || !string.Equals(p.Name, "expectedGeneration", StringComparison.Ordinal)) return false;
                    seen = true;
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt64(out long g) || g < 0) return false;
                    expected = g;
                }
                return true;
            }
            catch (JsonException) { return false; }
        }

        // ── Routes ─────────────────────────────────────────────────────────────

        app.MapPost("/api/v1/register", async Task<IResult> (HttpContext ctx, RegisterRequest? req) =>
        {
            if (req is null)
                return Results.BadRequest("Request body is required.");

            // Bound field lengths
            string code = req.InvitationCode ?? string.Empty;
            if (code.Length == 0 || code.Length > MaxInvitationCodeLength)
                return Results.BadRequest("InvitationCode is invalid.");

            // Registration rate limit by remote IP
            string ipKey = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!RegistrationRateGuard(ipKey))
                return Results.StatusCode(429);

            long verifierGeneration = ownerFeature?.VerifierGeneration ?? 0;
            bool invitationAccepted;
            if (ownerFeature is not null) {
                try { invitationAccepted = await ownerFeature.VerifyJoinPasswordAsync(code, options.InvitationCode, ctx.RequestAborted); }
                catch (OwnerRateLimitException) { return Results.StatusCode(429); }
            }
            else {
                var expectedBytes = System.Text.Encoding.UTF8.GetBytes(options.InvitationCode);
                var actualBytes = System.Text.Encoding.UTF8.GetBytes(code);
                invitationAccepted = expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
            }
            if (!invitationAccepted) return Results.Unauthorized();

            lock (ownerFeature?.RegistrationCommitGate ?? stateLock)
            lock (stateLock)
            {
                if (ownerFeature is not null && ownerFeature.VerifierGeneration != verifierGeneration) return Results.Unauthorized();
                // Check device quota (prune terminals first)
                CleanExpiredSessions();
                PruneTerminalSessions();
                if (devices.Count >= options.MaxDevices)
                    return Results.StatusCode(429);

                // Generate bearer *** (256 random bits); only its SHA-256 is retained.
                string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                byte[] tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
                if (devices.Values.Any(d => CryptographicOperations.FixedTimeEquals(d.TokenHash, tokenHash)))
                    return Results.StatusCode(503);

                // Server generates the DeviceId (10 decimal digits, first 1-9).
                // Bounded collision retries; an existing device is never overwritten.
                string? deviceId = null;
                for (int attempt = 0; attempt < DeviceIdGenerator.MaxCollisionRetries; attempt++)
                {
                    string candidateId = DeviceIdGenerator.NewId();
                    if (!devices.ContainsKey(candidateId))
                    {
                        deviceId = candidateId;
                        break;
                    }
                }
                if (deviceId is null)
                    return Results.StatusCode(503);

                // Prepare the candidate and its initial password (generation 1) before the disk commit.
                var now = options.Clock.UtcNow;
                var reg = new DeviceRegistration
                {
                    DeviceId = deviceId,
                    TokenHash = tokenHash,
                    RegisteredAt = now.ToUniversalTime()
                };
                var prepared = reg.PasswordCredential.PrepareRotate(now, out string plainPassword);
                var entry = ToPersisted(reg) with { PasswordGenerationFloor = prepared.Generation };
                if (!TryPersist(Snapshot(ToPersisted, entry), out _))
                    return Results.StatusCode(503);

                reg.PasswordCredential.Apply(prepared);
                if (!devices.TryAdd(deviceId, reg))
                    throw new InvalidOperationException("Device registry invariant violated.");

                // NOTE: token and plainPassword are returned in the response only – never written to logs.
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new RegisterResponse(deviceId, token, plainPassword, prepared.ExpiresAt, reg.PasswordCredential.Generation));
            }
        });

        app.MapGet("/api/v2/peers/presence", (HttpContext ctx) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId)) return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId)) return Results.StatusCode(429);
            if (ctx.Request.Query.Count != 1 || !ctx.Request.Query.TryGetValue("ids", out var values) ||
                values.Count != 1 || values[0] is null || values[0]!.Length > 219)
                return Results.BadRequest("Expected at most 20 device IDs.");
            var ids = values[0]!.Split(',');
            if (ids.Length < 1 || ids.Length > 20 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
                ids.Any(id => id.Length != 10 || id.Any(c => c < '0' || c > '9')))
                return Results.BadRequest("Expected unique 10-digit device IDs.");
            lock (stateLock)
            {
                if (!devices.ContainsKey(deviceId)) return Results.Unauthorized();
                ctx.Response.Headers.CacheControl = "no-store";
                // This observation neither refreshes device activity nor any session TTL.
                return Results.Ok(new { peers = peerPresence.Lookup(deviceId, ids, options.Clock.UtcNow, id => devices.ContainsKey(id)) });
            }
        });

        app.MapGet("/api/v1/devices/me", (HttpContext ctx) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId)) return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId)) return Results.StatusCode(429);
            lock (stateLock)
            {
                if (!devices.TryGetValue(deviceId, out var reg)) return Results.Unauthorized();
                ctx.Response.Headers.CacheControl = "no-store";
                peerPresence.MarkSelfSeen(deviceId, options.Clock.UtcNow);
                return Results.Ok(new
                {
                    deviceId = reg.DeviceId,
                    passwordExpiresAt = reg.PasswordCredential.ExpiresAt,
                    passwordGeneration = reg.PasswordCredential.Generation,
                    rotationRequired = reg.PasswordCredential.RotationRequired,
                    serverTimeUtc = options.Clock.UtcNow
                });
            }
        });

        app.MapPost("/api/v1/sessions/request", async Task<IResult> (HttpContext ctx, SessionRequest? req) =>
        {
            if (!AuthenticateDevice(ctx, out var initiatorId))
                return Results.Unauthorized();
            if (!DeviceRateGuard(initiatorId))
                return Results.StatusCode(429);

            if (req is null)
                return Results.BadRequest("Request body is required.");

            string targetId = req.TargetDeviceId ?? string.Empty;
            if (targetId.Length == 0 || targetId.Length > MaxDeviceIdLength)
                return Results.BadRequest("TargetDeviceId is invalid.");

            // Self-request check before password validation (400 preserved).
            if (initiatorId == targetId)
                return Results.BadRequest("Cannot request a session to self.");

            // null/empty ONLY => manual Pending. Whitespace is a supplied password; never trimmed.
            string? password = req.Password;
            if (string.IsNullOrEmpty(password))
            {
                lock (stateLock)
                {
                    if (!devices.ContainsKey(initiatorId)) return Results.Unauthorized();
                    CleanExpiredSessions();
                    PruneTerminalSessions();
                    // Unknown target: 401, no per-unknown-target state is created.
                    if (!devices.TryGetValue(targetId, out var manualTarget))
                        return Results.Unauthorized();
                    // Duplicate Pending manual request for the same pair is deduplicated.
                    var existing = sessions.Values.FirstOrDefault(s =>
                        s.InitiatorDeviceId == initiatorId && s.TargetDeviceId == targetId &&
                        s.Status == SessionStatus.Pending && s.AuthorizationKind == AuthorizationKinds.Manual);
                    if (existing is not null)
                        return Results.Ok(ToResponse(existing));
                    var blankNow = options.Clock.UtcNow;
                    manualTarget.BlankRequestTimes.RemoveAll(t => blankNow - t > BlankRateWindow);
                    if (manualTarget.BlankRequestTimes.Count >= BlankRateMaxPerWindow)
                        return Results.StatusCode(429);
                    manualTarget.BlankRequestTimes.Add(blankNow);
                    return CreateSession(initiatorId, manualTarget, AuthorizationKinds.Manual, 0, passwordVerified: false);
                }
            }

            DeviceRegistration fixedTarget;
            FixedPasswordCredential fixedSnapshot;
            DateTimeOffset reservation;
            lock (stateLock)
            {
                if (!devices.ContainsKey(initiatorId)) return Results.Unauthorized();
                CleanExpiredSessions();
                PruneTerminalSessions();

                // Unknown target returns 401 with same body as wrong password;
                // do not grow an unbounded unknown-target dictionary.
                if (!devices.TryGetValue(targetId, out var targetDevice))
                    return Results.Unauthorized();

                // Combined temporary OR fixed budget: 5 failures / 60 s per target, one charge per attempt.
                var now = options.Clock.UtcNow;
                if (targetDevice.PasswordCredential.IsLockedOut(now))
                    return Results.StatusCode(429);

                if (targetDevice.PasswordCredential.MatchesTemporary(password, now))
                    return CreateSession(initiatorId, targetDevice, AuthorizationKinds.TemporaryPassword,
                        targetDevice.PasswordCredential.Generation, passwordVerified: true);

                // Reserve the single charge before the KDF so in-flight attempts stay bounded;
                // refunded only on a verified fixed password.
                targetDevice.PasswordCredential.RecordFailedAttempt(now);
                if (!FixedPasswordCredential.IsAcceptable(password))
                    return Results.Unauthorized();
                reservation = now;
                fixedTarget = targetDevice;
                fixedSnapshot = targetDevice.FixedPassword;
            }

            // KDF outside stateLock, bounded concurrency; dummy KDF when no fixed password is set.
            bool? verified = await fixedSnapshot.VerifyBoundedAsync(password, ctx.RequestAborted);

            lock (stateLock)
            {
                if (verified is null)
                {
                    // No verification happened; return the reserved charge.
                    fixedTarget.PasswordCredential.RefundFailedAttempt(reservation);
                    return Results.StatusCode(429);
                }
                // Recheck identity and the fixed revision before granting.
                if (!devices.ContainsKey(initiatorId) ||
                    !devices.TryGetValue(targetId, out var current) || !ReferenceEquals(current, fixedTarget))
                    return Results.Unauthorized();
                if (verified != true || !fixedSnapshot.Configured || !ReferenceEquals(current.FixedPassword, fixedSnapshot))
                    return Results.Unauthorized();

                current.PasswordCredential.RefundFailedAttempt(reservation);
                CleanExpiredSessions();
                PruneTerminalSessions();
                return CreateSession(initiatorId, current, AuthorizationKinds.FixedPassword,
                    current.FixedPassword.Revision, passwordVerified: true);
            }
        });

        app.MapGet("/api/v1/devices/fixed-password", (HttpContext ctx) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId)) return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId)) return Results.StatusCode(429);
            lock (stateLock)
            {
                if (!devices.TryGetValue(deviceId, out var reg)) return Results.Unauthorized();
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Ok(FixedStatus(reg));
            }
        });

        app.MapPut("/api/v1/devices/fixed-password", async Task<IResult> (HttpContext ctx, FixedPasswordRequest? req) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId)) return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId)) return Results.StatusCode(429);
            if (req is null || !FixedPasswordCredential.IsAcceptable(req.Password))
                return Results.BadRequest("Password is invalid.");

            DeviceRegistration reg;
            FixedPasswordCredential previous;
            long nextRevision;
            lock (stateLock)
            {
                if (!devices.TryGetValue(deviceId, out var r)) return Results.Unauthorized();
                if (r.FixedPassword.Revision == long.MaxValue) return Results.StatusCode(503);
                reg = r;
                previous = r.FixedPassword;
                nextRevision = previous.Revision + 1;
            }

            // KDF outside stateLock, bounded concurrency.
            var created = await FixedPasswordCredential.CreateBoundedAsync(req.Password!, nextRevision, ctx.RequestAborted);
            if (created is null) return Results.StatusCode(429);

            lock (stateLock)
            {
                // Optimistic recheck: identity and revision unchanged since preparation.
                if (!devices.TryGetValue(deviceId, out var current) || !ReferenceEquals(current, reg))
                    return Results.Unauthorized();
                if (!ReferenceEquals(current.FixedPassword, previous))
                    return Results.StatusCode(409);
                // Durable before memory.
                if (!TryPersist(Snapshot(d => d.DeviceId == deviceId ? ToPersistedWithFixed(d, created) : ToPersisted(d), null), out _))
                    return Results.StatusCode(503);
                current.FixedPassword = created;
                CleanExpiredSessions();
                InvalidateFixedSessions(deviceId);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Ok(FixedStatus(current));
            }
        });

        app.MapDelete("/api/v1/devices/fixed-password", (HttpContext ctx) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId)) return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId)) return Results.StatusCode(429);
            lock (stateLock)
            {
                if (!devices.TryGetValue(deviceId, out var reg)) return Results.Unauthorized();
                if (reg.FixedPassword.Configured)
                {
                    if (reg.FixedPassword.Revision == long.MaxValue) return Results.StatusCode(503);
                    var cleared = reg.FixedPassword.Cleared(reg.FixedPassword.Revision + 1);
                    if (!TryPersist(Snapshot(d => d.DeviceId == deviceId ? ToPersistedWithFixed(d, cleared) : ToPersisted(d), null), out _))
                        return Results.StatusCode(503);
                    reg.FixedPassword = cleared;
                    CleanExpiredSessions();
                    InvalidateFixedSessions(deviceId);
                }
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Ok(FixedStatus(reg));
            }
        });

        // In-memory access runtime capability; never persisted, 60 s TTL.
        app.MapPost("/api/v1/devices/access-runtime", (HttpContext ctx, AccessRuntimeRequest? req) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId)) return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId)) return Results.StatusCode(429);
            if (req is null || req.Version != 1 || !IsHex32(req.InstanceNonce))
                return Results.BadRequest("Access runtime request is invalid.");
            string nonce = req.InstanceNonce!.ToUpperInvariant();

            lock (stateLock)
            {
                if (!devices.TryGetValue(deviceId, out var reg)) return Results.Unauthorized();
                var now = options.Clock.UtcNow;
                CleanExpiredSessions();
                if (req.Enabled)
                {
                    bool renew = reg.ActiveRuntimeEpoch(now) is not null &&
                        string.Equals(reg.RuntimeInstanceNonce, nonce, StringComparison.Ordinal);
                    if (!renew)
                    {
                        // Changed instance or stale capability: new epoch, auto pending falls back to manual.
                        DowngradeAutoPending(deviceId);
                        reg.RuntimeEpoch = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                        reg.RuntimeInstanceNonce = nonce;
                    }
                    reg.RuntimeExpiresAt = now + RuntimeTtl;
                }
                else
                {
                    DowngradeAutoPending(deviceId);
                    reg.RuntimeEpoch = null;
                    reg.RuntimeInstanceNonce = null;
                    reg.RuntimeExpiresAt = default;
                }
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new AccessRuntimeResponse(1, req.Enabled,
                    req.Enabled ? reg.RuntimeEpoch : null,
                    req.Enabled ? reg.RuntimeExpiresAt.UtcDateTime.ToString(UtcFormat, System.Globalization.CultureInfo.InvariantCulture) : null));
            }
        });

        app.MapPost("/api/v1/devices/password/rotate", async Task<IResult> (HttpContext ctx) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId))
                return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId))
                return Results.StatusCode(429);

            // Body is bounded by the Kestrel 16 KB limit; re-checked here.
            byte[] body;
            try
            {
                using var buffer = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
                if (buffer.Length > MaxRequestBodyBytes)
                    return Results.StatusCode(413);
                body = buffer.ToArray();
            }
            catch (BadHttpRequestException ex)
            {
                return Results.StatusCode(ex.StatusCode);
            }
            if (!TryParseRotateBody(body, out long? expectedGeneration))
                return Results.BadRequest("Rotate body is invalid.");

            lock (stateLock)
            {
                // Identity recheck: the device may have been removed while the body was read.
                if (!devices.TryGetValue(deviceId, out var reg))
                    return Results.Unauthorized();

                // Conditional rotate: mismatch rejected before any state mutation.
                if (expectedGeneration is long expected && reg.PasswordCredential.Generation != expected)
                    return Results.StatusCode(409);

                // Generation overflow fails closed before any state mutation.
                if (!reg.PasswordCredential.CanAdvanceGeneration)
                    return Results.StatusCode(503);

                // Prepare, durably commit the new generation, then install; failure leaves all state unchanged.
                var prepared = reg.PasswordCredential.PrepareRotate(options.Clock.UtcNow, out string plainPassword);
                if (!TryPersist(Snapshot(d => WithGeneration(d, deviceId, prepared.Generation), null), out _))
                    return Results.StatusCode(503);
                reg.PasswordCredential.Apply(prepared);
                var expiresAt = prepared.ExpiresAt;

                CleanExpiredSessions();

                // Revoke only Pending or Approved inbound sessions for this device.
                foreach (var s in sessions.Values)
                {
                    if (s.TargetDeviceId == deviceId &&
                        (s.Status == SessionStatus.Pending || s.Status == SessionStatus.Approved))
                    {
                        s.Status = SessionStatus.Revoked;
                        s.StatusChangedAt = options.Clock.UtcNow;
                        s.Media.Stop();
                    }
                    // Connected sessions are NOT revoked on password rotation.
                }

                // NOTE: plainPassword is returned in the response only – never written to logs.
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new PasswordResponse(plainPassword, expiresAt, reg.PasswordCredential.Generation));
            }
        });

        app.MapGet("/api/v1/sessions/pending", (HttpContext ctx) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId))
                return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId))
                return Results.StatusCode(429);

            lock (stateLock)
            {
                CleanExpiredSessions();
                var pending = sessions.Values
                    .Where(s => s.TargetDeviceId == deviceId && s.Status == SessionStatus.Pending)
                    .Select(ToResponse)
                    .ToList();
                return Results.Ok(new SessionListResponse(pending));
            }
        });

        // Read-only snapshot of every session (any status) the caller participates in.
        // Bounded by MaxSessions; never renews TTL or media activity.
        app.MapGet("/api/v1/sessions/snapshot", (HttpContext ctx) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId))
                return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId))
                return Results.StatusCode(429);

            lock (stateLock)
            {
                if (!devices.ContainsKey(deviceId)) return Results.Unauthorized();
                CleanExpiredSessions();
                var snapshot = sessions.Values
                    .Where(s => s.InitiatorDeviceId == deviceId || s.TargetDeviceId == deviceId)
                    .OrderBy(s => s.CreatedAt)
                    .ThenBy(s => s.SessionId, StringComparer.Ordinal)
                    .Select(ToResponse)
                    .ToList();
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new SessionListResponse(snapshot));
            }
        });

        app.MapPost("/api/v1/sessions/decision", (HttpContext ctx, DecisionRequest? req) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId))
                return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId))
                return Results.StatusCode(429);

            if (req is null)
                return Results.BadRequest("Request body is required.");

            string sid = req.SessionId ?? string.Empty;
            if (sid.Length == 0 || sid.Length > MaxSessionIdLength)
                return Results.BadRequest("SessionId is invalid.");

            // Mode: manual (default) | password. Password mode is approve-only.
            string mode = req.Mode ?? "manual";
            if (mode != "manual" && mode != "password")
                return Results.BadRequest("Mode is invalid.");
            bool passwordMode = mode == "password";
            if (passwordMode && !req.Approve)
                return Results.BadRequest("Password mode requires approve.");

            lock (stateLock)
            {
                CleanExpiredSessions();

                if (!sessions.TryGetValue(sid, out var session))
                    return Results.NotFound();

                // Only the target may decide.
                if (session.TargetDeviceId != deviceId)
                    return Results.StatusCode(403);

                // Initiator cannot approve their own request.
                if (session.InitiatorDeviceId == deviceId)
                    return Results.StatusCode(403);

                if (session.Status != SessionStatus.Pending)
                    return Results.BadRequest("Session is not in Pending state.");

                if (passwordMode)
                {
                    // One-shot auto approval: eligible session, matching active epoch, current revision.
                    if (!devices.TryGetValue(deviceId, out var self) ||
                        !PasswordAuthorizationCurrent(session, self, options.Clock.UtcNow) ||
                        !string.Equals(req.RuntimeEpoch, session.RuntimeEpoch, StringComparison.Ordinal))
                        return Results.StatusCode(409);
                    session.AutoApproved = true;
                }

                session.Status = req.Approve ? SessionStatus.Approved : SessionStatus.Rejected;
                session.StatusChangedAt = options.Clock.UtcNow;
                return Results.Ok(ToResponse(session));
            }
        });

        app.MapPost("/api/v1/sessions/connect", (HttpContext ctx, ConnectRequest? req) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId))
                return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId))
                return Results.StatusCode(429);

            if (req is null)
                return Results.BadRequest("Request body is required.");

            string sid = req.SessionId ?? string.Empty;
            if (sid.Length == 0 || sid.Length > MaxSessionIdLength)
                return Results.BadRequest("SessionId is invalid.");

            lock (stateLock)
            {
                CleanExpiredSessions();

                if (!sessions.TryGetValue(sid, out var session))
                    return Results.NotFound();

                // Only the initiator may connect.
                if (session.InitiatorDeviceId != deviceId)
                    return Results.StatusCode(403);

                // Duplicate connect (already Connected) is rejected here and never re-invalidates.
                if (session.Status != SessionStatus.Approved)
                    return Results.BadRequest("Session is not in Approved state.");

                if (!devices.TryGetValue(session.TargetDeviceId, out var target))
                    return Results.NotFound();

                var now = options.Clock.UtcNow;
                // Auto-approved sessions must still hold a current proof/revision/epoch.
                if (session.AutoApproved && !PasswordAuthorizationCurrent(session, target, now))
                    return Results.StatusCode(409);

                // Fixed-password Connect does not invalidate the temporary password;
                // temporary and manual Connect keep the durable invalidation.
                if (session.AuthorizationKind != AuthorizationKinds.FixedPassword)
                {
                    // Generation overflow fails closed before any state mutation.
                    if (!target.PasswordCredential.CanAdvanceGeneration)
                        return Results.StatusCode(503);

                    // Durably invalidate the target's password (no new plaintext) BEFORE any session change.
                    var prepared = target.PasswordCredential.PrepareInvalidate();
                    if (!TryPersist(Snapshot(d => WithGeneration(d, target.DeviceId, prepared.Generation), null), out _))
                        return Results.StatusCode(503);
                    target.PasswordCredential.Apply(prepared);
                }

                session.Status = SessionStatus.Connected;
                peerPresence.RememberApprovedPair(session.InitiatorDeviceId, session.TargetDeviceId);
                session.StatusChangedAt = now;
                session.LastMediaActivityAt = now;

                // Revoke other inbound Pending/Approved sessions for the target; Connected ones are preserved.
                foreach (var s in sessions.Values)
                {
                    if (!ReferenceEquals(s, session) &&
                        s.TargetDeviceId == session.TargetDeviceId &&
                        (s.Status == SessionStatus.Pending || s.Status == SessionStatus.Approved))
                    {
                        s.Status = SessionStatus.Revoked;
                        s.StatusChangedAt = now;
                        s.Media.Stop();
                    }
                }
                return Results.Ok(ToResponse(session));
            }
        });

        app.MapPost("/api/v1/sessions/revoke", (HttpContext ctx, RevokeRequest? req) =>
        {
            if (!AuthenticateDevice(ctx, out var deviceId))
                return Results.Unauthorized();
            if (!DeviceRateGuard(deviceId))
                return Results.StatusCode(429);

            if (req is null)
                return Results.BadRequest("Request body is required.");

            string sid = req.SessionId ?? string.Empty;
            if (sid.Length == 0 || sid.Length > MaxSessionIdLength)
                return Results.BadRequest("SessionId is invalid.");

            lock (stateLock)
            {
                CleanExpiredSessions();

                if (!sessions.TryGetValue(sid, out var session))
                    return Results.NotFound();

                // Both initiator and target may revoke.
                if (session.InitiatorDeviceId != deviceId && session.TargetDeviceId != deviceId)
                    return Results.StatusCode(403);

                // Terminal states are immutable.
                if (SessionStatus.IsTerminal(session.Status))
                    return Results.BadRequest("Session is already in a terminal state.");

                session.Status = SessionStatus.Revoked;
                session.Media.Stop();
                session.StatusChangedAt = options.Clock.UtcNow;
                return Results.Ok(ToResponse(session));
            }
        });

        // One authenticated per-device budget across legacy media and opaque v2 traffic.
        // Buckets are bounded by existing devices; unknown credentials cannot allocate one.
        var sharedMediaRates = new Dictionary<string, (DateTimeOffset Start, int Count)>(StringComparer.Ordinal);
        bool SharedMediaRate(string id)
        {
            lock (stateLock)
            {
                if (!devices.ContainsKey(id)) return false;
                var now = options.Clock.UtcNow;
                foreach (var stale in sharedMediaRates.Where(p => !devices.ContainsKey(p.Key) ||
                    now - p.Value.Start >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray())
                    sharedMediaRates.Remove(stale);
                if (!sharedMediaRates.TryGetValue(id, out var bucket) ||
                    now - bucket.Start >= TimeSpan.FromSeconds(1))
                    bucket = (now, 0);
                if (bucket.Count >= 60) return false;
                sharedMediaRates[id] = (bucket.Start, bucket.Count + 1);
                return true;
            }
        }
        MediaRelay.Map(app, stateLock, sessions,
            context => AuthenticateDevice(context, out var id) ? id : null,
            CleanExpiredSessions, options.Clock, SharedMediaRate);
        OpaqueSessionRelay.Map(app, stateLock,
            context => AuthenticateDevice(context, out var id) ? id : null,
            id => devices.ContainsKey(id),
            sid => sessions.TryGetValue(sid, out var session) ? session : null,
            CleanExpiredSessions, (context, id) => SharedMediaRate(id),
            () => options.Clock.UtcNow, options.MaxSessions);
        return app;
    }

    private static PersistedDevice ToPersisted(DeviceRegistration d) => ToPersistedWithFixed(d, d.FixedPassword);

    private static PersistedDevice ToPersistedWithFixed(DeviceRegistration d, FixedPasswordCredential fixedPassword) =>
        new(d.DeviceId, Convert.ToHexString(d.TokenHash), d.RegisteredAt.ToUniversalTime(), d.PasswordCredential.Generation,
            fixedPassword.Revision, fixedPassword.SaltHex, fixedPassword.HashHex, fixedPassword.VerifierIterations);
}
