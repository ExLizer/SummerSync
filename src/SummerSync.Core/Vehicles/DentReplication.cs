using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.Vehicles
{
    /// <summary>Messages of <see cref="DentReplication"/>.</summary>
    public static class DentWire
    {
        /// <summary>
        /// Either way, reliable: a blow that dented something, or the crash's sound of a vehicle, as the game that simulates it
        /// had it. The host passes it on to every other client.
        /// </summary>
        public const byte MessageDent = 107;

        /// <summary>A client to the host, reliable, nothing more: every dent of the host's world since it loaded, please.</summary>
        public const byte MessageAsk = 108;

        public const int MaxTargetBytes = 200;
        public const int MaxContacts = 16;

        /// <summary>From the host's journal: a dent from before the client asked, not news.</summary>
        public const byte FlagJournal = 1;

        /// <summary>Only the crash's sound of the vehicle that is the target; no contacts.</summary>
        public const byte FlagSound = 2;

        /// <summary>
        /// The target's mesh is put back as the game made it (Fleetari's body work); no contacts. Kept in the journal like a
        /// dent, so that whoever joins makes the dents after it on the repaired mesh.
        /// </summary>
        public const byte FlagRepair = 4;
    }

    /// <summary>Where a blow touched, and which way it pushed, in the coordinates of what it dented.</summary>
    public struct DentContact
    {
        public float X;
        public float Y;
        public float Z;
        public float NX;
        public float NY;
        public float NZ;
    }

    /// <summary>
    /// One blow on one deformable thing: what (the game side's name for it), how hard (the relative speed squared, which
    /// is all the game's deformation reads of it) and where.
    /// </summary>
    public sealed class Dent
    {
        public string Target;

        /// <summary>The relative speed of the blow, squared (m²/s²).</summary>
        public float Strength;

        public DentContact[] Contacts;

        /// <summary><see cref="DentWire.FlagJournal"/>, <see cref="DentWire.FlagSound"/>.</summary>
        public byte Flags;

        /// <summary>Whose game it happened in. Filled in by the host.</summary>
        public byte Player;

        public bool FromJournal
        {
            get { return (Flags & DentWire.FlagJournal) != 0; }
        }

        public bool SoundOnly
        {
            get { return (Flags & DentWire.FlagSound) != 0; }
        }

        public bool Repair
        {
            get { return (Flags & DentWire.FlagRepair) != 0; }
        }
    }

    /// <summary>
    /// The dents of the vehicles, and the sound of their crashes. A dent is made by the game that simulates what is hit (or
    /// that has the blow's body, the sledgehammer); every other game makes the same dent with the same blow, which comes out
    /// the same, as the game's deformation only reads the blow and what it had. The host keeps nothing: its game side keeps
    /// the dents since its world loaded and tells them to a client that asks, which is how a client that joins catches up.
    ///
    /// This class only carries the news. Denting is the game side's.
    /// </summary>
    public sealed class DentReplication
    {
        private const double MalformedLogInterval = 5.0;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(800);
        private readonly PacketReader reader = new PacketReader();
        private double clock;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;

        public DentReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
        }

        /// <summary>Another game made this dent, or its vehicle crashed: here too.</summary>
        public event Action<Dent> DentWanted;

        /// <summary>On the host: a client wants every dent of this world, to catch up (<see cref="TellTo"/>).</summary>
        public event Action<byte> JournalWanted;

        public long Told { get; private set; }

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
        }

        /// <summary>This machine's game made a dent, or its vehicle crashed. The others are told; false if it could not be said.</summary>
        public bool Tell(Dent dent)
        {
            if (session.State != SessionState.Connected || !Valid(dent))
            {
                return false;
            }

            dent.Player = session.LocalPlayerId;
            dent.Flags = (byte)(dent.Flags & (DentWire.FlagSound | DentWire.FlagRepair));
            Told++;
            writer.Reset();
            writer.WriteByte(DentWire.MessageDent);
            Write(dent);
            Send(Protocol.HostPlayerId);
            return true;
        }

        /// <summary>On the host: a dent of its journal, to one client that asked.</summary>
        public bool TellTo(byte player, Dent dent)
        {
            if (!IsHost || session.State != SessionState.Connected || !Valid(dent) || dent.SoundOnly)
            {
                return false;
            }

            byte flags = dent.Flags;
            dent.Flags = (byte)(DentWire.FlagJournal | (flags & DentWire.FlagRepair));
            Told++;
            writer.Reset();
            writer.WriteByte(DentWire.MessageDent);
            Write(dent);
            dent.Flags = flags;
            session.Send(player, writer.Buffer, 0, writer.Length, true);
            return true;
        }

        /// <summary>A client: asks the host for every dent of its world. False on the host or before connecting.</summary>
        public bool AskJournal()
        {
            if (IsHost || session.State != SessionState.Connected)
            {
                return false;
            }

            writer.Reset();
            writer.WriteByte(DentWire.MessageAsk);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            return true;
        }

        /// <summary>One line for the overlay, or null before anything happened.</summary>
        public string Describe()
        {
            if (Told == 0 && Heard == 0)
            {
                return null;
            }

            return "Dents: " + Told + " told, " + Heard + " heard" + (Malformed > 0 ? ", " + Malformed + " malformed" : string.Empty);
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

        /// <summary>Whether a dent can be said: a target that fits, a strength and contacts that are numbers.</summary>
        public static bool Valid(Dent dent)
        {
            if (dent == null || string.IsNullOrEmpty(dent.Target) || System.Text.Encoding.UTF8.GetByteCount(dent.Target) > DentWire.MaxTargetBytes
                || !Finite(dent.Strength) || dent.Strength < 0f)
            {
                return false;
            }

            int count = dent.Contacts == null ? 0 : dent.Contacts.Length;
            if (count > DentWire.MaxContacts || (count == 0 && (dent.Flags & (DentWire.FlagSound | DentWire.FlagRepair)) == 0))
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                DentContact c = dent.Contacts[i];
                if (!Finite(c.X) || !Finite(c.Y) || !Finite(c.Z) || !Finite(c.NX) || !Finite(c.NY) || !Finite(c.NZ))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // To the host from a client; to every client from the host, except the one it came from.
        private void Send(byte except)
        {
            if (!IsHost)
            {
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
                return;
            }

            IList<PlayerInfo> players = session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                byte id = players[i].Id;
                if (id != session.LocalPlayerId && id != except)
                {
                    session.Send(id, writer.Buffer, 0, writer.Length, true);
                }
            }
        }

        private void Write(Dent dent)
        {
            int count = dent.Contacts == null ? 0 : dent.Contacts.Length;
            writer.WriteByte(dent.Player);
            writer.WriteByte(dent.Flags);
            writer.WriteString(dent.Target);
            writer.WriteSingle(dent.Strength);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                DentContact c = dent.Contacts[i];
                writer.WriteSingle(c.X);
                writer.WriteSingle(c.Y);
                writer.WriteSingle(c.Z);
                writer.WriteSingle(c.NX);
                writer.WriteSingle(c.NY);
                writer.WriteSingle(c.NZ);
            }
        }

        private Dent Read()
        {
            var dent = new Dent
            {
                Player = reader.ReadByte(),
                Flags = reader.ReadByte(),
                Target = reader.ReadString(DentWire.MaxTargetBytes),
                Strength = reader.ReadSingle(),
            };
            int count = reader.ReadByte();
            if (count > DentWire.MaxContacts)
            {
                throw new PacketFormatException("A dent with " + count + " contacts.");
            }

            dent.Contacts = new DentContact[count];
            for (int i = 0; i < count; i++)
            {
                dent.Contacts[i] = new DentContact
                {
                    X = reader.ReadSingle(),
                    Y = reader.ReadSingle(),
                    Z = reader.ReadSingle(),
                    NX = reader.ReadSingle(),
                    NY = reader.ReadSingle(),
                    NZ = reader.ReadSingle(),
                };
            }

            if (!Valid(dent))
            {
                throw new PacketFormatException("A dent of nothing, or that is not numbers.");
            }

            return dent;
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != DentWire.MessageDent && type != DentWire.MessageAsk)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == DentWire.MessageAsk)
                {
                    if (!IsHost)
                    {
                        throw new PacketFormatException("A client asked another client for the dents.");
                    }

                    Action<byte> asked = JournalWanted;
                    if (asked != null)
                    {
                        asked(sender);
                    }

                    return;
                }

                if (!IsHost && sender != Protocol.HostPlayerId)
                {
                    throw new PacketFormatException("A dent from someone who is not the host.");
                }

                Dent dent = Read();
                if (IsHost)
                {
                    // Only a client's own news goes on; the host's journal is the host's to tell.
                    dent.Player = sender;
                    dent.Flags = (byte)(dent.Flags & (DentWire.FlagSound | DentWire.FlagRepair));
                    writer.Reset();
                    writer.WriteByte(DentWire.MessageDent);
                    Write(dent);
                    Send(sender);
                }

                Heard++;
                Action<Dent> handler = DentWanted;
                if (handler != null)
                {
                    handler(dent);
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Vehicle, "Dropped a message about a dent from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }
    }
}
