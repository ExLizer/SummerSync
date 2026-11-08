using System.Globalization;

namespace SummerSync.Core.Logging
{
    /// <summary>Renders log entries as single text lines with a stable, greppable layout.</summary>
    public static class LogFormatter
    {
        /// <summary>Example: <c>14:03:27.512 [WARN ] [net] Lost connection to peer 2</c>.</summary>
        public static string Format(LogEntry entry)
        {
            return entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " [" + LevelName(entry.Level) + "] [" + CategoryName(entry.Category) + "] " + entry.Message;
        }

        /// <summary>Fixed-width level tag so columns line up in a log file.</summary>
        public static string LevelName(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Trace: return "TRACE";
                case LogLevel.Debug: return "DEBUG";
                case LogLevel.Info: return "INFO ";
                case LogLevel.Warning: return "WARN ";
                case LogLevel.Error: return "ERROR";
                default: return "OFF  ";
            }
        }

        public static string CategoryName(LogCategory category)
        {
            switch (category)
            {
                case LogCategory.General: return "general";
                case LogCategory.Net: return "net";
                case LogCategory.Authority: return "authority";
                case LogCategory.Vehicle: return "vehicle";
                case LogCategory.Fsm: return "fsm";
                case LogCategory.Passenger: return "passenger";
                case LogCategory.Player: return "player";
                default: return category.ToString().ToLowerInvariant();
            }
        }
    }
}
