using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PeerDesk.Server;

/// <summary>
/// One persisted device: server-issued ID, SHA-256 of the bearer token (64 uppercase hex),
/// UTC registration time, the highest password generation ever committed, and (v2) the fixed
/// password revision with its optional PBKDF2-SHA256 verifier (salt/hash hex, iterations).
/// Never contains raw bearer tokens, plaintext passwords, or temporary password salts/hashes.
/// </summary>
public sealed record PersistedDevice(string DeviceId, string TokenHashSha256, DateTimeOffset RegisteredAt, long PasswordGenerationFloor,
    long FixedPasswordRevision = 0, string? FixedPasswordSaltHex = null, string? FixedPasswordHashHex = null, int FixedPasswordIterations = 0);

/// <summary>Raised when a device registry mutation could not be durably committed.</summary>
public sealed class DeviceStatePersistenceException : Exception
{
    public DeviceStatePersistenceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Strict, bounded JSON store for the device registry. Writes are atomic same-directory
/// temp-file replacements. The target must already exist (see <see cref="Initialize"/>);
/// the store never creates a missing state file on its own. No anti-rollback or
/// power-failure guarantees are made.
/// </summary>
public sealed class DeviceRegistryStateStore
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    // Writes are always v2; v1 (exact four device fields) is still accepted and migrated on save.
    public const int SchemaVersion = 2;
    public const int LegacySchemaVersion = 1;
    private const string RegisteredAtFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode GroupOtherMask =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly string _path;
    private readonly int _maxDevices;

    public DeviceRegistryStateStore(string path, int maxDevices)
    {
        _path = ValidatePath(path);
        if (maxDevices < 1 || maxDevices > ServerOptions.MaxDevicesLimit)
            throw new ArgumentOutOfRangeException(nameof(maxDevices));
        _maxDevices = maxDevices;
    }

    public string Path => _path;

    /// <summary>Loads and strictly validates the state. Missing/corrupt input throws.</summary>
    public IReadOnlyList<PersistedDevice> LoadRequired()
    {
        CheckDirectory(_path);
        CheckExistingFile(_path);
        byte[] bytes = ReadBounded(_path);
        return Parse(bytes, _maxDevices);
    }

    /// <summary>
    /// Atomically replaces the existing state file with <paramref name="entries"/>.
    /// The previous file is preserved unchanged if any step fails.
    /// </summary>
    public void Save(IReadOnlyList<PersistedDevice> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        byte[] bytes = Serialize(entries, _maxDevices);
        CheckDirectory(_path);
        CheckExistingFile(_path);
        string temp = WriteTemp(_path, bytes);
        try
        {
            File.Move(temp, _path, overwrite: true);
            temp = string.Empty;
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>
    /// Creates a missing state file containing an empty valid registry. An existing target
    /// (file, directory or link) is always rejected; nothing is overwritten.
    /// </summary>
    public static void Initialize(string path, int maxDevices)
    {
        var store = new DeviceRegistryStateStore(path, maxDevices);
        CheckDirectory(store._path);
        if (PathEntryExists(store._path))
            throw new IOException("Device state target already exists; refusing to overwrite.");
        byte[] bytes = Serialize(Array.Empty<PersistedDevice>(), maxDevices);
        string temp = WriteTemp(store._path, bytes);
        try
        {
            File.Move(temp, store._path, overwrite: false);
            temp = string.Empty;
        }
        finally
        {
            DeleteQuietly(temp);
        }
        if (store.LoadRequired().Count != 0)
            throw new InvalidDataException("Device state readback after initialization was not empty.");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static string ValidatePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Length > 4096 || path.Any(char.IsControl) ||
            !System.IO.Path.IsPathFullyQualified(path) ||
            path.EndsWith(System.IO.Path.DirectorySeparatorChar) || path.EndsWith(System.IO.Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Device state path must be an absolute file path.", nameof(path));
        return System.IO.Path.GetFullPath(path);
    }

    private static bool PathEntryExists(string path)
    {
        var info = new FileInfo(path);
        return info.Exists || Directory.Exists(path) || info.LinkTarget is not null ||
               (info.Attributes != (FileAttributes)(-1) && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private static void CheckDirectory(string path)
    {
        string dir = System.IO.Path.GetDirectoryName(path)
            ?? throw new IOException("Device state path has no parent directory.");
        var info = new DirectoryInfo(dir);
        if (!info.Exists)
            throw new DirectoryNotFoundException("Device state directory does not exist.");
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Device state directory must not be a link or reparse point.");
        if (!OperatingSystem.IsWindows() && (info.UnixFileMode & GroupOtherMask) != 0)
            throw new UnauthorizedAccessException("Device state directory must be private (0700).");
    }

    private static void CheckExistingFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Device state file does not exist; initialize it explicitly.");
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Device state file must not be a link or reparse point.");
        if (!OperatingSystem.IsWindows() && (info.UnixFileMode & GroupOtherMask) != 0)
            throw new UnauthorizedAccessException("Device state file must be private (0600).");
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException("Device state file exceeds the size limit.");
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MaxFileBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxFileBytes)
            throw new InvalidDataException("Device state file exceeds the size limit.");
        return buffer.AsSpan(0, total).ToArray();
    }

    private static string WriteTemp(string path, byte[] bytes)
    {
        string dir = System.IO.Path.GetDirectoryName(path)!;
        string temp = System.IO.Path.Combine(dir,
            "." + System.IO.Path.GetFileName(path) + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp");
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = PrivateFileMode;
        try
        {
            using (var stream = new FileStream(temp, fileOptions))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(temp) & GroupOtherMask) != 0)
                throw new UnauthorizedAccessException("Device state temp file is not private.");
            return temp;
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    private static void DeleteQuietly(string temp)
    {
        if (string.IsNullOrEmpty(temp)) return;
        try { File.Delete(temp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private static byte[] Serialize(IReadOnlyList<PersistedDevice> entries, int maxDevices)
    {
        ValidateEntries(entries, maxDevices);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", SchemaVersion);
            writer.WriteStartArray("Devices");
            foreach (var e in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("DeviceId", e.DeviceId);
                writer.WriteString("TokenHashSha256", e.TokenHashSha256);
                writer.WriteString("RegisteredAt", e.RegisteredAt.UtcDateTime.ToString(RegisteredAtFormat, CultureInfo.InvariantCulture));
                writer.WriteNumber("PasswordGenerationFloor", e.PasswordGenerationFloor);
                writer.WriteNumber("FixedPasswordRevision", e.FixedPasswordRevision);
                if (e.FixedPasswordSaltHex is not null)
                {
                    writer.WriteStartObject("FixedPassword");
                    writer.WriteString("SaltHex", e.FixedPasswordSaltHex);
                    writer.WriteString("HashHex", e.FixedPasswordHashHex);
                    writer.WriteNumber("Iterations", e.FixedPasswordIterations);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.Length > MaxFileBytes)
            throw new InvalidDataException("Device state would exceed the size limit.");
        return buffer.ToArray();
    }

    private static IReadOnlyList<PersistedDevice> Parse(byte[] bytes, int maxDevices)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new InvalidDataException("Device state must not start with a byte order mark.");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Device state is not valid JSON.", ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Corrupt("root must be an object");
            var rootProps = ExactProperties(root, "Version", "Devices");
            var version = rootProps["Version"];
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || (v != SchemaVersion && v != LegacySchemaVersion))
                throw Corrupt("unsupported Version");
            var devices = rootProps["Devices"];
            if (devices.ValueKind != JsonValueKind.Array)
                throw Corrupt("Devices must be an array");
            if (devices.GetArrayLength() > maxDevices)
                throw Corrupt("too many devices");
            var result = new List<PersistedDevice>(devices.GetArrayLength());
            foreach (var item in devices.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw Corrupt("device entry must be an object");
                bool hasFixed = v == SchemaVersion && HasProperty(item, "FixedPassword");
                var p = v == LegacySchemaVersion
                    ? ExactProperties(item, "DeviceId", "TokenHashSha256", "RegisteredAt", "PasswordGenerationFloor")
                    : hasFixed
                        ? ExactProperties(item, "DeviceId", "TokenHashSha256", "RegisteredAt", "PasswordGenerationFloor", "FixedPasswordRevision", "FixedPassword")
                        : ExactProperties(item, "DeviceId", "TokenHashSha256", "RegisteredAt", "PasswordGenerationFloor", "FixedPasswordRevision");
                string id = RequireString(p["DeviceId"], "DeviceId");
                string hash = RequireString(p["TokenHashSha256"], "TokenHashSha256");
                string at = RequireString(p["RegisteredAt"], "RegisteredAt");
                if (!DateTimeOffset.TryParseExact(at, RegisteredAtFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var registeredAt))
                    throw Corrupt("RegisteredAt must be UTC");
                var floorElement = p["PasswordGenerationFloor"];
                if (floorElement.ValueKind != JsonValueKind.Number || !floorElement.TryGetInt64(out long floor))
                    throw Corrupt("PasswordGenerationFloor must be an integer");
                long fixedRevision = 0;
                string? fixedSalt = null, fixedHash = null;
                int fixedIterations = 0;
                if (v == SchemaVersion)
                {
                    var revElement = p["FixedPasswordRevision"];
                    if (revElement.ValueKind != JsonValueKind.Number || !revElement.TryGetInt64(out fixedRevision))
                        throw Corrupt("FixedPasswordRevision must be an integer");
                    if (hasFixed)
                    {
                        var f = p["FixedPassword"];
                        if (f.ValueKind != JsonValueKind.Object) throw Corrupt("FixedPassword must be an object");
                        var fp = ExactProperties(f, "SaltHex", "HashHex", "Iterations");
                        fixedSalt = RequireString(fp["SaltHex"], "SaltHex");
                        fixedHash = RequireString(fp["HashHex"], "HashHex");
                        if (fp["Iterations"].ValueKind != JsonValueKind.Number || !fp["Iterations"].TryGetInt32(out fixedIterations))
                            throw Corrupt("Iterations must be an integer");
                    }
                }
                result.Add(new PersistedDevice(id, hash, registeredAt, floor, fixedRevision, fixedSalt, fixedHash, fixedIterations));
            }
            ValidateEntries(result, maxDevices);
            return result;
        }
    }

    private static Dictionary<string, JsonElement> ExactProperties(JsonElement obj, params string[] names)
    {
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (Array.IndexOf(names, prop.Name) < 0)
                throw Corrupt("unknown property");
            if (!found.TryAdd(prop.Name, prop.Value))
                throw Corrupt("duplicate property");
        }
        if (found.Count != names.Length)
            throw Corrupt("missing property");
        return found;
    }

    private static string RequireString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw Corrupt(name + " must be a string");

    private static void ValidateEntries(IReadOnlyList<PersistedDevice> entries, int maxDevices)
    {
        if (entries.Count > maxDevices)
            throw Corrupt("too many devices");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e is null) throw Corrupt("null device entry");
            if (!IsDeviceId(e.DeviceId)) throw Corrupt("invalid DeviceId");
            if (!IsUpperHex64(e.TokenHashSha256)) throw Corrupt("invalid TokenHashSha256");
            if (e.RegisteredAt.Offset != TimeSpan.Zero) throw Corrupt("RegisteredAt must be UTC");
            if (e.PasswordGenerationFloor < 0) throw Corrupt("negative PasswordGenerationFloor");
            try { _ = checked(e.PasswordGenerationFloor + 1); }
            catch (OverflowException ex) { throw new InvalidDataException("PasswordGenerationFloor cannot advance.", ex); }
            if (e.FixedPasswordRevision < 0) throw Corrupt("negative FixedPasswordRevision");
            if ((e.FixedPasswordSaltHex is null) != (e.FixedPasswordHashHex is null)) throw Corrupt("incomplete FixedPassword");
            if (e.FixedPasswordSaltHex is not null &&
                (!IsUpperHex(e.FixedPasswordSaltHex, FixedPasswordCredential.SaltBytes * 2) ||
                 !IsUpperHex(e.FixedPasswordHashHex, FixedPasswordCredential.HashBytes * 2) ||
                 e.FixedPasswordIterations < FixedPasswordCredential.MinIterations ||
                 e.FixedPasswordIterations > FixedPasswordCredential.MaxIterations))
                throw Corrupt("invalid FixedPassword");
            if (e.FixedPasswordSaltHex is null && e.FixedPasswordIterations != 0) throw Corrupt("invalid FixedPassword");
            if (!ids.Add(e.DeviceId)) throw Corrupt("duplicate DeviceId");
            if (!hashes.Add(e.TokenHashSha256)) throw Corrupt("duplicate TokenHashSha256");
        }
    }

    public static bool IsDeviceId(string? s)
    {
        if (s is null || s.Length != 10 || s[0] < '1' || s[0] > '9') return false;
        foreach (char c in s)
            if (c < '0' || c > '9') return false;
        return true;
    }

    private static bool HasProperty(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsUpperHex64(string? s) => IsUpperHex(s, 64);

    private static bool IsUpperHex(string? s, int length)
    {
        if (s is null || s.Length != length) return false;
        foreach (char c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }

    private static InvalidDataException Corrupt(string reason) => new("Device state is invalid: " + reason + ".");
}
