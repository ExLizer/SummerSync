namespace SummerSync.Core.Logging
{
    /// <summary>Destination for log entries (file, in-game console, test collector...).</summary>
    public interface ILogSink
    {
        void Write(LogEntry entry);
    }
}
