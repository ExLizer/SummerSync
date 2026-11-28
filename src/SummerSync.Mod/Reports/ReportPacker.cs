using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using SummerSync.Core.Json;
using SummerSync.Core.Logging;
using SummerSync.Core.Reports;

namespace SummerSync.Unity.Reports
{
    /// <summary>One file a report takes from the disk (or, for the snapshot, from memory).</summary>
    internal sealed class ReportSource
    {
        public string Name;

        /// <summary>Where it is read from; null for one given as <see cref="Bytes"/>.</summary>
        public string Path;

        public byte[] Bytes;

        public bool Required;
    }

    /// <summary>
    /// Reads a report's files, takes the user's folder out of them and packs them (<see cref="ReportPackage"/>) on a thread of
    /// its own: a few megabytes of logs take a moment to compress, and the game must not stop for it. Nothing of Unity is
    /// touched on that thread; what the report says about the game was read before, on the game's.
    /// </summary>
    internal sealed class ReportPacker
    {
        // The most read of one file: its end. More is cut by the package anyway, and the game's Mono has little memory to spare.
        private const int MostRead = 16 * 1024 * 1024;

        private readonly List<ReportSource> sources;
        private readonly Dictionary<string, object> facts;
        private readonly PathScrubber scrubber;
        private readonly object gate = new object();
        private byte[] bytes;
        private string error;
        private bool done;
        private string summary;

        /// <param name="facts">What report.json says besides the files, in its order (strings, numbers, booleans, string lists).</param>
        public ReportPacker(List<ReportSource> sources, Dictionary<string, object> facts, IEnumerable<string> profiles)
        {
            this.sources = sources;
            this.facts = facts;
            scrubber = new PathScrubber(profiles);
        }

        public bool Done
        {
            get { lock (gate) { return done; } }
        }

        /// <summary>The .tar.gz, when done and nothing went wrong.</summary>
        public byte[] Bytes
        {
            get { lock (gate) { return bytes; } }
        }

        /// <summary>Why it could not be packed, or null.</summary>
        public string Error
        {
            get { lock (gate) { return error; } }
        }

        /// <summary>What went in, for the log: "report.json, snapshot.txt (12 kB), …; left out: …".</summary>
        public string Summary
        {
            get { lock (gate) { return summary; } }
        }

        public void Start()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "SummerSync report" };
            thread.Start();
        }

        private void Run()
        {
            byte[] packed = null;
            string failure = null;
            string said = null;
            try
            {
                var files = new List<ReportFile>();
                var notes = new Dictionary<string, string>();
                var missing = new List<string>();
                for (int i = 0; i < sources.Count; i++)
                {
                    ReportSource source = sources[i];
                    long size = 0;
                    byte[] data = source.Bytes ?? Read(source.Path, out size);
                    if (data == null)
                    {
                        missing.Add(source.Name);
                        continue;
                    }

                    if (source.Bytes == null && size > data.Length)
                    {
                        notes[source.Name] = size.ToString(CultureInfo.InvariantCulture);
                    }

                    DateTime modified = source.Path != null && File.Exists(source.Path) ? File.GetLastWriteTimeUtc(source.Path) : DateTime.UtcNow;
                    files.Add(new ReportFile(source.Name, scrubber.Scrub(data), source.Required, modified));
                }

                var package = new ReportPackage();
                ReportContents contents;
                packed = package.Build(files, packedFiles => Describe(packedFiles, notes, missing), out contents);
                said = Say(contents, packed.Length, missing);
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name + ": " + exception.Message;
            }

            lock (gate)
            {
                bytes = packed;
                error = failure;
                summary = said;
                done = true;
            }
        }

        // The end of a file, read while the game may be writing to it (its log is open for writing all the time).
        private static byte[] Read(string path, out long size)
        {
            size = 0;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                size = stream.Length;
                long start = Math.Max(0, size - MostRead);
                stream.Position = start;
                var data = new byte[size - start];
                int read = 0;
                while (read < data.Length)
                {
                    int got = stream.Read(data, read, data.Length - read);
                    if (got <= 0)
                    {
                        break;
                    }

                    read += got;
                }

                if (read < data.Length)
                {
                    Array.Resize(ref data, read);
                }

                if (start > 0)
                {
                    byte[] note = Encoding.UTF8.GetBytes("[SummerSync: the first " + start + " bytes of this file were left out so that the report fits]\n");
                    var kept = new byte[note.Length + data.Length];
                    Buffer.BlockCopy(note, 0, kept, 0, note.Length);
                    Buffer.BlockCopy(data, 0, kept, note.Length, data.Length);
                    return kept;
                }

                return data;
            }
        }

        // report.json: the facts, then each file that went in and each that did not.
        private byte[] Describe(ReportContents contents, Dictionary<string, string> notes, List<string> missing)
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            var json = new JsonWriter(text);
            json.BeginObject();
            foreach (KeyValuePair<string, object> fact in facts)
            {
                json.Name(fact.Key);
                Value(json, fact.Value);
            }

            json.Name("limit").Value(contents.Limit);
            json.Name("files").BeginArray();
            for (int i = 0; i < contents.Included.Count; i++)
            {
                PackedFile file = contents.Included[i];
                string original;
                json.BeginObject().Property("name", file.Name).Property("bytes", file.Bytes).Property("compressed", file.Compressed);
                if (notes.TryGetValue(file.Name, out original))
                {
                    json.Property("bytesOnDisk", original).Property("cut", "its start, to its last " + MostRead + " bytes");
                }
                else if (file.CutBytes > 0)
                {
                    json.Property("cut", "its first " + file.CutBytes + " bytes, so that the report fits");
                }

                json.EndObject();
            }

            json.EndArray();
            json.Name("leftOut").BeginArray();
            for (int i = 0; i < contents.LeftOut.Count; i++)
            {
                LeftOutFile file = contents.LeftOut[i];
                json.BeginObject().Property("name", file.Name).Property("bytes", file.Bytes).Property("why", file.Why).EndObject();
            }

            for (int i = 0; i < missing.Count; i++)
            {
                json.BeginObject().Property("name", missing[i]).Property("why", "not there").EndObject();
            }

            json.EndArray();
            json.EndObject();
            return new UTF8Encoding(false).GetBytes(text.ToString());
        }

        private static void Value(JsonWriter json, object value)
        {
            if (value == null)
            {
                json.Null();
            }
            else if (value is string)
            {
                json.Value((string)value);
            }
            else if (value is bool)
            {
                json.Value((bool)value);
            }
            else if (value is int)
            {
                json.Value((int)value);
            }
            else if (value is long)
            {
                json.Value((long)value);
            }
            else if (value is float)
            {
                json.Value((float)value);
            }
            else if (value is double)
            {
                json.Value((double)value);
            }
            else if (value is IList<string>)
            {
                json.BeginArray();
                foreach (string item in (IList<string>)value)
                {
                    json.Value(item);
                }

                json.EndArray();
            }
            else
            {
                json.Value(value.ToString());
            }
        }

        private static string Say(ReportContents contents, int total, List<string> missing)
        {
            var text = new StringBuilder(300);
            text.Append(total / 1024).Append(" kB: report.json");
            for (int i = 0; i < contents.Included.Count; i++)
            {
                PackedFile file = contents.Included[i];
                text.Append(", ").Append(file.Name).Append(" (").Append(file.Bytes / 1024).Append(" kB").Append(file.CutBytes > 0 ? ", cut" : string.Empty).Append(')');
            }

            for (int i = 0; i < contents.LeftOut.Count; i++)
            {
                text.Append(i == 0 ? "; left out: " : ", ").Append(contents.LeftOut[i].Name).Append(" (").Append(contents.LeftOut[i].Why).Append(')');
            }

            for (int i = 0; i < missing.Count; i++)
            {
                text.Append(i == 0 && contents.LeftOut.Count == 0 ? "; not there: " : ", not there: ").Append(missing[i]);
            }

            return text.ToString();
        }
    }
}
