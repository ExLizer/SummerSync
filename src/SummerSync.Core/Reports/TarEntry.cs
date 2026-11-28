using System;
using System.Globalization;
using System.Text;

namespace SummerSync.Core.Reports
{
    /// <summary>
    /// One file of a tar archive in the ustar format (POSIX.1-1988): a 512-byte header, the bytes, and zeros up to the next
    /// 512. An archive is its entries one after the other and then <see cref="EndOfArchive"/>. Only plain files with short
    /// ASCII names, which is all a report has.
    /// </summary>
    public static class TarEntry
    {
        public const int BlockSize = 512;

        /// <summary>The longest name a header takes without the prefix field.</summary>
        public const int MaxNameBytes = 100;

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>What ends an archive: two blocks of zeros.</summary>
        public static byte[] EndOfArchive()
        {
            return new byte[BlockSize * 2];
        }

        /// <summary>Whether a name can be a report's file: plain ASCII that prints, no folders, at most 100 bytes.</summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameBytes || name == "." || name == "..")
            {
                return false;
            }

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c < 0x21 || c > 0x7E || c == '/' || c == '\\')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The entry for a file of that name with these bytes, last changed at that time.</summary>
        public static byte[] Make(string name, byte[] data, int offset, int count, DateTime modified)
        {
            if (!IsValidName(name))
            {
                throw new ArgumentException("Not a plain file name for a tar entry: '" + name + "'.", "name");
            }

            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            int padded = (count + BlockSize - 1) / BlockSize * BlockSize;
            var entry = new byte[BlockSize + padded];
            Put(entry, 0, name, MaxNameBytes);
            Put(entry, 100, "0000644", 8);
            Put(entry, 108, "0000000", 8);
            Put(entry, 116, "0000000", 8);
            Put(entry, 124, Octal(count, 11), 12);
            long seconds = (long)Math.Max(0.0, (modified.ToUniversalTime() - Epoch).TotalSeconds);
            Put(entry, 136, Octal(seconds, 11), 12);
            entry[156] = (byte)'0';
            Put(entry, 257, "ustar", 6);
            entry[263] = (byte)'0';
            entry[264] = (byte)'0';
            Put(entry, 265, "summersync", 32);
            Put(entry, 297, "summersync", 32);

            // The checksum is the sum of the header's bytes with its own field taken as eight spaces.
            for (int i = 148; i < 156; i++)
            {
                entry[i] = (byte)' ';
            }

            int sum = 0;
            for (int i = 0; i < BlockSize; i++)
            {
                sum += entry[i];
            }

            // Six digits, a zero byte and a space.
            Put(entry, 148, Octal(sum, 6), 6);
            entry[154] = 0;
            entry[155] = (byte)' ';
            Buffer.BlockCopy(data, offset, entry, BlockSize, count);
            return entry;
        }

        private static string Octal(long value, int digits)
        {
            string text = Convert.ToString(value, 8);
            if (text.Length > digits)
            {
                throw new ArgumentOutOfRangeException("value", "Too large for a tar header: " + value.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return text.PadLeft(digits, '0');
        }

        // ASCII, the rest of the field left as zeros.
        private static void Put(byte[] header, int at, string text, int field)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            Buffer.BlockCopy(bytes, 0, header, at, Math.Min(bytes.Length, field));
        }
    }
}
