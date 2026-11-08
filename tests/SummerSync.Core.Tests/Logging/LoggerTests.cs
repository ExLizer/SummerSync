using SummerSync.Core.Logging;

namespace SummerSync.Core.Tests.Logging;

public class LoggerTests
{
    private static readonly DateTime FixedTime = new(2026, 10, 12, 14, 3, 27, 512);

    private sealed class CollectingSink : ILogSink
    {
        public List<LogEntry> Entries { get; } = new();

        public void Write(LogEntry entry) => Entries.Add(entry);
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Write(LogEntry entry) => throw new IOException("disk full");
    }

    private static (Logger Logger, CollectingSink Sink) CreateLogger()
    {
        var logger = new Logger(() => FixedTime);
        var sink = new CollectingSink();
        logger.AddSink(sink);
        return (logger, sink);
    }

    [Fact]
    public void Writes_entry_with_timestamp_level_category_and_message()
    {
        var (logger, sink) = CreateLogger();

        logger.Info(LogCategory.Net, "connected");

        LogEntry entry = Assert.Single(sink.Entries);
        Assert.Equal(FixedTime, entry.Timestamp);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Equal(LogCategory.Net, entry.Category);
        Assert.Equal("connected", entry.Message);
    }

    [Fact]
    public void Default_minimum_level_drops_debug_and_trace()
    {
        var (logger, sink) = CreateLogger();

        logger.Trace(LogCategory.Net, "t");
        logger.Debug(LogCategory.Net, "d");
        logger.Info(LogCategory.Net, "i");

        Assert.Equal("i", Assert.Single(sink.Entries).Message);
    }

    [Fact]
    public void Lowering_minimum_level_lets_verbose_entries_through()
    {
        var (logger, sink) = CreateLogger();
        logger.MinimumLevel = LogLevel.Trace;

        logger.Trace(LogCategory.Fsm, "t");
        logger.Debug(LogCategory.Fsm, "d");

        Assert.Equal(new[] { "t", "d" }, sink.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void Disabled_category_is_dropped_below_warning()
    {
        var (logger, sink) = CreateLogger();
        logger.MinimumLevel = LogLevel.Trace;
        logger.EnabledCategories = LogCategory.Net | LogCategory.Vehicle;

        logger.Info(LogCategory.Fsm, "fsm");
        logger.Debug(LogCategory.Passenger, "passenger");
        logger.Info(LogCategory.Vehicle, "vehicle");

        Assert.Equal("vehicle", Assert.Single(sink.Entries).Message);
    }

    [Fact]
    public void Warnings_and_errors_ignore_the_category_filter()
    {
        var (logger, sink) = CreateLogger();
        logger.EnabledCategories = LogCategory.None;

        logger.Info(LogCategory.Authority, "info");
        logger.Warning(LogCategory.Authority, "warning");
        logger.Error(LogCategory.Net, "error");

        Assert.Equal(new[] { "warning", "error" }, sink.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void Minimum_level_still_applies_to_warnings()
    {
        var (logger, sink) = CreateLogger();
        logger.MinimumLevel = LogLevel.Error;

        logger.Warning(LogCategory.Net, "warning");
        logger.Error(LogCategory.Net, "error");

        Assert.Equal("error", Assert.Single(sink.Entries).Message);
    }

    [Fact]
    public void Off_disables_everything()
    {
        var (logger, sink) = CreateLogger();
        logger.MinimumLevel = LogLevel.Off;

        logger.Error(LogCategory.Net, "error");
        logger.Log(LogLevel.Off, LogCategory.Net, "off is not a writable level");

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void Off_is_never_a_writable_level()
    {
        var (logger, sink) = CreateLogger();
        logger.MinimumLevel = LogLevel.Trace;

        logger.Log(LogLevel.Off, LogCategory.Net, "nope");

        Assert.Empty(sink.Entries);
        Assert.False(logger.IsEnabled(LogLevel.Off, LogCategory.Net));
    }

    [Fact]
    public void A_failing_sink_does_not_stop_the_others_or_throw()
    {
        var logger = new Logger(() => FixedTime);
        var sink = new CollectingSink();
        logger.AddSink(new ThrowingSink());
        logger.AddSink(sink);

        logger.Error(LogCategory.Net, "still delivered");

        Assert.Equal("still delivered", Assert.Single(sink.Entries).Message);
    }

    // A log that silently stops is worse than no log: whoever reads it takes its silence for "nothing happened".
    [Fact]
    public void What_a_failing_sink_loses_is_counted_and_explained()
    {
        var (logger, sink) = CreateLogger();
        Assert.Equal(0, logger.SinkFailures);
        Assert.Null(logger.LastSinkError);
        logger.AddSink(new ThrowingSink());

        logger.Info(LogCategory.Net, "one");
        logger.Info(LogCategory.Net, "two");
        logger.Debug(LogCategory.Net, "filtered out before any sink sees it");

        Assert.Equal(2, logger.SinkFailures);
        Assert.Equal("ThrowingSink: IOException: disk full", logger.LastSinkError);
        Assert.Equal(2, sink.Entries.Count);
    }

    [Fact]
    public void An_entry_can_be_written_whatever_the_filters_say()
    {
        var (logger, sink) = CreateLogger();
        logger.MinimumLevel = LogLevel.Off;
        logger.EnabledCategories = LogCategory.None;

        logger.Error(LogCategory.Net, "dropped");
        logger.Always(LogCategory.General, "Log settings: off");

        LogEntry entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Equal(LogCategory.General, entry.Category);
        Assert.Equal("Log settings: off", entry.Message);
    }

    [Fact]
    public void Null_sink_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new Logger().AddSink(null!));
    }
}
