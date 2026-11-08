using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SummerSync.Core.Logging
{
    /// <summary>
    /// Writes log entries to a text file, flushing after every line so the log survives a crash.
    /// The logs of the sessions before are kept next to it: ".prev" for the last one, then ".prev2" and so
    /// on. A test evening restarts the game several times, and the run that matters is rarely the last.
    /// </summary>
    public sealed class FileLogSink : ILogSink, IDisposable
    {
        /// <summary>How many earlier logs are kept.</summary>
        public const int PreviousLogs = 5;

        private readonly StreamWriter writer;

        public FileLogSink(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("A log file path is required.", "path");
            }

            Path = path;
            PreviousPath = GetPreviousPath(path);

            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            RotatePrevious();

            // FileShare.Read lets the log be opened in an editor while the game is still running.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public string Path { get; }

        public string PreviousPath { get; }

        /// <summary>Lines written to the file by this sink.</summary>
        public long LinesWritten { get; private set; }

        /// <summary>"dir/SummerSync.log" -> "dir/SummerSync.prev.log".</summary>
        public static string GetPreviousPath(string path)
        {
            return GetPreviousPath(path, 1);
        }

        /// <summary>The log of <paramref name="sessionsAgo"/> sessions ago: ".prev.log", ".prev2.log", ".prev3.log"...</summary>
        public static string GetPreviousPath(string path, int sessionsAgo)
        {
            return System.IO.Path.ChangeExtension(path, null) + ".prev" + (sessionsAgo <= 1 ? string.Empty : sessionsAgo.ToString(CultureInfo.InvariantCulture))
                + System.IO.Path.GetExtension(path);
        }

        /// <summary>"dir/SummerSync.log", 2 -> "dir/SummerSync.2.log". Instance 1 is the path itself.</summary>
        public static string GetInstancePath(string path, int instance)
        {
            return instance <= 1 ? path : System.IO.Path.ChangeExtension(path, null) + "." + instance + System.IO.Path.GetExtension(path);
        }

        /// <summary>
        /// Opens the log at <paramref name="path"/>, or, when another process is already logging there (a second
        /// copy of the game on the same machine), the first free numbered name next to it.
        /// </summary>
        public static FileLogSink OpenFirstFree(string path, int maxInstances)
        {
            for (int instance = 1; ; instance++)
            {
                try
                {
                    return new FileLogSink(GetInstancePath(path, instance));
                }
                catch (IOException)
                {
                    if (instance >= maxInstances)
                    {
                        throw;
                    }
                }
            }
        }

        public void Write(LogEntry entry)
        {
            writer.WriteLine(LogFormatter.Format(entry));
            LinesWritten++;
        }

        public void Dispose()
        {
            writer.Dispose();
        }

        private void RotatePrevious()
        {
            if (!File.Exists(Path))
            {
                return;
            }

            // Fails here, before anything is deleted, when another process is still logging to this file (a
            // second copy of the game): otherwise its previous log would be lost and the move would fail anyway.
            using (new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
            }

            // Oldest first, each one step further back. Losing an old log to a locked file is not worth failing
            // for; the newest one moving out of the way is.
            for (int sessionsAgo = PreviousLogs; sessionsAgo >= 2; sessionsAgo--)
            {
                try
                {
                    string older = GetPreviousPath(Path, sessionsAgo);
                    string newer = GetPreviousPath(Path, sessionsAgo - 1);
                    if (File.Exists(newer))
                    {
                        File.Delete(older);
                        File.Move(newer, older);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            if (File.Exists(PreviousPath))
            {
                File.Delete(PreviousPath);
            }

            File.Move(Path, PreviousPath);
        }
    }
}
