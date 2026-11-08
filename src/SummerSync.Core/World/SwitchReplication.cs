using System;
using System.Collections.Generic;
using System.Text;
using SummerSync.Core.Hashing;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;

namespace SummerSync.Core.World
{
    /// <summary>Message types of switch replication.</summary>
    public static class SwitchWire
    {
        /// <summary>Client to host, reliable: a hash of the client's list of switches and how many there are.</summary>
        public const byte MessageHello = ObjectWire.MessagePresent + 1;

        /// <summary>Host to client, reliable: whether the lists agree and, if they do, the state of every switch as bits.</summary>
        public const byte MessageStates = ObjectWire.MessagePresent + 2;

        /// <summary>Either way, reliable: the index of a switch and its new state.</summary>
        public const byte MessageSet = ObjectWire.MessagePresent + 3;

        /// <summary>
        /// Host to a client whose list is not the host's, reliable: a run of the host's names of switches, the runs one
        /// after another in the host's order. How many names the host has, the place of the run's first, how many are in
        /// the run, and each name as how many characters it shares with the one before it in the run and the rest.
        /// (97 and 98 are taken.)
        /// </summary>
        public const byte MessageNames = 99;

        /// <summary>
        /// Host to a client whose list is not the host's, reliable, after all the host's names: how many switches the
        /// host has and the state of each as bits, in the host's order.
        /// </summary>
        public const byte MessageNamedStates = 100;

        /// <summary>The longest name that is sent; a longer one is sent empty, and is not shared with such a client.</summary>
        public const int MaxNameBytes = 512;

        // A run of names stops growing here, well inside one packet.
        internal const int NamesMessageBytes = 900;
    }

    /// <summary>
    /// Shares the two-state things of the game world between the players of a session: doors that are open or
    /// closed, to begin with.
    ///
    /// A switch is known by a name that is the same on every machine (where it sits in the game's scene), and
    /// the machines compare a hash of the list. The host's state is the state: a client that flips a switch tells
    /// the host, and the host tells everyone, the client that asked included. Whoever is told a state it already
    /// has does nothing, which is what makes two players flipping the same switch at the same moment end up
    /// agreeing.
    ///
    /// When a client's list is not the host's (a vehicle only one world has, a door the host's game moved), the host
    /// sends it its names, and the client finds by name where each of the host's switches is in its own list. From
    /// then on everything said between the two is in the host's places, and the client translates. Those both have
    /// are shared; one only the client has is left as its game has it, and one only the host has means nothing to
    /// that client. The game side always speaks of its own places.
    /// </summary>
    public sealed class SwitchReplication
    {
        private const double MalformedLogInterval = 5.0;

        // How many names of the switches only one side has the log spells out, of each side.
        private const int NamesLogged = 5;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly List<string> keys = new List<string>();
        private readonly List<bool> values = new List<bool>();
        private readonly Queue<KeyValuePair<int, bool>> toApply = new Queue<KeyValuePair<int, bool>>();
        private readonly Dictionary<byte, KeyValuePair<ulong, int>> hellos = new Dictionary<byte, KeyValuePair<ulong, int>>();
        private readonly List<byte> subscribers = new List<byte>();

        // Host: the clients whose list is not this world's, which hear states in this world's order after its names.
        private readonly HashSet<byte> named = new HashSet<byte>();
        private readonly PacketWriter writer = new PacketWriter(1000);
        private readonly PacketReader reader = new PacketReader();

        private ulong listHash;
        private bool helloSent;
        private double clock;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;

        // On a client: the host's names, as they come. They are the host's whatever this world is, so they are kept
        // until the host starts another list.
        private string[] hostNames;
        private int hostNamesHeard;

        // On a client whose list is not the host's: where each of the host's switches is here (-1 for one this world
        // does not have), and where each of this world's is in the host's list. Null when the lists are the same.
        private int[] hostToLocal;
        private int[] localToHost;

        public SwitchReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
            session.BeforeFlush += OnBeforeFlush;
            session.PlayerLeft += OnPlayerLeft;
        }

        /// <summary>Switches of this machine's world that were handed to the replication.</summary>
        public int Count
        {
            get { return keys.Count; }
        }

        /// <summary>
        /// Whether the switches are being shared: always on a host that has a world, and on a client once the
        /// host answered. On a client whose list is not the host's, not those only this world has (<see cref="IsOnlyHere"/>).
        /// </summary>
        public bool Shared { get; private set; }

        /// <summary>
        /// Set on a client whose list of switches is not the host's: only those both lists have, by name, are shared
        /// then (<see cref="SharedCount"/>, <see cref="OnlyHere"/>, <see cref="OnlyHost"/>).
        /// </summary>
        public bool Mismatch { get; private set; }

        /// <summary>This world's switches that are shared with the session: all of them while <see cref="Shared"/>, unless <see cref="Mismatch"/>.</summary>
        public int SharedCount
        {
            get
            {
                if (!Shared)
                {
                    return 0;
                }

                return localToHost == null ? keys.Count : keys.Count - OnlyHere;
            }
        }

        /// <summary>On a client whose list is not the host's: this world's switches the host's world does not have.</summary>
        public int OnlyHere { get; private set; }

        /// <summary>On a client whose list is not the host's: the host's switches this world does not have.</summary>
        public int OnlyHost { get; private set; }

        public long Malformed { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>
        /// Hands the switches of this machine's world to the replication: their names, sorted the same way on
        /// every machine by the caller's choice of names, and their current states.
        /// </summary>
        public void SetWorld(IList<string> names, IList<bool> states)
        {
            if (names == null || states == null || names.Count != states.Count)
            {
                throw new ArgumentException("One state per switch is required.");
            }

            keys.Clear();
            values.Clear();
            toApply.Clear();
            keys.AddRange(names);
            values.AddRange(states);
            listHash = HashOf(keys);
            helloSent = false;
            Forget();
            Shared = IsHost && keys.Count > 0;
            if (IsHost)
            {
                // Clients that loaded before the host did have been waiting for this.
                var waiting = new List<byte>(hellos.Keys);
                for (int i = 0; i < waiting.Count; i++)
                {
                    Answer(waiting[i]);
                }
            }
        }

        /// <summary>This machine left the game world.</summary>
        public void ClearWorld()
        {
            keys.Clear();
            values.Clear();
            toApply.Clear();
            subscribers.Clear();
            named.Clear();
            Forget();
            Shared = false;
            helloSent = false;
        }

        public bool Get(int index)
        {
            return values[index];
        }

        /// <summary>The name of a switch handed over with <see cref="SetWorld"/>.</summary>
        public string NameOf(int index)
        {
            return keys[index];
        }

        /// <summary>
        /// On a client whose list is not the host's: whether that switch of this world is one the host's world does not
        /// have. It is this game's alone then: nobody else changes it, what happens to it here is told to no one, and
        /// whatever would show it as another machine has it should leave it as this game has it.
        /// </summary>
        public bool IsOnlyHere(int index)
        {
            return localToHost != null && index >= 0 && index < localToHost.Length && localToHost[index] < 0;
        }

        /// <summary>The local player flipped a switch. Does nothing if that is its state already.</summary>
        public void SetLocal(int index, bool value)
        {
            if (index < 0 || index >= values.Count || values[index] == value)
            {
                return;
            }

            values[index] = value;
            if (!Shared)
            {
                return;
            }

            int wire = index;
            if (localToHost != null)
            {
                // Said in the host's places; one only this world has is told to no one.
                wire = localToHost[index];
                if (wire < 0)
                {
                    return;
                }
            }

            WriteSet(wire, value);
            if (IsHost)
            {
                SendToSubscribers();
            }
            else
            {
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            }
        }

        /// <summary>
        /// A switch that someone else flipped and that the game has to be made to show. Call it until it returns
        /// false, every frame.
        /// </summary>
        public bool TryTakeChange(out int index, out bool value)
        {
            if (toApply.Count == 0)
            {
                index = -1;
                value = false;
                return false;
            }

            KeyValuePair<int, bool> change = toApply.Dequeue();
            index = change.Key;
            value = change.Value;
            return true;
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            clock = now;
        }

        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
            session.BeforeFlush -= OnBeforeFlush;
            session.PlayerLeft -= OnPlayerLeft;
            ClearWorld();
            hellos.Clear();
            hostNames = null;
            hostNamesHeard = 0;
        }

        /// <summary>A hash of a list of names that changes if a name, the order or the count does.</summary>
        public static ulong HashOf(IList<string> names)
        {
            ulong hash = Fnv1a.OffsetBasis;
            var separator = new byte[] { 0 };
            for (int i = 0; i < names.Count; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(names[i]);
                hash = Fnv1a.Hash64(bytes, 0, bytes.Length, hash);
                hash = Fnv1a.Hash64(separator, 0, 1, hash);
            }

            return hash;
        }

        // What a client knew of how its list and the host's go together. The host's names stay: they are the host's.
        private void Forget()
        {
            Mismatch = false;
            hostToLocal = null;
            localToHost = null;
            OnlyHere = 0;
            OnlyHost = 0;
        }

        private void OnBeforeFlush()
        {
            if (IsHost || helloSent || keys.Count == 0 || session.State != SessionState.Connected)
            {
                return;
            }

            helloSent = true;
            writer.Reset();
            writer.WriteByte(SwitchWire.MessageHello);
            writer.WriteUInt64(listHash);
            writer.WriteUInt16((ushort)keys.Count);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            subscribers.Remove(player.Id);
            named.Remove(player.Id);
            hellos.Remove(player.Id);
        }

        private void WriteSet(int index, bool value)
        {
            writer.Reset();
            writer.WriteByte(SwitchWire.MessageSet);
            writer.WriteUInt16((ushort)index);
            writer.WriteByte(value ? (byte)1 : (byte)0);
        }

        private void WriteBits()
        {
            for (int start = 0; start < values.Count; start += 8)
            {
                int bits = 0;
                for (int bit = 0; bit < 8 && start + bit < values.Count; bit++)
                {
                    bits |= values[start + bit] ? 1 << bit : 0;
                }

                writer.WriteByte((byte)bits);
            }
        }

        /// <summary>
        /// The host: tells a client again what state each switch is in (a checksum found its game showing something else).
        /// </summary>
        public void Resend(byte client)
        {
            if (!IsHost || keys.Count == 0 || !subscribers.Contains(client))
            {
                return;
            }

            writer.Reset();
            if (named.Contains(client))
            {
                writer.WriteByte(SwitchWire.MessageNamedStates);
                writer.WriteUInt16((ushort)keys.Count);
            }
            else
            {
                writer.WriteByte(SwitchWire.MessageStates);
                writer.WriteByte(1);
                writer.WriteUInt16((ushort)keys.Count);
            }

            WriteBits();
            session.Send(client, writer.Buffer, 0, writer.Length, true);
        }

        private void SendToSubscribers()
        {
            for (int i = 0; i < subscribers.Count; i++)
            {
                session.Send(subscribers[i], writer.Buffer, 0, writer.Length, true);
            }
        }

        // Host: tells a client what state each switch is in: straight away if its list is this world's, and after
        // this world's names if not.
        private void Answer(byte client)
        {
            KeyValuePair<ulong, int> hello;
            if (keys.Count == 0 || !hellos.TryGetValue(client, out hello))
            {
                return;
            }

            hellos.Remove(client);
            bool same = hello.Key == listHash && hello.Value == keys.Count;
            if (same)
            {
                writer.Reset();
                writer.WriteByte(SwitchWire.MessageStates);
                writer.WriteByte(1);
                writer.WriteUInt16((ushort)keys.Count);
                WriteBits();
                session.Send(client, writer.Buffer, 0, writer.Length, true);
            }
            else
            {
                named.Add(client);
                int messages = SendNames(client);
                writer.Reset();
                writer.WriteByte(SwitchWire.MessageNamedStates);
                writer.WriteUInt16((ushort)keys.Count);
                WriteBits();
                session.Send(client, writer.Buffer, 0, writer.Length, true);
                log.Warning(
                    LogCategory.Fsm,
                    "Player " + client + " has " + hello.Value + " door(s) and switch(es) and this world has " + keys.Count
                    + ", or not the same ones: this world's names went to that player (" + messages + " message(s)), and those both worlds have are shared by name.");
            }

            if (!subscribers.Contains(client))
            {
                subscribers.Add(client);
            }
        }

        // Host: this world's names, in runs that each fit a message, to a client whose list is not this one. Returns
        // how many messages it took.
        private int SendNames(byte client)
        {
            int first = 0;
            int messages = 0;
            int tooLong = 0;
            while (first < keys.Count || messages == 0)
            {
                writer.Reset();
                writer.WriteByte(SwitchWire.MessageNames);
                writer.WriteUInt16((ushort)keys.Count);
                writer.WriteUInt16((ushort)first);
                int countAt = writer.Length;
                writer.WriteByte(0);
                int count = 0;
                string previous = string.Empty;
                while (first + count < keys.Count && count < byte.MaxValue)
                {
                    string name = keys[first + count];
                    if (Encoding.UTF8.GetByteCount(name) > SwitchWire.MaxNameBytes)
                    {
                        name = string.Empty;
                        tooLong++;
                    }

                    int shared = SharedStart(previous, name);
                    string rest = shared == 0 ? name : name.Substring(shared);
                    int bytes = Encoding.UTF8.GetByteCount(rest);
                    int size = PacketWriter.VarUInt32Size((uint)shared) + PacketWriter.VarUInt32Size((uint)bytes) + bytes;
                    if (count > 0 && writer.Length + size > SwitchWire.NamesMessageBytes)
                    {
                        break;
                    }

                    writer.WriteVarUInt32((uint)shared);
                    writer.WriteString(rest);
                    previous = name;
                    count++;
                }

                writer.Buffer[countAt] = (byte)count;
                session.Send(client, writer.Buffer, 0, writer.Length, true);
                messages++;
                first += count;
            }

            if (tooLong > 0)
            {
                log.Warning(LogCategory.Fsm, tooLong + " door(s) or switch(es) of this world have a name longer than " + SwitchWire.MaxNameBytes + " bytes: not shared with player " + client + ".");
            }

            return messages;
        }

        // How many characters the start of a name has in common with the one before it, never splitting a character
        // in two.
        private static int SharedStart(string previous, string name)
        {
            int limit = Math.Min(previous.Length, name.Length);
            int shared = 0;
            while (shared < limit && previous[shared] == name[shared])
            {
                shared++;
            }

            if (shared > 0 && char.IsHighSurrogate(name[shared - 1]))
            {
                shared--;
            }

            return shared;
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if ((type < SwitchWire.MessageHello || type > SwitchWire.MessageSet) && type != SwitchWire.MessageNames && type != SwitchWire.MessageNamedStates)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == SwitchWire.MessageHello)
                {
                    if (!IsHost)
                    {
                        throw new PacketFormatException("A switch hello sent to a client.");
                    }

                    ulong hash = reader.ReadUInt64();
                    hellos[sender] = new KeyValuePair<ulong, int>(hash, reader.ReadUInt16());
                    subscribers.Remove(sender);
                    Answer(sender);
                }
                else if (type == SwitchWire.MessageSet)
                {
                    HandleSet(sender);
                }
                else
                {
                    if (IsHost || sender != Protocol.HostPlayerId)
                    {
                        throw new PacketFormatException("Switch states or names, said by someone who is not the host.");
                    }

                    if (type == SwitchWire.MessageStates)
                    {
                        HandleStates();
                    }
                    else if (type == SwitchWire.MessageNames)
                    {
                        HandleNames();
                    }
                    else
                    {
                        HandleNamedStates();
                    }
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Fsm, "Dropped a switch message from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }

        private void HandleStates()
        {
            bool same = reader.ReadByte() != 0;
            int count = reader.ReadUInt16();
            if (keys.Count == 0)
            {
                // This world is gone: the next one says hello again.
                return;
            }

            if (!same || count != keys.Count)
            {
                Forget();
                Mismatch = true;
                Shared = false;
                log.Warning(
                    LogCategory.Fsm,
                    "The host has " + count + " door(s) and switch(es) and this world has " + keys.Count + ", or not the same ones. They are not shared.");
                return;
            }

            if (reader.Remaining < (count + 7) / 8)
            {
                throw new PacketFormatException("The states of " + count + " switches in " + reader.Remaining + " byte(s).");
            }

            Forget();
            Shared = true;
            int different = 0;
            for (int start = 0; start < count; start += 8)
            {
                int bits = reader.ReadByte();
                for (int bit = 0; bit < 8 && start + bit < count; bit++)
                {
                    bool value = (bits & (1 << bit)) != 0;
                    if (values[start + bit] != value)
                    {
                        values[start + bit] = value;
                        toApply.Enqueue(new KeyValuePair<int, bool>(start + bit, value));
                        different++;
                    }
                }
            }

            log.Info(LogCategory.Fsm, "Doors and switches: " + count + " shared with the host, " + different + " set to the host's state.");
        }

        // A run of the host's names. The runs come in order: the first of a list starts it over.
        private void HandleNames()
        {
            int total = reader.ReadUInt16();
            int first = reader.ReadUInt16();
            int count = reader.ReadByte();
            if (first == 0)
            {
                hostNames = new string[total];
                hostNamesHeard = 0;
            }

            if (hostNames == null || total != hostNames.Length || first != hostNamesHeard || first + count > total)
            {
                throw new PacketFormatException("Names of the host's switches out of order: " + count + " from " + first + " of " + total + ".");
            }

            string previous = string.Empty;
            for (int i = 0; i < count; i++)
            {
                uint shared = reader.ReadVarUInt32();
                string rest = reader.ReadString(SwitchWire.MaxNameBytes);
                if (shared > (uint)previous.Length || shared + (uint)rest.Length > SwitchWire.MaxNameBytes)
                {
                    throw new PacketFormatException("A name of the host's switches that starts with " + shared + " character(s) of one of " + previous.Length + ".");
                }

                string name = shared == 0 ? rest : previous.Substring(0, (int)shared) + rest;
                hostNames[first + i] = name;
                previous = name;
            }

            hostNamesHeard = first + count;
        }

        // The host's states in its own places, after its names: those this world has too are shared from now on.
        private void HandleNamedStates()
        {
            int count = reader.ReadUInt16();
            if (hostNames == null || hostNamesHeard != hostNames.Length || count != hostNames.Length)
            {
                throw new PacketFormatException("The states of the host's " + count + " switches, before all their names.");
            }

            if (reader.Remaining < (count + 7) / 8)
            {
                throw new PacketFormatException("The states of " + count + " switches in " + reader.Remaining + " byte(s).");
            }

            if (keys.Count == 0)
            {
                // This world is gone: the next one says hello again.
                return;
            }

            Map();
            Shared = true;
            Mismatch = true;
            int different = 0;
            for (int start = 0; start < count; start += 8)
            {
                int bits = reader.ReadByte();
                for (int bit = 0; bit < 8 && start + bit < count; bit++)
                {
                    int local = hostToLocal[start + bit];
                    bool value = (bits & (1 << bit)) != 0;
                    if (local >= 0 && values[local] != value)
                    {
                        values[local] = value;
                        toApply.Enqueue(new KeyValuePair<int, bool>(local, value));
                        different++;
                    }
                }
            }

            log.Warning(LogCategory.Fsm, DescribeDifference(different));
        }

        // Pairs the host's switches with this world's by name: the first of a name with the first of that name, and so
        // on. Both lists are walked in the order of their names.
        private void Map()
        {
            hostToLocal = new int[hostNames.Length];
            localToHost = new int[keys.Count];
            for (int i = 0; i < hostToLocal.Length; i++)
            {
                hostToLocal[i] = -1;
            }

            for (int i = 0; i < localToHost.Length; i++)
            {
                localToHost[i] = -1;
            }

            List<int> hostOrder = ByName(hostNames);
            List<int> localOrder = ByName(keys);
            int h = 0;
            int l = 0;
            while (h < hostOrder.Count && l < localOrder.Count)
            {
                string hostName = hostNames[hostOrder[h]];
                int compared = string.CompareOrdinal(hostName, keys[localOrder[l]]);
                if (compared == 0 && hostName.Length > 0)
                {
                    hostToLocal[hostOrder[h]] = localOrder[l];
                    localToHost[localOrder[l]] = hostOrder[h];
                }

                if (compared <= 0)
                {
                    h++;
                }

                if (compared >= 0)
                {
                    l++;
                }
            }

            OnlyHere = 0;
            for (int i = 0; i < localToHost.Length; i++)
            {
                OnlyHere += localToHost[i] < 0 ? 1 : 0;
            }

            OnlyHost = 0;
            for (int i = 0; i < hostToLocal.Length; i++)
            {
                OnlyHost += hostToLocal[i] < 0 ? 1 : 0;
            }
        }

        // The places of a list in the order of their names; the same names in the order of their places.
        private static List<int> ByName(IList<string> names)
        {
            var order = new List<int>(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                order.Add(i);
            }

            order.Sort(delegate(int a, int b)
            {
                int compared = string.CompareOrdinal(names[a], names[b]);
                return compared != 0 ? compared : a.CompareTo(b);
            });
            return order;
        }

        private string DescribeDifference(int different)
        {
            var text = new StringBuilder();
            text.Append("Doors and switches: the host's world has ").Append(hostNames.Length).Append(" and this one ").Append(keys.Count)
                .Append(", not the same ones. The ").Append(keys.Count - OnlyHere).Append(" both have are shared by name, ").Append(different)
                .Append(" set to the host's state; only here (left as this game has them): ").Append(OnlyHere);
            AppendNames(text, keys, localToHost);
            text.Append("; only in the host's world: ").Append(OnlyHost);
            AppendNames(text, hostNames, hostToLocal);
            return text.Append('.').ToString();
        }

        // The first few names of a list that have no partner in the other.
        private static void AppendNames(StringBuilder text, IList<string> names, int[] partners)
        {
            int said = 0;
            int left = 0;
            for (int i = 0; i < partners.Length; i++)
            {
                if (partners[i] >= 0)
                {
                    continue;
                }

                if (said == NamesLogged)
                {
                    left++;
                    continue;
                }

                text.Append(said == 0 ? " (" : ", ").Append('\'').Append(names[i]).Append('\'');
                said++;
            }

            if (left > 0)
            {
                text.Append(" and ").Append(left).Append(" more");
            }

            if (said > 0)
            {
                text.Append(')');
            }
        }

        private void HandleSet(byte sender)
        {
            int index = reader.ReadUInt16();
            bool value = reader.ReadByte() != 0;
            if (IsHost)
            {
                if (index >= values.Count)
                {
                    throw new PacketFormatException("Switch " + index + " of " + values.Count + ".");
                }

                if (!subscribers.Contains(sender))
                {
                    return;
                }

                if (values[index] != value)
                {
                    values[index] = value;
                    toApply.Enqueue(new KeyValuePair<int, bool>(index, value));
                }

                // To everyone, the sender included: that is the confirmation, and the correction when two
                // players flipped it at once. A client whose list is not this one finds the switch by this place.
                WriteSet(index, values[index]);
                SendToSubscribers();
                return;
            }

            if (sender != Protocol.HostPlayerId)
            {
                throw new PacketFormatException("A switch set, said by someone who is not the host.");
            }

            if (!Shared)
            {
                return;
            }

            int local = index;
            if (hostToLocal != null)
            {
                if (index >= hostToLocal.Length)
                {
                    throw new PacketFormatException("Switch " + index + " of the host's " + hostToLocal.Length + ".");
                }

                // One this world does not have means nothing here.
                local = hostToLocal[index];
                if (local < 0)
                {
                    return;
                }
            }
            else if (index >= values.Count)
            {
                throw new PacketFormatException("Switch " + index + " of " + values.Count + ".");
            }

            if (values[local] != value)
            {
                values[local] = value;
                toApply.Enqueue(new KeyValuePair<int, bool>(local, value));
            }
        }
    }
}
