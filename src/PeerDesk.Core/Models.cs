namespace PeerDesk.Core;

// Server assigns DeviceId; caller only supplies InvitationCode.
public record RegisterRequest(string InvitationCode);
public record RegisterResponse(string DeviceId, string BearerToken, string TemporaryPassword, DateTimeOffset PasswordExpiresAt, long PasswordGeneration);

public record SessionRequest(string TargetDeviceId, string? Password = null);
public record SessionResponse(
    string SessionId,
    string InitiatorDeviceId,
    string TargetDeviceId,
    string Status,
    DateTimeOffset CreatedAt,
    // Server-generated authorization metadata. Absent fields (older servers) mean manual/no auto.
    string AuthorizationKind = AuthorizationKinds.Manual,
    long AuthorizationRevision = 0,
    string? RuntimeEpoch = null,
    bool AutoApprovalEligible = false);

public static class AuthorizationKinds
{
    public const string Manual = "manual";
    public const string TemporaryPassword = "temporary-password";
    public const string FixedPassword = "fixed-password";
}

// Connect uses a dedicated type so it cannot be confused with SessionRequest.
public record ConnectRequest(string SessionId);
// Mode: null/"manual" (default) or "password" (target-only one-shot auto approval bound to RuntimeEpoch).
public record DecisionRequest(string SessionId, bool Approve, string? Mode = null, string? RuntimeEpoch = null);
public record RevokeRequest(string SessionId);
public record SessionListResponse(List<SessionResponse> Sessions);

public record PasswordResponse(string TemporaryPassword, DateTimeOffset PasswordExpiresAt, long PasswordGeneration);

// Fixed password: plaintext is only ever sent in the PUT body, never returned.
public record FixedPasswordRequest(string? Password);
public record FixedPasswordStatusResponse(bool Configured, long Revision);

// In-memory access runtime capability (60 s TTL); ExpiresAt is a UTC ISO-8601 string.
public record AccessRuntimeRequest(int Version, bool Enabled, string? InstanceNonce);
public record AccessRuntimeResponse(int Version, bool Enabled, string? RuntimeEpoch, string? ExpiresAt);
