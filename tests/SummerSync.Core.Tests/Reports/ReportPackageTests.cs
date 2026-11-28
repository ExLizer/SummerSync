using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using SummerSync.Core.Reports;
using SystemTarEntry = System.Formats.Tar.TarEntry;
using SystemTarEntryType = System.Formats.Tar.TarEntryType;
using SystemTarReader = System.Formats.Tar.TarReader;

namespace SummerSync.Core.Tests.Reports;

public class ReportPackageTests
{
    private static readonly DateTime When = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

    // A log as the mod writes them: timestamped lines that repeat in shape, some numbers that do not.
    private static byte[] FakeLog(int lines, int seed)
    {
        var random = new Random(seed);
        var text = new StringBuilder();
        string[] words = { "Vehicles:", "Objects:", "Hand:", "Follow:", "Drawn:", "Chat:", "Frames:" };
        for (int i = 0; i < lines; i++)
        {
            text.Append("2026-11-28 12:").Append((i / 60 % 60).ToString("00")).Append(':').Append((i % 60).ToString("00"))
                .Append(" [Info] [Vehicle] ").Append(words[random.Next(words.Length)]).Append(" 'SATSUMA(557kg, 248)' at (")
                .Append(random.Next(-2000, 2000)).Append(", ").Append(random.Next(-5, 50)).Append(", ").Append(random.Next(-2000, 2000))
                .Append("), speed ").Append(random.NextDouble().ToString("0.00")).Append(" m/s\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static byte[] RandomBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static Dictionary<string, byte[]> Unpack(byte[] gzip)
    {
        var files = new Dictionary<string, byte[]>();
        using var compressed = new MemoryStream(gzip);
        using var unzipped = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new SystemTarReader(unzipped);
        SystemTarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            using var data = new MemoryStream();
            entry.DataStream?.CopyTo(data);
            Assert.Equal(SystemTarEntryType.RegularFile, entry.EntryType);
            files[entry.Name] = data.ToArray();
        }

        return files;
    }

    private static List<string> Order(byte[] gzip)
    {
        var names = new List<string>();
        using var compressed = new MemoryStream(gzip);
        using var unzipped = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new SystemTarReader(unzipped);
        SystemTarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            names.Add(entry.Name);
        }

        return names;
    }

    [Fact]
    public void Deflate_blocks_put_together_inflate_back_to_what_went_in()
    {
        var deflater = new Deflater();
        byte[][] pieces = { FakeLog(3000, 1), RandomBytes(70_000, 2), new byte[0], Encoding.UTF8.GetBytes("äöå ñ ¿qué? " + new string('a', 1000)), FakeLog(10, 3) };
        using var stream = new MemoryStream();
        foreach (byte[] piece in pieces)
        {
            byte[] compressed = deflater.Compress(piece, 0, piece.Length);
            stream.Write(compressed, 0, compressed.Length);
        }

        stream.Write(Deflater.FinalBlock, 0, Deflater.FinalBlock.Length);
        stream.Position = 0;
        using var inflate = new DeflateStream(stream, CompressionMode.Decompress);
        using var back = new MemoryStream();
        inflate.CopyTo(back);
        Assert.Equal(pieces.SelectMany(p => p).ToArray(), back.ToArray());
    }

    [Fact]
    public void Long_repeats_and_far_repeats_inflate_back()
    {
        // Matches of every length up to 258 and distances up to the whole window.
        var text = new StringBuilder();
        for (int i = 0; i < 400; i++)
        {
            text.Append(new string((char)('a' + (i % 26)), i % 300)).Append(i).Append('\n');
        }

        byte[] head = Encoding.ASCII.GetBytes(text.ToString());
        byte[] data = head.Concat(RandomBytes(32_000, 4)).Concat(head).ToArray();
        byte[] compressed = new Deflater(200).Compress(data, 0, data.Length).Concat(Deflater.FinalBlock).ToArray();
        using var inflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var back = new MemoryStream();
        inflate.CopyTo(back);
        Assert.Equal(data, back.ToArray());
        Assert.True(compressed.Length < data.Length);
    }

    [Fact]
    public void Logs_compress_to_a_small_part_of_their_size()
    {
        byte[] log = FakeLog(20_000, 5);
        byte[] compressed = new Deflater().Compress(log, 0, log.Length);
        Assert.True(compressed.Length * 3 < log.Length, compressed.Length + " of " + log.Length);
    }

    [Fact]
    public void The_crc_of_two_pieces_comes_from_the_crcs_of_each()
    {
        byte[] a = RandomBytes(1000, 6);
        byte[] b = RandomBytes(777, 7);
        byte[] both = a.Concat(b).ToArray();
        Assert.Equal(Crc32.Of(both, 0, both.Length), Crc32.Combine(Crc32.Of(a, 0, a.Length), Crc32.Of(b, 0, b.Length), b.Length));
        Assert.Equal(Crc32.Of(a, 0, a.Length), Crc32.Combine(0, Crc32.Of(a, 0, a.Length), a.Length));
        Assert.Equal(0xCBF43926u, Crc32.Of(Encoding.ASCII.GetBytes("123456789"), 0, 9));
    }

    [Fact]
    public void A_report_is_a_tar_gz_with_report_json_first_and_every_file_as_it_was()
    {
        var files = new List<ReportFile>
        {
            new("snapshot.txt", Encoding.UTF8.GetBytes("Session: host with 2 players\n"), true, When),
            new("SummerSync.log", FakeLog(2000, 8), true, When),
            new("settings.json", Encoding.UTF8.GetBytes("{\"language\":0}"), true, When),
            new("SummerSync.prev.log", FakeLog(500, 9), false, When),
        };

        byte[] gzip = new ReportPackage().Build(files, contents => Encoding.UTF8.GetBytes("{\"files\":" + contents.Included.Count + "}"), out ReportContents packed);
        Dictionary<string, byte[]> back = Unpack(gzip);
        Assert.Equal(new[] { "report.json", "snapshot.txt", "SummerSync.log", "settings.json", "SummerSync.prev.log" }, Order(gzip));
        foreach (ReportFile file in files)
        {
            Assert.Equal(file.Data, back[file.Name]);
        }

        Assert.Equal("{\"files\":4}", Encoding.UTF8.GetString(back["report.json"]));
        Assert.Empty(packed.LeftOut);
        Assert.All(packed.Included, file => Assert.Equal(0, file.CutBytes));
    }

    [Fact]
    public void Over_the_limit_the_oldest_logs_are_left_out_and_said_so()
    {
        // Random bytes do not compress: each of these takes about 300 kB in the report.
        var files = new List<ReportFile>
        {
            new("SummerSync.log", RandomBytes(300_000, 10), true, When),
            new("SummerSync.prev.log", RandomBytes(300_000, 11), false, When),
            new("SummerSync.prev2.log", RandomBytes(300_000, 12), false, When),
            new("tiny.txt", Encoding.ASCII.GetBytes("small"), false, When),
            new("settings.json", Encoding.ASCII.GetBytes("{}"), true, When),
        };

        var package = new ReportPackage { Limit = 800_000, ReportReserve = 16_000 };
        byte[] gzip = package.Build(files, contents => Encoding.UTF8.GetBytes(string.Join(",", contents.LeftOut.Select(f => f.Name))), out ReportContents packed);
        Assert.True(gzip.Length <= 800_000, gzip.Length.ToString());
        Dictionary<string, byte[]> back = Unpack(gzip);
        Assert.Equal(new[] { "report.json", "SummerSync.log", "SummerSync.prev.log", "settings.json" }, Order(gzip));
        Assert.Equal(new[] { "SummerSync.prev2.log", "tiny.txt" }, packed.LeftOut.Select(f => f.Name));
        Assert.Equal("SummerSync.prev2.log,tiny.txt", Encoding.UTF8.GetString(back["report.json"]));
    }

    [Fact]
    public void A_required_file_too_big_for_the_report_keeps_its_last_lines()
    {
        var lines = new StringBuilder();
        var random = new Random(13);
        for (int i = 0; i < 60_000; i++)
        {
            lines.Append("line ").Append(i).Append(' ').Append(random.Next()).Append(random.Next()).Append('\n');
        }

        byte[] log = Encoding.ASCII.GetBytes(lines.ToString());
        var package = new ReportPackage { Limit = 300_000, ReportReserve = 8_000 };
        byte[] gzip = package.Build(new List<ReportFile> { new("SummerSync.log", log, true, When) }, _ => new byte[0], out ReportContents packed);
        Assert.True(gzip.Length <= 300_000, gzip.Length.ToString());
        string kept = Encoding.ASCII.GetString(Unpack(gzip)["SummerSync.log"]);
        Assert.StartsWith("[SummerSync: the first ", kept);
        Assert.EndsWith(lines.ToString().Split('\n')[59999] + "\n", kept);
        PackedFile file = Assert.Single(packed.Included);
        Assert.True(file.CutBytes > 0);

        // It starts at a whole line.
        string second = kept.Split('\n')[1];
        Assert.StartsWith("line ", second);
    }

    [Fact]
    public void Tar_names_are_plain_file_names()
    {
        Assert.True(TarEntry.IsValidName("SummerSync.prev5.log"));
        Assert.False(TarEntry.IsValidName("../evil"));
        Assert.False(TarEntry.IsValidName("a b"));
        Assert.False(TarEntry.IsValidName(new string('a', 101)));
        Assert.Throws<ArgumentException>(() => new ReportFile("dir/file", new byte[0], true, When));
    }

    [Fact]
    public void A_real_tar_reads_the_report()
    {
        string? tar = FindOnPath(OperatingSystem.IsWindows() ? "tar.exe" : "tar");
        if (tar == null)
        {
            return;
        }

        var files = new List<ReportFile>
        {
            new("SummerSync.log", FakeLog(3000, 14), true, When),
            new("output_log.txt", FakeLog(1000, 15), true, When),
        };

        byte[] gzip = new ReportPackage().Build(files, _ => Encoding.UTF8.GetBytes("{}"), out _);
        string folder = Path.Combine(Path.GetTempPath(), "summersync-report-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string archive = Path.Combine(folder, "report.tar.gz");
            File.WriteAllBytes(archive, gzip);
            var start = new ProcessStartInfo(tar, "-tzvf \"" + archive + "\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using Process process = Process.Start(start)!;
            string listing = process.StandardOutput.ReadToEnd();
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors);
            Assert.Contains("report.json", listing);
            Assert.Contains("SummerSync.log", listing);
            Assert.Contains("output_log.txt", listing);

            var extract = new ProcessStartInfo(tar, "-xzf \"" + archive + "\" -C \"" + folder + "\"") { RedirectStandardError = true, UseShellExecute = false };
            using Process extracting = Process.Start(extract)!;
            string extractErrors = extracting.StandardError.ReadToEnd();
            extracting.WaitForExit();
            Assert.True(extracting.ExitCode == 0, extractErrors);
            Assert.Equal(files[0].Data, File.ReadAllBytes(Path.Combine(folder, "SummerSync.log")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static string? FindOnPath(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string folder in (path ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
