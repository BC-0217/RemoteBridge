using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeerDesk.Core;

namespace PeerDesk.Server;

public sealed record DirectoryGrantOptions(
    string GrantHashSha256,
    string Alias,
    string ProfileJson,
    long ExpiresAtUnixSeconds);

/// <summary>
/// Directory config v1. Grants are stored only as SHA-256 hashes of the random secret grant (UTF-8).
/// ExpiresAtUnixSeconds 0 means the grant does not expire.
/// </summary>
public sealed record DirectoryFeatureOptions(
    int Version,
    string RegistryIdentity,
    string StatePath,
    IReadOnlyList<DirectoryGrantOptions> Grants)
{
    public const int MaxConfigBytes = 1024 * 1024;
    public const int MaxGrants = 256;

    internal static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };

    public static DirectoryFeatureOptions Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Directory config path must be absolute.");
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new InvalidOperationException("Directory config file not found.");
        if (info.Length > MaxConfigBytes)
            throw new InvalidOperationException("Directory config exceeds size limit.");
        return Parse(ReadBounded(path, MaxConfigBytes));
    }

    internal static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[maximum + 1];
        int total = 0;
        while (total < bytes.Length)
        {
            int count = stream.Read(bytes, total, bytes.Length - total);
            if (count == 0) break;
            total += count;
        }
        if (total == 0 || total > maximum) throw new InvalidOperationException("Directory file size is invalid.");
        return bytes.AsSpan(0, total).ToArray();
    }

    public static DirectoryFeatureOptions Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaxConfigBytes)
            throw new InvalidOperationException("Directory config size is invalid.");
        DirectoryFeatureOptions? options;
        try
        {
            options = JsonSerializer.Deserialize<DirectoryFeatureOptions>(utf8, StrictJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException("Directory config is not valid strict JSON.", ex);
        }
        if (options is null)
            throw new InvalidOperationException("Directory config is empty.");
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (Version != 1)
            throw new InvalidOperationException("Directory config version must be 1.");
        if (!DirectoryProtocol.IsValidRegistryIdentity(RegistryIdentity))
            throw new InvalidOperationException("Directory registryIdentity is invalid.");
        if (string.IsNullOrWhiteSpace(StatePath) || !Path.IsPathFullyQualified(StatePath))
            throw new InvalidOperationException("Directory statePath must be absolute.");
        if (Grants is null || Grants.Count > MaxGrants)
            throw new InvalidOperationException("Directory grants are missing or exceed the limit.");

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in Grants)
        {
            if (grant is null)
                throw new InvalidOperationException("Directory grant entry is null.");
            if (!IsLowerHex64(grant.GrantHashSha256))
                throw new InvalidOperationException("Directory grant hash must be 64 lowercase hex characters.");
            if (!DirectoryProtocol.IsValidAlias(grant.Alias))
                throw new InvalidOperationException("Directory grant alias is invalid.");
            if (grant.ExpiresAtUnixSeconds < 0)
                throw new InvalidOperationException("Directory grant expiry is invalid.");
            try
            {
                ServerProfile.ParseJson(grant.ProfileJson);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException("Directory grant profileJson is invalid.", ex);
            }
            if (!hashes.Add(grant.GrantHashSha256))
                throw new InvalidOperationException("Duplicate directory grant hash.");
            if (!aliases.Add(grant.Alias))
                throw new InvalidOperationException("Duplicate directory grant alias.");
        }
    }

    internal static bool IsLowerHex64(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (char c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    internal static string Sha256Hex(string secret) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
