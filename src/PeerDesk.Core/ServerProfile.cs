using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PeerDesk.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServerProfile
{
    public int Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public string RendezvousUrl { get; init; } = string.Empty;
    public string RelayUrl { get; init; } = string.Empty;
    public string CertificateSha256 { get; init; } = string.Empty;

    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    // Max byte length of the JSON payload (16 KB)
    private const int MaxJsonBytes = 16_384;

    public static ServerProfile ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Profile JSON cannot be empty.");

        // Count bytes without allocating a byte array
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxJsonBytes)
            throw new ArgumentException("Profile payload exceeds 16 KB limit.");

        ServerProfile? profile;
        try
        {
            // Use Utf8JsonReader to detect duplicate keys before full deserialisation
            DetectDuplicateKeys(json);
            profile = JsonSerializer.Deserialize<ServerProfile>(json, StrictOptions);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Invalid profile JSON or unknown/duplicate properties present.", ex);
        }

        if (profile == null)
            throw new InvalidOperationException("Failed to deserialize profile.");

        profile.Validate();
        return profile;
    }

    // Detect duplicate top-level keys via a raw reader pass.
    private static void DetectDuplicateKeys(string json)
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        int depth = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) depth++;
            else if (reader.TokenType == JsonTokenType.EndObject) depth--;
            else if (reader.TokenType == JsonTokenType.PropertyName && depth == 1)
            {
                string key = reader.GetString()!;
                if (!seen.Add(key))
                    throw new ArgumentException($"Duplicate property '{key}' in profile JSON.");
            }
        }
    }

    public void Validate()
    {
        if (Version != 1)
            throw new ArgumentException("ServerProfile Version must be 1.");

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64)
            throw new ArgumentException("Name is required and must be at most 64 characters.");

        ValidateHttpsUrl(RendezvousUrl, nameof(RendezvousUrl));
        ValidateHttpsUrl(RelayUrl, nameof(RelayUrl));

        if (string.IsNullOrWhiteSpace(CertificateSha256) ||
            CertificateSha256.Length != 64 ||
            !Regex.IsMatch(CertificateSha256, "^[0-9a-fA-F]{64}$"))
        {
            throw new ArgumentException("CertificateSha256 must be exactly 64 hex characters.");
        }
    }

    private static void ValidateHttpsUrl(string url, string paramName)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
            throw new ArgumentException($"{paramName} is required.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"{paramName} is not a valid absolute URL.");

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{paramName} must use HTTPS.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException($"{paramName} must not contain userinfo.");

        if (!string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException($"{paramName} must not contain a query string.");

        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException($"{paramName} must not contain a fragment.");

        // Reject paths beyond the bare root (no path segments allowed in a profile URL)
        if (uri.AbsolutePath != "/")
            throw new ArgumentException($"{paramName} must not contain a path.");
    }
}
