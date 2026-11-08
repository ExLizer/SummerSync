using System;
using System.Collections.Generic;
using System.Text;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.World;

namespace SummerSync.Core.Players
{
    /// <summary>Messages of <see cref="ChatReplication"/>, after the host's car picture's.</summary>
    public static class ChatWire
    {
        /// <summary>Client to host, reliable: a line the client's player wrote.</summary>
        public const byte MessageSay = ArtWire.MessagePart + 1;

        /// <summary>Host to client, reliable: a line another player wrote, with who wrote it.</summary>
        public const byte MessageLine = ArtWire.MessagePart + 2;
    }

    /// <summary>What a line of the chat is.</summary>
    public enum ChatLineKind : byte
    {
        /// <summary>A player wrote it.</summary>
        Said,

        /// <summary>A player joined the session.</summary>
        Joined,

        /// <summary>A player was already in the session this player joined.</summary>
        Present,

        /// <summary>A player left the session.</summary>
        Left,
    }

    /// <summary>One line for the chat window: something a player wrote, or a player coming or going.</summary>
    public sealed class ChatLine
    {
        public ChatLine(ChatLineKind kind, byte playerId, string name, string text, bool own)
        {
            Kind = kind;
            PlayerId = playerId;
            Name = name ?? string.Empty;
            Text = text ?? string.Empty;
            IsOwn = own;
        }

        public ChatLineKind Kind { get; }

        public byte PlayerId { get; }

        /// <summary>The player's name as the session had it when the line arrived.</summary>
        public string Name { get; }

        /// <summary>What was written; empty for a player coming or going.</summary>
        public string Text { get; }

        /// <summary>Whether this player wrote it.</summary>
        public bool IsOwn { get; }
    }

    /// <summary>What became of a line this player wrote.</summary>
    public enum ChatSayResult
    {
        Sent,

        /// <summary>Nothing left to send once it was cleaned.</summary>
        Empty,

        /// <summary>Too many lines in a short time: this one was not sent.</summary>
        TooFast,

        /// <summary>There is no session to send it to.</summary>
        NotConnected,
    }

    /// <summary>
    /// The text chat. A client sends what its player writes to the host; the host shows it and passes it on to the other
    /// clients, with the number of the player who wrote it. A line is shown here as soon as it is written, without waiting
    /// for it to come back. Players coming and going are lines too.
    ///
    /// A line is one line of plain text: what is not printable is taken out, and it is cut to
    /// <see cref="MaxCharacters"/>. Each player gets a few lines at once and then about one a second
    /// (<see cref="Burst"/>, <see cref="SecondsPerLine"/>): a client holds itself to that, and the host drops what goes over.
    /// </summary>
    public sealed class ChatReplication
    {
        public const int MaxCharacters = 200;

        /// <summary>What the longest line can take in UTF-8, after the cut to <see cref="MaxCharacters"/>.</summary>
        public const int MaxBytes = MaxCharacters * 3;

        /// <summary>Lines a player can write at once.</summary>
        public const int Burst = 5;

        /// <summary>How soon a player can write one more line once the burst is spent.</summary>
        public const double SecondsPerLine = 1.0;

        /// <summary>Lines kept for the window when nobody takes them.</summary>
        public const int MaxWaiting = 100;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(MaxBytes + 16);
        private readonly PacketReader reader = new PacketReader();
        private readonly List<ChatLine> waiting = new List<ChatLine>();
        private readonly Dictionary<byte, Allowance> allowances = new Dictionary<byte, Allowance>();
        private readonly HashSet<byte> warnedAbout = new HashSet<byte>();
        private double now;
        private bool listSeen;
        private bool listArriving;
        private bool detached;

        public ChatReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
            session.PlayerJoined += OnJoined;
            session.PlayerLeft += OnLeft;
        }

        /// <summary>Lines this player wrote and sent.</summary>
        public long Said { get; private set; }

        /// <summary>Lines of the other players that arrived here.</summary>
        public long Heard { get; private set; }

        /// <summary>On the host: lines dropped because their player wrote too many too fast.</summary>
        public long Dropped { get; private set; }

        public long Malformed { get; private set; }

        /// <summary>
        /// Takes a line out of what someone types: one line, nothing that does not print, no spaces at its ends, at most
        /// <see cref="MaxCharacters"/>. Empty when nothing is left.
        /// </summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var cleaned = new StringBuilder(Math.Min(text.Length, MaxCharacters + 1));
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\t' || c == '\r' || c == '\n' || c == '\u2028' || c == '\u2029')
                {
                    cleaned.Append(' ');
                }
                else if (!char.IsControl(c) && c != '\uFEFF')
                {
                    cleaned.Append(c);
                }
            }

            string line = cleaned.ToString().Trim();
            if (line.Length > MaxCharacters)
            {
                // Not half of a character that takes two.
                int length = char.IsHighSurrogate(line[MaxCharacters - 1]) ? MaxCharacters - 1 : MaxCharacters;
                line = line.Substring(0, length).TrimEnd();
            }

            return line;
        }

        /// <summary>Call once per frame, before the session's update, with the same clock.</summary>
        public void Update(double time)
        {
            now = time;

            // The joins of the player list a client gets when it connects all come in one update of the session.
            if (listArriving)
            {
                listArriving = false;
                listSeen = true;
            }
        }

        /// <summary>Sends a line this player wrote; when it is sent, it is also among the lines to take.</summary>
        public ChatSayResult Say(string text)
        {
            if (detached || session.State != SessionState.Connected)
            {
                return ChatSayResult.NotConnected;
            }

            string line = Clean(text);
            if (line.Length == 0)
            {
                return ChatSayResult.Empty;
            }

            byte self = session.LocalPlayerId;
            if (!Spend(self))
            {
                return ChatSayResult.TooFast;
            }

            writer.Reset();
            if (session.Role == SessionRole.Host)
            {
                writer.WriteByte(ChatWire.MessageLine);
                writer.WriteByte(self);
                writer.WriteString(line);
                session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);
            }
            else
            {
                writer.WriteByte(ChatWire.MessageSay);
                writer.WriteString(line);
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            }

            Said++;
            Add(new ChatLine(ChatLineKind.Said, self, NameOf(self), line, true));
            log.Debug(LogCategory.Player, "Chat: this player wrote a line (" + line.Length + " characters).");
            return ChatSayResult.Sent;
        }

        /// <summary>Moves the lines that came since the last call, oldest first, into <paramref name="into"/>.</summary>
        public int Take(List<ChatLine> into)
        {
            int count = waiting.Count;
            into.AddRange(waiting);
            waiting.Clear();
            return count;
        }

        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
            session.PlayerJoined -= OnJoined;
            session.PlayerLeft -= OnLeft;
            if (Said > 0 || Heard > 0 || Dropped > 0 || Malformed > 0)
            {
                log.Info(LogCategory.Player, "Chat: " + Said + " line(s) written here, " + Heard + " heard, " + Dropped + " dropped as too fast, " + Malformed + " unreadable.");
            }
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != ChatWire.MessageSay && type != ChatWire.MessageLine)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == ChatWire.MessageSay)
                {
                    if (session.Role != SessionRole.Host)
                    {
                        throw new PacketFormatException("A line to pass on, sent to a client.");
                    }

                    Pass(sender, reader.ReadString(MaxBytes));
                }
                else
                {
                    if (session.Role != SessionRole.Client || sender != Protocol.HostPlayerId)
                    {
                        throw new PacketFormatException("A line of the chat from player " + sender + ", who is not the host.");
                    }

                    byte author = reader.ReadByte();
                    string line = Clean(reader.ReadString(MaxBytes));
                    if (line.Length > 0 && author != session.LocalPlayerId)
                    {
                        Heard++;
                        Add(new ChatLine(ChatLineKind.Said, author, NameOf(author), line, false));
                    }
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (Malformed == 1)
                {
                    log.Warning(LogCategory.Net, "Player " + sender + " sent a line of the chat that cannot be read: " + exception.Message);
                }
            }
        }

        // On the host: a client's line, shown here and passed on to the other clients.
        private void Pass(byte author, string text)
        {
            string line = Clean(text);
            if (line.Length == 0)
            {
                return;
            }

            if (!Spend(author))
            {
                Dropped++;
                if (warnedAbout.Add(author))
                {
                    log.Info(LogCategory.Player, "Chat: player " + author + " writes faster than " + Burst + " lines at once; what goes over is dropped.");
                }

                return;
            }

            Heard++;
            Add(new ChatLine(ChatLineKind.Said, author, NameOf(author), line, false));
            writer.Reset();
            writer.WriteByte(ChatWire.MessageLine);
            writer.WriteByte(author);
            writer.WriteString(line);
            IList<PlayerInfo> players = session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                byte id = players[i].Id;
                if (id != author && id != session.LocalPlayerId)
                {
                    session.Send(id, writer.Buffer, 0, writer.Length, true);
                }
            }
        }

        private void OnJoined(PlayerInfo player)
        {
            ChatLineKind kind = ChatLineKind.Joined;
            if (session.Role == SessionRole.Client && !listSeen)
            {
                kind = ChatLineKind.Present;
                listArriving = true;
            }

            Add(new ChatLine(kind, player.Id, player.Name, string.Empty, false));
        }

        private void OnLeft(PlayerInfo player, DisconnectReason reason)
        {
            allowances.Remove(player.Id);
            warnedAbout.Remove(player.Id);
            Add(new ChatLine(ChatLineKind.Left, player.Id, player.Name, string.Empty, false));
        }

        // Whether the player may write one more line now, and if so, that line taken off what they have.
        private bool Spend(byte player)
        {
            Allowance allowance;
            if (!allowances.TryGetValue(player, out allowance))
            {
                allowance = new Allowance { Lines = Burst, At = now };
                allowances[player] = allowance;
            }

            allowance.Lines = Math.Min(Burst, allowance.Lines + ((now - allowance.At) / SecondsPerLine));
            allowance.At = now;
            if (allowance.Lines < 1.0)
            {
                return false;
            }

            allowance.Lines -= 1.0;
            return true;
        }

        private void Add(ChatLine line)
        {
            if (waiting.Count >= MaxWaiting)
            {
                waiting.RemoveAt(0);
            }

            waiting.Add(line);
        }

        private string NameOf(byte id)
        {
            IList<PlayerInfo> players = session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].Id == id)
                {
                    return players[i].Name;
                }
            }

            return "Player " + id;
        }

        private sealed class Allowance
        {
            public double Lines;

            public double At;
        }
    }
}
