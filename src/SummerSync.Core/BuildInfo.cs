using System;
using System.Reflection;

namespace SummerSync.Core
{
    /// <summary>
    /// Identity of the running build, read from the assembly that contains this type.
    /// Core sources are compiled into each mod assembly, so this reports the mod's own version.
    /// </summary>
    public static class BuildInfo
    {
        public const string ProductName = "SummerSync";

        private const int ShortRevisionLength = 8;

        /// <summary>Semantic version, e.g. "0.1.0".</summary>
        public static string Version { get; } = ReadVersion();

        /// <summary>Version plus short source revision when available, e.g. "0.1.0+3f2a9c1b".</summary>
        public static string InformationalVersion { get; } = ReadInformationalVersion();

        /// <summary>
        /// Shortens the revision part of an informational version ("0.1.0+&lt;40 hex chars&gt;") so it fits
        /// in an overlay or a log line. Values without a revision are returned unchanged.
        /// </summary>
        public static string ShortenRevision(string informationalVersion)
        {
            if (string.IsNullOrEmpty(informationalVersion))
            {
                return informationalVersion;
            }

            int separator = informationalVersion.IndexOf('+');
            if (separator < 0 || informationalVersion.Length - separator - 1 <= ShortRevisionLength)
            {
                return informationalVersion;
            }

            return informationalVersion.Substring(0, separator + 1 + ShortRevisionLength);
        }

        private static string ReadVersion()
        {
            Version version = typeof(BuildInfo).Assembly.GetName().Version;
            return version.Major + "." + version.Minor + "." + version.Build;
        }

        private static string ReadInformationalVersion()
        {
            var attribute = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                typeof(BuildInfo).Assembly, typeof(AssemblyInformationalVersionAttribute));
            return attribute == null ? ReadVersion() : ShortenRevision(attribute.InformationalVersion);
        }
    }
}
