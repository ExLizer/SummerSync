namespace SummerSync.Core.World
{
    /// <summary>Which saved world the game reads and writes.</summary>
    public enum SaveFolder
    {
        /// <summary>The player's own, where the game keeps it.</summary>
        Own,

        /// <summary>A copy of the host's world, in the mod's folder.</summary>
        Host,
    }

    /// <summary>What the main menu offers.</summary>
    public enum MenuOffer
    {
        /// <summary>The game's own buttons, as the game decides them: not connected to anyone as a guest.</summary>
        Game,

        /// <summary>Neither "Continue" nor "New game": a guest whose host's world is not here (yet).</summary>
        Wait,

        /// <summary>"Continue" into the host's world, and no "New game".</summary>
        Host,
    }

    /// <summary>What <see cref="GuestWorldPolicy.Decide"/> says to do.</summary>
    public struct GuestWorldDecision
    {
        public SaveFolder Folder;

        /// <summary>With <see cref="SaveFolder.Host"/>: which world, by its <see cref="WorldManifest.Id"/>.</summary>
        public ulong World;

        /// <summary>The host's world has to be copied into the folder the game will read, fresh, before anything else.</summary>
        public bool Prepare;

        public MenuOffer Menu;
    }

    /// <summary>
    /// Decides which saved world the game uses, so that a guest plays in the host's without its own being touched.
    ///
    /// The rules, each for a case that can happen:
    /// - The folder changes only in the main menu. In the world (and while it loads) the game reads and writes
    ///   whatever it loaded, even if the session ends: a guest who loses the host and saves must not save the
    ///   host's world over their own.
    /// - A guest uses the host's world when it is here, whole. Until then the menu offers nothing: "Continue" would
    ///   load their own world, and "New game" would start another one, neither of which is the host's.
    /// - A world being fetched does not take away one that is ready: the host saved again, and the guest keeps the
    ///   one they have until the new one is here. Unless that copy was already played in: then it is not the
    ///   host's any more, and the guest waits for the new one. Or unless the host chose another of its worlds (the
    ///   main menu's "HOST"): the one here is not a save of the one being fetched, and the guest waits for it.
    /// - Every time a guest is about to go into the host's world, it is copied again: what the game wrote into the
    ///   copy the last time (a guest's own save, the game deleting it when the player died) does not count.
    /// - Anyone who is not a connected guest uses their own world, and the menu is the game's.
    /// </summary>
    public static class GuestWorldPolicy
    {
        /// <param name="inMenu">In the main menu, where choosing is safe.</param>
        /// <param name="guest">Connected to a session as a client.</param>
        /// <param name="ready">The host's world that is here, whole; 0 for none.</param>
        /// <param name="folder">The folder the game uses now.</param>
        /// <param name="world">With <see cref="SaveFolder.Host"/>: which world is in it.</param>
        /// <param name="played">The game has been in the world since that copy was made.</param>
        public static GuestWorldDecision Decide(bool inMenu, bool guest, ulong ready, SaveFolder folder, ulong world, bool played)
        {
            return Decide(inMenu, guest, ready, folder, world, played, true);
        }

        /// <param name="sameWorld">The world being fetched (if any) is a newer save of the one in the folder, not another of the host's.</param>
        public static GuestWorldDecision Decide(bool inMenu, bool guest, ulong ready, SaveFolder folder, ulong world, bool played, bool sameWorld)
        {
            var decision = new GuestWorldDecision { Folder = folder, World = folder == SaveFolder.Host ? world : 0UL, Menu = MenuOffer.Game };
            if (!inMenu)
            {
                return decision;
            }

            if (!guest)
            {
                decision.Folder = SaveFolder.Own;
                decision.World = 0UL;
                return decision;
            }

            ulong use = ready != 0UL ? ready : (folder == SaveFolder.Host && !played && sameWorld ? world : 0UL);
            if (use == 0UL)
            {
                decision.Folder = SaveFolder.Own;
                decision.World = 0UL;
                decision.Menu = MenuOffer.Wait;
                return decision;
            }

            decision.Folder = SaveFolder.Host;
            decision.World = use;
            decision.Menu = MenuOffer.Host;
            decision.Prepare = folder != SaveFolder.Host || world != use || played;
            return decision;
        }
    }
}
