using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PeerDesk.Windows
{
    /// <summary>
    /// Local file-transfer endpoint. The transport owns serialization/networking and passes immutable metadata
    /// plus byte chunks; this class does not create a network connection.
    /// </summary>
    public sealed class FileTransferCoordinator
    {
        public const long MaxFileBytes = 256L * 1024 * 1024;
        public const int MaxChunkBytes = 64 * 1024;
        public const int MaxConcurrentTransfers = 2;
        private readonly string root;
        private readonly Func<TransferIdentity, bool> permissionIsCurrent;
        private readonly Func<TransferIdentity, bool> acceptIncoming;
        private readonly object gate = new object();
        private readonly Dictionary<string, TransferState> transfers = new Dictionary<string, TransferState>(StringComparer.Ordinal);

        public FileTransferCoordinator(string userSelectedRoot, Func<TransferIdentity, bool> permissionIsCurrent,
            Func<TransferIdentity, bool> acceptIncoming)
        {
            if (String.IsNullOrWhiteSpace(userSelectedRoot)) throw new ArgumentException("A user-selected destination root is required.", "userSelectedRoot");
            if (permissionIsCurrent == null) throw new ArgumentNullException("permissionIsCurrent");
            if (acceptIncoming == null) throw new ArgumentNullException("acceptIncoming");
            root = Path.GetFullPath(userSelectedRoot);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Destination root must already exist.");
            RejectReparseComponents(root, true);
            this.permissionIsCurrent = permissionIsCurrent;
            this.acceptIncoming = acceptIncoming;
        }

        /// <summary>Creates a receiver after consent. Consent/permission callbacks run under a coordinator lock and must be nonblocking; obtain UI consent asynchronously before calling and pass a one-use identity-bound result.</summary>
        public TransferProgress AcceptIncoming(TransferIdentity identity, string displayName, long size, string sha256)
        {
            ValidateIdentity(identity);
            ValidateDescriptor(displayName, size, sha256);
            lock (gate)
            {
                EnsurePermission(identity);
                if (transfers.ContainsKey(identity.TransferId)) throw new InvalidOperationException("Transfer ID already exists.");
                if (transfers.Count >= MaxConcurrentTransfers) throw new InvalidOperationException("Concurrent transfer limit reached.");
                if (!acceptIncoming(identity)) throw new UnauthorizedAccessException("Incoming file transfer was declined.");
                EnsurePermission(identity);
                RejectReparseComponents(root, true);
                string partial = Path.Combine(root, "." + Guid.NewGuid().ToString("N") + ".partial");
                using (new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                var state = new TransferState(identity, displayName, size, sha256.ToLowerInvariant(), partial);
                state.CheckpointPath = partial + ".checkpoint";
                transfers.Add(identity.TransferId, state);
                try { SaveCheckpoint(state); }
                catch { transfers.Remove(identity.TransferId); File.Delete(partial); throw; }
                return Snapshot(state);
            }
        }

        /// <summary>Append one contiguous chunk. Offset must equal the actual on-disk length and announced offset.</summary>
        public TransferProgress AppendChunk(TransferIdentity identity, long offset, byte[] chunk, string chunkSha256)
        {
            if (chunk == null || chunk.Length == 0 || chunk.Length > MaxChunkBytes) throw new ArgumentException("Chunk must be 1..64 KiB.", "chunk");
            lock (gate)
            {
                TransferState s = Get(identity);
                EnsurePermission(identity);
                if (s.Completed) throw new InvalidOperationException("Transfer is already complete.");
                if (s.Paused) throw new InvalidOperationException("Transfer is paused; explicit resume is required.");
                EnsureSafeIoPath(s.PartialPath);
                EnsureSafeIoPath(s.CheckpointPath);
                if (!File.Exists(s.PartialPath)) throw new UnauthorizedAccessException("Partial file is missing or an unresolved reparse point.");
                if (offset != s.Offset || new FileInfo(s.PartialPath).Length != s.Offset)
                    throw new InvalidOperationException("Chunk offset does not match checkpoint and actual partial length.");
                if (s.Offset + chunk.Length > s.Size) throw new InvalidDataException("Chunk exceeds announced file size.");
                if (!FixedTimeEquals(Hash(chunk), NormalizeHash(chunkSha256)))
                    throw new InvalidDataException("Chunk SHA-256 mismatch.");
                using (var stream = new FileStream(s.PartialPath, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    stream.Seek(s.Offset, SeekOrigin.Begin);
                    stream.Write(chunk, 0, chunk.Length);
                    stream.Flush(true);
                }
                long previousOffset = s.Offset;
                s.Offset += chunk.Length;
                s.Chunks.Add(new ChunkRecord(chunk.Length, Hash(chunk)));
                try { SaveCheckpoint(s); }
                catch
                {
                    s.Offset = previousOffset;
                    s.Chunks.RemoveAt(s.Chunks.Count - 1);
                    EnsureSafeIoPath(s.PartialPath);
                    using (var rollback = new FileStream(s.PartialPath, FileMode.Open, FileAccess.Write, FileShare.None))
                    { rollback.SetLength(previousOffset); rollback.Flush(true); }
                    throw;
                }
                return Snapshot(s);
            }
        }

        /// <summary>
        /// Resume after fresh consent/permission, including a new process instance. Partial length, checkpoint identity,
        /// per-chunk hashes, and prefix hash must match; no resume occurs after identity changes.
        /// </summary>
        public TransferProgress ResumeFresh(TransferIdentity identity, string displayName, long size, string sha256,
            string exactPartialPath, long acknowledgedOffset, string prefixSha256)
        {
            ValidateIdentity(identity);
            ValidateDescriptor(displayName, size, sha256);
            lock (gate)
            {
                EnsurePermission(identity);
                if (transfers.ContainsKey(identity.TransferId)) throw new InvalidOperationException("Transfer ID already exists.");
                if (transfers.Count >= MaxConcurrentTransfers) throw new InvalidOperationException("Concurrent transfer limit reached.");
                string partial = Path.GetFullPath(exactPartialPath);
                ValidateSafePath(partial);
                if (!String.Equals(Path.GetDirectoryName(partial), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                    !partial.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Select the exact partial file under the chosen root.");
                if (!acceptIncoming(identity)) throw new UnauthorizedAccessException("Resume was declined.");
                EnsurePermission(identity);
                EnsureSafeIoPath(partial);
                string checkpoint = partial + ".checkpoint";
                EnsureSafeIoPath(checkpoint);
                TransferState state = LoadCheckpoint(identity, displayName, size, sha256.ToLowerInvariant(), partial);
                var fi = new FileInfo(partial);
                if (!fi.Exists || fi.Length != acknowledgedOffset || acknowledgedOffset != state.Offset || acknowledgedOffset > size)
                    throw new InvalidDataException("Partial file does not match acknowledged offset.");
                VerifyChunks(state);
                if (!FixedTimeEquals(HashPrefix(partial, acknowledgedOffset), NormalizeHash(prefixSha256)))
                    throw new InvalidDataException("Partial file prefix hash mismatch.");
                EnsurePermission(identity);
                state.Paused = false;
                transfers.Add(identity.TransferId, state);
                return Snapshot(state);
            }
        }

        public TransferProgress Resume(TransferIdentity identity, long acknowledgedOffset, string prefixSha256)
        {
            lock (gate)
            {
                TransferState s = Get(identity);
                EnsurePermission(identity);
                if (!acceptIncoming(identity)) throw new UnauthorizedAccessException("Resume was declined.");
                EnsurePermission(identity);
                EnsureSafeIoPath(s.PartialPath);
                EnsureSafeIoPath(s.CheckpointPath);
                var fi = new FileInfo(s.PartialPath);
                if (!fi.Exists || fi.Length != acknowledgedOffset || acknowledgedOffset != s.Offset || acknowledgedOffset > s.Size)
                    throw new InvalidDataException("Partial file does not match acknowledged offset.");
                if (!FixedTimeEquals(HashPrefix(s.PartialPath, acknowledgedOffset), NormalizeHash(prefixSha256)))
                    throw new InvalidDataException("Partial file prefix hash mismatch.");
                s.Paused = false;
                return Snapshot(s);
            }
        }

        /// <summary>Marks a paused transfer; the random .partial file is retained for explicit resume.</summary>
        public void Cancel(TransferIdentity identity)
        {
            lock (gate) { TransferState s = Get(identity); EnsurePermission(identity); s.Paused = true; }
        }

        /// <summary>Verifies complete content and atomically moves to the exact user-selected destination without overwrite.</summary>
        public string Complete(TransferIdentity identity, string exactDestinationPath)
        {
            lock (gate)
            {
                TransferState s = Get(identity);
                EnsurePermission(identity);
                if (s.Paused) throw new InvalidOperationException("Transfer is paused; explicit resume is required.");
                EnsureSafeIoPath(s.PartialPath);
                EnsureSafeIoPath(s.CheckpointPath);
                if (!File.Exists(s.PartialPath)) throw new UnauthorizedAccessException("Partial file is missing or an unresolved reparse point.");
                if (s.Offset != s.Size || new FileInfo(s.PartialPath).Length != s.Size)
                    throw new InvalidDataException("Transfer is incomplete.");
                EnsureSafeIoPath(s.PartialPath);
                EnsureSafeIoPath(s.CheckpointPath);
                if (!FixedTimeEquals(HashFile(s.PartialPath), s.Sha256)) throw new InvalidDataException("Whole-file SHA-256 mismatch.");
                string dest = Path.GetFullPath(exactDestinationPath);
                ValidateDescriptor(Path.GetFileName(dest), s.Size, s.Sha256);
                if (!String.Equals(Path.GetDirectoryName(dest), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Destination must be an exact child of the user-selected root.");
                ValidateSafePath(dest);
                if (File.Exists(dest) || Directory.Exists(dest)) throw new IOException("Destination already exists; overwrite is forbidden.");
                // Recheck just before commit; File.Move in the same directory is atomic and does not overwrite.
                EnsurePermission(identity);
                RejectReparseComponents(root, true);
                EnsureSafeIoPath(s.PartialPath);
                EnsureSafeIoPath(s.CheckpointPath);
                EnsureSafeIoPath(dest);
                if (File.Exists(dest) || Directory.Exists(dest)) throw new IOException("Destination already exists; overwrite is forbidden.");
                File.Move(s.PartialPath, dest);
                try { File.Delete(s.CheckpointPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                s.Completed = true;
                transfers.Remove(identity.TransferId);
                return dest;
            }
        }

        public TransferProgress GetProgress(TransferIdentity identity)
        {
            lock (gate) { EnsurePermission(identity); TransferState s = Get(identity); EnsurePermission(identity); return Snapshot(s); }
        }

        private TransferState Get(TransferIdentity identity)
        {
            ValidateIdentity(identity);
            TransferState s;
            if (!transfers.TryGetValue(identity.TransferId, out s) || !s.Identity.Equals(identity))
                throw new UnauthorizedAccessException("Transfer identity/session/permission revision changed; transfer cannot resume.");
            return s;
        }

        private void EnsurePermission(TransferIdentity identity)
        {
            if (!permissionIsCurrent(identity)) throw new UnauthorizedAccessException("Transfer permission is not current.");
        }

        private static void ValidateIdentity(TransferIdentity id)
        {
            if (id == null || String.IsNullOrWhiteSpace(id.PeerId) || String.IsNullOrWhiteSpace(id.ServerId) ||
                String.IsNullOrWhiteSpace(id.SessionId) || String.IsNullOrWhiteSpace(id.TransferId) ||
                id.PeerId.Length > 256 || id.ServerId.Length > 256 || id.SessionId.Length > 256 ||
                id.TransferId.Length > 128 || id.PermissionRevision < 0 || id.SessionEpoch < 0)
                throw new ArgumentException("Invalid transfer identity.");
        }

        private static void ValidateDescriptor(string name, long size, string hash)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 255 || name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOf(':') >= 0 ||
                name == "." || name == ".." || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
                throw new ArgumentException("Display filename must be a safe basename.");
            string stem = name.Split('.')[0];
            if (Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new ArgumentException("Reserved Windows filename.");
            if (size < 0 || size > MaxFileBytes) throw new ArgumentOutOfRangeException("size");
            NormalizeHash(hash);
        }

        private static string NormalizeHash(string hash)
        {
            if (hash == null || !Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$")) throw new ArgumentException("Expected SHA-256 hex.");
            return hash.ToLowerInvariant();
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        private static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        private static string HashPrefix(string path, long length)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[MaxChunkBytes];
                long remaining = length;
                while (remaining > 0)
                {
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new EndOfStreamException();
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(sha.Hash);
            }
        }

        private static string Hex(byte[] bytes) { return String.Concat(bytes.Select(b => b.ToString("x2"))); }
        private static byte[] HexToBytes(string value)
        {
            var bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static void ValidateSafePath(string path)
        {
            if (ContainsAlternateDataStream(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new UnauthorizedAccessException("ADS or invalid path denied.");
            string full = Path.GetFullPath(path);
            string name = Path.GetFileName(full);
            if (name.IndexOf("..", StringComparison.Ordinal) >= 0 || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Unsafe path denied.");
            RejectReparseComponents(Path.GetDirectoryName(full), true);
        }

        private static bool ContainsAlternateDataStream(string path)
        {
            string volumeRoot = Path.GetPathRoot(path) ?? String.Empty;
            return path.IndexOf(':', volumeRoot.Length) >= 0;
        }

        private void EnsureSafeIoPath(string filePath)
        {
            string full = Path.GetFullPath(filePath);
            string parent = Path.GetDirectoryName(full);
            if (!String.Equals(parent, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Transfer I/O must remain directly under the selected root.");
            RejectReparseComponents(parent, true);
            try
            {
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Reparse-point transfer file denied.");
            }
            catch (FileNotFoundException)
            {
                // A dangling reparse entry is not File.Exists(); detect the directory entry itself and fail closed.
                if (Directory.GetFileSystemEntries(parent, Path.GetFileName(full)).Length != 0)
                    throw new UnauthorizedAccessException("Dangling reparse-point transfer file denied.");
            }
        }

        private static void RejectReparseComponents(string directory, bool includeDirectory)
        {
            string current = Path.GetFullPath(directory);
            var chain = new Stack<string>();
            while (!String.IsNullOrEmpty(current))
            {
                chain.Push(current);
                string parent = Path.GetDirectoryName(current);
                if (String.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }
            while (chain.Count > 0)
            {
                string part = chain.Pop();
                if (Directory.Exists(part) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Reparse-point path denied.");
            }
        }

        private void SaveCheckpoint(TransferState s)
        {
            EnsureSafeIoPath(s.PartialPath);
            EnsureSafeIoPath(s.CheckpointPath);
            if (s.Chunks.Count > 4096) throw new InvalidDataException("Checkpoint chunk bound exceeded.");
            string temp = s.CheckpointPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                EnsureSafeIoPath(temp);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
                {
                    writer.Write(new byte[] { 0x52, 0x42, 0x54, 0x31 }); writer.Write((byte)1);
                    WriteBounded(writer, s.Identity.PeerId, 256); WriteBounded(writer, s.Identity.ServerId, 256);
                    WriteBounded(writer, s.Identity.SessionId, 256); WriteBounded(writer, s.Identity.TransferId, 128);
                    writer.Write(s.Identity.PermissionRevision); writer.Write(s.Identity.SessionEpoch);
                    WriteBounded(writer, s.DisplayName, 255); writer.Write(s.Size); WriteBounded(writer, s.Sha256, 64);
                    writer.Write(s.Offset); writer.Write(s.Chunks.Count);
                    foreach (ChunkRecord chunk in s.Chunks) { writer.Write(chunk.Length); writer.Write(HexToBytes(chunk.Hash)); }
                    writer.Flush(); stream.Flush(true);
                    if (stream.Length > 1024 * 1024) throw new InvalidDataException("Checkpoint exceeds 1 MiB.");
                }
                if (File.Exists(s.CheckpointPath)) File.Replace(temp, s.CheckpointPath, null);
                else File.Move(temp, s.CheckpointPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static TransferState LoadCheckpoint(TransferIdentity identity, string name, long size, string sha, string partial)
        {
            string checkpoint = partial + ".checkpoint";
            var info = new FileInfo(checkpoint);
            if (!info.Exists || info.Length > 1024 * 1024) throw new InvalidDataException("Checkpoint missing or exceeds 1 MiB.");
            using (var stream = new FileStream(checkpoint, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                byte[] magic = reader.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != 0x52 || magic[1] != 0x42 || magic[2] != 0x54 || magic[3] != 0x31 || reader.ReadByte() != 1)
                    throw new InvalidDataException("Unknown checkpoint version.");
                string peer = ReadBounded(reader, 256), server = ReadBounded(reader, 256), session = ReadBounded(reader, 256), transfer = ReadBounded(reader, 128);
                long revision = reader.ReadInt64(), epoch = reader.ReadInt64();
                string storedName = ReadBounded(reader, 255); long storedSize = reader.ReadInt64();
                string storedHash = ReadBounded(reader, 64); long offset = reader.ReadInt64(); int count = reader.ReadInt32();
                if (peer != identity.PeerId || server != identity.ServerId || session != identity.SessionId ||
                    transfer != identity.TransferId || revision != identity.PermissionRevision || epoch != identity.SessionEpoch ||
                    storedName != name || storedSize != size || storedHash != sha || count < 0 || count > 4096 || offset < 0 || offset > size)
                    throw new InvalidDataException("Checkpoint identity or offer mismatch.");
                var state = new TransferState(identity, name, size, sha, partial);
                state.CheckpointPath = checkpoint;
                long computed = 0;
                for (int i = 0; i < count; i++)
                {
                    int length = reader.ReadInt32(); byte[] hash = reader.ReadBytes(32);
                    if (length <= 0 || length > MaxChunkBytes || hash.Length != 32 || computed + length > size)
                        throw new InvalidDataException("Invalid checkpoint chunk record.");
                    computed += length;
                    state.Chunks.Add(new ChunkRecord(length, Hex(hash)));
                }
                if (stream.Position != stream.Length || computed != offset) throw new InvalidDataException("Checkpoint is noncanonical or offset inconsistent.");
                state.Offset = offset;
                state.Paused = true;
                return state;
            }
        }

        private void VerifyChunks(TransferState state)
        {
            EnsureSafeIoPath(state.PartialPath);
            using (var stream = new FileStream(state.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[MaxChunkBytes];
                foreach (ChunkRecord chunk in state.Chunks)
                {
                    int readTotal = 0;
                    while (readTotal < chunk.Length)
                    {
                        int read = stream.Read(buffer, readTotal, chunk.Length - readTotal);
                        if (read == 0) throw new InvalidDataException("Partial file truncated within a checkpoint chunk.");
                        readTotal += read;
                    }
                    byte[] exact = new byte[chunk.Length]; Buffer.BlockCopy(buffer, 0, exact, 0, chunk.Length);
                    if (!FixedTimeEquals(Hash(exact), chunk.Hash)) throw new InvalidDataException("Partial file chunk hash mismatch.");
                }
                if (stream.Position != state.Offset) throw new InvalidDataException("Partial file/checkpoint offset mismatch.");
            }
        }

        private static void WriteBounded(BinaryWriter writer, string value, int maxChars)
        {
            if (value == null || value.Length > maxChars) throw new InvalidDataException("Checkpoint field exceeds bound.");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
            if (bytes.Length > maxChars * 4) throw new InvalidDataException("Checkpoint field exceeds byte bound.");
            writer.Write(bytes.Length); writer.Write(bytes);
        }

        private static string ReadBounded(BinaryReader reader, int maxChars)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maxChars * 4) throw new InvalidDataException("Invalid checkpoint string length.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            string value = new UTF8Encoding(false, true).GetString(bytes);
            if (value.Length > maxChars) throw new InvalidDataException("Checkpoint field exceeds character bound.");
            return value;
        }

        private static TransferProgress Snapshot(TransferState s)
        {
            return new TransferProgress(s.Identity, s.Offset, s.Size, s.Paused, s.PartialPath);
        }

        private sealed class ChunkRecord
        {
            public readonly int Length; public readonly string Hash;
            public ChunkRecord(int length, string hash) { Length = length; Hash = hash; }
        }

        private sealed class TransferState
        {
            public readonly TransferIdentity Identity; public readonly string DisplayName, Sha256, PartialPath; public readonly long Size;
            public readonly List<ChunkRecord> Chunks = new List<ChunkRecord>();
            public string CheckpointPath;
            public long Offset; public bool Paused, Completed;
            public TransferState(TransferIdentity identity, string name, long size, string sha, string path)
            { Identity = identity; DisplayName = name; Size = size; Sha256 = sha; PartialPath = path; }
        }
    }

    /// <summary>Immutable binding for transport messages; include all fields in the authenticated message.</summary>
    public sealed class TransferIdentity : IEquatable<TransferIdentity>
    {
        public string PeerId { get; private set; }
        public string ServerId { get; private set; }
        public string SessionId { get; private set; }
        public string TransferId { get; private set; }
        public long PermissionRevision { get; private set; }
        public long SessionEpoch { get; private set; }

        public TransferIdentity(string peerId, string serverId, string sessionId, string transferId, long permissionRevision, long sessionEpoch)
        {
            PeerId = peerId; ServerId = serverId; SessionId = sessionId; TransferId = transferId;
            PermissionRevision = permissionRevision; SessionEpoch = sessionEpoch;
        }
        public bool Equals(TransferIdentity other)
        {
            return other != null && PeerId == other.PeerId && ServerId == other.ServerId && SessionId == other.SessionId &&
                TransferId == other.TransferId && PermissionRevision == other.PermissionRevision && SessionEpoch == other.SessionEpoch;
        }
        public override bool Equals(object obj) { return Equals(obj as TransferIdentity); }
        public override int GetHashCode() { return (PeerId + "|" + ServerId + "|" + SessionId + "|" + TransferId + "|" + PermissionRevision + "|" + SessionEpoch).GetHashCode(); }
    }

    public sealed class TransferProgress
    {
        public TransferIdentity Identity { get; private set; }
        public long Offset { get; private set; }
        public long Size { get; private set; }
        public bool Paused { get; private set; }
        public string PartialPath { get; private set; }
        internal TransferProgress(TransferIdentity id, long offset, long size, bool paused, string path)
        { Identity = id; Offset = offset; Size = size; Paused = paused; PartialPath = path; }
    }
}
