using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Hashing;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The host's saved world for everyone, so that nobody has to pass save files around.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "La partida guardada"): every save file is read and
    /// written by Easy Save 2, which builds each path from one folder; when <c>ES2GlobalSettings.defaultPCDataPath</c>
    /// is set, that folder is it. Easy Save keeps where each value is inside a file it has read
    /// (<c>ES2Cache</c>, by full path): a file changed behind its back has to be forgotten there. Saving is always
    /// "save and quit to menu"; the menu shows "Continue" if the main file has the player's name and money
    /// ("Interface/Buttons", machine "Check Save"), and "New game" deletes the world files first.
    ///
    /// On the host: the world files in the folder the game uses are read and offered (<see cref="WorldShare"/>),
    /// again whenever they change while in the menu, which is where saving ends.
    ///
    /// On a guest: what arrives is kept in a cache in the mod's folder, by hash. In the main menu, once the host's
    /// world is here, it is copied into a folder of the mod's with the guest's own settings, and the game is
    /// pointed at that folder; the guest's own save is never touched. Until it is here, the menu offers neither
    /// "Continue" nor "New game". The folder never changes outside the menu (<see cref="GuestWorldPolicy"/>).
    ///
    /// "Own" is the world this player chose (<see cref="SaveWorlds"/>): the game's own save, or one of the mod's from the
    /// menu's "LOAD SAVE" and "HOST". When the menu loads and this game is not hosting, it is the game's own save again.
    /// </summary>
    internal static class HostWorld
    {
        private const string MenuLevel = "MainMenu";
        private const string OptionsFile = "options.txt";
        private const string RecheckEvent = "SUMMERSYNC CHECK SAVE";
        private const string ButtonsPath = "Interface/Buttons";
        private const double ScanSeconds = 2.0;
        private const double SettleSeconds = 1.5;
        private const double RetrySeconds = 5.0;
        private const double NoticeSeconds = 14.0;

        private static readonly WorldCache Cache = new WorldCache();
        private static WorldShare share;
        private static Logger log;
        private static bool started;
        private static bool inMenu;
        private static bool levelKnown;
        private static string level = string.Empty;

        // Which world the game reads and writes.
        private static SaveFolder folder = SaveFolder.Own;
        private static ulong folderWorld;
        private static ulong folderKey;
        private static bool played;
        private static string originalDataPath;
        private static string ownFolder;
        private static string playFolder;
        private static string playPath;
        private static byte[] optionsCopiedIn;
        private static string problem;
        private static double retryAt;
        private static bool guardWarned;
        private static bool menuArrived;
        private static bool wasHosting;

        // The menu's buttons, as last set.
        private static MenuOffer menuOffer = MenuOffer.Game;
        private static bool menuTouched;
        private static GameObject continueButton;
        private static GameObject newGameButton;
        private static GameObject buttons;

        // Host: what is offered, and the files' sizes and times when it was read.
        private static WorldShare offeredTo;
        private static string offeredStamp;
        private static string seenStamp;
        private static double seenSince;
        private static double nextScan;

        // Guest: who the host is, for what is shown (null while not known; empty if the session does not say).
        private static string hostName;
        private static string guestTitle;
        private static bool wasGuest;
        private static bool wasConnected;

        // What is shown on the screen, built when it changes.
        private static string banner;
        private static double bannerAt;
        private static string notice;
        private static string noticeSpanish;
        private static double noticeUntil;
        private static double clock;
        private static GUIStyle style;
        private static string measured;
        private static GUIContent content;
        private static float height;

        /// <summary>Whether the main menu is the scene that is loaded.</summary>
        public static bool InMenu
        {
            get { return inMenu && levelKnown; }
        }

        /// <summary>Whether the world loaded is a copy of the host's (this game is a guest in it).</summary>
        public static bool PlaysHostWorld
        {
            get { return folder == SaveFolder.Host; }
        }

        /// <summary>Whether it broke: then a guest plays in their own world until the game is closed.</summary>
        public static bool Failed { get; private set; }

        /// <summary>
        /// Switches it off for the rest of the process after an error, leaving the game as safe as it can: its own
        /// world, if that can still be chosen (in the menu), and its own menu.
        /// </summary>
        public static void Disable(Exception exception)
        {
            Failed = true;
            if (log != null)
            {
                log.Error(LogCategory.Net, "Sharing the host's saved world failed; a guest plays in their own world until the game is closed. " + exception);
            }

            try
            {
                if (inMenu && folder == SaveFolder.Host)
                {
                    UseOwn();
                }

                // The menu's own "New game" comes back below: it must not find a world of the mod's to empty. Not while the
                // licence of a new world is up: its BEGIN would empty the game's own save instead.
                if (inMenu && !SaveWorlds.Pinned)
                {
                    SaveWorlds.UseGame();
                }

                if (inMenu && menuTouched && FindButtons())
                {
                    SetActive(newGameButton, true);
                    Recheck();
                }
            }
            catch (Exception)
            {
                // Whatever broke may break the clean-up too.
            }

            banner = null;
        }

        /// <summary>The scene that loaded: the folder is chosen only in the main menu.</summary>
        public static void OnLevelLoaded(string loaded)
        {
            level = loaded ?? string.Empty;
            levelKnown = true;
            bool menu = level == MenuLevel;
            if (!menu && inMenu)
            {
                if (folder == SaveFolder.Host)
                {
                    played = true;
                }

                if (log != null)
                {
                    log.Info(
                        LogCategory.Net,
                        "Loading '" + level + "' with " + (folder == SaveFolder.Host
                            ? "a copy of the host's world " + SharedFile.Name(folderWorld) + " (" + playFolder + ")."
                            : "this player's own world."));
                }

                ShowNoticeForLevel();
            }

            if (!menu && inMenu && folder == SaveFolder.Own)
            {
                // What is loaded is what this player's world was: a copy of it, unless one of the same is kept.
                SaveWorlds.CopyCurrent();
            }

            inMenu = menu;
            if (inMenu)
            {
                menuArrived = true;
                continueButton = null;
                newGameButton = null;
                buttons = null;
                menuTouched = false;
                menuOffer = MenuOffer.Game;
            }
        }

        /// <summary>Every frame, in every scene.</summary>
        public static void Update(NetworkManager manager, Logger logger, double now)
        {
            log = logger;
            clock = now;
            if (!started)
            {
                Start();
            }

            WorldShare current = manager.SavedWorld;
            if (current != share)
            {
                share = current;
                offeredTo = null;
                if (share != null)
                {
                    share.SetStore(Cache);
                }
            }

            // A guest without a folder of the mod's cannot keep the host's world: it plays in its own, and is told.
            NetSession session = manager.Session;
            bool connected = session != null && session.State == SessionState.Connected && share != null;
            bool guest = connected && session.Role == SessionRole.Client && playFolder != null;
            if (guest)
            {
                hostName = HostName(session);
            }

            if (connected && session.Role == SessionRole.Host)
            {
                Offer(now);
            }

            // A world of the mod's stays chosen while hosting it; otherwise the menu is the game's own save's.
            bool hosting = manager.Mode == NetworkMode.Host;
            if (inMenu && levelKnown && (menuArrived || (wasHosting && !hosting)))
            {
                menuArrived = false;
                if (!hosting && SaveWorlds.UsesModWorld)
                {
                    SaveWorlds.UseGame();
                }
                else
                {
                    // Saved while it was played: its name may be new (a new world's licence).
                    SaveWorlds.RefreshTitle();
                }
            }

            wasHosting = hosting;
            Guard();
            ulong ready = guest && share.IsReady ? share.Manifest.Id : 0UL;

            // The host may have chosen another of its worlds since the copy here was made: then the copy is not a save of it.
            WorldManifest heard = share == null ? null : share.Manifest;
            bool sameWorld = heard == null || heard.Key == 0UL || folderKey == 0UL || heard.Key == folderKey;
            GuestWorldDecision decision = GuestWorldPolicy.Decide(inMenu && levelKnown, guest, ready, folder, folderWorld, played, sameWorld);
            if (decision.Folder == SaveFolder.Own && folder == SaveFolder.Host)
            {
                UseOwn();
            }
            else if (decision.Folder == SaveFolder.Host && decision.Prepare && now >= retryAt)
            {
                if (UseHost(decision.World))
                {
                    problem = null;
                }
                else
                {
                    retryAt = now + RetrySeconds;
                }
            }

            if (inMenu)
            {
                // "Continue" only into a fresh copy: if copying failed, the game would load something else.
                MenuOffer offer = decision.Menu == MenuOffer.Host && (folder != SaveFolder.Host || played) ? MenuOffer.Wait : decision.Menu;
                ApplyMenu(offer);
                if (now >= bannerAt)
                {
                    bannerAt = now + 0.25;
                    banner = connected && session.Role == SessionRole.Client && playFolder == null
                        ? Texts.Pick(
                            "Connected, but SummerSync has no folder of its own to keep the host's world in: you will play in your own save.",
                            "Conectado, pero SummerSync no tiene una carpeta propia donde guardar el mundo del host: vas a jugar en tu partida.")
                        : MenuBanner(guest, connected && session.Role == SessionRole.Host, offer);
                }
            }
            else
            {
                banner = null;
                if (guest != wasGuest || connected != wasConnected)
                {
                    ShowNoticeForLevel(guest, connected);
                }
            }

            wasGuest = guest;
            wasConnected = connected;
        }

        /// <summary>A message in the world for a while: something the player did, or someone else did for everyone.</summary>
        public static void Notify(string english, string spanish, double seconds)
        {
            notice = english;
            noticeSpanish = spanish;
            noticeUntil = clock + seconds;
        }

        /// <summary>The message box: in the menu, how the host's world is coming along; in the world, a warning for a while.</summary>
        public static void OnGUI()
        {
            string text = banner ?? (clock < noticeUntil ? Texts.Pick(notice, noticeSpanish) : null);

            // In the menu, not over the game's licence or credits, and not where the lobby's box says it all.
            if (text == null || (inMenu && (!Menu.MenuColumn.InterfaceShown || Menu.WorldWindow.LobbyShown)))
            {
                return;
            }

            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box) { fontSize = 16, wordWrap = true, alignment = TextAnchor.MiddleCenter };
                style.normal.textColor = Color.white;
            }

            float width = Mathf.Min(720f, Screen.width - 40f);
            if (!ReferenceEquals(text, measured))
            {
                measured = text;
                content = new GUIContent(text);
                height = style.CalcHeight(content, width) + 16f;
            }

            GUI.Box(new Rect((Screen.width - width) / 2f, Screen.height * 0.12f, width, height), content, style);
        }

        /// <summary>One line for the overlay.</summary>
        public static void AppendStatus(StringBuilder text)
        {
            text.Append("Save folder: ");
            if (playFolder == null)
            {
                text.Append(started ? "own (no mod folder: the host's world cannot be used)" : "own");
            }
            else if (folder == SaveFolder.Host)
            {
                text.Append("a copy of the host's world ").Append(SharedFile.Name(folderWorld).Substring(0, 6)).Append(played ? ", played in" : string.Empty);
            }
            else
            {
                text.Append("own");
            }

            if (problem != null)
            {
                text.Append(" | PROBLEM: ").Append(problem);
            }
        }

        private static void Start()
        {
            started = true;
            try
            {
                originalDataPath = ES2GlobalSettings.defaultPCDataPath ?? string.Empty;
                ownFolder = ES2FilenameData.GetPersistentPath();
                if (ModRuntime.SettingsFolder != null)
                {
                    string root = Path.Combine(ModRuntime.SettingsFolder, "HostWorld");
                    Cache.Folder = Path.Combine(root, "cache");
                    playFolder = Path.Combine(root, "play");
                    playPath = Path.GetFullPath(playFolder).Replace('\\', '/').TrimEnd('/');
                    Cache.Log = log;
                }

                log.Info(
                    LogCategory.Net,
                    "Saved worlds: the game's own are in '" + ownFolder + "'" + (originalDataPath.Length > 0 ? " (set by the game to '" + originalDataPath + "')" : string.Empty)
                    + (playFolder == null ? "; there is no mod folder, so a guest cannot use the host's." : "; a guest plays the host's from '" + playPath + "'."));
                SaveWorlds.Start(originalDataPath, ownFolder, ModRuntime.SettingsFolder, log);
            }
            catch (Exception exception)
            {
                playFolder = null;
                log.Error(LogCategory.Net, "The game's save folder could not be worked out; a guest plays in their own world. " + exception);
            }
        }

        private static string HostName(NetSession session)
        {
            IList<PlayerInfo> players = session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].Id == Protocol.HostPlayerId)
                {
                    return players[i].Name;
                }
            }

            return string.Empty;
        }

        // The host's name as it is shown in that language: what the session says, or what stands for it.
        private static string HostCalled(bool spanish)
        {
            if (string.IsNullOrEmpty(hostName))
            {
                return hostName == null ? (spanish ? "tu amigo" : "your friend") : (spanish ? "el host" : "the host");
            }

            return hostName;
        }

        // ---- The host ----

        private static void Offer(double now)
        {
            if (offeredTo == share)
            {
                // Saving ends in the menu: only there can the files have changed for a reason. A world offered empty is
                // looked at in the world too: a new one is written by the licence just before the world loads.
                bool empty = share.Manifest != null && !share.Manifest.HasWorld;
                if ((!inMenu && !empty) || now < nextScan)
                {
                    return;
                }

                nextScan = now + ScanSeconds;
                string stamp = Stamp();
                if (stamp == offeredStamp)
                {
                    return;
                }

                if (stamp != seenStamp)
                {
                    seenStamp = stamp;
                    seenSince = now;
                    return;
                }

                if (now - seenSince < SettleSeconds)
                {
                    return;
                }

                log.Info(LogCategory.Net, "This game's saved world changed on disk: offering it again.");
            }
            else if (now < nextScan)
            {
                return;
            }

            nextScan = now + ScanSeconds;
            string folderNow = ES2FilenameData.GetPersistentPath();
            try
            {
                var files = new List<WorldFile>();
                var bytes = new Dictionary<ulong, byte[]>();
                long savedAt = 0;
                string stampBefore = Stamp();
                for (int i = 0; i < WorldFiles.Names.Length; i++)
                {
                    string path = Path.Combine(folderNow, WorldFiles.Names[i]);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    byte[] content = File.ReadAllBytes(path);
                    if (content.Length == 0)
                    {
                        continue;
                    }

                    ulong id = Fnv1a.Hash64(content);
                    bytes[id] = content;
                    files.Add(new WorldFile(i, id, content.Length));
                    if (i == WorldFiles.Main)
                    {
                        savedAt = (long)(File.GetLastWriteTimeUtc(path) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    }
                }

                if (Stamp() != stampBefore)
                {
                    // Written while it was being read: the next scan reads it again.
                    seenStamp = null;
                    return;
                }

                Cache.SetOffered(bytes);
                share.Offer(files, savedAt, SaveWorlds.CurrentKey);
                offeredTo = share;
                offeredStamp = stampBefore;
                seenStamp = stampBefore;
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "This game's saved world could not be read from '" + folderNow + "' (" + exception.Message + "); trying again in a moment.");
            }
        }

        // The size and time of every file of the world: what changes when the game saves.
        private static string Stamp()
        {
            string folderNow = ES2FilenameData.GetPersistentPath();
            var text = new StringBuilder();
            for (int i = 0; i < WorldFiles.Names.Length; i++)
            {
                var info = new FileInfo(Path.Combine(folderNow, WorldFiles.Names[i]));
                text.Append(info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) + "@" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "-").Append(';');
            }

            return text.ToString();
        }

        // ---- The guest's folder ----

        private static bool UseHost(ulong world)
        {
            WorldManifest manifest = share == null ? null : share.Manifest;
            if (manifest == null || manifest.Id != world)
            {
                return Fail("the host's world changed while it was being copied");
            }

            try
            {
                Directory.CreateDirectory(playFolder);
                foreach (string old in Directory.GetFiles(playFolder))
                {
                    File.Delete(old);
                }

                for (int i = 0; i < manifest.Files.Length; i++)
                {
                    WorldFile file = manifest.Files[i];
                    byte[] content = Cache.ReadKept(file.Id);
                    if (content == null || content.Length != file.Size || Fnv1a.Hash64(content) != file.Id)
                    {
                        Cache.Forget(file.Id);
                        return Fail(WorldFiles.Names[file.Name] + " of the host's world is not whole in the cache any more; leave and join again");
                    }

                    File.WriteAllBytes(Path.Combine(playFolder, WorldFiles.Names[file.Name]), content);
                }

                // The guest's own settings: controls and graphics are theirs, not the host's.
                string ownOptions = Path.Combine(ownFolder, OptionsFile);
                optionsCopiedIn = File.Exists(ownOptions) ? File.ReadAllBytes(ownOptions) : null;
                if (optionsCopiedIn != null)
                {
                    File.WriteAllBytes(Path.Combine(playFolder, OptionsFile), optionsCopiedIn);
                }

                ForgetCached(playPath);
                guestTitle = SaveWorlds.TitleIn(playPath);
                ES2GlobalSettings.defaultPCDataPath = playPath;
                folder = SaveFolder.Host;
                folderWorld = world;
                folderKey = manifest.Key;
                played = false;
                guardWarned = false;
                Cache.KeepOnly(manifest);
                log.Info(
                    LogCategory.Net,
                    "The game now reads and writes a copy of the host's world " + SharedFile.Name(world) + " (" + manifest.Files.Length + " file(s), "
                    + (manifest.TotalBytes / 1024) + " kB) in '" + playPath + "'. This player's own save is not touched.");
                return true;
            }
            catch (Exception exception)
            {
                return Fail("the copy could not be written (" + exception.Message + ")");
            }
        }

        private static void UseOwn()
        {
            try
            {
                // Settings changed while in the host's world are the player's: they go back with them.
                string playOptions = Path.Combine(playFolder, OptionsFile);
                if (File.Exists(playOptions))
                {
                    byte[] options = File.ReadAllBytes(playOptions);
                    if (optionsCopiedIn == null || !Same(options, optionsCopiedIn))
                    {
                        File.WriteAllBytes(Path.Combine(ownFolder, OptionsFile), options);
                        ES2Cache.DeleteCachedFile(ownFolder + "/" + OptionsFile);
                        log.Info(LogCategory.Net, "The settings changed in the host's world were kept in this player's own options.txt.");
                    }
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "The settings changed in the host's world could not be kept: " + exception.Message);
            }

            ES2GlobalSettings.defaultPCDataPath = SaveWorlds.DataPath;
            ForgetCached(playPath);
            log.Info(LogCategory.Net, "The game reads and writes this player's own world again ('" + ES2FilenameData.GetPersistentPath() + "').");
            folder = SaveFolder.Own;
            folderWorld = 0UL;
            folderKey = 0UL;
            played = false;
            optionsCopiedIn = null;
        }

        // Something else may set the folder back (another mod, the save library's own settings object waking up).
        private static void Guard()
        {
            if (playPath == null)
            {
                return;
            }

            string now = ES2GlobalSettings.defaultPCDataPath;
            if (folder == SaveFolder.Host && now != playPath)
            {
                ES2GlobalSettings.defaultPCDataPath = playPath;
                if (!guardWarned)
                {
                    guardWarned = true;
                    log.Warning(LogCategory.Net, "Something set the game's save folder to '" + now + "' while the host's world was in use; set back.");
                }
            }
            else if (folder == SaveFolder.Own && (now == playPath || (SaveWorlds.UsesModWorld && now != SaveWorlds.DataPath)))
            {
                ES2GlobalSettings.defaultPCDataPath = SaveWorlds.DataPath;
                if (now != playPath && !guardWarned)
                {
                    guardWarned = true;
                    log.Warning(LogCategory.Net, "Something set the game's save folder to '" + now + "' while a world of the mod's was in use; set back.");
                }
            }
        }

        /// <summary>
        /// The world in use changed (<see cref="SaveWorlds"/>): a host offers it at once, before the game can go into it, and
        /// the menu's own check for "Continue" looks at it.
        /// </summary>
        public static void WorldChanged()
        {
            offeredTo = null;
            nextScan = 0;
            seenStamp = null;
            guardWarned = false;
            if (inMenu && FindButtons())
            {
                Recheck();
            }
        }

        private static bool Fail(string why)
        {
            if (problem != why)
            {
                log.Warning(LogCategory.Net, "The host's world cannot be used yet: " + why + ".");
            }

            problem = why;
            return false;
        }

        // What the save library remembers of files it read there: they were written behind its back.
        private static void ForgetCached(string folderPath)
        {
            for (int i = 0; i < WorldFiles.Names.Length; i++)
            {
                ES2Cache.DeleteCachedFile(folderPath + "/" + WorldFiles.Names[i]);
            }

            ES2Cache.DeleteCachedFile(folderPath + "/" + OptionsFile);
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        // ---- The menu ----

        private static void ApplyMenu(MenuOffer offer)
        {
            if (!FindButtons())
            {
                return;
            }

            if (offer == MenuOffer.Game)
            {
                if (menuTouched)
                {
                    menuTouched = false;
                    SetActive(newGameButton, true);
                    Recheck();
                }

                // A world of the mod's is not the game's to begin again: its "New game" would empty it. And the lobby's column
                // has its own buttons.
                SetActive(newGameButton, !SaveWorlds.UsesModWorld && !Menu.MenuColumn.LobbyMode);
                menuOffer = offer;
                return;
            }

            if (offer != menuOffer)
            {
                log.Info(LogCategory.Net, offer == MenuOffer.Host ? "The menu offers 'Continue' into the host's world, and no 'New game'." : "The menu offers neither 'Continue' nor 'New game' until the host's world is here.");
            }

            // Every frame: the game's own check may run after this and show "Continue" again.
            menuTouched = true;
            menuOffer = offer;
            SetActive(continueButton, offer == MenuOffer.Host);
            SetActive(newGameButton, false);
        }

        private static bool FindButtons()
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (buttons != null && continueButton != null && newGameButton != null)
            {
                return true;
            }

            buttons = GameObject.Find(ButtonsPath);
            if (buttons == null)
            {
                return false;
            }

            // Found under their parent: the game switches "Continue" off when there is no save.
            Transform found = buttons.transform.Find("ButtonContinue");
            continueButton = found == null ? null : found.gameObject;
            found = buttons.transform.Find("ButtonNewgame");
            newGameButton = found == null ? null : found.gameObject;
            if (continueButton == null || newGameButton == null)
            {
                log.Warning(LogCategory.Net, "The main menu's 'Continue' or 'New game' is not where it was expected under '" + ButtonsPath + "'; the menu is left as the game has it.");
                buttons = null;
                return false;
            }

            return true;
        }

        // The game decides again whether there is a world to continue: its own check, sent back to its start.
        private static void Recheck()
        {
            foreach (PlayMakerFSM machine in buttons.GetComponents<PlayMakerFSM>())
            {
                if (machine == null || machine.Fsm == null || !DoorSync.HasState(machine, "Money"))
                {
                    continue;
                }

                if (!DoorSync.HasGlobalTransition(machine, RecheckEvent))
                {
                    machine.AddGlobalTransition(RecheckEvent, machine.Fsm.StartState);
                }

                machine.SendEvent(RecheckEvent);
                log.Info(LogCategory.Net, "The main menu checks again for this player's own world ('" + machine.FsmName + "' from '" + machine.Fsm.StartState + "').");
                return;
            }

            bool own = File.Exists(Path.Combine(ownFolder, WorldFiles.Names[WorldFiles.Main]));
            SetActive(continueButton, own);
            log.Warning(LogCategory.Net, "The main menu's own check for a saved world was not found; 'Continue' is " + (own ? "shown" : "hidden") + " by whether this player's save file exists.");
        }

        private static void SetActive(GameObject target, bool active)
        {
            if (target != null && target.activeSelf != active)
            {
                target.SetActive(active);
            }
        }

        // ---- What is shown ----

        private static string MenuBanner(bool guest, bool host, MenuOffer offer)
        {
            if (host)
            {
                WorldManifest offered = share == null ? null : share.Manifest;
                if (offered == null)
                {
                    return null;
                }

                if (!offered.HasWorld)
                {
                    return Texts.Pick(
                        "You are the host and this save is empty: choose one with HOST or start a new one there. Your guests get it by themselves.",
                        "Sos el host y esta partida está vacía: elegí una con HOST o empezá una nueva ahí. Tus invitados la reciben sola.");
                }

                string title = SaveWorlds.CurrentTitle;
                string named = string.IsNullOrEmpty(title) ? string.Empty : " \"" + title + "\"";
                return Texts.Spanish
                    ? "Sos el host. Tus invitados juegan en tu partida" + named + ", guardada " + Saved(offered) + ". Lo que hiciste después de guardar no lo ven al entrar."
                    : "You are the host. Your guests play in your save" + named + ", saved " + Saved(offered) + ". What you did after saving is not there for them when they join.";
            }

            if (!guest)
            {
                return null;
            }

            WorldManifest world = share.Manifest;
            string name = HostNameShown;
            if (offer == MenuOffer.Host)
            {
                string newer = world != null && world.Id != folderWorld && world.HasWorld && !share.IsReady
                    ? Texts.Pick("\n(A newer one is coming: ", "\n(Llegando una más nueva: ") + Percent(share.Progress) + ")" : string.Empty;
                bool dated = world != null && world.Id == folderWorld;
                return Texts.Spanish
                    ? "Conectado a " + name + ". Su mundo está listo" + (dated ? " (guardado " + Saved(world) + ")" : string.Empty) + ".\nTocá Continuar para entrar. Tu partida no se toca." + newer
                    : "Connected to " + name + ". Their world is ready" + (dated ? " (saved " + Saved(world) + ")" : string.Empty) + ".\nClick Continue to go in. Your own save is not touched." + newer;
            }

            if (problem != null)
            {
                return Texts.Spanish
                    ? "Conectado a " + name + ". No se pudo preparar su mundo: " + problem + "."
                    : "Connected to " + name + ". Their world could not be made ready: " + problem + ".";
            }

            if (world == null)
            {
                return Texts.Spanish ? "Conectado a " + name + ". Esperando su mundo..." : "Connected to " + name + ". Waiting for their world...";
            }

            if (!world.HasWorld)
            {
                return Texts.Spanish
                    ? "Conectado a " + name + ". Todavía no eligió una partida guardada: cuando la elija o empiece una nueva, te llega sola."
                    : "Connected to " + name + ". They have not chosen a save yet: when they choose one or start a new one, it comes to you by itself.";
            }

            if (share.HasFailed)
            {
                return Texts.Spanish
                    ? "El mundo de " + name + " llegó mal más de una vez. Salgan de la sesión y vuelvan a conectarse."
                    : name + "'s world arrived broken more than once. Leave the session and connect again.";
            }

            return Texts.Spanish
                ? "Conectado a " + name + ". Bajando su mundo: " + Percent(share.Progress) + " de " + Size(world.TotalBytes) + "."
                : "Connected to " + name + ". Downloading their world: " + Percent(share.Progress) + " of " + Size(world.TotalBytes) + ".";
        }

        /// <summary>The host's name, as a guest shows it.</summary>
        public static string HostNameShown
        {
            get { return HostCalled(Texts.Spanish); }
        }

        /// <summary>A guest's lobby box: the name of the host's world, read from the copy once it is here; null before.</summary>
        public static string GuestWorldTitle
        {
            get { return folder == SaveFolder.Host ? guestTitle : null; }
        }

        /// <summary>A guest's lobby box: when the host's world was saved, or null.</summary>
        public static string GuestSaved()
        {
            WorldManifest world = share == null ? null : share.Manifest;
            string when = world == null || !world.HasWorld ? string.Empty : Saved(world);
            return when.Length == 0 ? null : Texts.Pick("Saved ", "Guardada ") + when;
        }

        /// <summary>A guest's lobby box: how the host's world is coming along, in a line.</summary>
        public static string GuestStatus()
        {
            if (playFolder == null)
            {
                return Texts.Pick("SummerSync has no folder of its own: you will play in your own save.", "SummerSync no tiene una carpeta propia: vas a jugar en tu partida.");
            }

            WorldManifest world = share == null ? null : share.Manifest;
            if (menuOffer == MenuOffer.Host)
            {
                return world != null && world.Id != folderWorld && world.HasWorld && !share.IsReady
                    ? Texts.Pick("A newer one is coming: ", "Llegando una más nueva: ") + Percent(share.Progress) + "."
                    : Texts.Pick("Ready: ENTER GAME to go in. Your own save is not touched.", "Lista: ENTER GAME para entrar. Tu partida no se toca.");
            }

            if (problem != null)
            {
                return Texts.Pick("The save could not be made ready: ", "No se pudo preparar la partida: ") + problem + ".";
            }

            if (world == null)
            {
                return Texts.Pick("Waiting for the host's save...", "Esperando la partida del host...");
            }

            if (!world.HasWorld)
            {
                return Texts.Pick("The host has not chosen a save yet: it comes by itself.", "El host todavía no eligió una partida: llega sola.");
            }

            if (share.HasFailed)
            {
                return Texts.Pick("The save arrived broken more than once: LEAVE LOBBY and join again.", "La partida llegó mal más de una vez: LEAVE LOBBY y volvé a entrar.");
            }

            return Texts.Pick("Downloading the host's save: ", "Bajando la partida del host: ") + Percent(share.Progress) + Texts.Pick(" of ", " de ") + Size(world.TotalBytes) + ".";
        }

        /// <summary>For the lobby's box in the menu: when the world the guests get was saved ("Guardada el 16/10 a las 18:30"), or null.</summary>
        public static string HostSaved()
        {
            WorldManifest offered = share == null ? null : share.Manifest;
            string when = offered == null || !offered.HasWorld ? string.Empty : Saved(offered);
            return when.Length == 0 ? null : Texts.Pick("Saved ", "Guardada ") + when;
        }

        private static void ShowNoticeForLevel()
        {
            ShowNoticeForLevel(wasGuest, wasConnected);
        }

        private static void ShowNoticeForLevel(bool guest, bool connected)
        {
            string text = null;
            string spanish = null;
            if (folder == SaveFolder.Host && !connected)
            {
                text = "This is a copy of " + HostCalled(false) + "'s world. What you save here does not count: to go on together, go back to the menu and connect again.";
                spanish = "Esta es una copia del mundo de " + HostCalled(true) + ". Lo que guardes acá no cuenta: para seguir juntos, volvé al menú y conectate de nuevo.";
            }
            else if (folder == SaveFolder.Own && guest)
            {
                text = "You are in YOUR save, not in " + HostCalled(false) + "'s. To play in theirs: go back to the menu, connect again and click Continue.";
                spanish = "Estás en TU partida, no en la de " + HostCalled(true) + ". Para jugar en la suya: volvé al menú, conectate de nuevo y tocá Continuar.";
            }

            if (text != null)
            {
                notice = text;
                noticeSpanish = spanish;
                noticeUntil = clock + NoticeSeconds;
                if (log != null)
                {
                    log.Info(LogCategory.Net, folder == SaveFolder.Host ? "In a copy of the host's world without the host." : "Connected as a guest while in this player's own world.");
                }
            }
        }

        private static string Saved(WorldManifest world)
        {
            if (world.SavedAt <= 0)
            {
                return string.Empty;
            }

            DateTime saved = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(world.SavedAt).ToLocalTime();
            return Texts.Spanish
                ? "el " + saved.ToString("dd/MM 'a las' HH:mm", CultureInfo.InvariantCulture)
                : "on " + saved.ToString("dd/MM 'at' HH:mm", CultureInfo.InvariantCulture);
        }

        private static string Percent(float progress)
        {
            return Mathf.FloorToInt(Mathf.Clamp01(progress) * 100f).ToString(CultureInfo.InvariantCulture) + " %";
        }

        private static string Size(long bytes)
        {
            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024).ToString(CultureInfo.InvariantCulture) + " kB";
            }

            string megabytes = (bytes / (1024f * 1024f)).ToString("0.0", CultureInfo.InvariantCulture);
            return (Texts.Spanish ? megabytes.Replace('.', ',') : megabytes) + " MB";
        }

        /// <summary>
        /// The bytes of the world: on the host, what was read to offer; on a guest, files in the mod's folder,
        /// named by their hash. A world is a few megabytes, and only the last one is kept.
        /// </summary>
        private sealed class WorldCache : IFileStore
        {
            private Dictionary<ulong, byte[]> offered = new Dictionary<ulong, byte[]>();
            private FileStream writeStream;
            private ulong writeId;

            public string Folder { get; set; }

            public Logger Log { get; set; }

            public void SetOffered(Dictionary<ulong, byte[]> bytes)
            {
                offered = bytes;
            }

            public int Read(ulong id, int offset, byte[] buffer, int bufferOffset, int count)
            {
                byte[] bytes;
                if (!offered.TryGetValue(id, out bytes) || offset < 0 || offset + count > bytes.Length)
                {
                    return 0;
                }

                Array.Copy(bytes, offset, buffer, bufferOffset, count);
                return count;
            }

            public bool Has(ulong id, int size)
            {
                if (Folder == null)
                {
                    return false;
                }

                try
                {
                    var info = new FileInfo(Kept(id));
                    return info.Exists && info.Length == size;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            public void Write(ulong id, int size, int offset, byte[] data, int dataOffset, int count)
            {
                if (Folder == null)
                {
                    return;
                }

                try
                {
                    if (writeStream == null || writeId != id)
                    {
                        Close();
                        Directory.CreateDirectory(Folder);
                        writeStream = new FileStream(Unfinished(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                        writeStream.SetLength(size);
                        writeId = id;
                    }

                    writeStream.Seek(offset, SeekOrigin.Begin);
                    writeStream.Write(data, dataOffset, count);
                }
                catch (Exception exception)
                {
                    Log.Warning(LogCategory.Net, "A part of the host's world could not be written to '" + Folder + "': " + exception.Message);
                    Close();
                }
            }

            public bool Complete(ulong id, int size)
            {
                Close();
                if (Folder == null)
                {
                    return false;
                }

                string unfinished = Unfinished(id);
                try
                {
                    byte[] bytes = File.ReadAllBytes(unfinished);
                    if (bytes.Length != size || Fnv1a.Hash64(bytes) != id)
                    {
                        return false;
                    }

                    string kept = Kept(id);
                    if (File.Exists(kept))
                    {
                        File.Delete(kept);
                    }

                    File.Move(unfinished, kept);
                    return true;
                }
                catch (Exception exception)
                {
                    Log.Warning(LogCategory.Net, "A file of the host's world could not be kept: " + exception.Message);
                    return false;
                }
            }

            public void Discard(ulong id)
            {
                Close();
                if (Folder != null)
                {
                    Delete(Unfinished(id));
                }
            }

            public byte[] ReadKept(ulong id)
            {
                string kept = Kept(id);
                return File.Exists(kept) ? File.ReadAllBytes(kept) : null;
            }

            public void Forget(ulong id)
            {
                Delete(Kept(id));
            }

            // Only the world in use stays: the next one shares what did not change, and it is in this one.
            public void KeepOnly(WorldManifest manifest)
            {
                try
                {
                    var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < manifest.Files.Length; i++)
                    {
                        keep.Add(Path.GetFileName(Kept(manifest.Files[i].Id)));
                    }

                    foreach (string path in Directory.GetFiles(Folder))
                    {
                        if (!keep.Contains(Path.GetFileName(path)) && (writeStream == null || !path.EndsWith(SharedFile.Name(writeId) + ".part", StringComparison.OrdinalIgnoreCase)))
                        {
                            File.Delete(path);
                        }
                    }
                }
                catch (Exception exception)
                {
                    Log.Warning(LogCategory.Net, "Older worlds could not be cleared from '" + Folder + "': " + exception.Message);
                }
            }

            private string Kept(ulong id)
            {
                return Path.Combine(Folder, SharedFile.Name(id) + ".world");
            }

            private string Unfinished(ulong id)
            {
                return Path.Combine(Folder, SharedFile.Name(id) + ".part");
            }

            private void Close()
            {
                if (writeStream != null)
                {
                    try
                    {
                        writeStream.Close();
                    }
                    catch (Exception)
                    {
                        // Nothing more can be done about a file that cannot be closed.
                    }

                    writeStream = null;
                }

                writeId = 0;
            }

            private static void Delete(string path)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception)
                {
                    // Left for the next clean-up.
                }
            }
        }
    }
}
