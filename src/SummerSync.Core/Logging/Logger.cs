using System;
using System.Collections.Generic;

namespace SummerSync.Core.Logging
{
    /// <summary>
    /// Filters log entries by level and category and forwards them to the registered sinks.
    /// Warnings and errors ignore the category filter: a problem must be diagnosable from the log
    /// even when its category was not enabled beforehand.
    /// </summary>
    public sealed class Logger
    {
        private readonly object gate = new object();
        private readonly List<ILogSink> sinks = new List<ILogSink>();
        private readonly Func<DateTime> clock;

        public Logger()
            : this(null)
        {
        }

        public Logger(Func<DateTime> clock)
        {
            this.clock = clock ?? (() => DateTime.Now);
        }

        public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        public LogCategory EnabledCategories { get; set; } = LogCategory.All;

        /// <summary>Entries a sink failed to write. They are lost; this is the only trace of them.</summary>
        public long SinkFailures { get; private set; }

        /// <summary>Why the most recent of those failed, or null.</summary>
        public string LastSinkError { get; private set; }

        public void AddSink(ILogSink sink)
        {
            if (sink == null)
            {
                throw new ArgumentNullException("sink");
            }

            lock (gate)
            {
                sinks.Add(sink);
            }
        }

        /// <summary>Stops writing to a sink. Does nothing if the log does not write to it.</summary>
        public void RemoveSink(ILogSink sink)
        {
            lock (gate)
            {
                sinks.Remove(sink);
            }
        }

        /// <summary>
        /// Whether an entry would be written. Check this before building an expensive message on a hot path.
        /// </summary>
        public bool IsEnabled(LogLevel level, LogCategory category)
        {
            if (level < MinimumLevel || level >= LogLevel.Off)
            {
                return false;
            }

            return level >= LogLevel.Warning || (EnabledCategories & category) != 0;
        }

        public void Log(LogLevel level, LogCategory category, string message)
        {
            if (!IsEnabled(level, category))
            {
                return;
            }

            Write(level, category, message);
        }

        /// <summary>
        /// Writes an informational entry whatever the level and the categories are set to. For the few lines
        /// without which a log cannot be read at all, such as what it is set to record.
        /// </summary>
        public void Always(LogCategory category, string message)
        {
            Write(LogLevel.Info, category, message);
        }

        private void Write(LogLevel level, LogCategory category, string message)
        {
            var entry = new LogEntry(clock(), level, category, message);
            lock (gate)
            {
                for (int i = 0; i < sinks.Count; i++)
                {
                    try
                    {
                        sinks[i].Write(entry);
                    }
                    catch (Exception exception)
                    {
                        // A failing sink (disk full, console not ready) must never take the game loop down. It is
                        // counted, because a log that silently stops is worse than no log.
                        SinkFailures++;
                        LastSinkError = sinks[i].GetType().Name + ": " + exception.GetType().Name + ": " + exception.Message;
                    }
                }
            }
        }

        public void Trace(LogCategory category, string message)
        {
            Log(LogLevel.Trace, category, message);
        }

        public void Debug(LogCategory category, string message)
        {
            Log(LogLevel.Debug, category, message);
        }

        public void Info(LogCategory category, string message)
        {
            Log(LogLevel.Info, category, message);
        }

        public void Warning(LogCategory category, string message)
        {
            Log(LogLevel.Warning, category, message);
        }

        public void Error(LogCategory category, string message)
        {
            Log(LogLevel.Error, category, message);
        }
    }
}
