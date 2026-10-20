using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using PeerDesk.Windows;

internal static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateSymbolicLinkW")]
    private static extern bool CreateSymbolicLink(string linkPath, string targetPath, int flags);
    private static int checks;
    private static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception("FAIL: " + name); }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        checks++;
        try { action(); } catch (T) { return; }
        throw new Exception("FAIL: expected " + typeof(T).Name + ": " + name);
    }
    private static string Hash(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerDesk.TransferEndpointSmoke." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            bool permitted = true, accepted = true;
            var id = new TransferIdentity("peer", "server", "session", Guid.NewGuid().ToString("N"), 4, 7);
            var receiver = new FileTransferCoordinator(root, x => permitted, x => accepted);
            byte[] body = Encoding.UTF8.GetBytes("transfer bytes ✓");
            string hash = Hash(body);

            accepted = false;
            Throws<UnauthorizedAccessException>(() => receiver.AcceptIncoming(id, "a.txt", body.Length, hash), "declined incoming");
            Check(Directory.GetFiles(root).Length == 0, "deny produces zero writes");
            accepted = true;

            var progress = receiver.AcceptIncoming(id, "a.txt", body.Length, hash);
            Check(progress.Offset == 0 && File.Exists(progress.PartialPath), "accepted transfer creates random partial");
            Throws<InvalidDataException>(() => receiver.AppendChunk(id, 0, body, new string('0', 64)), "chunk hash tamper");
            Check(new FileInfo(progress.PartialPath).Length == 0, "tampered chunk writes nothing");
            Throws<InvalidOperationException>(() => receiver.AppendChunk(id, 1, body, hash), "bad offset rejected");

            int split = Math.Min(body.Length, 4);
            byte[] first = new byte[split]; Buffer.BlockCopy(body, 0, first, 0, split);
            progress = receiver.AppendChunk(id, 0, first, Hash(first));
            Throws<InvalidDataException>(() => receiver.Resume(id, progress.Offset, new string('0', 64)), "prefix tamper");
            receiver.Cancel(id);
            Throws<InvalidOperationException>(() => receiver.AppendChunk(id, progress.Offset, new byte[] { 0 }, Hash(new byte[] { 0 })), "cancel pauses transfer");
            var restarted = new FileTransferCoordinator(root, x => permitted, x => accepted);
            byte[] savedPartial = File.ReadAllBytes(progress.PartialPath);
            using (var tamper = new FileStream(progress.PartialPath, FileMode.Open, FileAccess.Write, FileShare.None))
            { tamper.Position = 0; tamper.WriteByte((byte)(savedPartial[0] ^ 0xff)); tamper.Flush(true); }
            Throws<InvalidDataException>(() => restarted.ResumeFresh(id, "a.txt", body.Length, hash, progress.PartialPath, progress.Offset, Hash(first)), "fresh resume detects partial tamper");
            File.WriteAllBytes(progress.PartialPath, savedPartial);
            progress = restarted.ResumeFresh(id, "a.txt", body.Length, hash, progress.PartialPath, progress.Offset, Hash(first));
            receiver = restarted;
            Check(progress.Offset == split, "fresh-instance explicit resume");
            byte[] rest = new byte[body.Length - split]; Buffer.BlockCopy(body, split, rest, 0, rest.Length);
            progress = receiver.AppendChunk(id, split, rest, Hash(rest));
            permitted = false;
            Throws<UnauthorizedAccessException>(() => receiver.Complete(id, Path.Combine(root, "a.txt")), "permission revocation");
            Throws<UnauthorizedAccessException>(() => receiver.GetProgress(id), "revoked permission hides progress");
            permitted = true;
            string final = receiver.Complete(id, Path.Combine(root, "a.txt"));
            Check(File.ReadAllBytes(final).Length == body.Length, "verified atomic completion");
            var idExisting = new TransferIdentity("peer", "server", "session", Guid.NewGuid().ToString("N"), 4, 7);
            receiver.AcceptIncoming(idExisting, "a.txt", body.Length, hash);
            receiver.AppendChunk(idExisting, 0, body, hash);
            Throws<IOException>(() => receiver.Complete(idExisting, final), "existing destination overwrite forbidden");

            var id2 = new TransferIdentity("peer", "server", "session", Guid.NewGuid().ToString("N"), 4, 7);
            Throws<ArgumentException>(() => receiver.AcceptIncoming(id2, "..\\escape.txt", 1, Hash(new byte[] { 1 })), "traversal name");
            Throws<ArgumentException>(() => receiver.AcceptIncoming(id2, "x.txt:stream", 1, Hash(new byte[] { 1 })), "ADS name");
            Throws<ArgumentException>(() => receiver.AcceptIncoming(id2, "CON.txt", 1, Hash(new byte[] { 1 })), "reserved name");
            Check(Directory.GetFiles(root).Length == 3, "invalid descriptors create no extra partials");

            var idLink = new TransferIdentity("peer", "server", "session", Guid.NewGuid().ToString("N"), 4, 7);
            var linkReceiver = new FileTransferCoordinator(root, x => permitted, x => accepted);
            var linkState = linkReceiver.AcceptIncoming(idLink, "link.txt", 1, Hash(new byte[] { 1 }));
            File.Delete(linkState.PartialPath);
            if (CreateSymbolicLink(linkState.PartialPath, final, 0))
                Throws<UnauthorizedAccessException>(() => linkReceiver.AppendChunk(idLink, 0, new byte[] { 1 }, Hash(new byte[] { 1 })), "reparse partial blocked");
            else
                Console.WriteLine("SKIP reparse symlink fixture: CreateSymbolicLink unavailable, error=" + Marshal.GetLastWin32Error());

            int delivered = 0;
            var text = new TextClipboardCoordinator(x => permitted && x.PermissionRevision == 4, (identity, value) => delivered++);
            byte[] validText = text.SendText(id, "hello 😀");
            text.AcceptText(id, validText);
            Check(delivered == 1, "explicit text roundtrip");
            var stale = new TransferIdentity("peer", "server", "session", "text", 3, 7);
            Throws<UnauthorizedAccessException>(() => text.SendText(stale, "x"), "stale text permission");
            Throws<ArgumentException>(() => text.AcceptText(id, new byte[] { 0xc3, 0x28 }), "invalid UTF8");
            Throws<ArgumentException>(() => text.SendText(id, "bad\u0001"), "control character");
            Check(delivered == 1, "failed text did not deliver");
            Console.WriteLine("PASS checks=" + checks);
        }
        finally
        {
            // This test owns this uniquely-created temp directory and removes only its own generated fixtures.
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
