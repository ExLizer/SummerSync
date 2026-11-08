using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;

namespace SummerSync.Core.World
{
    /// <summary>Messages of <see cref="TallyReplication"/>.</summary>
    public static class TallyWire
    {
        /// <summary>Host to client, reliable: a tally's name and the numbers it has now.</summary>
        public const byte MessageState = ObjectWire.MessageGone + 1;

        /// <summary>Client to host, reliable: a tally's name and by how much this player's game changed each number.</summary>
        public const byte MessageChange = ObjectWire.MessageGone + 2;

        /// <summary>Client to host, reliable: this world is ready, send every tally there is.</summary>
        public const byte MessageRequest = ObjectWire.MessageGone + 3;

        /// <summary>
        /// Client to host, reliable: a tally's name and the numbers it has now, whatever it had: for what only one
        /// player says (which character they are). Outside the three above, after the objects' two.
        /// </summary>
        public const byte MessageSet = ItemWire.MessageThrown + 1;

        /// <summary>
        /// Host to client, reliable, nothing more: what comes next, up to <see cref="MessageAllEnd"/>, is the host's answer
        /// to the client's <see cref="MessageRequest"/> (every tally it has, as it has it now), not news. Right after the
        /// assembly's messages, which end at 96.
        /// </summary>
        public const byte MessageAllBegin = 97;

        /// <summary>Host to client, reliable, nothing more: the host's answer to a request is over.</summary>
        public const byte MessageAllEnd = 98;

        public const int MaxNameBytes = 64;
        public const int MaxValues = 8;
    }

    /// <summary>
    /// A few small numbers of the game world kept under a name, the same on every machine: for each product of
    /// the shop, how many are on the counter and how many were taken off the shelf.
    ///
    /// Each game goes on changing its own numbers as it always did. A client tells the host by how much its game
    /// changed a tally; the host applies that to its own game, and tells every client the numbers it ends up
    /// with, the one that changed them too. So two players taking the same thing at once both take it, and
    /// whatever the host says last is what everyone has. The host also keeps the last numbers of each tally, for
    /// a client whose world loads later.
    ///
    /// What only one player ever says (which character they are) is set instead: the host keeps it as said, and
    /// hears it, like the clients, through <see cref="StateWanted"/>.
    ///
    /// A client whose world is ready asks for every tally there is (<see cref="RequestAll"/>). The host's answer comes
    /// between two marks, and while a client hears it <see cref="CatchingUp"/> is true: those numbers are how the
    /// host's world is, not something that has just happened in it (a log split an hour ago, an order served long ago).
    /// What the host says after the answer is news again. Requests made before an answer starts are answered once.
    ///
    /// This class only carries the news. Reading and writing the game's numbers is the game side's.
    /// </summary>
    public sealed class TallyReplication
    {
        private const double MalformedLogInterval = 5.0;

        // A request whose answer has not started after this long may be made again.
        private const double AnswerTimeout = 10.0;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(1000);
        private readonly PacketReader reader = new PacketReader();
        private readonly Dictionary<string, int[]> known = new Dictionary<string, int[]>();
        private readonly List<string> order = new List<string>();
        private bool requestWaiting;
        private bool answerAwaited;
        private double askedAt;
        private int answerCount;
        private double clock;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;

        public TallyReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
        }

        /// <summary>
        /// On a client: the host says a tally has these numbers now. Make the game's so. While <see cref="CatchingUp"/>
        /// is true they are part of the host's answer to a request: how its world is, not news.
        /// </summary>
        public event Action<string, int[]> StateWanted;

        /// <summary>On a client: the host's answer to a request has been heard whole, after its last <see cref="StateWanted"/>.</summary>
        public event Action CaughtUp;

        /// <summary>
        /// On a client, while <see cref="StateWanted"/> is raised: true if the numbers are part of the host's answer to a
        /// request (<see cref="RequestAll"/>), a catch-up of how the host's world is, and not something that has just
        /// happened there. A handler that does something only when a thing happens (a sound, an order served, a
        /// payment) takes such numbers as they are, without doing it. Read it in the handler: it is true only from the
        /// start of the answer to its end. Always false on the host.
        /// </summary>
        public bool CatchingUp { get; private set; }

        /// <summary>Answers to a request: heard whole on a client, sent on the host.</summary>
        public long Answers { get; private set; }

        /// <summary>
        /// On the host: a client's game changed a tally by these amounts (and which player). Apply them to this
        /// game's numbers, and then call <see cref="Tell"/> with what they were and what they are.
        /// </summary>
        public event Action<string, int[], byte> ChangeWanted;

        /// <summary>Changes of this machine's game told.</summary>
        public long Told { get; private set; }

        /// <summary>States (on a client) or changes (on the host) heard from the others.</summary>
        public long Heard { get; private set; }

        public long Malformed { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            clock = now;
            if (requestWaiting && !IsHost && session.State == SessionState.Connected)
            {
                // A client in its game world asked while it was still connecting: the question goes now.
                requestWaiting = false;
                SendRequest();
            }
        }

        /// <summary>
        /// A tally of this machine's game went from <paramref name="before"/> to <paramref name="after"/>, by itself
        /// or, on the host, because a client changed it. The host tells every client the new numbers; a client
        /// tells the host the difference.
        /// </summary>
        public void Tell(string name, int[] before, int[] after)
        {
            if (session.State != SessionState.Connected || string.IsNullOrEmpty(name) || after == null || after.Length > TallyWire.MaxValues)
            {
                return;
            }

            name = Protocol.LimitText(name, TallyWire.MaxNameBytes);
            if (IsHost)
            {
                Remember(name, after);
                Told++;
                writer.Reset();
                writer.WriteByte(TallyWire.MessageState);
                WriteTally(name, after);
                session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);
                return;
            }

            var change = new int[after.Length];
            bool any = false;
            for (int i = 0; i < after.Length; i++)
            {
                change[i] = after[i] - (before != null && i < before.Length ? before[i] : 0);
                any |= change[i] != 0;
            }

            if (!any)
            {
                return;
            }

            Told++;
            writer.Reset();
            writer.WriteByte(TallyWire.MessageChange);
            WriteTally(name, change);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        /// <summary>
        /// This machine says what the tally is, whatever it was: on the host, every client is told; on a client, the
        /// host is told, and tells every client, this one too. For a tally only one player changes.
        /// </summary>
        /// <remarks>
        /// Nothing is said before the session is connected: a client has no number of its own until then, and a
        /// tally named after it would be another player's.
        /// </remarks>
        public void Set(string name, int[] values)
        {
            if (session.State != SessionState.Connected || string.IsNullOrEmpty(name) || values == null || values.Length > TallyWire.MaxValues)
            {
                return;
            }

            name = Protocol.LimitText(name, TallyWire.MaxNameBytes);
            Told++;
            if (IsHost)
            {
                Remember(name, values);
                writer.Reset();
                writer.WriteByte(TallyWire.MessageState);
                WriteTally(name, values);
                session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);
                return;
            }

            SendSet(name, values);
        }

        /// <summary>
        /// On a client whose world is ready: asks the host for every tally it has changed so far. A client already in
        /// its game world when it joins asks before it is connected: the question goes once it is. Whoever asks listens
        /// to <see cref="StateWanted"/> already, so a question asked while another's answer has not started yet is not
        /// asked again: that answer is for both.
        /// </summary>
        public void RequestAll()
        {
            if (session.Role == SessionRole.Client && session.State == SessionState.Connecting)
            {
                requestWaiting = true;
                return;
            }

            if (IsHost || session.State != SessionState.Connected)
            {
                return;
            }

            if (requestWaiting || (answerAwaited && clock - askedAt < AnswerTimeout))
            {
                return;
            }

            SendRequest();
        }

        /// <summary>One line for the overlay, or null before anything changed.</summary>
        public string Describe()
        {
            if (Told == 0 && Heard == 0)
            {
                return null;
            }

            return "Tallies: " + Told + " change(s) here told, " + Heard + " heard" + (Malformed > 0 ? ", " + Malformed + " malformed" : string.Empty);
        }

        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
        }

        private void SendRequest()
        {
            answerAwaited = true;
            askedAt = clock;
            writer.Reset();
            writer.WriteByte(TallyWire.MessageRequest);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        private void SendMark(byte client, byte type)
        {
            writer.Reset();
            writer.WriteByte(type);
            session.Send(client, writer.Buffer, 0, writer.Length, true);
        }

        /// <summary>
        /// Each tally as this machine knows it: the host, as it remembers them; a client, as it heard them last.
        /// </summary>
        public void ReadKnown(List<KeyValuePair<string, int[]>> into)
        {
            for (int i = 0; i < order.Count; i++)
            {
                into.Add(new KeyValuePair<string, int[]>(order[i], known[order[i]]));
            }
        }

        /// <summary>The host: tells one client again what a tally is (a checksum found it otherwise there).</summary>
        public void ResendTo(byte client, string name)
        {
            int[] values;
            if (!IsHost || session.State != SessionState.Connected || !known.TryGetValue(name, out values))
            {
                return;
            }

            writer.Reset();
            writer.WriteByte(TallyWire.MessageState);
            WriteTally(name, values);
            session.Send(client, writer.Buffer, 0, writer.Length, true);
        }

        /// <summary>Whether a client heard the host's answer at least once (and so knows what the host remembers).</summary>
        public bool HasHeardAnswer
        {
            get { return IsHost || Answers > 0; }
        }

        private void SendSet(string name, int[] values)
        {
            writer.Reset();
            writer.WriteByte(TallyWire.MessageSet);
            WriteTally(name, values);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        private void Remember(string name, int[] values)
        {
            if (!known.ContainsKey(name))
            {
                order.Add(name);
            }

            known[name] = (int[])values.Clone();
        }

        private void WriteTally(string name, int[] values)
        {
            writer.WriteString(name);
            writer.WriteByte((byte)values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                writer.WriteVarUInt32(SpawnWire.ZigZag(values[i]));
            }
        }

        private int[] ReadTally(out string name)
        {
            name = reader.ReadString(TallyWire.MaxNameBytes);
            int count = reader.ReadByte();
            if (name.Length == 0 || count > TallyWire.MaxValues)
            {
                throw new PacketFormatException("A tally with no name, or with " + count + " numbers.");
            }

            var values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = SpawnWire.UnZigZag(reader.ReadVarUInt32());
            }

            return values;
        }

        // Host: every tally there is, to a client whose world has just loaded, between the two marks of an answer. The
        // messages go out in this order, reliable and ordered, with nothing else of this class in between.
        private void SendAll(byte client)
        {
            SendMark(client, TallyWire.MessageAllBegin);
            for (int i = 0; i < order.Count; i++)
            {
                writer.Reset();
                writer.WriteByte(TallyWire.MessageState);
                WriteTally(order[i], known[order[i]]);
                session.Send(client, writer.Buffer, 0, writer.Length, true);
            }

            SendMark(client, TallyWire.MessageAllEnd);
            Answers++;
            log.Info(LogCategory.Fsm, "Tallies: player " + client + " asked for everything; " + order.Count + " told as this game has them (a catch-up, not news).");
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != TallyWire.MessageState && type != TallyWire.MessageChange && type != TallyWire.MessageRequest && type != TallyWire.MessageSet
                && type != TallyWire.MessageAllBegin && type != TallyWire.MessageAllEnd)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == TallyWire.MessageState)
                {
                    if (IsHost || sender != Protocol.HostPlayerId)
                    {
                        throw new PacketFormatException("A tally's numbers, said by someone who is not the host.");
                    }

                    string name;
                    int[] values = ReadTally(out name);
                    Heard++;
                    if (CatchingUp)
                    {
                        answerCount++;
                    }

                    // A client keeps what it heard last of each tally: what the checksums compare with the host's.
                    Remember(name, values);

                    Action<string, int[]> handler = StateWanted;
                    if (handler != null)
                    {
                        handler(name, values);
                    }
                }
                else if (type == TallyWire.MessageAllBegin)
                {
                    if (IsHost || sender != Protocol.HostPlayerId)
                    {
                        throw new PacketFormatException("The start of an answer about the tallies, said by someone who is not the host.");
                    }

                    // Requests made from now on are not answered by this answer: whoever makes one may not have heard its start.
                    answerAwaited = false;
                    CatchingUp = true;
                    answerCount = 0;
                }
                else if (type == TallyWire.MessageAllEnd)
                {
                    if (IsHost || sender != Protocol.HostPlayerId)
                    {
                        throw new PacketFormatException("The end of an answer about the tallies, said by someone who is not the host.");
                    }

                    CatchingUp = false;
                    Answers++;
                    log.Info(LogCategory.Fsm, "Tallies: the host's answer heard, " + answerCount + " tally(ies) taken as the host's world has them now (a catch-up, not news).");
                    Action handler = CaughtUp;
                    if (handler != null)
                    {
                        handler();
                    }
                }
                else if (type == TallyWire.MessageChange)
                {
                    if (!IsHost)
                    {
                        throw new PacketFormatException("A change of a tally, sent to a client.");
                    }

                    string name;
                    int[] change = ReadTally(out name);
                    Heard++;
                    Action<string, int[], byte> handler = ChangeWanted;
                    if (handler != null)
                    {
                        handler(name, change, sender);
                    }
                }
                else if (type == TallyWire.MessageSet)
                {
                    if (!IsHost)
                    {
                        throw new PacketFormatException("A tally set, sent to a client.");
                    }

                    string name;
                    int[] values = ReadTally(out name);
                    Heard++;
                    Remember(name, values);
                    writer.Reset();
                    writer.WriteByte(TallyWire.MessageState);
                    WriteTally(name, values);
                    session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);

                    // The host's game hears it as a client would.
                    Action<string, int[]> handler = StateWanted;
                    if (handler != null)
                    {
                        handler(name, values);
                    }
                }
                else
                {
                    if (!IsHost)
                    {
                        throw new PacketFormatException("A request for the tallies, sent to a client.");
                    }

                    SendAll(sender);
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Fsm, "Dropped a message about the shop from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }
    }
}
