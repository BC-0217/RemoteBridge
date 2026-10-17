using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerDesk.Server;

/// <summary>
/// Persisted owner state (v1). Contains only the display name, the salted
/// PBKDF2-SHA256 join password verifier and the owner room server IDs (primary plus
/// retained aliases); never the owner secret or plaintext. Files written before the
/// room fields existed load with both room fields absent (null).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OwnerState
{
    public const int MinIterations = 100_000;
    public const int MaxIterations = 10_000_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const int MaxServerIds = 8;

    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
    [JsonPropertyName("joinSaltBase64")] public string? JoinSaltBase64 { get; init; }
    [JsonPropertyName("joinHashBase64")] public string? JoinHashBase64 { get; init; }
    [JsonPropertyName("joinIterations")] public int JoinIterations { get; init; }
    [JsonPropertyName("primaryServerId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryServerId { get; init; }
    [JsonPropertyName("serverIds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ServerIds { get; init; }

    [JsonIgnore] public bool JoinPasswordConfigured => JoinHashBase64 is not null;

    public void Validate()
    {
        if (Version != 1)
            throw new InvalidOperationException("Owner state version must be 1.");
        if (DisplayName is not null && !OwnerFeature.IsValidDisplayName(DisplayName))
            throw new InvalidOperationException("Owner state displayName is invalid.");
        ValidateServerIds();

        bool anyJoin = JoinSaltBase64 is not null || JoinHashBase64 is not null || JoinIterations != 0;
        if (!anyJoin)
        {
            if (ServerIds is not null) throw new InvalidOperationException("Owner room requires a configured join verifier.");
            return;
        }
        if (JoinSaltBase64 is null || JoinHashBase64 is null)
            throw new InvalidOperationException("Owner state join verifier is incomplete.");
        if (JoinIterations < MinIterations || JoinIterations > MaxIterations)
            throw new InvalidOperationException("Owner state join iterations out of range.");
        if (DecodeExact(JoinSaltBase64, SaltBytes) is null || DecodeExact(JoinHashBase64, HashBytes) is null)
            throw new InvalidOperationException("Owner state join verifier is malformed.");
    }

    private void ValidateServerIds()
    {
        int count = ServerIds?.Count ?? 0;
        if (count == 0)
        {
            if (PrimaryServerId is not null || ServerIds is not null)
                throw new InvalidOperationException("Owner state server IDs are inconsistent.");
            return;
        }
        if (count > MaxServerIds)
            throw new InvalidOperationException("Owner state server IDs exceed the limit.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ServerIds!)
        {
            // Persisted IDs must already be canonical; no normalization on load.
            if (!PeerDesk.Core.DirectoryProtocol.IsValidAlias(id))
                throw new InvalidOperationException("Owner state server ID is invalid.");
            if (!seen.Add(id))
                throw new InvalidOperationException("Owner state server IDs contain duplicates.");
        }
        if (PrimaryServerId is null || !seen.Contains(PrimaryServerId))
            throw new InvalidOperationException("Owner state primary server ID is not in the server ID list.");
    }

    internal static byte[]? DecodeExact(string value, int length)
    {
        if (value.Length > 64) return null;
        var buffer = new byte[length + 3];
        if (!Convert.TryFromBase64String(value, buffer, out int written) || written != length)
            return null;
        return buffer.AsSpan(0, length).ToArray();
    }
}

/// <summary>
/// Atomic bounded file store for <see cref="OwnerState"/>. Malformed files fail
/// loudly (startup failure); there is no silent reset. On Unix the file is 0600.
/// </summary>
public sealed class OwnerStateStore
{
    public const int MaxStateBytes = 64 * 1024;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public OwnerStateStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Owner statePath must be an absolute path.");
        _path = path;
    }

    /// <summary>Returns the persisted state, or an empty state when the file does not exist.</summary>
    public OwnerState Load()
    {
        if (!File.Exists(_path))
            return new OwnerState();

        byte[] bytes = OwnerFeatureOptions.ReadBounded(_path, MaxStateBytes);
        OwnerState? state;
        try
        {
            StrictJson.RejectDuplicateKeys(bytes);
            state = JsonSerializer.Deserialize<OwnerState>(bytes, OwnerFeatureOptions.StrictOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("Owner state file is malformed.", ex);
        }

        if (state is null)
            throw new InvalidOperationException("Owner state file is malformed.");
        state.Validate();
        return state;
    }

    /// <summary>Synchronous atomic write for short critical sections (no await while coordinated locks are held).</summary>
    public void Save(OwnerState state)
    {
        state.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, WriteOptions);
        if (bytes.Length > MaxStateBytes)
            throw new InvalidOperationException("Owner state exceeds size limit.");

        _writeGate.Wait();
        string dir = Path.GetDirectoryName(_path)!;
        string temp = Path.Combine(dir, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var fso = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(temp, fso))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _writeGate.Release();
        }
    }

    /// <summary>Writes the state atomically (temp file + flush + replace). Throws on failure.</summary>
    public async Task SaveAsync(OwnerState state, CancellationToken ct)
    {
        state.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, WriteOptions);
        if (bytes.Length > MaxStateBytes)
            throw new InvalidOperationException("Owner state exceeds size limit.");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        string dir = Path.GetDirectoryName(_path)!;
        string temp = Path.Combine(dir, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var fso = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(temp, fso))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _writeGate.Release();
        }
    }
}
