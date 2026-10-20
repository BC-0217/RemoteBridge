using System;
using System.Text;

namespace PeerDesk.Windows
{
    /// <summary>Explicit, permission-checked text payload endpoint. It never polls or writes the OS clipboard.</summary>
    public sealed class TextClipboardCoordinator
    {
        public const int MaxUtf8Bytes = 64 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly Func<TransferIdentity, bool> permissionIsCurrent;
        private readonly Action<TransferIdentity, string> deliverText;

        public TextClipboardCoordinator(Func<TransferIdentity, bool> permissionIsCurrent, Action<TransferIdentity, string> deliverText)
        {
            if (permissionIsCurrent == null) throw new ArgumentNullException("permissionIsCurrent");
            if (deliverText == null) throw new ArgumentNullException("deliverText");
            this.permissionIsCurrent = permissionIsCurrent;
            this.deliverText = deliverText;
        }

        public byte[] SendText(TransferIdentity identity, string text)
        {
            ValidateIdentity(identity);
            EnsurePermission(identity);
            ValidateText(text);
            byte[] payload = StrictUtf8.GetBytes(text);
            EnsurePermission(identity);
            return payload;
        }

        public void AcceptText(TransferIdentity identity, byte[] utf8)
        {
            ValidateIdentity(identity);
            EnsurePermission(identity);
            if (utf8 == null || utf8.Length > MaxUtf8Bytes)
                throw new ArgumentException("Text payload exceeds the 64 KiB limit.", "utf8");
            string text;
            try { text = StrictUtf8.GetString(utf8); }
            catch (DecoderFallbackException ex) { throw new ArgumentException("Text payload is not strict UTF-8.", "utf8", ex); }
            ValidateText(text);
            // The consumer must execute permission revalidation and OS clipboard write on the same UI actor.
            EnsurePermission(identity);
            deliverText(identity, text);
        }

        private void EnsurePermission(TransferIdentity identity)
        {
            if (!permissionIsCurrent(identity)) throw new UnauthorizedAccessException("Text permission/session identity is not current.");
        }

        private static void ValidateIdentity(TransferIdentity identity)
        {
            if (identity == null || String.IsNullOrWhiteSpace(identity.PeerId) ||
                String.IsNullOrWhiteSpace(identity.ServerId) || String.IsNullOrWhiteSpace(identity.SessionId) ||
                String.IsNullOrWhiteSpace(identity.TransferId) || identity.PermissionRevision < 0 || identity.SessionEpoch < 0)
                throw new ArgumentException("Invalid peer/server/session/permission identity.", "identity");
        }

        private static void ValidateText(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            byte[] bytes;
            try { bytes = StrictUtf8.GetBytes(text); }
            catch (EncoderFallbackException ex) { throw new ArgumentException("Text contains invalid Unicode.", "text", ex); }
            if (bytes.Length > MaxUtf8Bytes) throw new ArgumentException("Text payload exceeds the 64 KiB limit.", "text");
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
                    throw new ArgumentException("Text contains a disallowed control character.", "text");
                if (Char.IsSurrogate(c))
                {
                    if (!Char.IsHighSurrogate(c) || i + 1 >= text.Length || !Char.IsLowSurrogate(text[++i]))
                        throw new ArgumentException("Text contains invalid Unicode.", "text");
                }
            }
        }
    }
}
