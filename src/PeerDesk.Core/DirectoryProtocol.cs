using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PeerDesk.Core;

/// <summary>Single-use challenge issued by the registry for one approved grant.</summary>
public sealed record DirectoryChallenge(
    string ChallengeId,
    string Nonce,
    string Alias,
    string RegistryIdentity,
    long ExpiresAtUnixSeconds,
    string ProfileJson);

/// <summary>Owner-signed proof over <see cref="DirectoryProtocol.CanonicalPayload"/>.</summary>
public sealed record DirectoryProof(
    DirectoryChallenge Challenge,
    string CertificateDerBase64,
    string SignatureBase64);

public static class DirectoryProtocol
{
    /// <summary>Fixed purpose string; the signed payload is never reusable for other purposes.</summary>
    public const string Purpose = "PeerDesk.DirectoryPublish.v1";

    public const int MaxChallengeLifetimeSeconds = 120;
    public const int MaxCertificateDerBytes = 8192;
    public const int MaxRegistryIdentityLength = 128;

    /// <summary>
    /// Canonical bytes: for each field (purpose, challengeId, nonce, alias, registryIdentity,
    /// expiresAtUnixSeconds as invariant decimal, profileJson) a 4-byte big-endian length followed by UTF-8 bytes.
    /// </summary>
    public static byte[] CanonicalPayload(DirectoryChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        string[] fields =
        [
            Purpose,
            challenge.ChallengeId ?? throw new ArgumentException("ChallengeId is required."),
            challenge.Nonce ?? throw new ArgumentException("Nonce is required."),
            challenge.Alias ?? throw new ArgumentException("Alias is required."),
            challenge.RegistryIdentity ?? throw new ArgumentException("RegistryIdentity is required."),
            challenge.ExpiresAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
            challenge.ProfileJson ?? throw new ArgumentException("ProfileJson is required.")
        ];

        var encoding = new UTF8Encoding(false, true);
        using var ms = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        foreach (var field in fields)
        {
            byte[] bytes = encoding.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(len, bytes.Length);
            ms.Write(len);
            ms.Write(bytes);
        }
        return ms.ToArray();
    }

    /// <summary>Lowercase ASCII letters/digits/hyphen, 3..32 chars, no leading/trailing hyphen.</summary>
    public static bool IsValidAlias(string? alias)
    {
        if (alias is null || alias.Length < 3 || alias.Length > 32) return false;
        if (alias[0] == '-' || alias[^1] == '-') return false;
        foreach (char c in alias)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
        }
        return true;
    }

    /// <summary>
    /// Canonical alias normalization: input may contain only ASCII [A-Za-z0-9-]; ASCII A-Z is
    /// lowered, nothing else is changed (no trimming, no Unicode case folding). The result must
    /// satisfy <see cref="IsValidAlias"/>; otherwise returns false.
    /// </summary>
    public static bool TryNormalizeAlias(string? input, out string alias)
    {
        alias = string.Empty;
        if (input is null || input.Length < 3 || input.Length > 32) return false;
        Span<char> buffer = stackalloc char[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c >= 'A' && c <= 'Z') c = (char)(c + ('a' - 'A'));
            else if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
            buffer[i] = c;
        }
        string candidate = new(buffer);
        if (!IsValidAlias(candidate)) return false;
        alias = candidate;
        return true;
    }

    /// <summary>Public stable registry ID: printable ASCII [A-Za-z0-9._:-], 1..128 chars.</summary>
    public static bool IsValidRegistryIdentity(string? identity)
    {
        if (string.IsNullOrEmpty(identity) || identity.Length > MaxRegistryIdentityLength) return false;
        foreach (char c in identity)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                  c == '.' || c == '_' || c == ':' || c == '-')) return false;
        }
        return true;
    }
}
