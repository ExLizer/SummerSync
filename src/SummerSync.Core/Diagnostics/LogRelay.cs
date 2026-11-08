using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.World;

namespace SummerSync.Core.Diagnostics
{
    /// <summary>Message of <see cref="LogRelay"/>.</summary>
    public static class LogRelayWire
    {
        /// <summary>Client to host, reliable: one line of the client's log, with its level and category.</summary>
        public const byte MessageLine = WorldWire.MessageWeather + 1;
    }

    /// <summary>
    /// Sends the lines of a client's log that tell what went wrong there to the host, which writes them into
    /// its own log. Most of what the mod gets wrong shows on the machine that does not simulate a thing, and
    /// that machine's log is on another computer: without this, half of every report is missing.
    ///
    /// What goes: warnings and errors, and the informational lines about vehicles, state machines, riding and
    /// who simulates what. Not the lines about the network or the players, and never faster than a few per
    /// frame: a line that does not fit is dropped and counted, and the count is sent.
    /// </summary>
    public sealed class LogRelay : ILogSink
    {
        /// <summary>What a relayed line starts with in the host's log, before the number of the player it is from.</summary>
        public const string Prefix = "[from player ";

        private const LogCategory Relayed = LogCategory.Vehicle | LogCategory.Fsm | LogCategory.Passenger | LogCategory.Authority;
        private const int MaxQueued = 200;
        private const int MaxCharacters = 400;
        private const int MaxLineBytes = 800;
        private const int MaxBytes = 2000;
        private const int LinesPerFlush = 3;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly bool sends;
        private readonly object gate = new object();
        private readonly Queue<LogEntry> queue = new Queue<LogEntry>();
        private readonly PacketWriter writer = new PacketWriter(1000);
        private readonly PacketReader reader = new PacketReader();
        private long dropped;
        private bool detached;

        /// <param name="sends">False for a session inside one process (the loopback self-test), whose two ends share a log.</param>
        public LogRelay(NetSession session, Logger log, bool sends)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            this.sends = sends;
            session.MessageReceived += OnMessage;
            session.BeforeFlush += OnBeforeFlush;
            this.log.AddSink(this);
        }

        /// <summary>Lines sent to the host, on a client; lines received and written, on the host.</summary>
        public long Lines { get; private set; }

        public long Malformed { get; private set; }

        /// <summary>Called by the log for every entry it writes. Only notes the entry: nothing is sent from here.</summary>
        public void Write(LogEntry entry)
        {
            if (!sends || detached || entry.Level < LogLevel.Info)
            {
                return;
            }

            if (entry.Level < LogLevel.Warning && (entry.Category & Relayed) == 0)
            {
                return;
            }

            // What the host wrote for another client of a session this process is also a client of.
            if (entry.Message != null && entry.Message.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return;
            }

            lock (gate)
            {
                if (queue.Count >= MaxQueued)
                {
                    dropped++;
                    return;
                }

                queue.Enqueue(entry);
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
            session.BeforeFlush -= OnBeforeFlush;
            log.RemoveSink(this);
            lock (gate)
            {
                queue.Clear();
            }
        }

        private void OnBeforeFlush()
        {
            if (!sends || session.Role != SessionRole.Client || session.State != SessionState.Connected)
            {
                return;
            }

            for (int sent = 0; sent < LinesPerFlush; sent++)
            {
                LogEntry entry;
                lock (gate)
                {
                    if (queue.Count == 0)
                    {
                        if (dropped == 0)
                        {
                            return;
                        }

                        entry = new LogEntry(DateTime.MinValue, LogLevel.Warning, LogCategory.General, dropped + " line(s) of this log were not sent to the host: too many at once.");
                        dropped = 0;
                    }
                    else
                    {
                        entry = queue.Dequeue();
                    }
                }

                string text = entry.Message ?? string.Empty;
                text = text.Length > MaxCharacters ? text.Substring(0, MaxCharacters) + "..." : text;

                // A line has to fit in one packet whatever it is written in.
                while (System.Text.Encoding.UTF8.GetByteCount(text) > MaxLineBytes)
                {
                    text = text.Substring(0, text.Length / 2);
                }

                writer.Reset();
                writer.WriteByte(LogRelayWire.MessageLine);
                writer.WriteByte((byte)entry.Level);
                writer.WriteByte((byte)entry.Category);
                writer.WriteString(text);
                try
                {
                    session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
                    Lines++;
                }
                catch (ArgumentException)
                {
                    // A transport with smaller packets than any this was written for. The log is not worth
                    // an error in the middle of a frame.
                    lock (gate)
                    {
                        dropped++;
                    }

                    return;
                }
            }
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            if (data[0] != LogRelayWire.MessageLine)
            {
                return;
            }

            try
            {
                if (session.Role != SessionRole.Host)
                {
                    throw new PacketFormatException("A line of a log sent to a client.");
                }

                reader.SetBuffer(data, 1, data.Length - 1);
                byte level = reader.ReadByte();
                var category = (LogCategory)reader.ReadByte();
                string text = reader.ReadString(MaxBytes);
                if (level < (byte)LogLevel.Info || level > (byte)LogLevel.Error)
                {
                    throw new PacketFormatException("A line of a log with level " + level + ".");
                }

                // One line of this log per line of theirs, whatever they sent.
                text = text.Replace('\r', ' ').Replace('\n', ' ');
                Lines++;
                log.Log((LogLevel)level, category & LogCategory.All, Prefix + sender + "] " + text);
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (Malformed == 1)
                {
                    log.Warning(LogCategory.Net, "Player " + sender + " sent a line of their log that cannot be read: " + exception.Message);
                }
            }
        }
    }
}
