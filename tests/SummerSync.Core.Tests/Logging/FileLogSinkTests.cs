using SummerSync.Core.Logging;

namespace SummerSync.Core.Tests.Logging;

public sealed class FileLogSinkTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SummerSync.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LogEntry Entry(string message) =>
        new(new DateTime(2026, 10, 12, 9, 0, 0), LogLevel.Info, LogCategory.General, message);

    [Fact]
    public void Creates_the_directory_and_writes_formatted_lines()
    {
        string path = Path.Combine(directory, "nested", "SummerSync.log");

        using (var sink = new FileLogSink(path))
        {
            sink.Write(Entry("first"));
            sink.Write(Entry("second"));
        }

        Assert.Equal(
            new[] { "09:00:00.000 [INFO ] [general] first", "09:00:00.000 [INFO ] [general] second" },
            File.ReadAllLines(path));
    }

    [Fact]
    public void Lines_are_readable_while_the_sink_is_still_open()
    {
        string path = Path.Combine(directory, "SummerSync.log");
        using var sink = new FileLogSink(path);

        sink.Write(Entry("flushed immediately"));

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        Assert.Contains("flushed immediately", reader.ReadToEnd());
    }

    // A test evening restarts the game several times. With a single previous log, the run that mattered on
    // 2026-10-12 had already been overwritten when it was looked for.
    [Fact]
    public void Keeps_the_logs_of_the_five_sessions_before_the_current_one()
    {
        string path = Path.Combine(directory, "SummerSync.log");

        for (int session = 1; session <= 9; session++)
        {
            using var sink = new FileLogSink(path);
            sink.Write(Entry("session " + session));
            Assert.Equal(1, sink.LinesWritten);
        }

        Assert.Contains("session 9", File.ReadAllText(path));
        Assert.Contains("session 8", File.ReadAllText(FileLogSink.GetPreviousPath(path)));
        Assert.Contains("session 7", File.ReadAllText(FileLogSink.GetPreviousPath(path, 2)));
        Assert.Contains("session 4", File.ReadAllText(FileLogSink.GetPreviousPath(path, 5)));
        Assert.Equal(1 + FileLogSink.PreviousLogs, Directory.GetFiles(directory).Length);
        Assert.EndsWith("SummerSync.prev.log", FileLogSink.GetPreviousPath(path));
        Assert.EndsWith("SummerSync.prev3.log", FileLogSink.GetPreviousPath(path, 3));
    }

    [Fact]
    public void An_old_log_that_cannot_be_moved_does_not_stop_a_new_one_from_starting()
    {
        string path = Path.Combine(directory, "SummerSync.log");
        for (int session = 1; session <= 3; session++)
        {
            using var sink = new FileLogSink(path);
            sink.Write(Entry("session " + session));
        }

        // Someone has the log of two sessions ago open in a program that locks it.
        using (new FileStream(FileLogSink.GetPreviousPath(path, 2), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var sink = new FileLogSink(path);
            sink.Write(Entry("session 4"));
        }

        Assert.Contains("session 4", File.ReadAllText(path));
        Assert.Contains("session 3", File.ReadAllText(FileLogSink.GetPreviousPath(path)));
    }

    // Why the mod keeps one process-wide sink: MSCLoader re-creates the mod when the main menu reloads, and a
    // second sink on the same file cannot be opened while the first one is alive.
    [Fact]
    public void A_second_sink_on_the_same_file_is_refused_while_the_first_is_open()
    {
        string path = Path.Combine(directory, "SummerSync.log");
        using var first = new FileLogSink(path);
        first.Write(Entry("still here"));

        Assert.ThrowsAny<IOException>(() => new FileLogSink(path));

        first.Write(Entry("and still writable"));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        Assert.Contains("still here", text);
        Assert.Contains("and still writable", text);
    }

    [Fact]
    public void Previous_path_keeps_the_extension()
    {
        Assert.Equal(Path.Combine("logs", "SummerSync.prev.log"), FileLogSink.GetPreviousPath(Path.Combine("logs", "SummerSync.log")));
    }

    [Fact]
    public void Empty_path_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new FileLogSink(""));
    }

    // Two copies of the game on one machine share the mod's settings folder.
    [Fact]
    public void A_second_instance_logs_to_a_numbered_file_and_leaves_the_first_one_alone()
    {
        string path = Path.Combine(directory, "SummerSync.log");
        using (var earlier = new FileLogSink(path))
        {
            earlier.Write(Entry("earlier session"));
        }

        using FileLogSink first = FileLogSink.OpenFirstFree(path, 4);
        first.Write(Entry("first instance"));
        using FileLogSink second = FileLogSink.OpenFirstFree(path, 4);
        second.Write(Entry("second instance"));

        Assert.Equal(path, first.Path);
        Assert.Equal(Path.Combine(directory, "SummerSync.2.log"), second.Path);
        Assert.Contains("first instance", ReadWhileOpen(first.Path));
        Assert.Contains("second instance", ReadWhileOpen(second.Path));
        Assert.Contains("earlier session", File.ReadAllText(FileLogSink.GetPreviousPath(path)));
    }

    private static string ReadWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Opening_gives_up_after_the_allowed_number_of_instances()
    {
        string path = Path.Combine(directory, "SummerSync.log");
        using FileLogSink first = FileLogSink.OpenFirstFree(path, 2);
        using FileLogSink second = FileLogSink.OpenFirstFree(path, 2);

        Assert.ThrowsAny<IOException>(() => FileLogSink.OpenFirstFree(path, 2));
    }
}
