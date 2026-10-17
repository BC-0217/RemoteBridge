using System.Text.Json;
using System.Text.Json.Serialization;
using PeerDesk.Core;

namespace PeerDesk.Server;

/// <summary>
/// Owner feature configuration (v1). Loaded from a bounded strict JSON file.
/// The owner secret is never persisted by the server or echoed in responses.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class OwnerFeatureOptions
{
    public const int MaxConfigBytes = 64 * 1024;
    public const int MinOwnerSecretLength = 32;
    public const int MaxOwnerSecretLength = 512;
    public const int MaxRegistryIdentityLength = 128;

    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("ownerSecret")] public string OwnerSecret { get; set; } = string.Empty;
    [JsonPropertyName("statePath")] public string StatePath { get; set; } = string.Empty;
    [JsonPropertyName("publicProfileJson")] public string PublicProfileJson { get; set; } = string.Empty;
    [JsonPropertyName("registryIdentity")] public string RegistryIdentity { get; set; } = string.Empty;
    /// <summary>
    /// Optional (default false). Enables owner-room local self-publishing only when a same-process
    /// directory has the same registry identity and the configured profile pin matches the TLS certificate.
    /// </summary>
    [JsonPropertyName("allowSelfPublish")] public bool AllowSelfPublish { get; set; }

    internal static readonly JsonSerializerOptions StrictOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    public static OwnerFeatureOptions Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Owner config path must be absolute.");

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new InvalidOperationException("Owner config file does not exist.");
        if (info.Length > MaxConfigBytes)
            throw new InvalidOperationException("Owner config exceeds 64 KiB.");

        byte[] bytes = ReadBounded(path, MaxConfigBytes);
        OwnerFeatureOptions? options;
        try
        {
            StrictJson.RejectDuplicateKeys(bytes);
            options = JsonSerializer.Deserialize<OwnerFeatureOptions>(bytes, StrictOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("Owner config is not valid strict JSON.", ex);
        }

        if (options is null)
            throw new InvalidOperationException("Owner config is empty.");
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (Version != 1)
            throw new InvalidOperationException("Owner config version must be 1.");
        if (OwnerSecret is null || OwnerSecret.Length < MinOwnerSecretLength || OwnerSecret.Length > MaxOwnerSecretLength)
            throw new InvalidOperationException($"Owner secret must be {MinOwnerSecretLength}..{MaxOwnerSecretLength} characters.");
        foreach (char c in OwnerSecret)
        {
            if (c < 0x21 || c > 0x7E)
                throw new InvalidOperationException("Owner secret must be printable ASCII without whitespace.");
        }
        if (string.IsNullOrWhiteSpace(StatePath) || !Path.IsPathFullyQualified(StatePath))
            throw new InvalidOperationException("Owner statePath must be an absolute path.");
        if (!IsValidRegistryIdentity(RegistryIdentity))
            throw new InvalidOperationException("Owner registryIdentity is invalid.");
        try
        {
            ServerProfile.ParseJson(PublicProfileJson);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("Owner publicProfileJson is not a valid profile.", ex);
        }
    }

    public ServerProfile ParsedProfile() => ServerProfile.ParseJson(PublicProfileJson);

    internal static bool IsValidRegistryIdentity(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxRegistryIdentityLength)
            return false;
        foreach (char c in id)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.' || c == ':';
            if (!ok) return false;
        }
        return true;
    }

    internal static byte[] ReadBounded(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[maxBytes + 1];
        int total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;
        if (total > maxBytes)
            throw new InvalidOperationException("File exceeds size limit.");
        return buffer.AsSpan(0, total).ToArray();
    }
}

internal static class StrictJson
{
    /// <summary>Rejects duplicate property names at any depth.</summary>
    public static void RejectDuplicateKeys(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });
        var stack = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    stack.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    stack.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    stack.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var set = stack.Peek();
                    if (set is not null && !set.Add(reader.GetString()!))
                        throw new ArgumentException("Duplicate JSON property.");
                    break;
            }
        }
    }
}
