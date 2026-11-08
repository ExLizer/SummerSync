using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>Messages of <see cref="PanelReplication"/>.</summary>
    public static class PanelWire
    {
        /// <summary>Any to the host, reliable: a button of a panel was pressed in this player's game.</summary>
        public const byte MessagePress = WorldShareWire.MessageManifest + 1;

        /// <summary>Host to every client, reliable: who works a panel now.</summary>
        public const byte MessageOperator = MessagePress + 1;

        /// <summary>Host to the player who works a panel, reliable: a press of someone else, for their game to make.</summary>
        public const byte MessageApply = MessagePress + 2;

        /// <summary>The panel's worker to the host, and the host to the others, reliable: what some of its parts show now.</summary>
        public const byte MessageShow = MessagePress + 3;

        /// <summary>The panel's worker to the host, reliable: the game is back where it waits for someone.</summary>
        public const byte MessageRelease = MessagePress + 4;

        /// <summary>Client to host, reliable: this world is ready, send who works each panel and all they show.</summary>
        public const byte MessageRequest = MessagePress + 5;

        public const int MaxPanels = 4;
        public const int MaxSlots = 1024;
        public const int MaxValueBytes = 96;
        public const int MaxButtons = 64;

        /// <summary>No one works the panel.</summary>
        public const byte NoOperator = 255;

        // A message of shown parts stops growing here, well inside one packet.
        internal const int ShowBytes = 800;
    }

    /// <summary>
    /// Machines of the world that one player works and everyone watches, like the video poker in Teimo's pub: the
    /// game of the player who works it runs it, and what it shows is shown in every game.
    ///
    /// Whoever presses a button of an idle panel works it from then on: the host decides, so two players pressing at
    /// once do not both start it. A press anywhere else goes to the game of whoever works it, which makes it as if its
    /// own player had pressed. That game says what each part of the panel shows (a short text per part, its "slot")
    /// whenever it changes, and the others show the same; the host keeps it all for a client that comes later. Once
    /// the game is back where it waits for someone, its worker lets go of it; a worker who leaves the session lets go
    /// of it too.
    ///
    /// Both sides list a panel's parts the same way (<see cref="Define"/>); the list's "shape", a number made from
    /// it, travels with what is shown, and what comes with another shape is dropped. This class only carries the
    /// news; pressing, reading and showing are the game side's.
    /// </summary>
    public sealed class PanelReplication
    {
        private const double MalformedLogInterval = 5.0;

        /// <summary>Seconds between two messages of shown parts from the same worker: about ten a second.</summary>
        public const double ShowInterval = 0.1;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(1100);
        private readonly PacketReader reader = new PacketReader();
        private readonly Panel[] panels = new Panel[PanelWire.MaxPanels];
        private double clock;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;
        private bool requestWaiting;

        public PanelReplication(NetSession session, Logger log)
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

        /// <summary>On the game of whoever works a panel: make this press of another player (panel, button).</summary>
        public event Action<int, int> PressWanted;

        /// <summary>On every other game: a part of a panel shows this now (panel, slot, what).</summary>
        public event Action<int, int, string> ShowWanted;

        /// <summary>Who works a panel changed (panel, the player, or <see cref="PanelWire.NoOperator"/>).</summary>
        public event Action<int, byte> OperatorChanged;

        /// <summary>Messages dropped as malformed or not for this build.</summary>
        public long Malformed { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>
        /// The game side's list of a panel's parts: how many, and its shape (a number made from the list, the same in
        /// every game that lists them the same way).
        /// </summary>
        public void Define(int panel, int slots, uint shape)
        {
            if (panel < 0 || panel >= PanelWire.MaxPanels || slots < 0 || slots > PanelWire.MaxSlots)
            {
                throw new ArgumentOutOfRangeException("panel");
            }

            Panel existing = panels[panel];
            if (existing != null && existing.Shape == shape && existing.Values.Length == slots)
            {
                return;
            }

            panels[panel] = new Panel(slots, shape) { Operator = existing == null ? PanelWire.NoOperator : existing.Operator };
        }

        /// <summary>Who works the panel, or <see cref="PanelWire.NoOperator"/>.</summary>
        public byte Operator(int panel)
        {
            Panel at = Get(panel);
            return at == null ? PanelWire.NoOperator : at.Operator;
        }

        /// <summary>Whether this game works the panel.</summary>
        public bool IsMine(int panel)
        {
            Panel at = Get(panel);
            return at != null && session.State == SessionState.Connected && at.Operator == session.LocalPlayerId;
        }

        /// <summary>What a part of the panel shows, as last said; null before anything was.</summary>
        public string Shown(int panel, int slot)
        {
            Panel at = Get(panel);
            return at == null || slot < 0 || slot >= at.Values.Length ? null : at.Values[slot];
        }

        /// <summary>This game's player pressed a button of a panel this game does not work: the host decides.</summary>
        public void Press(int panel, int button)
        {
            if (Get(panel) == null || button < 0 || button >= PanelWire.MaxButtons || session.State != SessionState.Connected)
            {
                return;
            }

            if (IsHost)
            {
                Pressed(panel, button, session.LocalPlayerId);
                return;
            }

            writer.Reset();
            writer.WriteByte(PanelWire.MessagePress);
            writer.WriteByte((byte)panel);
            writer.WriteByte((byte)button);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        /// <summary>
        /// The host's game is being played when the session starts: it works the panel, if nobody does. Only the host:
        /// a client's world comes from the host's save, where the machine waits for someone.
        /// </summary>
        public void Claim(int panel)
        {
            Panel at = Get(panel);
            if (at != null && IsHost && session.State == SessionState.Connected && at.Operator == PanelWire.NoOperator)
            {
                SetOperator(panel, session.LocalPlayerId);
            }
        }

        /// <summary>This game works the panel: a part shows this now. Sent with the others within <see cref="ShowInterval"/>.</summary>
        public void Show(int panel, int slot, string value)
        {
            Panel at = Get(panel);
            if (at == null || slot < 0 || slot >= at.Values.Length || !IsMine(panel))
            {
                return;
            }

            value = Protocol.LimitText(value ?? string.Empty, PanelWire.MaxValueBytes);
            if (at.Values[slot] == value)
            {
                return;
            }

            at.Values[slot] = value;
            if (!at.Dirty.Contains(slot))
            {
                at.Dirty.Add(slot);
            }
        }

        /// <summary>This game works the panel and its game waits for someone again: let go of it.</summary>
        public void Release(int panel)
        {
            Panel at = Get(panel);
            if (at == null || !IsMine(panel))
            {
                return;
            }

            Flush(panel, at);
            if (IsHost)
            {
                SetOperator(panel, PanelWire.NoOperator);
                return;
            }

            writer.Reset();
            writer.WriteByte(PanelWire.MessageRelease);
            writer.WriteByte((byte)panel);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        /// <summary>
        /// On a client whose world is ready: asks the host who works each panel and all it shows. A client already in
        /// its game world when it joins asks before it is connected: the question goes once it is.
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

            SendRequest();
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            clock = now;
            if (requestWaiting && !IsHost && session.State == SessionState.Connected)
            {
                requestWaiting = false;
                SendRequest();
            }

            for (int i = 0; i < panels.Length; i++)
            {
                Panel at = panels[i];
                if (at != null && at.Dirty.Count > 0 && now >= at.NextShow && IsMine(i))
                {
                    at.NextShow = now + ShowInterval;
                    Flush(i, at);
                }
            }
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

        private void SendRequest()
        {
            writer.Reset();
            writer.WriteByte(PanelWire.MessageRequest);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        private Panel Get(int panel)
        {
            return panel < 0 || panel >= panels.Length ? null : panels[panel];
        }

        // What changed, from its worker: to the host, or from the host to every client.
        private void Flush(int panel, Panel at)
        {
            if (at.Dirty.Count == 0)
            {
                return;
            }

            at.Dirty.Sort();
            SendSlots(panel, at, at.Dirty, IsHost ? Protocol.Broadcast : Protocol.HostPlayerId);
            at.Dirty.Clear();
        }

        private void SendSlots(int panel, Panel at, IList<int> slots, byte to)
        {
            int start = 0;
            while (start < slots.Count)
            {
                writer.Reset();
                writer.WriteByte(PanelWire.MessageShow);
                writer.WriteByte((byte)panel);
                writer.WriteUInt32(at.Shape);
                int countAt = writer.Length;
                writer.WriteUInt16(0);
                int count = 0;
                while (start < slots.Count && (count == 0 || writer.Length < PanelWire.ShowBytes))
                {
                    int slot = slots[start++];
                    writer.WriteVarUInt32((uint)slot);
                    writer.WriteString(at.Values[slot] ?? string.Empty);
                    count++;
                }

                writer.Buffer[countAt] = (byte)count;
                writer.Buffer[countAt + 1] = (byte)(count >> 8);
                session.Send(to, writer.Buffer, 0, writer.Length, true);
            }
        }

        // The host: a press of some player's game. An idle panel becomes theirs; the press goes to whoever works it.
        private void Pressed(int panel, int button, byte player)
        {
            Panel at = Get(panel);
            if (at == null)
            {
                return;
            }

            if (at.Operator == PanelWire.NoOperator)
            {
                SetOperator(panel, player);
            }

            if (at.Operator == session.LocalPlayerId)
            {
                Action<int, int> handler = PressWanted;
                if (handler != null)
                {
                    handler(panel, button);
                }

                return;
            }

            writer.Reset();
            writer.WriteByte(PanelWire.MessageApply);
            writer.WriteByte((byte)panel);
            writer.WriteByte((byte)button);
            session.Send(at.Operator, writer.Buffer, 0, writer.Length, true);
        }

        // The host: a panel's worker changes, and everyone is told.
        private void SetOperator(int panel, byte player)
        {
            Panel at = Get(panel);
            if (at == null || at.Operator == player)
            {
                return;
            }

            at.Operator = player;
            SendOperator(panel, at, Protocol.Broadcast);
            RaiseOperator(panel, player);
        }

        private void SendOperator(int panel, Panel at, byte to)
        {
            writer.Reset();
            writer.WriteByte(PanelWire.MessageOperator);
            writer.WriteByte((byte)panel);
            writer.WriteByte(at.Operator);
            session.Send(to, writer.Buffer, 0, writer.Length, true);
        }

        private void RaiseOperator(int panel, byte player)
        {
            Action<int, byte> handler = OperatorChanged;
            if (handler != null)
            {
                handler(panel, player);
            }
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            if (!IsHost)
            {
                return;
            }

            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null && panels[i].Operator == player.Id)
                {
                    SetOperator(i, PanelWire.NoOperator);
                }
            }
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type < PanelWire.MessagePress || type > PanelWire.MessageRequest)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == PanelWire.MessageRequest)
                {
                    RequireHost(type);
                    for (int i = 0; i < panels.Length; i++)
                    {
                        Panel each = panels[i];
                        if (each == null)
                        {
                            continue;
                        }

                        SendOperator(i, each, sender);
                        var all = new List<int>();
                        for (int slot = 0; slot < each.Values.Length; slot++)
                        {
                            if (each.Values[slot] != null)
                            {
                                all.Add(slot);
                            }
                        }

                        SendSlots(i, each, all, sender);
                    }

                    return;
                }

                int panel = reader.ReadByte();
                Panel at = Get(panel);
                if (at == null)
                {
                    throw new PacketFormatException("A message about panel " + panel + ", which this game does not have.");
                }

                switch (type)
                {
                    case PanelWire.MessagePress:
                    {
                        RequireHost(type);
                        int button = reader.ReadByte();
                        if (button >= PanelWire.MaxButtons)
                        {
                            throw new PacketFormatException("Button " + button + " of panel " + panel + ".");
                        }

                        Pressed(panel, button, sender);
                        break;
                    }

                    case PanelWire.MessageOperator:
                    {
                        if (IsHost || sender != Protocol.HostPlayerId)
                        {
                            throw new PacketFormatException("Who works a panel, said by someone who is not the host.");
                        }

                        byte player = reader.ReadByte();
                        if (at.Operator != player)
                        {
                            at.Operator = player;
                            at.Dirty.Clear();
                            RaiseOperator(panel, player);
                        }

                        break;
                    }

                    case PanelWire.MessageApply:
                    {
                        if (IsHost || sender != Protocol.HostPlayerId || at.Operator != session.LocalPlayerId)
                        {
                            throw new PacketFormatException("A press to make, for a game that does not work panel " + panel + ".");
                        }

                        int button = reader.ReadByte();
                        if (button >= PanelWire.MaxButtons)
                        {
                            throw new PacketFormatException("Button " + button + " of panel " + panel + ".");
                        }

                        Action<int, int> handler = PressWanted;
                        if (handler != null)
                        {
                            handler(panel, button);
                        }

                        break;
                    }

                    case PanelWire.MessageShow:
                        Shown(panel, at, sender, data);
                        break;

                    case PanelWire.MessageRelease:
                        RequireHost(type);
                        if (at.Operator == sender)
                        {
                            SetOperator(panel, PanelWire.NoOperator);
                        }

                        break;
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Fsm, "Dropped a message about a machine's panel from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }

        private void RequireHost(byte type)
        {
            if (!IsHost)
            {
                throw new PacketFormatException("Message " + type + " about a panel, sent to a client.");
            }
        }

        // What a panel's parts show: from its worker (on the host, passed on to the others) or from the host.
        private void Shown(int panel, Panel at, byte sender, byte[] data)
        {
            if (IsHost ? sender != at.Operator : sender != Protocol.HostPlayerId)
            {
                throw new PacketFormatException("What panel " + panel + " shows, from player " + sender + ", who does not work it.");
            }

            uint shape = reader.ReadUInt32();
            if (shape != at.Shape)
            {
                throw new PacketFormatException("Panel " + panel + " is listed differently in player " + sender + "'s build.");
            }

            int count = reader.ReadUInt16();
            var slots = new int[count];
            var values = new string[count];
            for (int i = 0; i < count; i++)
            {
                uint slot = reader.ReadVarUInt32();
                if (slot >= (uint)at.Values.Length)
                {
                    throw new PacketFormatException("Part " + slot + " of panel " + panel + ", which has " + at.Values.Length + ".");
                }

                slots[i] = (int)slot;
                values[i] = reader.ReadString(PanelWire.MaxValueBytes);
            }

            if (IsHost)
            {
                for (int p = 0; p < session.Players.Count; p++)
                {
                    byte id = session.Players[p].Id;
                    if (id != Protocol.HostPlayerId && id != sender)
                    {
                        session.Send(id, data, 0, data.Length, true);
                    }
                }
            }

            Action<int, int, string> handler = ShowWanted;
            for (int i = 0; i < count; i++)
            {
                at.Values[slots[i]] = values[i];
                if (handler != null && !IsMine(panel))
                {
                    handler(panel, slots[i], values[i]);
                }
            }
        }

        private sealed class Panel
        {
            public Panel(int slots, uint shape)
            {
                Values = new string[slots];
                Shape = shape;
            }

            public string[] Values { get; }

            public uint Shape { get; }

            public byte Operator { get; set; }

            public List<int> Dirty { get; } = new List<int>();

            public double NextShow { get; set; }
        }
    }
}
