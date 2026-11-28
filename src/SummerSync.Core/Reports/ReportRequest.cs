using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SummerSync.Core.Reports
{
    /// <summary>
    /// What a bug report says about itself, as the headers of its upload carry it (the reports' contract,
    /// docs/DECISIONS.md "Reportes de bugs"): the build, the session, what the player wrote, the tags they ticked and
    /// how to reach them if they said. The server never opens the file; it reads only these. Every value is cleaned and
    /// cut here to what the contract allows.
    /// </summary>
    public sealed class ReportRequest
    {
        public const int MaxDescription = 500;

        public const int MaxContact = 64;

        public const int MaxVersion = 64;

        public const int MaxPlayers = 4;

        public const string RoleHost = "host";

        public const string RoleGuest = "guest";

        public const string RoleSolo = "solo";

        /// <summary>The tags a player can tick, in the order the window shows them.</summary>
        public static readonly string[] Tags = { "vehicles", "players", "world", "people-jobs", "items", "other" };

        private readonly List<string> tags = new List<string>();

        public ReportRequest()
        {
            Version = string.Empty;
            Edition = "public";
            Role = RoleSolo;
            Players = 1;
            Description = string.Empty;
            Contact = string.Empty;
        }

        /// <summary>The mod's version and commit, "0.2.33+903887bd".</summary>
        public string Version { get; set; }

        /// <summary>"public" or "dev".</summary>
        public string Edition { get; set; }

        /// <summary><see cref="RoleHost"/>, <see cref="RoleGuest"/> or <see cref="RoleSolo"/> (no session).</summary>
        public string Role { get; set; }

        /// <summary>How many players the session has, 1 without one.</summary>
        public int Players { get; set; }

        /// <summary>This player's number in the session, 0 for the host and without a session.</summary>
        public int Player { get; set; }

        /// <summary>The code of another player's report of this session to file this one with, or null.</summary>
        public string Group { get; set; }

        public string Description { get; set; }

        /// <summary>The player's Discord user name, if they chose to give it.</summary>
        public string Contact { get; set; }

        /// <summary>The ticked tags, among <see cref="Tags"/>.</summary>
        public IList<string> TagsTicked
        {
            get { return tags; }
        }

        /// <summary>Ticks a tag of <see cref="Tags"/>; anything else is not one.</summary>
        public bool Tick(string tag)
        {
            if (Array.IndexOf(Tags, tag) < 0 || tags.Contains(tag))
            {
                return false;
            }

            tags.Add(tag);
            return true;
        }

        /// <summary>What a player wrote about the bug: lines of plain text, at most <see cref="MaxDescription"/> characters.</summary>
        public static string CleanDescription(string text)
        {
            return Clean(text, MaxDescription, true);
        }

        /// <summary>A user name: one line of plain text, at most <see cref="MaxContact"/> characters.</summary>
        public static string CleanContact(string text)
        {
            return Clean(text, MaxContact, false);
        }

        /// <summary>A version as a header takes it: only <c>[0-9A-Za-z.+-]</c>, at most <see cref="MaxVersion"/>.</summary>
        public static string CleanVersion(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return "unknown";
            }

            var cleaned = new StringBuilder(Math.Min(version.Length, MaxVersion));
            for (int i = 0; i < version.Length && cleaned.Length < MaxVersion; i++)
            {
                char c = version[i];
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '.' || c == '+' || c == '-')
                {
                    cleaned.Append(c);
                }
            }

            return cleaned.Length == 0 ? "unknown" : cleaned.ToString();
        }

        /// <summary>
        /// Percent-encoding of the text's UTF-8 (RFC 3986: what <c>Uri.EscapeDataString</c> makes and
        /// <c>decodeURIComponent</c> reads), done here so that it does not depend on the game's Mono.
        /// </summary>
        public static string PercentEncode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            var encoded = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                bool plain = (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || b == '-' || b == '.' || b == '_' || b == '~';
                if (plain)
                {
                    encoded.Append((char)b);
                }
                else
                {
                    encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            return encoded.ToString();
        }

        /// <summary>
        /// The upload's headers, in the contract's names, with every value cleaned. Optional ones only when set. Who the
        /// player is follows the server's rules: the host and a player alone are player 0; alone means one player and no
        /// group; a guest is player 1 to 3 of two or more (a guest without a number yet goes as a player alone).
        /// </summary>
        public IList<KeyValuePair<string, string>> Headers()
        {
            string role = Role == RoleHost || Role == RoleGuest ? Role : RoleSolo;
            int player = Math.Max(0, Math.Min(MaxPlayers - 1, Player));
            int players = Math.Max(1, Math.Min(MaxPlayers, Players));
            if (role == RoleGuest && player == 0)
            {
                role = RoleSolo;
            }

            if (role == RoleSolo)
            {
                player = 0;
                players = 1;
            }
            else if (role == RoleHost)
            {
                player = 0;
            }
            else
            {
                players = Math.Max(2, players);
            }

            var headers = new List<KeyValuePair<string, string>>();
            headers.Add(Pair("X-SS-Version", CleanVersion(Version)));
            headers.Add(Pair("X-SS-Edition", Edition == "dev" ? "dev" : "public"));
            headers.Add(Pair("X-SS-Role", role));
            headers.Add(Pair("X-SS-Players", players.ToString(CultureInfo.InvariantCulture)));
            headers.Add(Pair("X-SS-Player", player.ToString(CultureInfo.InvariantCulture)));
            string group = role == RoleSolo ? null : ReportCode.Normalize(Group);
            if (group != null)
            {
                headers.Add(Pair("X-SS-Group", group));
            }

            headers.Add(Pair("X-SS-Description", PercentEncode(CleanDescription(Description))));
            if (tags.Count > 0)
            {
                headers.Add(Pair("X-SS-Tags", string.Join(",", tags.ToArray())));
            }

            string contact = CleanContact(Contact);
            if (contact.Length > 0)
            {
                headers.Add(Pair("X-SS-Contact", PercentEncode(contact)));
            }

            return headers;
        }

        private static KeyValuePair<string, string> Pair(string name, string value)
        {
            return new KeyValuePair<string, string>(name, value);
        }

        // Plain text: no control characters but, if allowed, line breaks (Windows' made into one); no spaces at the ends;
        // cut to that many characters without cutting one that takes two in half.
        private static string Clean(string text, int most, bool lines)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var cleaned = new StringBuilder(Math.Min(text.Length, most + 1));
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (lines && (i + 1 >= text.Length || text[i + 1] != '\n'))
                    {
                        cleaned.Append('\n');
                    }
                    else if (!lines)
                    {
                        cleaned.Append(' ');
                    }
                }
                else if (c == '\n' || c == '\u2028' || c == '\u2029')
                {
                    cleaned.Append(lines ? '\n' : ' ');
                }
                else if (c == '\t')
                {
                    cleaned.Append(' ');
                }
                else if (!char.IsControl(c) && c != '\uFEFF')
                {
                    cleaned.Append(c);
                }
            }

            string result = cleaned.ToString().Trim();
            if (result.Length > most)
            {
                int length = char.IsHighSurrogate(result[most - 1]) ? most - 1 : most;
                result = result.Substring(0, length).TrimEnd();
            }

            return result;
        }
    }
}
