using System.Text;

namespace SummerSync.Core.Net
{
    /// <summary>Constants of the wire protocol. Both ends must agree on <see cref="Version"/> to connect.</summary>
    public static class Protocol
    {
        /// <summary>Bumped on any change that makes old and new builds misunderstand each other.</summary>
        public const ushort Version = 38;

        // First byte of every datagram.
        public const byte ConnectRequest = 1;
        public const byte ConnectAccept = 2;
        public const byte ConnectReject = 3;
        public const byte Data = Connection.DataKind;
        public const byte Disconnect = 5;

        // First byte of every message carried inside a data datagram.
        public const byte MessagePlayerList = 1;
        public const byte MessageTimeRequest = 2;
        public const byte MessageTimeResponse = 3;
        public const byte MessageTestReliable = 4;
        public const byte MessageTestUnreliable = 5;

        /// <summary>Message types below this belong to the session layer; the game uses this value and above.</summary>
        public const byte FirstUserMessage = 32;

        public const byte HostPlayerId = 0;

        /// <summary>As a send target on the host: every connected client.</summary>
        public const byte Broadcast = 255;

        public const int MaxNameBytes = 64;
        public const int MaxVersionBytes = 64;
        public const int MaxReasonBytes = 200;

        /// <summary>A disconnect notice is unreliable, so it is sent a few times.</summary>
        public const int DisconnectRepeats = 3;

        /// <summary>
        /// Cuts <paramref name="text"/> so that its UTF-8 form fits in <paramref name="maxBytes"/>, without
        /// splitting a character. The receiving end refuses longer strings, so a long player name would
        /// otherwise make its owner unable to connect.
        /// </summary>
        public static string LimitText(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            char[] chars = text.ToCharArray();
            int length = chars.Length;
            while (length > 0 && (Encoding.UTF8.GetByteCount(chars, 0, length) > maxBytes || char.IsHighSurrogate(chars[length - 1])))
            {
                length--;
            }

            return length == chars.Length ? text : new string(chars, 0, length);
        }
    }

    public enum SessionRole
    {
        None,
        Host,
        Client,
    }

    public enum SessionState
    {
        Idle,
        Connecting,
        Connected,
    }

    public enum DisconnectReason : byte
    {
        None = 0,
        ClosedByUser = 1,
        HostClosed = 2,
        Timeout = 3,
        ConnectFailed = 4,
        ProtocolMismatch = 5,
        ModVersionMismatch = 6,
        GameVersionMismatch = 7,
        ServerFull = 8,
        ProtocolError = 9,
        ReliableOverflow = 10,
    }
}
