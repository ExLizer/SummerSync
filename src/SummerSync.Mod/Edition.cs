namespace SummerSync.Unity
{
    /// <summary>
    /// Which build this is. The developers' build (the default of build.cmd) has what testing needs: every player may use
    /// the test aids of the console, the network test, simulator and log settings are in the mod's settings, the overlay
    /// is on, and the main menu installs a newer build by itself. The public one (publicar.cmd, the property
    /// SummerSyncEdition=Public) leaves the test aids to the host, shows players only their own settings, starts with the
    /// overlay off, and only says where to get a newer build.
    /// </summary>
    internal static class Edition
    {
        /// <summary>Whether this is the developers' build.</summary>
        public static readonly bool Developer = IsDeveloper();

        /// <summary>Where players get the mod, as the menu shows it.</summary>
        public const string Site = "mysummersync.com";

        /// <summary>The page of the site to get the mod from, opened from the menu's notice of a newer build.</summary>
        public const string DownloadPage = "https://mysummersync.com/#descargar";

        /// <summary>
        /// The latest published build's version.txt (its tag and the SHA-256 of its SummerSync.dll), as the site serves it
        /// from the latest release: the builds' repository is private (2026-10-17). Not found while the site's download is
        /// off.
        /// </summary>
        public const string VersionFile = "https://mysummersync.com/version.txt";

        /// <summary>"dev" in the developers' build, nothing in the public one: for the overlay and the log.</summary>
        public static string Mark
        {
            get { return Developer ? " (dev)" : string.Empty; }
        }

        private static bool IsDeveloper()
        {
#if DEVELOPER
            return true;
#else
            return false;
#endif
        }
    }
}
