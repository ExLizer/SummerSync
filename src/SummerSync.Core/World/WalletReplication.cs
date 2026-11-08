using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>Messages of <see cref="WalletReplication"/>.</summary>
    public static class WalletWire
    {
        /// <summary>
        /// Host to one client, reliable: how much money there is, and the number of the last change of that
        /// client's that is counted in it.
        /// </summary>
        public const byte MessageBalance = SongWire.MessagePart + 1;

        /// <summary>Client to host, reliable: the number of a change, and by how much this player's game changed the money.</summary>
        public const byte MessageChange = SongWire.MessagePart + 2;
    }

    /// <summary>
    /// One wallet for every player of a session: the host's, because the game that is saved is the host's.
    ///
    /// Each game goes on changing its own number as it always did: a shop takes money, a job pays. This class is
    /// told that number once per frame and says what it should be. On the host, what its game did stands, and
    /// what a client's game did is added. On a client, what its game did is sent to the host as a change and
    /// stays in the local number meanwhile; when the host says how much there is, the client's number becomes
    /// that plus whatever changes of its own the host had not counted yet. So nobody waits for the network to
    /// pay, and two players paying at once both pay.
    ///
    /// What it cannot do is refuse: each game decides by its own number whether its player can afford a thing,
    /// and two players spending the last of the money in the same moment leave less than nothing.
    /// </summary>
    public sealed class WalletReplication
    {
        /// <summary>Changes smaller than this are a float being a float, not money.</summary>
        public const float Smallest = 0.005f;

        private const int MaxPending = 256;
        private const double MalformedLogInterval = 5.0;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(64);
        private readonly PacketReader reader = new PacketReader();
        private readonly List<Change> pending = new List<Change>();
        private readonly List<Change> received = new List<Change>();
        private readonly Dictionary<byte, uint> counted = new Dictionary<byte, uint>();
        private readonly List<byte> owed = new List<byte>();
        private double clock;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;
        private bool inWorld;
        private float known;
        private bool dirty;
        private uint nextNumber = 1;
        private bool heard;
        private bool fresh;
        private float hostBalance;
        private uint hostCounted;

        public WalletReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
            session.BeforeFlush += OnBeforeFlush;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
        }

        /// <summary>Whether this machine's money is the session's: always on the host in a world, on a client once the host said how much there is.</summary>
        public bool Shared
        {
            get { return inWorld && (IsHost || heard); }
        }

        /// <summary>How much there is, as far as this machine knows. Only meaningful while <see cref="Shared"/>.</summary>
        public float Balance
        {
            get { return known; }
        }

        /// <summary>On the host: changes of clients counted. On a client: changes sent.</summary>
        public long Changes { get; private set; }

        public long Malformed { get; private set; }

        /// <summary>On a client: changes of its own the host has not said it counted yet.</summary>
        public int Waiting
        {
            get { return pending.Count; }
        }

        /// <summary>On the host: the player, the amount and what is left, for each change of a client's that is counted.</summary>
        public event Action<byte, float, float> ClientChanged;

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            clock = now;
        }

        /// <summary>
        /// Once per frame while there is a game world: <paramref name="local"/> is this game's money right now;
        /// what comes back is what it should be, for the caller to write if it differs.
        /// </summary>
        public float Sync(float local)
        {
            if (IsHost)
            {
                if (!inWorld || Math.Abs(local - known) >= Smallest)
                {
                    // The host's own game earned or spent, or its world has just loaded.
                    known = local;
                    dirty = true;
                }

                inWorld = true;
                for (int i = 0; i < received.Count; i++)
                {
                    Change change = received[i];
                    known += change.Amount;
                    counted[change.Player] = change.Number;
                    Changes++;
                    dirty = true;
                    Action<byte, float, float> handler = ClientChanged;
                    if (handler != null)
                    {
                        handler(change.Player, change.Amount, known);
                    }
                }

                received.Clear();
                return known;
            }

            bool entering = !inWorld;
            inWorld = true;
            if (!heard)
            {
                // Not the session's money yet: nothing is told and nothing is changed.
                known = local;
                return local;
            }

            if (entering)
            {
                // A world that has just loaded has the money of its save: that is not something this player's
                // game earned or spent. The host's word is what counts, and it is applied below.
                known = local;
            }

            float delta = local - known;
            if (Math.Abs(delta) >= Smallest)
            {
                // This player's game earned or spent. It stands here at once, and the host is told.
                if (pending.Count < MaxPending)
                {
                    var change = new Change { Number = nextNumber++, Amount = delta };
                    pending.Add(change);
                    writer.Reset();
                    writer.WriteByte(WalletWire.MessageChange);
                    writer.WriteUInt32(change.Number);
                    writer.WriteSingle(change.Amount);
                    session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
                    Changes++;
                }

                known = local;
            }

            if (!fresh)
            {
                return local;
            }

            // The host said how much there is: that, and whatever of this player's the host had not counted yet.
            fresh = false;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].Number <= hostCounted)
                {
                    pending.RemoveAt(i);
                }
            }

            float target = hostBalance;
            for (int i = 0; i < pending.Count; i++)
            {
                target += pending[i].Amount;
            }

            known = target;
            return target;
        }

        /// <summary>One line for the overlay, or null while this machine has no game world.</summary>
        public string Describe()
        {
            if (!inWorld)
            {
                return null;
            }

            if (IsHost)
            {
                return "Money: " + Text(known) + ", this game's, shared with the others (" + Changes + " change(s) of theirs counted)";
            }

            if (!heard)
            {
                return "Money: this game's own until the host says how much there is";
            }

            return "Money: " + Text(known) + ", the host's (" + Changes + " change(s) of this game's told, " + pending.Count + " not counted yet)";
        }

        /// <summary>"1234.50".</summary>
        public static string Text(float amount)
        {
            return amount.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>This machine left the game world: its money is not looked at until it is back.</summary>
        public void ClearWorld()
        {
            inWorld = false;
            dirty = false;

            // Back in a world, a client takes the host's last word again, whatever its own save says.
            fresh = heard;
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
            session.PlayerJoined -= OnPlayerJoined;
            session.PlayerLeft -= OnPlayerLeft;
            pending.Clear();
            received.Clear();
            inWorld = false;
            heard = false;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (IsHost && player.Id != session.LocalPlayerId && !owed.Contains(player.Id))
            {
                owed.Add(player.Id);
            }
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            owed.Remove(player.Id);
            counted.Remove(player.Id);
            for (int i = received.Count - 1; i >= 0; i--)
            {
                if (received[i].Player == player.Id)
                {
                    received.RemoveAt(i);
                }
            }
        }

        private void OnBeforeFlush()
        {
            if (!IsHost || !inWorld || session.State != SessionState.Connected)
            {
                return;
            }

            IList<PlayerInfo> players = session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                byte id = players[i].Id;
                if (id == session.LocalPlayerId || (!dirty && !owed.Contains(id)))
                {
                    continue;
                }

                uint number;
                counted.TryGetValue(id, out number);
                writer.Reset();
                writer.WriteByte(WalletWire.MessageBalance);
                writer.WriteSingle(known);
                writer.WriteUInt32(number);
                session.Send(id, writer.Buffer, 0, writer.Length, true);
            }

            dirty = false;
            owed.Clear();
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != WalletWire.MessageBalance && type != WalletWire.MessageChange)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == WalletWire.MessageBalance)
                {
                    if (IsHost || sender != Protocol.HostPlayerId)
                    {
                        throw new PacketFormatException("How much money there is, said by someone who is not the host.");
                    }

                    float balance = reader.ReadSingle();
                    uint number = reader.ReadUInt32();
                    if (float.IsNaN(balance) || float.IsInfinity(balance))
                    {
                        throw new PacketFormatException("An amount of money that is not a number.");
                    }

                    hostBalance = balance;
                    hostCounted = number;
                    heard = true;
                    fresh = true;
                }
                else
                {
                    if (!IsHost)
                    {
                        throw new PacketFormatException("A change of the money sent to a client.");
                    }

                    uint number = reader.ReadUInt32();
                    float amount = reader.ReadSingle();
                    if (float.IsNaN(amount) || float.IsInfinity(amount) || Math.Abs(amount) > 1e7f)
                    {
                        throw new PacketFormatException("A change of the money by an amount that is not money.");
                    }

                    received.Add(new Change { Player = sender, Number = number, Amount = amount });
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Fsm, "Dropped a message about money from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }

        private struct Change
        {
            public byte Player;
            public uint Number;
            public float Amount;
        }
    }
}
