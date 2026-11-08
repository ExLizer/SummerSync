namespace SummerSync.Core.Logging
{
    /// <summary>Severity of a log entry, ordered from most to least verbose.</summary>
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warning = 3,
        Error = 4,

        /// <summary>Only valid as a minimum level: disables all output.</summary>
        Off = 5,
    }
}
