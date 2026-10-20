using System;

namespace PeerDesk.SessionProtocol
{
    /// <summary>
    /// Negotiated capability flags for a session channel. Unknown bits are rejected
    /// by the parser so that future versions cannot silently introduce behavior the
    /// receiver does not understand. <see cref="SecurityRequired"/> flags can never be
    /// downgraded: a value with fewer security bits than the local requirement is rejected.
    /// No delta/tile encoding is supported in this version.
    /// </summary>
    [Flags]
    public enum SessionPermissions : int
    {
        None = 0,
        Screen = 1,
        Input = 2,
        Clipboard = 4,
        FileTransfer = 8,
        MonitorControl = 16
    }

    /// <summary>
    /// Capability negotiation record exchanged before any media payload. Version 1 only.
    /// <see cref="MaxFrameLength"/> is bounded to 524288 (512 KiB). <see cref="SecurityRequired"/>
    /// must be a subset of <see cref="Permissions"/> and can never be removed, only added.
    /// </summary>
    public sealed class CapabilityNegotiation
    {
        public const int CurrentVersion = 1;
        public const int AbsoluteMaxFrameLength = 524288;

        public int Version { get; }
        public SessionPermissions Permissions { get; }
        public SessionPermissions SecurityRequired { get; }
        public int MaxFrameLength { get; }

        public CapabilityNegotiation(int version, SessionPermissions permissions, SessionPermissions securityRequired, int maxFrameLength)
        {
            if (version != CurrentVersion)
                throw new ArgumentException("capability negotiation version must be " + CurrentVersion, nameof(version));
            ValidateKnownFlags(permissions, nameof(permissions));
            ValidateKnownFlags(securityRequired, nameof(securityRequired));
            // SecurityRequired must be a subset of Permissions.
            if ((securityRequired & ~permissions) != SessionPermissions.None)
                throw new ArgumentException("securityRequired must be a subset of permissions", nameof(securityRequired));
            if (maxFrameLength <= 0 || maxFrameLength > AbsoluteMaxFrameLength)
                throw new ArgumentOutOfRangeException(nameof(maxFrameLength), "maxFrameLength must be in 1.." + AbsoluteMaxFrameLength);

            Version = version;
            Permissions = permissions;
            SecurityRequired = securityRequired;
            MaxFrameLength = maxFrameLength;
        }

        /// <summary>
        /// Returns true if the remote <paramref name="offered"/> capabilities are acceptable:
        /// version matches, no unknown bits, offered permissions are a superset of local
        /// security requirements, and the offered max frame length does not exceed the local
        /// bound. SecurityRequired is never downgraded: the local requirement must be met.
        /// </summary>
        public bool Accepts(CapabilityNegotiation offered)
        {
            if (offered == null) return false;
            if (offered.Version != Version) return false;
            if ((offered.Permissions & AllKnownFlags) != offered.Permissions) return false;
            if ((offered.SecurityRequired & ~AllKnownFlags) != SessionPermissions.None) return false;
            // Local security requirement must be satisfied by the offered permissions.
            if ((offered.Permissions & this.SecurityRequired) != this.SecurityRequired) return false;
            if (offered.MaxFrameLength > this.MaxFrameLength) return false;
            return true;
        }

        /// <summary>All known flag bits for this version.</summary>
        public static readonly SessionPermissions AllKnownFlags =
            SessionPermissions.Screen | SessionPermissions.Input | SessionPermissions.Clipboard |
            SessionPermissions.FileTransfer | SessionPermissions.MonitorControl;

        internal static void ValidateKnownFlags(SessionPermissions flags, string name)
        {
            if ((flags & ~AllKnownFlags) != SessionPermissions.None)
                throw new ArgumentException(name + " contains unknown flag bits", name);
        }
    }
}
