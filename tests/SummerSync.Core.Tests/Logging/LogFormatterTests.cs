using SummerSync.Core.Logging;

namespace SummerSync.Core.Tests.Logging;

public class LogFormatterTests
{
    [Fact]
    public void Formats_a_single_line_with_time_level_and_category()
    {
        var entry = new LogEntry(new DateTime(2026, 10, 12, 14, 3, 27, 512), LogLevel.Warning, LogCategory.Net, "Lost connection to peer 2");

        Assert.Equal("14:03:27.512 [WARN ] [net] Lost connection to peer 2", LogFormatter.Format(entry));
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Info)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    public void Level_tags_share_one_width(LogLevel level)
    {
        Assert.Equal(5, LogFormatter.LevelName(level).Length);
    }

    [Theory]
    [InlineData(LogCategory.General, "general")]
    [InlineData(LogCategory.Net, "net")]
    [InlineData(LogCategory.Authority, "authority")]
    [InlineData(LogCategory.Vehicle, "vehicle")]
    [InlineData(LogCategory.Fsm, "fsm")]
    [InlineData(LogCategory.Passenger, "passenger")]
    public void Category_names_are_lowercase(LogCategory category, string expected)
    {
        Assert.Equal(expected, LogFormatter.CategoryName(category));
    }
}
