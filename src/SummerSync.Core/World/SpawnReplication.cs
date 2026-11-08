using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;

namespace SummerSync.Core.World
{
    /// <summary>Messages of <see cref="SpawnReplication"/>.</summary>
    public static class SpawnWire
    {
        /// <summary>Client to host, reliable: this player's game made something (see <see cref="SpawnEvent"/>).</summary>
        public const byte MessageMade = WalletWire.MessageChange + 1;

        /// <summary>Host to clients, reliable: someone's game made something; the player, then the same as <see cref="MessageMade"/>.</summary>
        public const byte MessageMake = WalletWire.MessageChange + 2;

        /// <summary>
        /// Either way, reliable: what a container (a shopping bag) holds now. The host passes it on to every
        /// client, the sender too, so that whatever arrives last everywhere is the same.
        /// </summary>
        public const byte MessageContents = WalletWire.MessageChange + 3;

        public const int MaxSpawnerBytes = 96;
        public const int MaxNameBytes = 64;
        public const int MaxLists = 4;
        public const int MaxListNameBytes = 32;
        public const int MaxListLength = 256;

        /// <summary>A message about a container is at most this long, so that it fits in one packet.</summary>
        public const int MaxContentsBytes = 900;

        /// <summary>Bytes a message about this container takes, without its type byte.</summary>
        public static int ContentsSize(string name, IList<ContainerList> lists)
        {
            int size = StringSize(name) + 1;
            int count = Math.Min(lists.Count, MaxLists);
            for (int i = 0; i < count; i++)
            {
                ContainerList list = lists[i];
                int length = Math.Min(list.Values.Length, MaxListLength);
                size += StringSize(list.Name) + 2;
                for (int j = 0; j < length; j++)
                {
                    size += PacketWriter.VarUInt32Size(ZigZag(list.Values[j]));
                }
            }

            return size;
        }

        /// <summary>Small numbers of either sign as small unsigned ones: 0, -1, 1, -2… become 0, 1, 2, 3…</summary>
        public static uint ZigZag(int value)
        {
            return (uint)((value << 1) ^ (value >> 31));
        }

        public static int UnZigZag(uint value)
        {
            return (int)(value >> 1) ^ -(int)(value & 1);
        }

        private static int StringSize(string value)
        {
            int bytes = string.IsNullOrEmpty(value) ? 0 : System.Text.Encoding.UTF8.GetByteCount(value);
            return PacketWriter.VarUInt32Size((uint)bytes) + bytes;
        }
    }

    /// <summary>One thing a game made with one of its numbered makers.</summary>
    public struct SpawnEvent
    {
        /// <summary>The game's state machine that made it, as "path :: name".</summary>
        public string Spawner;

        /// <summary>Its number: the game names what it makes with the maker's prefix and this.</summary>
        public int Number;

        /// <summary>Where it was when it was made, in the world.</summary>
        public BodyPose Pose;

        /// <summary>The object it came out of (a shopping bag), or empty.</summary>
        public string Near;

        /// <summary>Whose game made it. Filled in by the host.</summary>
        public byte Player;
    }

    /// <summary>One list of a container: its name and its numbers.</summary>
    public sealed class ContainerList
    {
        public ContainerList(string name, int[] values)
        {
            Name = name ?? string.Empty;
            Values = values ?? new int[0];
        }

        public string Name { get; private set; }

        public int[] Values { get; private set; }
    }

    /// <summary>
    /// What the games make while they run, made on every machine.
    ///
    /// The game makes the things that did not exist when it loaded (what is bought, what comes out of a bag, a
    /// trophy) with a few dozen state machines, each with a running number that names what it makes and that the
    /// save keeps. When one of them makes something on one machine, every other machine has its own copy of that
    /// state machine make the same, with the same number: then the objects have the same name everywhere, and the
    /// loose objects' replication pairs them like any other.
    ///
    /// And some of what is made holds lists of what it contains (a shopping bag, of what was bought): those lists
    /// are passed on whenever they change.
    ///
    /// This class only carries the news. Making things and reading lists is the game side's.
    /// </summary>
    public sealed class SpawnReplication
    {
        /// <summary>A client may report this many things made per second, on average, and a few more at once.</summary>
        public const double MadePerSecond = 20.0;

        public const double MadeBurst = 60.0;

        private const double MalformedLogInterval = 5.0;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(1200);
        private readonly PacketReader reader = new PacketReader();
        private readonly Dictionary<byte, double> allowance = new Dictionary<byte, double>();
        private double clock;
        private double lastAllowance = double.NaN;
        private double nextMalformedLog = double.NegativeInfinity;
        private double nextDroppedLog = double.NegativeInfinity;
        private bool detached;

        public SpawnReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
            session.PlayerLeft += OnPlayerLeft;
        }

        /// <summary>Someone else's game made something: make the same here.</summary>
        public event Action<SpawnEvent> Wanted;

        /// <summary>A container holds this now, as some machine has it: the container's name and its lists.</summary>
        public event Action<string, IList<ContainerList>> ContentsWanted;

        /// <summary>Things this machine's game made and told.</summary>
        public long Told { get; private set; }

        /// <summary>Things someone else's game made, to be made here.</summary>
        public long Heard { get; private set; }

        /// <summary>On the host: things a client said it made beyond what it may, not passed on.</summary>
        public long Dropped { get; private set; }

        public long ContentsTold { get; private set; }

        public long ContentsHeard { get; private set; }

        public long Malformed { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            clock = now;
        }

        /// <summary>This machine's game made something. The others are told.</summary>
        public void Report(SpawnEvent made)
        {
            if (session.State != SessionState.Connected || string.IsNullOrEmpty(made.Spawner) || made.Number <= 0)
            {
                return;
            }

            made.Spawner = Protocol.LimitText(made.Spawner, SpawnWire.MaxSpawnerBytes);
            made.Near = Protocol.LimitText(made.Near ?? string.Empty, SpawnWire.MaxNameBytes);
            made.Pose.Parent = 0;
            Told++;
            if (IsHost)
            {
                made.Player = session.LocalPlayerId;
                SendMake(made, Protocol.Broadcast);
            }
            else
            {
                writer.Reset();
                writer.WriteByte(SpawnWire.MessageMade);
                WriteSpawn(made);
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            }
        }

        /// <summary>A container of this machine's world holds this now, and not because another machine said so.</summary>
        public void ReportContents(string name, IList<ContainerList> lists)
        {
            if (session.State != SessionState.Connected || string.IsNullOrEmpty(name) || lists == null)
            {
                return;
            }

            name = Protocol.LimitText(name, SpawnWire.MaxNameBytes);
            if (SpawnWire.ContentsSize(name, lists) > SpawnWire.MaxContentsBytes)
            {
                log.Warning(LogCategory.Fsm, "What '" + name + "' holds does not fit in one message; the others do not hear of it.");
                return;
            }

            ContentsTold++;
            writer.Reset();
            writer.WriteByte(SpawnWire.MessageContents);
            WriteContents(name, lists);
            session.Send(IsHost ? Protocol.Broadcast : Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        /// <summary>One line for the overlay, or null before anything was made.</summary>
        public string Describe()
        {
            if (Told == 0 && Heard == 0 && ContentsTold == 0 && ContentsHeard == 0)
            {
                return null;
            }

            return "Made: " + Told + " thing(s) here told, " + Heard + " of the others' heard; " + ContentsTold + " change(s) of containers told, "
                + ContentsHeard + " heard" + (Dropped > 0 ? ", " + Dropped + " dropped" : string.Empty) + (Malformed > 0 ? ", " + Malformed + " malformed" : string.Empty);
        }

        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
            session.PlayerLeft -= OnPlayerLeft;
        }

        private void SendMake(SpawnEvent made, byte target)
        {
            writer.Reset();
            writer.WriteByte(SpawnWire.MessageMake);
            writer.WriteByte(made.Player);
            WriteSpawn(made);
            if (target != Protocol.Broadcast)
            {
                session.Send(target, writer.Buffer, 0, writer.Length, true);
                return;
            }

            IList<PlayerInfo> players = session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                byte id = players[i].Id;
                if (id != session.LocalPlayerId && id != made.Player)
                {
                    session.Send(id, writer.Buffer, 0, writer.Length, true);
                }
            }
        }

        private void WriteSpawn(SpawnEvent made)
        {
            writer.WriteString(made.Spawner);
            writer.WriteInt32(made.Number);
            made.Pose.Write(writer);
            writer.WriteString(made.Near ?? string.Empty);
        }

        private SpawnEvent ReadSpawn()
        {
            var made = new SpawnEvent();
            made.Spawner = reader.ReadString(SpawnWire.MaxSpawnerBytes);
            made.Number = reader.ReadInt32();
            made.Pose = BodyPose.Read(reader);
            made.Near = reader.ReadString(SpawnWire.MaxNameBytes);
            if (made.Spawner.Length == 0 || made.Number <= 0 || made.Pose.Parent != 0)
            {
                throw new PacketFormatException("Something made that has no maker, no number, or a place on a vehicle.");
            }

            return made;
        }

        private void WriteContents(string name, IList<ContainerList> lists)
        {
            int count = Math.Min(lists.Count, SpawnWire.MaxLists);
            writer.WriteString(name);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                ContainerList list = lists[i];
                int length = Math.Min(list.Values.Length, SpawnWire.MaxListLength);
                writer.WriteString(Protocol.LimitText(list.Name, SpawnWire.MaxListNameBytes));
                writer.WriteUInt16((ushort)length);
                for (int j = 0; j < length; j++)
                {
                    writer.WriteVarUInt32(SpawnWire.ZigZag(list.Values[j]));
                }
            }
        }

        private void ReadContents(out string name, out List<ContainerList> lists)
        {
            name = reader.ReadString(SpawnWire.MaxNameBytes);
            int count = reader.ReadByte();
            if (name.Length == 0 || count > SpawnWire.MaxLists)
            {
                throw new PacketFormatException("A container with no name, or with " + count + " lists.");
            }

            lists = new List<ContainerList>(count);
            for (int i = 0; i < count; i++)
            {
                string listName = reader.ReadString(SpawnWire.MaxListNameBytes);
                int length = reader.ReadUInt16();
                if (length > SpawnWire.MaxListLength)
                {
                    throw new PacketFormatException("A list of " + length + " numbers.");
                }

                var values = new int[length];
                for (int j = 0; j < length; j++)
                {
                    values[j] = SpawnWire.UnZigZag(reader.ReadVarUInt32());
                }

                lists.Add(new ContainerList(listName, values));
            }
        }

        // Host: whether a client may report one more thing made now. A few at once, a steady stream not.
        private bool Allow(byte player)
        {
            if (!double.IsNaN(lastAllowance))
            {
                double elapsed = Math.Max(0.0, clock - lastAllowance);
                var players = new List<byte>(allowance.Keys);
                for (int i = 0; i < players.Count; i++)
                {
                    allowance[players[i]] = Math.Min(MadeBurst, allowance[players[i]] + (elapsed * MadePerSecond));
                }
            }

            lastAllowance = clock;
            double left;
            if (!allowance.TryGetValue(player, out left))
            {
                left = MadeBurst;
            }

            if (left < 1.0)
            {
                allowance[player] = left;
                return false;
            }

            allowance[player] = left - 1.0;
            return true;
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            allowance.Remove(player.Id);
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != SpawnWire.MessageMade && type != SpawnWire.MessageMake && type != SpawnWire.MessageContents)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == SpawnWire.MessageMade)
                {
                    if (!IsHost)
                    {
                        throw new PacketFormatException("Something made, told to a client as if it were the host.");
                    }

                    SpawnEvent made = ReadSpawn();
                    made.Player = sender;
                    if (!Allow(sender))
                    {
                        Dropped++;
                        if (clock >= nextDroppedLog)
                        {
                            nextDroppedLog = clock + MalformedLogInterval;
                            log.Warning(LogCategory.Fsm, "Player " + sender + " says its game made more things than any game makes (" + Dropped + " not passed on so far).");
                        }

                        return;
                    }

                    Heard++;
                    SendMake(made, Protocol.Broadcast);
                    Raise(made);
                }
                else if (type == SpawnWire.MessageMake)
                {
                    if (IsHost || sender != Protocol.HostPlayerId)
                    {
                        throw new PacketFormatException("Something to make, said by someone who is not the host.");
                    }

                    byte player = reader.ReadByte();
                    SpawnEvent made = ReadSpawn();
                    made.Player = player;
                    Heard++;
                    Raise(made);
                }
                else
                {
                    if (!IsHost && sender != Protocol.HostPlayerId)
                    {
                        throw new PacketFormatException("What a container holds, said by a client to a client.");
                    }

                    string name;
                    List<ContainerList> lists;
                    ReadContents(out name, out lists);
                    if (IsHost)
                    {
                        // To everyone, the sender too: the host may have told it something else a moment ago.
                        writer.Reset();
                        writer.WriteByte(SpawnWire.MessageContents);
                        WriteContents(name, lists);
                        session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);
                    }

                    ContentsHeard++;
                    Action<string, IList<ContainerList>> handler = ContentsWanted;
                    if (handler != null)
                    {
                        handler(name, lists);
                    }
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Fsm, "Dropped a message about something made from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }

        private void Raise(SpawnEvent made)
        {
            Action<SpawnEvent> handler = Wanted;
            if (handler != null)
            {
                handler(made);
            }
        }
    }
}
