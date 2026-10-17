using System.Security.Cryptography;
using System.Text;

namespace PeerDesk.Server;

/// <summary>
/// Server-side fixed (owner-chosen) password verifier: PBKDF2-SHA256 salt/hash/iterations
/// plus a monotonic revision. Plaintext is never retained. Instances are immutable; a new
/// instance is installed under stateLock after a durable commit.
/// </summary>
public sealed class FixedPasswordCredential
{
    public const int MinLength = 8;
    public const int MaxLength = 128;
    public const int Iterations = 100_000;
    public const int MinIterations = 10_000;
    public const int MaxIterations = 10_000_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    // Bounded KDF concurrency shared by every caller in the process.
    private static readonly SemaphoreSlim KdfGate = new(2, 2);
    private static readonly TimeSpan KdfWait = TimeSpan.FromSeconds(2);
    // Dummy verifier so an unconfigured target costs the same KDF as a configured one.
    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(SaltBytes);

    private readonly byte[]? _salt;
    private readonly byte[]? _hash;
    private readonly int _iterations;

    public FixedPasswordCredential(long revision, byte[]? salt, byte[]? hash, int iterations)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if ((salt is null) != (hash is null)) throw new ArgumentException("Salt and hash must both be present or absent.");
        if (salt is not null && (salt.Length != SaltBytes || hash!.Length != HashBytes || iterations < MinIterations || iterations > MaxIterations))
            throw new ArgumentException("Invalid fixed password verifier.");
        Revision = revision;
        _salt = salt;
        _hash = hash;
        _iterations = salt is null ? 0 : iterations;
    }

    public static FixedPasswordCredential None { get; } = new(0, null, null, 0);

    public long Revision { get; }
    public bool Configured => _salt is not null;
    public int VerifierIterations => _iterations;
    public string? SaltHex => _salt is null ? null : Convert.ToHexString(_salt);
    public string? HashHex => _hash is null ? null : Convert.ToHexString(_hash);

    /// <summary>Length-only policy (8..128 UTF-16 code units). Never trims; case-sensitive.</summary>
    public static bool IsAcceptable(string? s) => s is not null && s.Length >= MinLength && s.Length <= MaxLength;

    /// <summary>Builds a configured verifier at the given revision. Runs the KDF; call outside stateLock.</summary>
    public static FixedPasswordCredential Create(string password, long revision)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new FixedPasswordCredential(revision, salt, Derive(password, salt, Iterations), Iterations);
    }

    public FixedPasswordCredential Cleared(long revision) => new(revision, null, null, 0);

    /// <summary>Constant-time verify; runs the KDF (dummy when unconfigured). Call outside stateLock.</summary>
    public bool Verify(string candidate)
    {
        if (_salt is null)
        {
            _ = Derive(candidate, DummySalt, Iterations);
            return false;
        }
        byte[] actual = Derive(candidate, _salt, _iterations);
        try { return CryptographicOperations.FixedTimeEquals(actual, _hash!); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    /// <summary>Bounded-concurrency <see cref="Verify"/>; null when the KDF gate is saturated (map to 429).</summary>
    public async Task<bool?> VerifyBoundedAsync(string candidate, CancellationToken cancellationToken)
    {
        if (!await KdfGate.WaitAsync(KdfWait, cancellationToken).ConfigureAwait(false)) return null;
        try { return await Task.Run(() => Verify(candidate), cancellationToken).ConfigureAwait(false); }
        finally { KdfGate.Release(); }
    }

    /// <summary>Bounded-concurrency <see cref="Create"/>; null when the KDF gate is saturated (map to 429).</summary>
    public static async Task<FixedPasswordCredential?> CreateBoundedAsync(string password, long revision, CancellationToken cancellationToken)
    {
        if (!await KdfGate.WaitAsync(KdfWait, cancellationToken).ConfigureAwait(false)) return null;
        try { return await Task.Run(() => Create(password, revision), cancellationToken).ConfigureAwait(false); }
        finally { KdfGate.Release(); }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
