using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SummerSync.Core.Hashing;
using SummerSync.Core.Logging;
using SummerSync.Core.World;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The saved worlds of this player (the main menu's "LOAD SAVE" and "HOST"): the game's
    /// own save, and the worlds made from "HOST", each in a folder of its own under the mod's settings ("Worlds/&lt;id&gt;").
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "La partida guardada" and "Las partidas del menú"): every save
    /// file is read and written by Easy Save in one folder, <c>ES2GlobalSettings.defaultPCDataPath</c> when it is set; which
    /// world is played is which folder that is. The main file has the player's name ("PlayerName", "PlayerFirstName",
    /// "PlayerLastName", written by the licence), the day of the week ("WorldDay"), the hour ("WorldTime") and the money
    /// ("PlayerMoney"). Easy Save keeps what it read of a file by its full path: a file read or written here is forgotten
    /// there after.
    ///
    /// The folder changes only in the main menu, and only on the player's word (a card of "LOAD SAVE" or "HOST"); when the
    /// menu loads and this game is not hosting, it is the game's own again, so that the game's "Continue" and "New game" are
    /// what they always were. Each world has a folder of the mod's too ("WorldInfo/&lt;key&gt;"): the photo of its Satsuma taken
    /// when it was saved, its guests (guests.txt) and its last three copies, made when it is loaded. The game's own save is
    /// written only by the game, and by a restore the player asks for.
    /// </summary>
    internal static class SaveWorlds
    {
        public const string PhotoFile = "satsuma.png";
        public const string GuestsFile = "guests.txt";

        private const string WorldsFolder = "Worlds";
        private const string InfoFolder = "WorldInfo";
        private const string CopiesFolder = "copies";
        private const string HashFile = "hash.txt";
        private const string OptionsFile = "options.txt";
        private const string GameKeyPrefix = "game-";

        private static Logger log;
        private static string gamePath;
        private static string gameFolder;
        private static string worldsRoot;
        private static string infoRoot;
        private static SavedWorld current;
        private static SavedWorld titled;
        private static byte[] optionsCopiedIn;

        /// <summary>
        /// While the licence of a new world is up: the folder stays that world's whatever else happens (a session that ends),
        /// because the licence's BEGIN deletes the world files of whatever folder the game uses.
        /// </summary>
        public static bool Pinned { get; set; }

        /// <summary>Whether it could start: there is a folder of the mod's to keep worlds in.</summary>
        public static bool Ready
        {
            get { return worldsRoot != null; }
        }

        /// <summary>Whether the world in use is one of the mod's (not the game's own save).</summary>
        public static bool UsesModWorld
        {
            get { return current != null; }
        }

        /// <summary>The world in use: null for the game's own save.</summary>
        public static SavedWorld Current
        {
            get { return current; }
        }

        /// <summary>What Easy Save's folder is set to for this player's own play: the game's own setting, or a world of the mod's.</summary>
        public static string DataPath
        {
            get { return current == null ? gamePath : current.Folder; }
        }

        /// <summary>
        /// Which world is in use, for the guests (<see cref="WorldManifest.Key"/>): the same for every save of one world, another
        /// for another world. Never 0.
        /// </summary>
        public static ulong CurrentKey
        {
            get
            {
                ulong key = Fnv1a.Hash64(System.Text.Encoding.UTF8.GetBytes(current == null ? "game" : "world " + current.Id));
                return key == 0UL ? 1UL : key;
            }
        }

        /// <summary>The name of the world in use, as its card shows it.</summary>
        public static string CurrentTitle
        {
            get { return titled == null ? string.Empty : titled.Title; }
        }

        /// <summary>From <see cref="HostWorld"/>, once: the game's own folder, as the game set it and as Easy Save makes it.</summary>
        public static void Start(string originalDataPath, string ownFolder, string settingsFolder, Logger logger)
        {
            log = logger;
            gamePath = originalDataPath ?? string.Empty;
            gameFolder = Normal(ownFolder);
            titled = null;
            if (settingsFolder == null)
            {
                return;
            }

            worldsRoot = Normal(Path.Combine(settingsFolder, WorldsFolder));
            infoRoot = Normal(Path.Combine(settingsFolder, InfoFolder));

            // Before there were worlds, the host's guests were kept in the mod's folder: they were the game's own save's.
            try
            {
                string old = Path.Combine(settingsFolder, GuestsFile);
                string kept = Path.Combine(InfoOf(GameWorld()), GuestsFile);
                if (File.Exists(old) && !File.Exists(kept))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(kept));
                    File.Move(old, kept);
                    log.Info(LogCategory.Net, "Saved worlds: the guests of the game's own save moved to '" + kept + "'.");
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "Saved worlds: the guests of the game's own save could not be moved next to it: " + exception.Message);
            }

            log.Info(LogCategory.Net, "Saved worlds: the mod's are in '" + worldsRoot + "', what the mod keeps of each in '" + infoRoot + "'.");
            RefreshTitle();
        }

        /// <summary>The name of the world in use, read again from its save (after it was played and saved).</summary>
        public static void RefreshTitle()
        {
            try
            {
                SavedWorld world = ReadCurrent();
                titled = world.HasSave ? world : null;
                if (current != null && world.HasSave)
                {
                    current.CopyTitle(world);
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "Saved worlds: the name of the world in use could not be read: " + exception.Message);
            }
        }

        /// <summary>The game's own save and the mod's worlds that have something saved (or kept copies), newest first after the game's.</summary>
        public static List<SavedWorld> List()
        {
            var list = new List<SavedWorld>();
            SavedWorld game = GameWorld();
            Read(game);
            if (game.HasSave || game.Copies.Count > 0)
            {
                list.Add(game);
            }

            if (worldsRoot == null || !Directory.Exists(worldsRoot))
            {
                return list;
            }

            var mods = new List<SavedWorld>();
            foreach (string folder in Directory.GetDirectories(worldsRoot))
            {
                SavedWorld world = ModWorld(Path.GetFileName(folder));
                Read(world);
                if (world.HasSave || world.Copies.Count > 0)
                {
                    mods.Add(world);
                }
                else if (!IsCurrent(world))
                {
                    // A world begun and never played (the licence was left, the game closed): nothing to keep.
                    Forget(world, "it has nothing saved");
                }
            }

            mods.Sort(delegate(SavedWorld a, SavedWorld b) { return b.SavedAt.CompareTo(a.SavedAt); });
            list.AddRange(mods);
            return list;
        }

        /// <summary>The name of the world saved in that folder (a copy of a host's), or null if it has no save.</summary>
        public static string TitleIn(string folder)
        {
            var world = new SavedWorld { Folder = folder };
            ReadSave(world);
            return world.HasSave ? world.Title : null;
        }

        /// <summary>The world in use, read again (for its name and whether it has a save).</summary>
        public static SavedWorld ReadCurrent()
        {
            SavedWorld world = current == null ? GameWorld() : ModWorld(current.Id);
            Read(world);
            return world;
        }

        /// <summary>
        /// Points the game at that world. Only in the main menu, and never while this game plays in a copy of a host's world.
        /// The player's settings go with them: the game's own options.txt is copied in, and back out when the world is left.
        /// </summary>
        public static bool Use(SavedWorld world, out string problem)
        {
            problem = null;
            if (!HostWorld.InMenu || HostWorld.PlaysHostWorld)
            {
                problem = HostWorld.PlaysHostWorld
                    ? Texts.Pick("you are in another player's world", "estás en el mundo de otro jugador")
                    : Texts.Pick("only from the menu", "solo se puede desde el menú");
                return false;
            }

            if (world == null || (!world.IsGame && worldsRoot == null))
            {
                problem = Texts.Pick("there is no mod folder to keep saves in", "no hay una carpeta del mod donde guardar partidas");
                return false;
            }

            if (IsCurrent(world))
            {
                return true;
            }

            if (Pinned)
            {
                problem = Texts.Pick("the licence of a new game is open", "está abierta la licencia de una partida nueva");
                log.Warning(LogCategory.Net, "Saved worlds: '" + world.Title + "' was not used: the licence of a new world is up.");
                return false;
            }

            if (world.IsGame)
            {
                UseGame();
                return true;
            }

            UseGame();
            try
            {
                Directory.CreateDirectory(world.Folder);
                string ownOptions = Path.Combine(gameFolder, OptionsFile);
                optionsCopiedIn = File.Exists(ownOptions) ? File.ReadAllBytes(ownOptions) : null;
                if (optionsCopiedIn != null)
                {
                    File.WriteAllBytes(Path.Combine(world.Folder, OptionsFile), optionsCopiedIn);
                }
            }
            catch (Exception exception)
            {
                problem = Texts.Pick("its folder could not be made ready (" + exception.Message + ")", "no se pudo preparar su carpeta (" + exception.Message + ")");
                return false;
            }

            ES2GlobalSettings.defaultPCDataPath = world.Folder;
            string seen = Normal(ES2FilenameData.GetPersistentPath());
            if (!string.Equals(seen, world.Folder, StringComparison.OrdinalIgnoreCase))
            {
                // Never go on with the game reading some other folder: "New game" there would empty it.
                ES2GlobalSettings.defaultPCDataPath = gamePath;
                problem = Texts.Pick("the game did not take its folder", "el juego no tomó su carpeta");
                log.Warning(LogCategory.Net, "Saved worlds: the game was pointed at '" + world.Folder + "' and reads '" + seen + "'; left on its own save.");
                return false;
            }

            ForgetCached(world.Folder);
            current = world;
            titled = world;
            log.Info(LogCategory.Net, "Saved worlds: the game now reads and writes the world '" + world.Title + "' (" + world.Id + ") in '" + world.Folder + "'.");
            HostWorld.WorldChanged();
            return true;
        }

        /// <summary>The game's own save again, with the settings changed in a world of the mod's.</summary>
        public static void UseGame()
        {
            if (current == null)
            {
                return;
            }

            if (Pinned)
            {
                // The licence's BEGIN would delete the game's own save.
                log.Warning(LogCategory.Net, "Saved worlds: the game's own save was not used again: the licence of a new world is up.");
                return;
            }

            SavedWorld left = current;
            try
            {
                string worldOptions = Path.Combine(left.Folder, OptionsFile);
                if (File.Exists(worldOptions))
                {
                    byte[] options = File.ReadAllBytes(worldOptions);
                    if (optionsCopiedIn == null || !Same(options, optionsCopiedIn))
                    {
                        File.WriteAllBytes(Path.Combine(gameFolder, OptionsFile), options);
                        ES2Cache.DeleteCachedFile(gameFolder + "/" + OptionsFile);
                        log.Info(LogCategory.Net, "Saved worlds: the settings changed in '" + left.Title + "' were kept in the game's own options.txt.");
                    }
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "Saved worlds: the settings changed in '" + left.Title + "' could not be kept: " + exception.Message);
            }

            current = null;
            optionsCopiedIn = null;
            ES2GlobalSettings.defaultPCDataPath = gamePath;
            ForgetCached(left.Folder);
            ForgetCached(gameFolder);
            SavedWorld game = GameWorld();
            Read(game);
            titled = game.HasSave ? game : null;
            log.Info(LogCategory.Net, "Saved worlds: the game reads and writes its own save again ('" + gameFolder + "').");
            HostWorld.WorldChanged();
        }

        /// <summary>A new world's folder, empty: the licence of the game fills it in.</summary>
        public static SavedWorld Create(out string problem)
        {
            problem = null;
            if (worldsRoot == null)
            {
                problem = Texts.Pick("there is no mod folder to keep saves in", "no hay una carpeta del mod donde guardar partidas");
                return null;
            }

            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            for (int n = 2; Directory.Exists(Path.Combine(worldsRoot, id)) || Directory.Exists(Path.Combine(infoRoot, id)); n++)
            {
                id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            try
            {
                SavedWorld world = ModWorld(id);
                Directory.CreateDirectory(world.Folder);
                world.StandIn("New game", "Partida nueva");
                log.Info(LogCategory.Net, "Saved worlds: a new world begins in '" + world.Folder + "'.");
                return world;
            }
            catch (Exception exception)
            {
                problem = Texts.Pick("its folder could not be made (" + exception.Message + ")", "no se pudo crear su carpeta (" + exception.Message + ")");
                return null;
            }
        }

        /// <summary>A world begun and left before it had anything saved: its folder goes, and the game's own save is used again.</summary>
        public static void Abandon(SavedWorld world)
        {
            if (world == null || world.IsGame)
            {
                return;
            }

            if (IsCurrent(world))
            {
                UseGame();
            }

            if (!File.Exists(Path.Combine(world.Folder, WorldFiles.Names[WorldFiles.Main])))
            {
                Forget(world, "it was left before it had anything saved");
            }
        }

        /// <summary>A world of the mod's, gone for good with everything the mod kept of it. Never the game's own save.</summary>
        public static bool Delete(SavedWorld world, out string problem)
        {
            problem = null;
            if (world == null || world.IsGame)
            {
                problem = Texts.Pick("the game's own save is not deleted from here", "la partida del juego no se borra desde acá");
                return false;
            }

            if (IsCurrent(world))
            {
                UseGame();
            }

            try
            {
                if (Directory.Exists(world.Folder))
                {
                    Directory.Delete(world.Folder, true);
                }

                if (Directory.Exists(world.Info))
                {
                    Directory.Delete(world.Info, true);
                }

                log.Info(LogCategory.Net, "Saved worlds: '" + world.Title + "' (" + world.Id + ") deleted by the player.");
                return true;
            }
            catch (Exception exception)
            {
                problem = exception.Message;
                log.Warning(LogCategory.Net, "Saved worlds: '" + world.Folder + "' could not be deleted: " + exception.Message);
                return false;
            }
        }

        /// <summary>The mod's folder of the world in use, for its photo and guests. Null while playing a copy of a host's world.</summary>
        public static string CurrentInfo
        {
            get
            {
                if (HostWorld.PlaysHostWorld || infoRoot == null)
                {
                    return null;
                }

                return current == null ? InfoOf(GameWorld()) : current.Info;
            }
        }

        /// <summary>
        /// When a world is loaded: a copy of what it loads, unless a copy of the same is kept already; only the newest three
        /// stay. Not while playing a copy of a host's world.
        /// </summary>
        public static void CopyCurrent()
        {
            if (HostWorld.PlaysHostWorld || infoRoot == null)
            {
                return;
            }

            CopyOf(current == null ? GameWorld() : ModWorld(current.Id), current == null ? "the game's own save" : current.Title);
        }

        private static void CopyOf(SavedWorld world, string label)
        {
            try
            {
                byte[][] files = ReadFiles(world.Folder);
                ulong hash = WorldCopies.Hash(files);
                string main = world.Folder + "/" + WorldFiles.Names[WorldFiles.Main];
                if (hash != 0UL && !Played(main))
                {
                    // Only the licence's names: a world that was never played and saved.
                    return;
                }

                List<WorldCopy> kept = ReadCopies(world);
                var known = new List<WorldCopyInfo>();
                for (int i = 0; i < kept.Count; i++)
                {
                    known.Add(new WorldCopyInfo(kept[i].Name, kept[i].Hash));
                }

                if (!WorldCopies.Worth(known, hash))
                {
                    return;
                }

                DateTime saved = File.GetLastWriteTime(Path.Combine(world.Folder, WorldFiles.Names[WorldFiles.Main]));
                string name = WorldCopies.NameFor(saved, known);
                string folder = Path.Combine(Path.Combine(world.Info, CopiesFolder), name);
                Directory.CreateDirectory(folder);
                for (int i = 0; i < files.Length; i++)
                {
                    if (files[i] != null)
                    {
                        // With the time it was saved: the copy's card says when that was.
                        string copied = Path.Combine(folder, WorldFiles.Names[i]);
                        File.WriteAllBytes(copied, files[i]);
                        File.SetLastWriteTime(copied, File.GetLastWriteTime(Path.Combine(world.Folder, WorldFiles.Names[i])));
                    }
                }

                CopyIfThere(Path.Combine(world.Info, GuestsFile), Path.Combine(folder, GuestsFile));
                CopyIfThere(Path.Combine(world.Info, PhotoFile), Path.Combine(folder, PhotoFile));
                File.WriteAllText(Path.Combine(folder, HashFile), hash.ToString("x16", CultureInfo.InvariantCulture));
                known.Add(new WorldCopyInfo(name, hash));
                List<string> drop = WorldCopies.ToDrop(known, WorldCopies.Kept);
                for (int i = 0; i < drop.Count; i++)
                {
                    Directory.Delete(Path.Combine(Path.Combine(world.Info, CopiesFolder), drop[i]), true);
                }

                log.Info(LogCategory.Net, "Saved worlds: a copy of '" + label + "' (" + name + ")"
                    + (drop.Count > 0 ? "; the oldest went (" + string.Join(", ", drop.ToArray()) + ")." : "."));
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "Saved worlds: no copy of the world could be made this time: " + exception.Message);
            }
        }

        /// <summary>The copies of a world, newest first, with what each one's save says (name, day, money).</summary>
        public static List<WorldCopy> Copies(SavedWorld world)
        {
            List<WorldCopy> copies = ReadCopies(world);
            for (int i = 0; i < copies.Count; i++)
            {
                var summary = new SavedWorld { Folder = copies[i].Folder };
                ReadSave(summary);
                copies[i].Summary = summary;
            }

            return copies;
        }

        /// <summary>
        /// The world as that copy has it. What it has now is first kept as a copy itself (unless one has the same), so that a
        /// restore can be undone.
        /// </summary>
        public static bool Restore(SavedWorld world, WorldCopy copy, out string problem)
        {
            problem = null;
            if (!HostWorld.InMenu)
            {
                problem = Texts.Pick("only from the menu", "solo se puede desde el menú");
                return false;
            }

            try
            {
                byte[][] files = ReadFiles(copy.Folder);
                if (files[WorldFiles.Main] == null)
                {
                    problem = Texts.Pick("the copy has no main file", "la copia no tiene el archivo principal");
                    return false;
                }

                byte[] guests = ReadIfThere(Path.Combine(copy.Folder, GuestsFile));
                byte[] photo = ReadIfThere(Path.Combine(copy.Folder, PhotoFile));
                DateTime saved = File.GetLastWriteTime(Path.Combine(copy.Folder, WorldFiles.Names[WorldFiles.Main]));

                CopyOf(world.IsGame ? GameWorld() : ModWorld(world.Id), world.Title + " (before a restore)");

                for (int i = 0; i < WorldFiles.Names.Length; i++)
                {
                    string path = Path.Combine(world.Folder, WorldFiles.Names[i]);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    if (files[i] != null)
                    {
                        File.WriteAllBytes(path, files[i]);
                        File.SetLastWriteTime(path, saved);
                    }
                }

                Directory.CreateDirectory(world.Info);
                Replace(Path.Combine(world.Info, GuestsFile), guests);
                Replace(Path.Combine(world.Info, PhotoFile), photo);
                ForgetCached(world.Folder);
                log.Info(LogCategory.Net, "Saved worlds: '" + world.Title + "' (" + (world.IsGame ? "the game's own save" : world.Id) + ") restored by the player from the copy " + copy.Name + ".");
                if (IsCurrent(world) || (world.IsGame && current == null))
                {
                    HostWorld.WorldChanged();
                }

                return true;
            }
            catch (Exception exception)
            {
                problem = exception.Message;
                log.Warning(LogCategory.Net, "Saved worlds: the copy " + copy.Name + " could not be restored: " + exception);
                return false;
            }
        }

        /// <summary>Whether that world is the one in use.</summary>
        public static bool IsCurrent(SavedWorld world)
        {
            if (world == null)
            {
                return false;
            }

            return world.IsGame ? current == null : current != null && current.Id == world.Id;
        }

        // ---- The worlds ----

        private static SavedWorld GameWorld()
        {
            return new SavedWorld
            {
                Id = string.Empty,
                IsGame = true,
                Folder = gameFolder,
                Info = InfoOf(null),
            };
        }

        private static SavedWorld ModWorld(string id)
        {
            return new SavedWorld
            {
                Id = id,
                Folder = Normal(Path.Combine(worldsRoot, id)),
                Info = Normal(Path.Combine(infoRoot, id)),
            };
        }

        // The game's own save's things go under a name made from its folder: the test copy of the bridge is another save.
        private static string InfoOf(SavedWorld world)
        {
            if (infoRoot == null)
            {
                return null;
            }

            if (world != null && !world.IsGame)
            {
                return world.Info;
            }

            ulong hash = Fnv1a.Hash64(System.Text.Encoding.UTF8.GetBytes(gameFolder.ToLowerInvariant()));
            return Normal(Path.Combine(infoRoot, GameKeyPrefix + hash.ToString("x16", CultureInfo.InvariantCulture).Substring(0, 8)));
        }

        private static void Read(SavedWorld world)
        {
            ReadSave(world);
            world.Copies = world.Info == null ? new List<WorldCopy>() : ReadCopies(world);
            string photo = world.Info == null ? null : Path.Combine(world.Info, PhotoFile);
            world.Photo = photo != null && File.Exists(photo) ? photo : null;
        }

        // What the main file says, by Easy Save, the way the game reads it.
        private static void ReadSave(SavedWorld world)
        {
            string main = world.Folder + "/" + WorldFiles.Names[WorldFiles.Main];
            world.HasSave = false;
            if (world.IsGame)
            {
                world.StandIn("The game's save", "Partida del juego");
            }
            else
            {
                world.StandIn("Unnamed save", "Partida sin nombre");
            }

            world.Day = 0;
            world.Hour = -1;
            world.Money = float.NaN;
            world.SavedAt = DateTime.MinValue;
            if (!File.Exists(main))
            {
                return;
            }

            try
            {
                // As the game's own check for "Continue": the player's name or their money.
                world.HasSave = ES2.Exists(main + "?tag=PlayerName") || ES2.Exists(main + "?tag=PlayerMoney");
                string named = SavedWorldText.Title(Load(main, "PlayerName", string.Empty), Load(main, "PlayerFirstName", string.Empty), Load(main, "PlayerLastName", string.Empty));
                if (named.Length > 0)
                {
                    world.Title = named;
                }
                world.Day = Load(main, "WorldDay", 0);
                world.Hour = Load(main, "WorldTime", -1);
                world.Money = Load(main, "PlayerMoney", float.NaN);
                world.PermaDeath = Load(main, "PlayerPermaDeath", false);
                world.SavedAt = File.GetLastWriteTime(main);
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "Saved worlds: '" + main + "' could not be read: " + exception.Message);
                world.StandIn("Unreadable", "No se pudo leer");
            }
            finally
            {
                ES2Cache.DeleteCachedFile(main);
            }
        }

        /// <summary>
        /// Writes the world's permanent death into its save, where the game's licence leaves it and "Continue" reads it
        /// ("PlayerPermaDeath" in the main file). False with the reason if it could not be written.
        /// </summary>
        public static bool SetPermaDeath(SavedWorld world, bool on, out string problem)
        {
            problem = null;
            string main = world.Folder + "/" + WorldFiles.Names[WorldFiles.Main];
            if (!File.Exists(main))
            {
                problem = Texts.Pick("it has nothing saved", "no tiene partida guardada");
                return false;
            }

            try
            {
                ES2.Save(on, main + "?tag=PlayerPermaDeath");
                world.PermaDeath = on;
                return true;
            }
            catch (Exception exception)
            {
                problem = exception.Message;
                log.Warning(LogCategory.Net, "Saved worlds: the permanent death of '" + main + "' could not be written: " + exception.Message);
                return false;
            }
            finally
            {
                ES2Cache.DeleteCachedFile(main);
            }
        }

        // Whether the game ever saved the world itself, not only the licence's names: its money is in the save then.
        private static bool Played(string main)
        {
            try
            {
                return ES2.Exists(main + "?tag=PlayerMoney");
            }
            finally
            {
                ES2Cache.DeleteCachedFile(main);
            }
        }

        private static T Load<T>(string file, string tag, T otherwise)
        {
            string identifier = file + "?tag=" + tag;
            return ES2.Exists(identifier) ? ES2.Load<T>(identifier) : otherwise;
        }

        private static List<WorldCopy> ReadCopies(SavedWorld world)
        {
            var copies = new List<WorldCopy>();
            string root = world.Info == null ? null : Path.Combine(world.Info, CopiesFolder);
            if (root == null || !Directory.Exists(root))
            {
                return copies;
            }

            foreach (string folder in Directory.GetDirectories(root))
            {
                string main = Path.Combine(folder, WorldFiles.Names[WorldFiles.Main]);
                if (!File.Exists(main))
                {
                    continue;
                }

                ulong hash = 0UL;
                string hashPath = Path.Combine(folder, HashFile);
                if (!File.Exists(hashPath) || !ulong.TryParse(File.ReadAllText(hashPath).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
                {
                    hash = WorldCopies.Hash(ReadFiles(folder));
                }

                copies.Add(new WorldCopy
                {
                    Name = Path.GetFileName(folder),
                    Folder = Normal(folder),
                    Hash = hash,
                    SavedAt = File.GetLastWriteTime(main),
                    Photo = File.Exists(Path.Combine(folder, PhotoFile)) ? Path.Combine(folder, PhotoFile) : null,
                });
            }

            copies.Sort(delegate(WorldCopy a, WorldCopy b) { return WorldCopies.Older(b.Name, a.Name); });
            return copies;
        }

        /// <summary>The hash of the files a world has now, to tell which copy is the same as it.</summary>
        public static ulong HashNow(SavedWorld world)
        {
            try
            {
                return WorldCopies.Hash(ReadFiles(world.Folder));
            }
            catch (Exception)
            {
                return 0UL;
            }
        }

        private static byte[][] ReadFiles(string folder)
        {
            var files = new byte[WorldFiles.Names.Length][];
            for (int i = 0; i < files.Length; i++)
            {
                files[i] = ReadIfThere(Path.Combine(folder, WorldFiles.Names[i]));
            }

            return files;
        }

        private static byte[] ReadIfThere(string path)
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private static void CopyIfThere(string from, string to)
        {
            if (File.Exists(from))
            {
                File.Copy(from, to, true);
            }
        }

        private static void Replace(string path, byte[] content)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (content != null)
            {
                File.WriteAllBytes(path, content);
            }
        }

        private static void Forget(SavedWorld world, string why)
        {
            try
            {
                if (Directory.Exists(world.Folder))
                {
                    Directory.Delete(world.Folder, true);
                }

                if (world.Info != null && Directory.Exists(world.Info))
                {
                    Directory.Delete(world.Info, true);
                }

                log.Info(LogCategory.Net, "Saved worlds: the empty world " + world.Id + " was cleared away: " + why + ".");
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "Saved worlds: the empty world '" + world.Folder + "' could not be cleared away: " + exception.Message);
            }
        }

        // What Easy Save remembers of files read or written behind its back.
        private static void ForgetCached(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            for (int i = 0; i < WorldFiles.Names.Length; i++)
            {
                ES2Cache.DeleteCachedFile(folder + "/" + WorldFiles.Names[i]);
            }

            ES2Cache.DeleteCachedFile(folder + "/" + OptionsFile);
        }

        private static string Normal(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
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
    }

    /// <summary>A saved world, as a card of the main menu shows it.</summary>
    internal sealed class SavedWorld
    {
        /// <summary>The folder's name for a world of the mod's; empty for the game's own save.</summary>
        public string Id = string.Empty;

        public bool IsGame;

        /// <summary>Where its save files are, with forward slashes, as Easy Save is given it.</summary>
        public string Folder;

        /// <summary>The mod's folder for it: photo, guests, copies.</summary>
        public string Info;

        /// <summary>Whether the game would offer "Continue" into it.</summary>
        public bool HasSave;

        private string title = string.Empty;
        private string spanishTitle;

        /// <summary>The player's name in its save, or what stands for it in the player's language.</summary>
        public string Title
        {
            get { return spanishTitle != null && Texts.Spanish ? spanishTitle : title; }

            set
            {
                title = value;
                spanishTitle = null;
            }
        }

        /// <summary>The day of the week (1 Monday, 7 Sunday), 0 if not known.</summary>
        public int Day;

        /// <summary>The hour its two-hour block starts, -1 if not known.</summary>
        public int Hour = -1;

        /// <summary>Its money, NaN if not known.</summary>
        public float Money = float.NaN;

        public bool PermaDeath;

        /// <summary>When it was saved (local time), MinValue if never.</summary>
        public DateTime SavedAt = DateTime.MinValue;

        /// <summary>The photo of its Satsuma, or null.</summary>
        public string Photo;

        public List<WorldCopy> Copies = new List<WorldCopy>();

        /// <summary>What stands for a name the save does not give.</summary>
        public void StandIn(string english, string spanish)
        {
            title = english;
            spanishTitle = spanish;
        }

        /// <summary>The name as another reading of the same world has it.</summary>
        public void CopyTitle(SavedWorld from)
        {
            title = from.title;
            spanishTitle = from.spanishTitle;
        }
    }

    /// <summary>A copy of a saved world, kept when it was loaded.</summary>
    internal sealed class WorldCopy
    {
        public string Name;
        public string Folder;
        public ulong Hash;
        public DateTime SavedAt;
        public string Photo;

        /// <summary>What its save says, read only when the copies are shown.</summary>
        public SavedWorld Summary;
    }
}
