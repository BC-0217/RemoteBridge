using System;

namespace PeerDesk.SessionProtocol
{
    /// <summary>
    /// Logical channel a payload belongs to. Exact values are encoded as a single byte.
    /// </summary>
    public enum WireChannel : byte
    {
        Control = 0,
        Screen = 1,
        Input = 2,
        Clipboard = 3,
        File = 4
    }

    /// <summary>
    /// Immutable plaintext envelope: a fixed 44-byte network-byte-order header followed
    /// by a raw payload. This is FRAMING ONLY. It cannot mark a payload as encrypted or
    /// authenticated. Any confidentiality/integrity must be provided by an outer layer
    /// (pinned TLS) and the per-channel permission checks; the header itself provides no
    /// cryptographic protection.
    /// </summary>
    public sealed class WireEnvelope
    {
        /// <summary>4-byte ASCII magic: "RB01".</summary>
        public const int MagicLength = 4;
        public const string MagicAscii = "RB01";
        public static byte[] MagicBytes { get { return new byte[] { (byte)'R', (byte)'B', (byte)'0', (byte)'1' }; } }

        /// <summary>Total header size in bytes.</summary>
        public const int HeaderLength = 44;

        /// <summary>Current wire protocol version.</summary>
        public const byte CurrentVersion = 1;

        /// <summary>Header flags are reserved and must be 0 in this version.</summary>
        public const ushort AllowedHeaderFlags = 0;

        /// <summary>Channel the payload belongs to.</summary>
        public WireChannel Channel { get; }

        /// <summary>Header flags (reserved, currently 0).</summary>
        public ushort Flags { get; }

        /// <summary>16-byte session ID copied into the header.</summary>
        private readonly byte[] _SessionId;
        public byte[] SessionId { get { return (byte[])_SessionId.Clone(); } }

        /// <summary>Positive epoch from the binding.</summary>
        public long Epoch { get; }

        /// <summary>Positive monotonic sequence number within (direction, channel, epoch).</summary>
        public long Sequence { get; }

        /// <summary>Immutable payload bytes (never null).</summary>
        private readonly byte[] _Payload;
        public byte[] Payload { get { return (byte[])_Payload.Clone(); } }

        public WireEnvelope(WireChannel channel, ushort flags, byte[] sessionId, long epoch, long sequence, byte[] payload)
        {
            if (channel != WireChannel.Control && channel != WireChannel.Screen
                && channel != WireChannel.Input && channel != WireChannel.Clipboard
                && channel != WireChannel.File)
                throw new ArgumentException("unknown channel", nameof(channel));
            if (flags != AllowedHeaderFlags)
                throw new ArgumentException("flags must be 0 in this version", nameof(flags));
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (sessionId.Length != 16) throw new ArgumentException("session ID must be 16 bytes", nameof(sessionId));
            if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch), "epoch must be positive");
            if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence), "sequence must be positive");
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            int limit = MaxPayloadForChannel(channel);
            checked
            {
                if (payload.Length > limit)
                    throw new ArgumentException("payload exceeds channel limit " + limit, nameof(payload));
            }

            Channel = channel;
            Flags = flags;
            _SessionId = (byte[])sessionId.Clone();
            Epoch = epoch;
            Sequence = sequence;
            _Payload = (byte[])payload.Clone();
        }

        /// <summary>Maximum payload length allowed for a given channel.</summary>
        public static int MaxPayloadForChannel(WireChannel channel)
        {
            switch (channel)
            {
                case WireChannel.Control: return 16 * 1024;       // 16 KiB
                case WireChannel.Screen: return 512 * 1024;       // 512 KiB
                case WireChannel.Input: return 16 * 1024;         // 16 KiB
                case WireChannel.Clipboard: return 64 * 1024;    // 64 KiB
                case WireChannel.File: return 256 * 1024;        // 256 KiB
                default: throw new ArgumentOutOfRangeException(nameof(channel), "unknown channel");
            }
        }
    }

    /// <summary>
    /// Stateless encoder/decoder for <see cref="WireEnvelope"/>. All multi-byte header
    /// fields are written/read in network byte order (big-endian). Decode validates the
    /// header exactly: magic, version, known channel, reserved flags, session ID length,
    /// positive epoch, positive sequence and exact payload length with no trailing bytes.
    /// All arithmetic is checked for overflow.
    /// </summary>
    public static class WireCodec
    {
        /// <summary>
        /// Encodes a <see cref="WireEnvelope"/> into a new byte array: 44-byte header
        /// followed by the payload. Throws on any invalid field.
        /// </summary>
        public static byte[] Encode(WireEnvelope envelope)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            byte[] payload = envelope.Payload;
            int total;
            checked
            {
                total = WireEnvelope.HeaderLength + payload.Length;
            }
            byte[] buf = new byte[total];
            int o = 0;
            // magic 4 ASCII
            buf[o++] = (byte)'R';
            buf[o++] = (byte)'B';
            buf[o++] = (byte)'0';
            buf[o++] = (byte)'1';
            // version uint8
            buf[o++] = WireEnvelope.CurrentVersion;
            // channel uint8
            buf[o++] = (byte)envelope.Channel;
            // flags uint16 BE
            buf[o++] = (byte)(envelope.Flags >> 8);
            buf[o++] = (byte)(envelope.Flags & 0xFF);
            // session ID 16 bytes
            Buffer.BlockCopy(envelope.SessionId, 0, buf, o, 16);
            o += 16;
            // epoch uint64 BE
            ulong epoch = (ulong)envelope.Epoch;
            buf[o++] = (byte)(epoch >> 56);
            buf[o++] = (byte)(epoch >> 48);
            buf[o++] = (byte)(epoch >> 40);
            buf[o++] = (byte)(epoch >> 32);
            buf[o++] = (byte)(epoch >> 24);
            buf[o++] = (byte)(epoch >> 16);
            buf[o++] = (byte)(epoch >> 8);
            buf[o++] = (byte)(epoch & 0xFF);
            // sequence uint64 BE
            ulong seq = (ulong)envelope.Sequence;
            buf[o++] = (byte)(seq >> 56);
            buf[o++] = (byte)(seq >> 48);
            buf[o++] = (byte)(seq >> 40);
            buf[o++] = (byte)(seq >> 32);
            buf[o++] = (byte)(seq >> 24);
            buf[o++] = (byte)(seq >> 16);
            buf[o++] = (byte)(seq >> 8);
            buf[o++] = (byte)(seq & 0xFF);
            // payloadLength uint32 BE
            uint plen = (uint)payload.Length;
            buf[o++] = (byte)(plen >> 24);
            buf[o++] = (byte)(plen >> 16);
            buf[o++] = (byte)(plen >> 8);
            buf[o++] = (byte)(plen & 0xFF);
            // payload
            Buffer.BlockCopy(payload, 0, buf, o, payload.Length);
            return buf;
        }

        /// <summary>
        /// Decodes exactly one envelope from <paramref name="buffer"/> at the given offset.
        /// Returns the number of bytes consumed (header + payload) via <paramref name="consumed"/>.
        /// Throws if the header is malformed, the payload is truncated/oversized, or trailing
        /// bytes are present within the consumed region. The caller must supply a buffer that
        /// contains exactly one envelope; trailing extra bytes after the payload are rejected.
        /// </summary>
        public static WireEnvelope Decode(byte[] buffer, int offset, out int consumed)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));

            // Need at least the header.
            checked
            {
                if (buffer.Length - offset < WireEnvelope.HeaderLength)
                    throw new ArgumentException("truncated header", nameof(buffer));
            }

            int o = offset;
            // magic
            if (buffer[o] != (byte)'R' || buffer[o + 1] != (byte)'B'
                || buffer[o + 2] != (byte)'0' || buffer[o + 3] != (byte)'1')
                throw new ArgumentException("bad magic", nameof(buffer));
            o += 4;
            // version
            byte version = buffer[o++];
            if (version != WireEnvelope.CurrentVersion)
                throw new ArgumentException("unsupported version " + version, nameof(buffer));
            // channel
            byte ch = buffer[o++];
            WireChannel channel;
            switch (ch)
            {
                case 0: channel = WireChannel.Control; break;
                case 1: channel = WireChannel.Screen; break;
                case 2: channel = WireChannel.Input; break;
                case 3: channel = WireChannel.Clipboard; break;
                case 4: channel = WireChannel.File; break;
                default: throw new ArgumentException("unknown channel " + ch, nameof(buffer));
            }
            // flags uint16 BE
            ushort flags = (ushort)(((int)buffer[o] << 8) | buffer[o + 1]);
            o += 2;
            if (flags != WireEnvelope.AllowedHeaderFlags)
                throw new ArgumentException("unsupported flags " + flags, nameof(buffer));
            // session ID 16 bytes
            byte[] sessionId = new byte[16];
            Buffer.BlockCopy(buffer, o, sessionId, 0, 16);
            o += 16;
            // epoch uint64 BE
            ulong epoch = ((ulong)buffer[o] << 56) | ((ulong)buffer[o + 1] << 48)
                | ((ulong)buffer[o + 2] << 40) | ((ulong)buffer[o + 3] << 32)
                | ((ulong)buffer[o + 4] << 24) | ((ulong)buffer[o + 5] << 16)
                | ((ulong)buffer[o + 6] << 8) | (ulong)buffer[o + 7];
            o += 8;
            if (epoch == 0 || epoch > (ulong)long.MaxValue)
                throw new ArgumentException("epoch must be positive and <= long.MaxValue", nameof(buffer));
            // sequence uint64 BE
            ulong seq = ((ulong)buffer[o] << 56) | ((ulong)buffer[o + 1] << 48)
                | ((ulong)buffer[o + 2] << 40) | ((ulong)buffer[o + 3] << 32)
                | ((ulong)buffer[o + 4] << 24) | ((ulong)buffer[o + 5] << 16)
                | ((ulong)buffer[o + 6] << 8) | (ulong)buffer[o + 7];
            o += 8;
            if (seq == 0 || seq > (ulong)long.MaxValue)
                throw new ArgumentException("sequence must be positive and <= long.MaxValue", nameof(buffer));
            // payloadLength uint32 BE
            uint plen = (uint)(((int)buffer[o] << 24) | ((int)buffer[o + 1] << 16)
                | ((int)buffer[o + 2] << 8) | buffer[o + 3]);
            o += 4;

            int limit = WireEnvelope.MaxPayloadForChannel(channel);
            // Validate payload length against channel limit using checked arithmetic.
            if (plen > (uint)limit)
                throw new ArgumentException("payload length " + plen + " exceeds channel limit " + limit, nameof(buffer));

            int payloadLen;
            checked { payloadLen = (int)plen; }

            // Need payloadLen more bytes available.
            checked
            {
                if (buffer.Length - o != payloadLen)
                    throw new ArgumentException("truncated or trailing payload", nameof(buffer));
            }

            byte[] payload = new byte[payloadLen];
            if (payloadLen > 0)
                Buffer.BlockCopy(buffer, o, payload, 0, payloadLen);
            o += payloadLen;

            consumed = o - offset;
            return new WireEnvelope(channel, flags, sessionId, (long)epoch, (long)seq, payload);
        }
    }
}
