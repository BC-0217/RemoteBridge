using System;

namespace PeerDesk.SessionProtocol
{
    /// <summary>
    /// Immutable binding that uniquely identifies a RemoteBridge session and the
    /// negotiated context in which every <see cref="WireEnvelope"/> must be interpreted.
    /// This is a plaintext framing contract: it does NOT authenticate peer identity,
    /// encrypt media, or grant permissions by itself. Authentication, transport security
    /// and per-channel authorization are external concerns. The fields here are compared
    /// by the receiver before any payload is dispatched.
    /// </summary>
    public sealed class SessionBinding
    {
        /// <summary>Exactly 16 bytes identifying the session.</summary>
        private readonly byte[] _SessionId;
        public byte[] SessionId { get { return (byte[])_SessionId.Clone(); } }

        /// <summary>
        /// The local participant's ID (1..64 ASCII chars). This is NOT an authenticated
        /// identity; it is a bounded label used for direction and history.
        /// </summary>
        public string OwnId { get; }

        /// <summary>
        /// The remote participant's ID (1..64 ASCII chars). NOT an authenticated identity.
        /// </summary>
        public string PeerId { get; }

        /// <summary>
        /// 32-byte full SHA-256 pin of the relay server's TLS certificate. Compared
        /// byte-for-byte against any server presenting frames for this session.
        /// </summary>
        private readonly byte[] _ServerFullPin;
        public byte[] ServerFullPin { get { return (byte[])_ServerFullPin.Clone(); } }

        /// <summary>
        /// 32-byte immutable peer identity pin. Exact-match required before dispatch.
        /// Provided by an external authority lease; not authenticated by this contract.
        /// </summary>
        private readonly byte[] _PeerIdentityPin;
        public byte[] PeerIdentityPin { get { return (byte[])_PeerIdentityPin.Clone(); } }

        /// <summary>
        /// Canonical HTTPS root origin (e.g. "https://relay.example.com") that the
        /// session is bound to. Must not be null or empty.
        /// </summary>
        public string Origin { get; }

        /// <summary>Sender role of the local participant.</summary>
        public SessionRole LocalRole { get; }

        /// <summary>
        /// Positive local epoch. A new epoch requires a fresh <see cref="SessionBinding"/>
        /// instance; it cannot be advanced silently on an existing one.
        /// </summary>
        public long Epoch { get; }

        /// <summary>
        /// Non-negative credential revision. Exact-match required before dispatch so that
        /// a stale credential context cannot inject frames into a refreshed session.
        /// </summary>
        public long CredentialRevision { get; }

        /// <summary>
        /// Positive permission revision. Exact-match required before dispatch.
        /// </summary>
        public long PermissionRevision { get; }

        /// <summary>
        /// Positive geometry revision. Exact-match required before dispatch so that
        /// screen frames decoded under an old geometry are rejected.
        /// </summary>
        public long GeometryVersion { get; }

        public SessionBinding(
            byte[] sessionId,
            string ownId,
            string peerId,
            byte[] serverFullPin,
            byte[] peerIdentityPin,
            string origin,
            SessionRole localRole,
            long epoch,
            long credentialRevision,
            long permissionRevision,
            long geometryVersion)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (sessionId.Length != 16) throw new ArgumentException("session ID must be exactly 16 bytes", nameof(sessionId));
            if (string.IsNullOrEmpty(ownId)) throw new ArgumentException("ownId must not be empty", nameof(ownId));
            if (string.IsNullOrEmpty(peerId)) throw new ArgumentException("peerId must not be empty", nameof(peerId));
            ValidateAsciiBounded(ownId, nameof(ownId));
            ValidateAsciiBounded(peerId, nameof(peerId));
            if (serverFullPin == null) throw new ArgumentNullException(nameof(serverFullPin));
            if (serverFullPin.Length != 32) throw new ArgumentException("serverFullPin must be exactly 32 bytes", nameof(serverFullPin));
            if (peerIdentityPin == null) throw new ArgumentNullException(nameof(peerIdentityPin));
            if (peerIdentityPin.Length != 32) throw new ArgumentException("peerIdentityPin must be exactly 32 bytes", nameof(peerIdentityPin));
            if (string.IsNullOrEmpty(origin)) throw new ArgumentException("origin must not be empty", nameof(origin));
            if (!IsCanonicalHttpsOrigin(origin)) throw new ArgumentException("origin must be a canonical https:// origin", nameof(origin));
            if (localRole != SessionRole.Initiator && localRole != SessionRole.Target)
                throw new ArgumentException("localRole must be Initiator or Target", nameof(localRole));
            if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch), "epoch must be positive");
            if (credentialRevision < 0) throw new ArgumentOutOfRangeException(nameof(credentialRevision), "credentialRevision must be non-negative");
            if (permissionRevision <= 0) throw new ArgumentOutOfRangeException(nameof(permissionRevision), "permissionRevision must be positive");
            if (geometryVersion <= 0) throw new ArgumentOutOfRangeException(nameof(geometryVersion), "geometryVersion must be positive");

            _SessionId = (byte[])sessionId.Clone();
            OwnId = ownId;
            PeerId = peerId;
            _ServerFullPin = (byte[])serverFullPin.Clone();
            _PeerIdentityPin = (byte[])peerIdentityPin.Clone();
            Origin = origin;
            LocalRole = localRole;
            Epoch = epoch;
            CredentialRevision = credentialRevision;
            PermissionRevision = permissionRevision;
            GeometryVersion = geometryVersion;
        }

        private static void ValidateAsciiBounded(string value, string name)
        {
            if (value.Length > 64) throw new ArgumentException(name + " exceeds 64 characters", name);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < 0x21 || c > 0x7E)
                    throw new ArgumentException(name + " must be printable ASCII (0x21..0x7E)", name);
            }
        }

        private static bool IsCanonicalHttpsOrigin(string origin)
        {
            Uri uri;
            if (origin.Length > 2048 || !Uri.TryCreate(origin, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Host.Length == 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
            return String.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns the sender role for the local participant. The remote participant
        /// has the opposite role.
        /// </summary>
        public SessionRole RemoteRole
        {
            get { return LocalRole == SessionRole.Initiator ? SessionRole.Target : SessionRole.Initiator; }
        }

        /// <summary>
        /// Exact-match check for all binding context fields that must agree before any
        /// payload dispatch: session ID, epoch, peer identity pin, credential revision,
        /// permission revision and geometry version.
        /// </summary>
        public bool ContextMatches(SessionBinding other)
        {
            if (other == null) return false;
            return OwnId == other.OwnId && PeerId == other.PeerId && LocalRole == other.LocalRole
                && Origin == other.Origin && ConstantTimeEquals(_ServerFullPin, other._ServerFullPin)
                && ConstantTimeEquals(_SessionId, other._SessionId)
                && Epoch == other.Epoch
                && ConstantTimeEquals(PeerIdentityPin, other.PeerIdentityPin)
                && CredentialRevision == other.CredentialRevision
                && PermissionRevision == other.PermissionRevision
                && GeometryVersion == other.GeometryVersion;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /// <summary>Sender role within a session.</summary>
    public enum SessionRole : byte
    {
        /// <summary>The party that initiated the session request.</summary>
        Initiator = 1,
        /// <summary>The party being controlled / sharing the screen.</summary>
        Target = 2
    }
}
