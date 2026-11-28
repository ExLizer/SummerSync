using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SummerSync.Core.Reports
{
    /// <summary>
    /// Takes the player's own folder out of what a report carries: the Windows user's folder ("C:\Users\name", with either
    /// slash, any drive and any case) becomes <see cref="Replacement"/>, and so do a Linux home under Proton ("Z:\home\name",
    /// "/home/name") and the folder the system itself names as the user's, wherever it is. The name in such a path is
    /// often the person's own; the rest of the path is what a report needs.
    /// </summary>
    public sealed class PathScrubber
    {
        public const string Replacement = "%USERPROFILE%";

        // The common shared folders under Users are not anyone's. A name ends where Windows does not let one go on (and at
        // a quote or a bracket, which would end the path in a sentence).
        private static readonly Regex UsersFolder = new Regex(
            @"(?<![A-Za-z0-9])[A-Za-z]:[\\/]+(?:Users|Documents and Settings)[\\/]+(?!(?:Public|Default|All Users|Default User)(?:[\\/""'\s]|$))[^\\/:*?""<>|\[\]=;,+'()\r\n\t]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex HomeFolder = new Regex(
            @"(?:(?<![A-Za-z0-9])[A-Za-z]:[\\/]+|(?<![A-Za-z0-9.~])/)home[\\/]+[^\\/:*?""<>|\[\]=;,+'()\s]+",
            RegexOptions.CultureInvariant);

        private readonly List<string> exact = new List<string>();

        /// <param name="profiles">
        /// The user's folder as the system names it (the profile, the documents, the local application data): each is taken
        /// out wherever it shows, before the patterns, with either slash and in any case.
        /// </param>
        public PathScrubber(IEnumerable<string> profiles)
        {
            if (profiles == null)
            {
                return;
            }

            foreach (string profile in profiles)
            {
                string trimmed = profile == null ? string.Empty : profile.Trim().TrimEnd('\\', '/');
                if (trimmed.Length < 4)
                {
                    // "C:" or "/" would take out far too much.
                    continue;
                }

                AddExact(trimmed);
                AddExact(trimmed.Replace('\\', '/'));
                AddExact(trimmed.Replace('/', '\\'));
            }

            // The longest first, so that a folder inside another goes whole.
            exact.Sort((a, b) => b.Length.CompareTo(a.Length));
        }

        public PathScrubber()
            : this(null)
        {
        }

        /// <summary>The text with every user's folder in it replaced.</summary>
        public string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            string result = text;
            for (int i = 0; i < exact.Count; i++)
            {
                result = ReplaceIgnoringCase(result, exact[i], Replacement);
            }

            result = UsersFolder.Replace(result, Replacement);
            result = HomeFolder.Replace(result, Replacement);
            return result;
        }

        /// <summary>UTF-8 text in, UTF-8 text out (bytes that are not UTF-8 come out as U+FFFD).</summary>
        public byte[] Scrub(byte[] utf8)
        {
            if (utf8 == null || utf8.Length == 0)
            {
                return utf8 ?? new byte[0];
            }

            var encoding = new UTF8Encoding(false);
            int skip = utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF ? 3 : 0;
            string text = encoding.GetString(utf8, skip, utf8.Length - skip);
            return encoding.GetBytes(Scrub(text));
        }

        private void AddExact(string path)
        {
            for (int i = 0; i < exact.Count; i++)
            {
                if (string.Equals(exact[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            exact.Add(path);
        }

        private static string ReplaceIgnoringCase(string text, string find, string with)
        {
            int at = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return text;
            }

            var result = new StringBuilder(text.Length);
            int from = 0;
            while (at >= 0)
            {
                int after = at + find.Length;

                // The whole folder, not the start of another one's name ("C:\Users\ann" in "C:\Users\anna").
                if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_' || text[after] == '-' || text[after] == '.'))
                {
                    at = text.IndexOf(find, at + 1, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                result.Append(text, from, at - from).Append(with);
                from = after;
                at = text.IndexOf(find, from, StringComparison.OrdinalIgnoreCase);
            }

            result.Append(text, from, text.Length - from);
            return result.ToString();
        }
    }
}
