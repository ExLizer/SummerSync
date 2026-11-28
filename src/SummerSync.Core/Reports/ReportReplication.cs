using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Players;

namespace SummerSync.Core.Reports
{
    /// <summary>Messages of <see cref="ReportReplication"/>, after the chat's.</summary>
    public static class ReportWire
    {
        /// <summary>Client to host, reliable: this client's player sent a bug report, with its code.</summary>
        public const byte MessageSent = ChatWire.MessageLine + 1;

        /// <summary>Host to client, reliable: a player of the session sent a bug report, who and its code.</summary>
        public const byte MessageNotice = ChatWire.MessageLine + 2;
    }

    /// <summary>Another player's bug report, as the session told it.</summary>
    public sealed class ReportNotice
    {
        public ReportNotice(byte playerId, string name, string code, double at)
        {
            PlayerId = playerId;
            Name = name ?? string.Empty;
            Code = code;
            At = at;
        }

        public byte PlayerId { get; private set; }

        /// <summary>The player's name as the session had it when the notice came.</summary>
        public string Name { get; private set; }

        /// <summary>The report's code, which a report of this player's names to be filed with it.</summary>
        public string Code { get; private set; }

        /// <summary>When it came, on the clock of <see cref="ReportReplication.Update"/>.</summary>
        public double At { get; private set; }
    }

    /// <summary>
    /// Tells the other players of a session that one of them sent a bug report, so that each can be asked whether to send
    /// their own log with it. A client tells the host the code its report got; the host tells every other client who sent
    /// it, as it does with the chat. Nothing is sent from here but the code: each player's own report, if they agree to it,
    /// goes to the server from their own game.
    /// </summary>
    public sealed class ReportReplication
    {
        /// <summary>Notices kept for the window when nobody takes them.</summary>
        public const int MaxWaiting = 8;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(32);
        private readonly PacketReader reader = new PacketReader();
        private readonly List<ReportNotice> waiting = new List<ReportNotice>();
        private double now;
        private bool detached;

        public ReportReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
        }

        /// <summary>Notices of this player's reports sent to the others.</summary>
        public long Told { get; private set; }

        /// <summary>Notices of the others' reports that arrived here.</summary>
        public long Heard { get; private set; }

        public long Malformed { get; private set; }

        /// <summary>Call once per frame, before the session's update, with the same clock.</summary>
        public void Update(double time)
        {
            now = time;
        }

        /// <summary>This player's report got that code: the others are told. False without a session or for no code.</summary>
        public bool Announce(string code)
        {
            string checkedCode = ReportCode.Normalize(code);
            if (detached || checkedCode == null || session.State != SessionState.Connected)
            {
                return false;
            }

            writer.Reset();
            if (session.Role == SessionRole.Host)
            {
                writer.WriteByte(ReportWire.MessageNotice);
                writer.WriteByte(session.LocalPlayerId);
                writer.WriteString(checkedCode);
                session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);
            }
            else
            {
                writer.WriteByte(ReportWire.MessageSent);
                writer.WriteString(checkedCode);
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            }

            Told++;
            log.Info(LogCategory.General, "Report: the other players are told of report " + checkedCode + ".");
            return true;
        }

        /// <summary>Moves the notices that came since the last call, oldest first, into <paramref name="into"/>.</summary>
        public int Take(List<ReportNotice> into)
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
            if (Told > 0 || Heard > 0 || Malformed > 0)
            {
                log.Info(LogCategory.General, "Report notices: " + Told + " told, " + Heard + " heard, " + Malformed + " unreadable.");
            }
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != ReportWire.MessageSent && type != ReportWire.MessageNotice)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == ReportWire.MessageSent)
                {
                    if (session.Role != SessionRole.Host)
                    {
                        throw new PacketFormatException("A report's notice to pass on, sent to a client.");
                    }

                    string code = ReadCode();
                    Heard++;
                    Add(sender, code);

                    // Passed on to the other clients.
                    writer.Reset();
                    writer.WriteByte(ReportWire.MessageNotice);
                    writer.WriteByte(sender);
                    writer.WriteString(code);
                    IList<PlayerInfo> players = session.Players;
                    for (int i = 0; i < players.Count; i++)
                    {
                        byte id = players[i].Id;
                        if (id != sender && id != session.LocalPlayerId)
                        {
                            session.Send(id, writer.Buffer, 0, writer.Length, true);
                        }
                    }
                }
                else
                {
                    if (session.Role != SessionRole.Client || sender != Protocol.HostPlayerId)
                    {
                        throw new PacketFormatException("A report's notice from player " + sender + ", who is not the host.");
                    }

                    byte author = reader.ReadByte();
                    string code = ReadCode();
                    if (author != session.LocalPlayerId)
                    {
                        Heard++;
                        Add(author, code);
                    }
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (Malformed == 1)
                {
                    log.Warning(LogCategory.Net, "Player " + sender + " sent a report's notice that cannot be read: " + exception.Message);
                }
            }
        }

        private string ReadCode()
        {
            string code = ReportCode.Normalize(reader.ReadString(ReportCode.Length * 3));
            if (code == null)
            {
                throw new PacketFormatException("Not a report's code.");
            }

            return code;
        }

        private void Add(byte author, string code)
        {
            if (waiting.Count >= MaxWaiting)
            {
                waiting.RemoveAt(0);
            }

            string name = NameOf(author);
            waiting.Add(new ReportNotice(author, name, code, now));
            log.Info(LogCategory.General, "Report: " + name + " (player " + author + ") sent bug report " + code + ".");
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
    }
}
