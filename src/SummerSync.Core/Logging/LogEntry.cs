using System;

namespace SummerSync.Core.Logging
{
    /// <summary>A single log record handed to every <see cref="ILogSink"/>.</summary>
    public struct LogEntry
    {
        public LogEntry(DateTime timestamp, LogLevel level, LogCategory category, string message)
        {
            Timestamp = timestamp;
            Level = level;
            Category = category;
            Message = message;
        }

        public DateTime Timestamp { get; }

        public LogLevel Level { get; }

        public LogCategory Category { get; }

        public string Message { get; }
    }
}
