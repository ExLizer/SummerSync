using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SummerSync.Core.Reports
{
    /// <summary>One file that a report would carry.</summary>
    public sealed class ReportFile
    {
        /// <param name="required">
        /// Whether the report goes only with it (the log of this run, the snapshot, the settings); the others (the logs of
        /// the runs before, newest first) are left out, from the first that does not fit on, when the report would be too big.
        /// </param>
        public ReportFile(string name, byte[] data, bool required, DateTime modified)
        {
            if (!TarEntry.IsValidName(name))
            {
                throw new ArgumentException("Not a plain file name: '" + name + "'.", "name");
            }

            Name = name;
            Data = data ?? new byte[0];
            Required = required;
            Modified = modified;
        }

        public string Name { get; private set; }

        public byte[] Data { get; private set; }

        public bool Required { get; private set; }

        public DateTime Modified { get; private set; }
    }

    /// <summary>A file as it went into a report.</summary>
    public sealed class PackedFile
    {
        public string Name;

        /// <summary>Its size as it was.</summary>
        public long Bytes;

        /// <summary>How many of its first bytes were left out so that it fits (0 for none).</summary>
        public long CutBytes;

        /// <summary>What it takes in the report, compressed with its header.</summary>
        public long Compressed;
    }

    /// <summary>A file the report would have carried and does not.</summary>
    public sealed class LeftOutFile
    {
        public string Name;

        public long Bytes;

        public string Why;
    }

    /// <summary>What went into a report and what did not, for its report.json.</summary>
    public sealed class ReportContents
    {
        public readonly List<PackedFile> Included = new List<PackedFile>();

        public readonly List<LeftOutFile> LeftOut = new List<LeftOutFile>();

        /// <summary>The most the whole report may take.</summary>
        public long Limit;
    }

    /// <summary>
    /// Packs a report's files as a gzip'd tar, as the reports' server takes it: at most <see cref="Limit"/> bytes in all.
    /// Each file is compressed on its own (<see cref="Deflater"/>), so what it takes is known before it is put in: the files
    /// go in their order while they fit; a required file that does not fit loses its oldest lines until it does, and the
    /// optional ones, from the first that does not fit on, are left out. A file over <see cref="MaxFileBytes"/> keeps only
    /// its last lines in any case. report.json, written last with what went in and what did not, is the archive's first
    /// file.
    /// </summary>
    public sealed class ReportPackage
    {
        /// <summary>The most the reports' server takes: 8 MiB.</summary>
        public const long ServerLimit = 8L * 1024 * 1024;

        public const string ReportName = "report.json";

        // gzip's header (10) and trailer (8), and the final empty block (2).
        private const int GzipOverhead = 20;

        private readonly Deflater deflater;

        public ReportPackage()
        {
            deflater = new Deflater();
            Limit = ServerLimit;
            MaxFileBytes = 16 * 1024 * 1024;
            ReportReserve = 64 * 1024;
        }

        /// <summary>The most the archive may take, compressed.</summary>
        public long Limit { get; set; }

        /// <summary>The most of any one file that goes in, before compression: its last bytes.</summary>
        public int MaxFileBytes { get; set; }

        /// <summary>What is kept free for report.json.</summary>
        public int ReportReserve { get; set; }

        /// <summary>
        /// The archive of these files, in their order, after a report.json made by <paramref name="describe"/> from what went
        /// in and what did not (the description of the report is the caller's).
        /// </summary>
        public byte[] Build(IList<ReportFile> files, Func<ReportContents, byte[]> describe, out ReportContents contents)
        {
            if (files == null)
            {
                throw new ArgumentNullException("files");
            }

            if (describe == null)
            {
                throw new ArgumentNullException("describe");
            }

            contents = new ReportContents { Limit = Limit };
            var chunks = new List<Chunk>();
            Chunk end = Pack(TarEntry.EndOfArchive());
            long used = GzipOverhead + end.Compressed.Length;
            long budget = Limit - ReportReserve;
            bool full = false;
            for (int i = 0; i < files.Count; i++)
            {
                ReportFile file = files[i];
                if (file.Name == ReportName)
                {
                    throw new ArgumentException("report.json is written here, not given.", "files");
                }

                if (full && !file.Required)
                {
                    LeaveOut(contents, file, "the report would be over its size with it");
                    continue;
                }

                long cut;
                byte[] data = KeepTail(file.Data, MaxFileBytes, out cut);
                Chunk chunk = Pack(Entry(file.Name, data, file.Modified));
                if (used + chunk.Compressed.Length > budget)
                {
                    if (!file.Required)
                    {
                        full = true;
                        LeaveOut(contents, file, "the report would be over its size with it");
                        continue;
                    }

                    // Cut further, from what the compression made of it, until it fits.
                    for (int attempt = 0; attempt < 8 && used + chunk.Compressed.Length > budget; attempt++)
                    {
                        long room = budget - used - TarEntry.BlockSize;
                        if (room < 1024)
                        {
                            break;
                        }

                        double ratio = (double)chunk.Compressed.Length / Math.Max(1, data.Length);
                        int keep = (int)Math.Min(int.MaxValue, Math.Max(256.0, room / Math.Max(ratio, 1e-6) * 0.9));
                        data = KeepTail(file.Data, Math.Min(keep, data.Length - 1), out cut);
                        chunk = Pack(Entry(file.Name, data, file.Modified));
                    }

                    if (used + chunk.Compressed.Length > budget)
                    {
                        LeaveOut(contents, file, "too big for the report even with only its last lines");
                        continue;
                    }
                }

                used += chunk.Compressed.Length;
                chunks.Add(chunk);
                contents.Included.Add(new PackedFile { Name = file.Name, Bytes = file.Data.Length, CutBytes = cut, Compressed = chunk.Compressed.Length });
            }

            byte[] report = describe(contents) ?? new byte[0];
            Chunk first = Pack(Entry(ReportName, report, DateTime.UtcNow));
            chunks.Insert(0, first);
            chunks.Add(end);

            using (var output = new MemoryStream((int)Math.Min(int.MaxValue, used + first.Compressed.Length + 64)))
            {
                // gzip: deflate, no name or extra fields, no time, the system not said.
                output.Write(new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0, 0, 0, 0, 0x00, 0xFF }, 0, 10);
                uint crc = 0;
                long total = 0;
                for (int i = 0; i < chunks.Count; i++)
                {
                    output.Write(chunks[i].Compressed, 0, chunks[i].Compressed.Length);
                    crc = Crc32.Combine(crc, chunks[i].Crc, chunks[i].RawLength);
                    total += chunks[i].RawLength;
                }

                output.Write(Deflater.FinalBlock, 0, Deflater.FinalBlock.Length);
                WriteLittleEndian(output, crc);
                WriteLittleEndian(output, (uint)(total & 0xFFFFFFFF));
                return output.ToArray();
            }
        }

        /// <summary>
        /// The last <paramref name="most"/> bytes of a text, from the start of a line, after a line that says how much was
        /// left out; the text itself when it is not longer.
        /// </summary>
        public static byte[] KeepTail(byte[] data, int most, out long cut)
        {
            cut = 0;
            if (data == null || data.Length <= most)
            {
                return data ?? new byte[0];
            }

            int start = data.Length - Math.Max(0, most);

            // From the next line on, unless that is far: a log's lines are short.
            for (int i = start; i < data.Length && i < start + 4096; i++)
            {
                if (data[i] == (byte)'\n')
                {
                    start = i + 1;
                    break;
                }
            }

            cut = start;
            byte[] note = Encoding.UTF8.GetBytes("[SummerSync: the first " + start + " bytes of this file were left out so that the report fits]\n");
            var kept = new byte[note.Length + (data.Length - start)];
            Buffer.BlockCopy(note, 0, kept, 0, note.Length);
            Buffer.BlockCopy(data, start, kept, note.Length, data.Length - start);
            return kept;
        }

        private static byte[] Entry(string name, byte[] data, DateTime modified)
        {
            return TarEntry.Make(name, data, 0, data.Length, modified);
        }

        private static void LeaveOut(ReportContents contents, ReportFile file, string why)
        {
            contents.LeftOut.Add(new LeftOutFile { Name = file.Name, Bytes = file.Data.Length, Why = why });
        }

        private static void WriteLittleEndian(Stream output, uint value)
        {
            output.WriteByte((byte)value);
            output.WriteByte((byte)(value >> 8));
            output.WriteByte((byte)(value >> 16));
            output.WriteByte((byte)(value >> 24));
        }

        private Chunk Pack(byte[] raw)
        {
            return new Chunk { Compressed = deflater.Compress(raw, 0, raw.Length), Crc = Crc32.Of(raw, 0, raw.Length), RawLength = raw.Length };
        }

        private struct Chunk
        {
            public byte[] Compressed;
            public uint Crc;
            public long RawLength;
        }
    }
}
