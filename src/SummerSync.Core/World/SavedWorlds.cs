using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Hashing;

namespace SummerSync.Core.World
{
    /// <summary>
    /// What a card of a saved world says (the main menu's "LOAD SAVE" and "HOST"), from what the game keeps in its save:
    /// the player's name, the day of the week and the hour, the money. docs/GAME_NOTES.md, "Las partidas del menú".
    /// </summary>
    public static class SavedWorldText
    {
        private static readonly string[] Days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        private static readonly string[] SpanishDays = { "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo" };

        /// <summary>
        /// The game's day ("WorldDay", the global "GlobalDay": 1 is Monday, 7 is Sunday), in English or in Spanish, or empty.
        /// </summary>
        public static string Weekday(int day, bool spanish)
        {
            return day >= 1 && day <= 7 ? (spanish ? SpanishDays : Days)[day - 1] : string.Empty;
        }

        /// <summary>
        /// When in the week the world was saved: "Monday 12:00" ("Lunes 12:00"). The game keeps the time only as the hour that
        /// starts its two-hour block ("WorldTime").
        /// </summary>
        public static string When(int day, int hour, bool spanish)
        {
            string weekday = Weekday(day, spanish);
            if (hour < 0 || hour > 24)
            {
                return weekday;
            }

            // Midnight is kept as 24.
            string time = (hour % 24).ToString("00", CultureInfo.InvariantCulture) + ":00";
            return weekday.Length == 0 ? time : weekday + " " + time;
        }

        /// <summary>Marks as the game shows them, with a dot between thousands: "3.000 mk".</summary>
        public static string Money(double marks)
        {
            long whole = (long)Math.Round(marks, MidpointRounding.AwayFromZero);
            string digits = Math.Abs(whole).ToString(CultureInfo.InvariantCulture);
            var text = new StringBuilder();
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0)
                {
                    text.Append('.');
                }

                text.Append(digits[i]);
            }

            return (whole < 0 ? "-" : string.Empty) + text + " mk";
        }

        /// <summary>
        /// The world's name: the player's name as the licence wrote it ("PlayerName", the first name, a space and the last
        /// name), or the two names if it is missing; empty if there is none, for the player's language to stand for it.
        /// </summary>
        public static string Title(string full, string first, string last)
        {
            string name = (full ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                name = ((first ?? string.Empty).Trim() + " " + (last ?? string.Empty).Trim()).Trim();
            }

            return name;
        }
    }

    /// <summary>A copy of a saved world: its name (when the world was saved, so that names sort as time does) and its contents' hash.</summary>
    public sealed class WorldCopyInfo
    {
        public WorldCopyInfo(string name, ulong hash)
        {
            Name = name ?? string.Empty;
            Hash = hash;
        }

        public string Name { get; private set; }

        public ulong Hash { get; private set; }
    }

    /// <summary>
    /// The copies kept of a saved world (the last three). A copy is made when a world is loaded, of the
    /// save it loads, unless a copy of the same contents is kept already; once there are more than three, the oldest goes.
    /// </summary>
    public static class WorldCopies
    {
        public const int Kept = 3;

        /// <summary>
        /// One number for a world's files, in the order of <see cref="WorldFiles.Names"/> (a missing one is null). 0 when
        /// there is no main file: nothing worth a copy.
        /// </summary>
        public static ulong Hash(IList<byte[]> files)
        {
            if (files == null || files.Count <= WorldFiles.Main || files[WorldFiles.Main] == null)
            {
                return 0UL;
            }

            ulong hash = Fnv1a.OffsetBasis;
            var head = new byte[13];
            for (int i = 0; i < files.Count; i++)
            {
                byte[] content = files[i];
                if (content == null)
                {
                    continue;
                }

                ulong one = Fnv1a.Hash64(content);
                head[0] = (byte)i;
                for (int b = 0; b < 8; b++)
                {
                    head[1 + b] = (byte)(one >> (8 * b));
                }

                int length = content.Length;
                for (int b = 0; b < 4; b++)
                {
                    head[9 + b] = (byte)(length >> (8 * b));
                }

                hash = Fnv1a.Hash64(head, 0, head.Length, hash);
            }

            return hash == 0UL ? 1UL : hash;
        }

        /// <summary>Whether the world as it is now is worth a copy: it has a save, and no copy kept has the same contents.</summary>
        public static bool Worth(IList<WorldCopyInfo> kept, ulong current)
        {
            if (current == 0UL)
            {
                return false;
            }

            for (int i = 0; kept != null && i < kept.Count; i++)
            {
                if (kept[i].Hash == current)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A name for a new copy: when the world was saved ("20261016-183000"), with "-2", "-3"… if that is taken.</summary>
        public static string NameFor(DateTime saved, IList<WorldCopyInfo> kept)
        {
            string name = saved.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string candidate = name;
            for (int n = 2; Taken(kept, candidate); n++)
            {
                candidate = name + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            return candidate;
        }

        /// <summary>The copies that go so that only the newest <paramref name="keep"/> stay, oldest first.</summary>
        public static List<string> ToDrop(IList<WorldCopyInfo> kept, int keep)
        {
            var names = new List<string>();
            for (int i = 0; kept != null && i < kept.Count; i++)
            {
                names.Add(kept[i].Name);
            }

            names.Sort(Older);
            int over = names.Count - Math.Max(0, keep);
            return over > 0 ? names.GetRange(0, over) : new List<string>();
        }

        /// <summary>Oldest first: by the time in the name, and by the number after it for the same time.</summary>
        public static int Older(string a, string b)
        {
            string timeA = TimeOf(a);
            string timeB = TimeOf(b);
            int byTime = string.CompareOrdinal(timeA, timeB);
            return byTime != 0 ? byTime : NumberOf(a).CompareTo(NumberOf(b));
        }

        private static bool Taken(IList<WorldCopyInfo> kept, string name)
        {
            for (int i = 0; kept != null && i < kept.Count; i++)
            {
                if (string.Equals(kept[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string TimeOf(string name)
        {
            return name.Length > 15 ? name.Substring(0, 15) : name;
        }

        private static int NumberOf(string name)
        {
            int n;
            return name.Length > 16 && int.TryParse(name.Substring(16), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 1;
        }
    }
}
