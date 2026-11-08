using System;
using System.Globalization;
using System.Text;

namespace SummerSync.Core
{
    /// <summary>
    /// Whether the latest published build is this one, and whether a download is it. A release's tag is "v" and the
    /// zip's version: "v0.1.0-1ac84f76" for a build whose informational version is "0.1.0+1ac84f76" (tools/Publish.ps1).
    /// The latest one is the one to have: any other build, newer or not, is offered the update. Each release carries
    /// version.txt: its tag on the first line, the SHA-256 of its SummerSync.dll on the second.
    /// </summary>
    public static class UpdateCheck
    {
        /// <summary>The tag this build would be published under.</summary>
        public static string TagOf(string informationalVersion)
        {
            return string.IsNullOrEmpty(informationalVersion) ? string.Empty : "v" + informationalVersion.Replace('+', '-');
        }

        /// <summary>Whether <paramref name="latestTag"/> is another build than <paramref name="informationalVersion"/>.</summary>
        public static bool Differs(string latestTag, string informationalVersion)
        {
            return !string.IsNullOrEmpty(latestTag) && !string.Equals(latestTag.Trim(), TagOf(informationalVersion), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A release's version.txt: its tag and the mod's SHA-256 (64 hexadecimal digits). False if it is not one.</summary>
        public static bool ReadVersion(string text, out string tag, out string sha256)
        {
            tag = null;
            sha256 = null;
            string[] lines = (text ?? string.Empty).TrimStart('﻿').Replace("\r", string.Empty).Split('\n');
            if (lines.Length < 2)
            {
                return false;
            }

            string first = lines[0].Trim();
            string second = lines[1].Trim().ToUpperInvariant();
            if (!first.StartsWith("v", StringComparison.Ordinal) || first.Length < 2 || first.IndexOf(' ') >= 0 || second.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < second.Length; i++)
            {
                if (!Uri.IsHexDigit(second[i]))
                {
                    return false;
                }
            }

            tag = first;
            sha256 = second;
            return true;
        }

        /// <summary>A hash as version.txt writes it: upper-case hexadecimal.</summary>
        public static string Hex(byte[] hash)
        {
            var text = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                text.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }
    }
}
