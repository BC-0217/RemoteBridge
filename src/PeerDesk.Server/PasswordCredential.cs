using System.Security.Cryptography;

namespace PeerDesk.Server;

/// <summary>
/// Holds the server-side password credential for a registered device.
/// Plaintext is never stored; only salt, hash, and expiry are retained.
/// All access must occur under stateLock.
/// </summary>
public sealed class PasswordCredential
{
    // Password format: 9 ASCII letters A-Z/a-z, at least one uppercase and one lowercase.
    // Case-sensitive.
    public const int PasswordLength = 9;
    private const string PasswordAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int SaltBytes = 16;
    public static readonly TimeSpan PasswordTtl = TimeSpan.FromHours(1);

    // Max 5 failed attempts per 60-second sliding window before lockout.
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan FailedAttemptWindow = TimeSpan.FromSeconds(60);

    // Stored credential (set under stateLock).
    private byte[] _salt = Array.Empty<byte>();
    private byte[] _hash = Array.Empty<byte>();
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    public DateTimeOffset ExpiresAt => _expiresAt;

    // Nonnegative, monotonic; advanced by every successful Rotate and by InvalidateAfterConnect.
    private long _generation;
    public long Generation => _generation;
    // Set when the password was invalidated by a connect; cleared by Rotate.
    public bool RotationRequired { get; private set; }
    // False once the generation cannot advance; callers must fail closed before mutating.
    public bool CanAdvanceGeneration => _generation < long.MaxValue;

    // Failed-attempt timestamps (access under stateLock).
    private readonly List<DateTimeOffset> _failedAttempts = new();

    /// <summary>
    /// Generates a new password, stores its hash, and returns the plaintext + expiry.
    /// Clears the failed-attempt window.
    /// Must be called under stateLock.
    /// </summary>
    public (string PlainPassword, DateTimeOffset ExpiresAt) Rotate(DateTimeOffset now)
    {
        var prepared = PrepareRotate(now, out string plain);
        Apply(prepared);
        return (plain, _expiresAt);
    }

    /// <summary>
    /// Immediately invalidates the current password after a successful session connect.
    /// No new plaintext is generated; the owner must rotate to obtain a new password.
    /// Failed-attempt history is retained. Must be called under stateLock.
    /// </summary>
    public void InvalidateAfterConnect()
    {
        Apply(PrepareInvalidate());
    }

    /// <summary>
    /// Prepares (without installing) a rotated credential at the next generation.
    /// The plaintext is returned only through <paramref name="plainPassword"/>.
    /// Throws if the generation cannot advance. Must be called under stateLock.
    /// </summary>
    public PreparedPasswordState PrepareRotate(DateTimeOffset now, out string plainPassword)
    {
        if (!CanAdvanceGeneration)
            throw new InvalidOperationException("Password generation exhausted.");
        plainPassword = GeneratePassword();
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = ComputeHash(salt, plainPassword);
        return new PreparedPasswordState(salt, hash, now + PasswordTtl, checked(_generation + 1),
            rotationRequired: false, clearFailedAttempts: true);
    }

    /// <summary>
    /// Prepares (without installing) an invalidated credential at the next generation.
    /// Failed-attempt history is retained on apply. Must be called under stateLock.
    /// </summary>
    public PreparedPasswordState PrepareInvalidate()
    {
        if (!CanAdvanceGeneration)
            throw new InvalidOperationException("Password generation exhausted.");
        return new PreparedPasswordState(RandomNumberGenerator.GetBytes(SaltBytes),
            RandomNumberGenerator.GetBytes(SHA256.HashSizeInBytes), DateTimeOffset.MinValue,
            checked(_generation + 1), rotationRequired: true, clearFailedAttempts: false);
    }

    /// <summary>
    /// Installs a prepared state produced by this instance for the next generation.
    /// Must be called under stateLock, after any durable commit of the new generation.
    /// </summary>
    public void Apply(PreparedPasswordState prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Generation != checked(_generation + 1))
            throw new InvalidOperationException("Prepared password state is stale.");
        CryptographicOperations.ZeroMemory(_hash);
        _salt = prepared.Salt;
        _hash = prepared.Hash;
        _expiresAt = prepared.ExpiresAt;
        _generation = prepared.Generation;
        RotationRequired = prepared.RotationRequired;
        if (prepared.ClearFailedAttempts)
            _failedAttempts.Clear();
    }

    /// <summary>
    /// Creates a credential restored from a persisted generation floor: generation is
    /// floor + 1 (checked; long.MaxValue overflows), the password is expired and
    /// RotationRequired is set. No plaintext exists for a restored credential.
    /// </summary>
    public static PasswordCredential Restore(long persistedFloor)
    {
        if (persistedFloor < 0)
            throw new ArgumentOutOfRangeException(nameof(persistedFloor));
        var credential = new PasswordCredential
        {
            _salt = RandomNumberGenerator.GetBytes(SaltBytes),
            _hash = RandomNumberGenerator.GetBytes(SHA256.HashSizeInBytes),
            _expiresAt = DateTimeOffset.MinValue,
            _generation = checked(persistedFloor + 1),
            RotationRequired = true
        };
        return credential;
    }

    /// <summary>
    /// Validates a candidate password string.
    /// Returns a <see cref="PasswordCheckResult"/> indicating the outcome.
    /// Must be called under stateLock.
    /// </summary>
    public PasswordCheckResult Check(string? candidate, DateTimeOffset now)
    {
        // Prune stale failed-attempt timestamps.
        _failedAttempts.RemoveAll(t => now - t > FailedAttemptWindow);

        // Sixth attempt while window is full → 429, even if password would be correct.
        if (_failedAttempts.Count >= MaxFailedAttempts)
            return PasswordCheckResult.RateLimited;

        // Missing/malformed candidates consume the same failed-attempt budget.
        if (!IsValidFormat(candidate))
        {
            _failedAttempts.Add(now);
            return PasswordCheckResult.InvalidFormat;
        }

        // Expired credential → record failure and reject.
        if (now >= _expiresAt)
        {
            _failedAttempts.Add(now);
            return PasswordCheckResult.Expired;
        }

        // Constant-time comparison.
        byte[] candidateHash = ComputeHash(_salt, candidate!);
        bool match = CryptographicOperations.FixedTimeEquals(candidateHash, _hash);

        if (!match)
        {
            _failedAttempts.Add(now);
            return PasswordCheckResult.Wrong;
        }

        return PasswordCheckResult.Ok;
    }

    // ── Combined (temporary OR fixed) attempt accounting ──────────────────────
    // The failed-attempt window is the device's single shared budget: callers that combine
    // the temporary and fixed checks charge exactly one entry per attempt via these helpers.

    /// <summary>True when the shared failed-attempt window is full. Must be called under stateLock.</summary>
    public bool IsLockedOut(DateTimeOffset now)
    {
        _failedAttempts.RemoveAll(t => now - t > FailedAttemptWindow);
        return _failedAttempts.Count >= MaxFailedAttempts;
    }

    /// <summary>Charges one failed attempt. Must be called under stateLock.</summary>
    public void RecordFailedAttempt(DateTimeOffset now) => _failedAttempts.Add(now);

    /// <summary>Refunds one charge previously reserved at <paramref name="at"/>. Must be called under stateLock.</summary>
    public void RefundFailedAttempt(DateTimeOffset at) => _failedAttempts.Remove(at);

    /// <summary>
    /// Non-mutating temporary password match (format, expiry, constant-time hash).
    /// Must be called under stateLock.
    /// </summary>
    public bool MatchesTemporary(string candidate, DateTimeOffset now)
    {
        if (!IsValidFormat(candidate) || now >= _expiresAt) return false;
        byte[] candidateHash = ComputeHash(_salt, candidate);
        return CryptographicOperations.FixedTimeEquals(candidateHash, _hash);
    }

    /// <summary>
    /// Clears all failed-attempt timestamps (call on rotate).
    /// Must be called under stateLock.
    /// </summary>
    public void ClearFailedAttempts()
    {
        _failedAttempts.Clear();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    // Whole-password rejection sampling: each char unbiased via GetInt32; redraw
    // the whole password until it contains both an uppercase and a lowercase letter.
    private static string GeneratePassword()
    {
        var chars = new char[PasswordLength];
        while (true)
        {
            for (int i = 0; i < PasswordLength; i++)
                chars[i] = PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
            if (HasUpperAndLower(chars))
                return new string(chars);
        }
    }

    private static bool HasUpperAndLower(IEnumerable<char> s)
    {
        bool upper = false, lower = false;
        foreach (char c in s)
        {
            if (c >= 'A' && c <= 'Z') upper = true;
            else if (c >= 'a' && c <= 'z') lower = true;
        }
        return upper && lower;
    }

    private static byte[] ComputeHash(byte[] salt, string password)
    {
        // Hash = SHA-256(salt || ASCII(password)); case-sensitive, no normalization.
        byte[] pwBytes = System.Text.Encoding.ASCII.GetBytes(password);
        byte[] input = new byte[salt.Length + pwBytes.Length];
        salt.CopyTo(input, 0);
        pwBytes.CopyTo(input, salt.Length);
        return SHA256.HashData(input);
    }

    public static bool IsValidFormat(string? s)
    {
        if (s is null || s.Length != PasswordLength)
            return false;
        foreach (char c in s)
        {
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
                return false;
        }
        return HasUpperAndLower(s);
    }
}

/// <summary>
/// A prepared, not-yet-installed password credential state. Holds only salt/hash,
/// never plaintext. Installed via <see cref="PasswordCredential.Apply"/>.
/// </summary>
public sealed class PreparedPasswordState
{
    internal PreparedPasswordState(byte[] salt, byte[] hash, DateTimeOffset expiresAt, long generation,
        bool rotationRequired, bool clearFailedAttempts)
    {
        Salt = salt;
        Hash = hash;
        ExpiresAt = expiresAt;
        Generation = generation;
        RotationRequired = rotationRequired;
        ClearFailedAttempts = clearFailedAttempts;
    }

    internal byte[] Salt { get; }
    internal byte[] Hash { get; }
    public DateTimeOffset ExpiresAt { get; }
    public long Generation { get; }
    public bool RotationRequired { get; }
    internal bool ClearFailedAttempts { get; }
}

public enum PasswordCheckResult
{
    Ok,
    InvalidFormat,
    RateLimited,
    Expired,
    Wrong
}
