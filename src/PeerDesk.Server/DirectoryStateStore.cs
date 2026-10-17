using System.Collections.Immutable;
using System.Text.Json;
using PeerDesk.Core;

namespace PeerDesk.Server;

public sealed record DirectoryStateEntry(
    string Alias,
    string ProfileJson,
    string GrantHashSha256,
    long PublishedAtUnixSeconds);

/// <summary>Persisted directory state: published profiles plus the grant hash each one consumed.</summary>
public sealed record DirectoryStateFile(
    int Version,
    string RegistryIdentity,
    IReadOnlyList<DirectoryStateEntry> Entries);

/// <summary>
/// Immutable in-memory snapshot. A new snapshot replaces the old one only after it has been
/// persisted atomically, so a failed write leaves memory unchanged.
/// </summary>
public sealed class DirectoryState
{
    public static readonly DirectoryState Empty = new(ImmutableDictionary<string, DirectoryStateEntry>.Empty.WithComparers(StringComparer.Ordinal));

    public ImmutableDictionary<string, DirectoryStateEntry> ByAlias { get; }
    public ImmutableHashSet<string> ConsumedGrantHashes { get; }

    private DirectoryState(ImmutableDictionary<string, DirectoryStateEntry> byAlias)
    {
        ByAlias = byAlias;
        ConsumedGrantHashes = byAlias.Values.Select(e => e.GrantHashSha256).ToImmutableHashSet(StringComparer.Ordinal);
    }

    public int Count => ByAlias.Count;

    public DirectoryState With(DirectoryStateEntry entry) => new(ByAlias.Add(entry.Alias, entry));
}

public sealed class DirectoryStateStore
{
    public const int MaxStateBytes = 1024 * 1024;
    public const int MaxEntries = 256;

    private readonly string _path;
    private readonly DirectoryFeatureOptions _options;

    public DirectoryStateStore(DirectoryFeatureOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _path = options.StatePath;
    }

    /// <summary>Missing file means empty state; any malformed or inconsistent file fails closed.</summary>
    public DirectoryState Load()
    {
        var info = new FileInfo(_path);
        if (!info.Exists) return DirectoryState.Empty;
        if (info.Length <= 0 || info.Length > MaxStateBytes)
            throw new InvalidOperationException("Directory state file size is invalid.");

        DirectoryStateFile? file;
        try
        {
            file = JsonSerializer.Deserialize<DirectoryStateFile>(DirectoryFeatureOptions.ReadBounded(_path, MaxStateBytes), DirectoryFeatureOptions.StrictJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException("Directory state file is not valid strict JSON.", ex);
        }
        if (file is null || file.Version != 1)
            throw new InvalidOperationException("Directory state file version is invalid.");
        if (!string.Equals(file.RegistryIdentity, _options.RegistryIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("Directory state registry identity does not match configuration.");
        if (file.Entries is null || file.Entries.Count > MaxEntries)
            throw new InvalidOperationException("Directory state entries are missing or exceed the limit.");

        var grantsByHash = _options.Grants.ToDictionary(g => g.GrantHashSha256, StringComparer.Ordinal);
        var state = DirectoryState.Empty;
        foreach (var entry in file.Entries)
        {
            if (entry is null)
                throw new InvalidOperationException("Directory state entry is null.");
            ValidateEntry(entry);
            if (state.ByAlias.ContainsKey(entry.Alias))
                throw new InvalidOperationException("Duplicate alias in directory state.");
            if (state.ConsumedGrantHashes.Contains(entry.GrantHashSha256))
                throw new InvalidOperationException("Duplicate consumed grant in directory state.");
            if (grantsByHash.TryGetValue(entry.GrantHashSha256, out var grant) &&
                (!string.Equals(grant.Alias, entry.Alias, StringComparison.Ordinal) ||
                 !string.Equals(grant.ProfileJson, entry.ProfileJson, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Directory state is inconsistent with configured grant alias/pin.");
            }
            var aliasGrant = _options.Grants.FirstOrDefault(g => string.Equals(g.Alias, entry.Alias, StringComparison.Ordinal));
            if (aliasGrant is not null && !string.Equals(aliasGrant.GrantHashSha256, entry.GrantHashSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Directory state alias is bound to a different grant.");
            state = state.With(entry);
        }
        return state;
    }

    private static void ValidateEntry(DirectoryStateEntry entry)
    {
        if (!DirectoryProtocol.IsValidAlias(entry.Alias))
            throw new InvalidOperationException("Directory state alias is invalid.");
        if (!DirectoryFeatureOptions.IsLowerHex64(entry.GrantHashSha256))
            throw new InvalidOperationException("Directory state grant hash is invalid.");
        if (entry.PublishedAtUnixSeconds <= 0)
            throw new InvalidOperationException("Directory state timestamp is invalid.");
        try
        {
            ServerProfile.ParseJson(entry.ProfileJson);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("Directory state profile is invalid.", ex);
        }
    }

    /// <summary>Writes the full state to a private temp file, flushes, then atomically replaces the state file.</summary>
    public void Save(DirectoryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Count > MaxEntries)
            throw new InvalidOperationException("Directory state exceeds entry limit.");

        var file = new DirectoryStateFile(
            1,
            _options.RegistryIdentity,
            state.ByAlias.Values.OrderBy(e => e.Alias, StringComparer.Ordinal).ToList());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(file, DirectoryFeatureOptions.StrictJson);
        if (bytes.Length > MaxStateBytes)
            throw new InvalidOperationException("Directory state exceeds size limit.");

        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Directory state path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var stream = new FileStream(temp, streamOptions))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
