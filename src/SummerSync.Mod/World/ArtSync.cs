using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The host's car picture for the Satsuma's "Art" paint, in every game.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "El taller de Fleetari"): <c>Systems/ImportCarTexture :: Get</c>
    /// loads the game folder's <c>Images/car.png</c> when its <c>Activate</c> is set (each painted part's <c>Paint</c> sets it
    /// in its state <c>Art</c>, and Fleetari's paint job): <c>State 1</c> builds the address (<c>BuildString</c> into
    /// <c>Address</c>), fetches it with <c>WWW</c>, and puts it on the parts painted "Art" (<c>PaintType</c> 4).
    ///
    /// The host offers its picture (<see cref="FileChannel.Art"/>; nothing if it has none). In a guest's game that plays the
    /// host's world, an action of the mod's after the address is built puts there the host's picture as it arrived here, or a
    /// grey pixel of the mod's own if the host has none or it has not arrived yet (the game's look without one).
    /// When the host's arrives, the game is asked to load it again. Back in its own world, a game reads its own.
    /// </summary>
    internal static class ArtSync
    {
        private const string ImporterRoot = "/Systems";
        private const string ImporterPath = "ImportCarTexture";
        private const string ImporterMachine = "Get";
        private const string LoadState = "State 1";
        private const string AddressVariable = "Address";
        private const string ActivateVariable = "Activate";
        private const string PictureFolder = "Images";
        private const string Picture = "car.png";
        private const string CacheFolder = "ArtCache";
        private const string Neutral = "neutral.png";
        private const int HashPerFrame = 2 * 1024 * 1024;
        private const double CheckEvery = 0.5;

        private static readonly HashSet<int> Hooked = new HashSet<int>();

        private static Logger log;
        private static SongFiles files;
        private static string cacheFolder;
        private static FileTransfer current;
        private static NetworkManager network;
        private static bool scanned;
        private static bool offered;
        private static double nextCheck;
        private static PlayMakerFSM importer;
        private static ulong shown;
        private static bool hostsShown;

        /// <summary>Whether it broke: then each game reads its own picture until it is closed.</summary>
        public static bool Failed { get; private set; }

        /// <summary>Every frame, in every scene.</summary>
        public static void Update(NetworkManager manager, Logger logger, double now, string settingsFolder)
        {
            if (Failed)
            {
                return;
            }

            try
            {
                Run(manager, logger, now, settingsFolder);
            }
            catch (Exception exception)
            {
                Failed = true;
                logger.Error(LogCategory.General, "Sharing the host's car picture failed; each game reads its own until it is closed. " + exception);
            }
        }

        private static void Run(NetworkManager manager, Logger logger, double now, string settingsFolder)
        {
            log = logger;
            network = manager;
            if (files == null)
            {
                cacheFolder = Path.Combine(settingsFolder ?? ".", CacheFolder);
                files = new SongFiles(logger, cacheFolder, FileChannel.Art, ".png", SongFiles.KindOfPicture);
            }

            FileTransfer transfer = manager.Art;
            if (transfer != current)
            {
                current = transfer;
                scanned = false;
                offered = false;
                shown = 0;
                hostsShown = false;
                if (current != null)
                {
                    current.SetStore(files);
                    if (manager.Mode != NetworkMode.Host)
                    {
                        files.Tidy();
                    }
                }
            }

            if (current != null && manager.Mode == NetworkMode.Host)
            {
                Offer();
            }

            if (now < nextCheck)
            {
                return;
            }

            nextCheck = now + CheckEvery;
            Hook();
            bool hosts = UsesHosts();
            ulong id = hosts ? HostsPicture() : 0;
            if (hosts == hostsShown && id == shown)
            {
                return;
            }

            // Taken as done only once the game is asked: the picture may arrive in the menu, before the world's importer is there.
            if (Reload(hosts ? (id != 0 ? "the host's car picture arrived" : "the host has no car picture (or it is on its way)") : "back to this game's own car picture"))
            {
                hostsShown = hosts;
                shown = id;
            }
        }

        // The host: its picture found, numbered and offered (an empty list if it has none: then the others show none).
        private static void Offer()
        {
            if (!scanned)
            {
                scanned = true;
                string game = Path.GetDirectoryName(Application.dataPath) ?? ".";
                files.ScanFiles(new[] { Path.Combine(Path.Combine(game, PictureFolder), Picture) });
                log.Info(LogCategory.General, files.Count == 0 ? "This game has no car picture (Images/car.png): the others' \"Art\" paint shows none either." : "This game's car picture (Images/car.png) is offered to the others.");
            }

            if (!files.Ready)
            {
                files.HashSome(HashPerFrame);
                return;
            }

            if (!offered)
            {
                offered = true;
                current.Offer(files.Library);
            }
        }

        private static bool UsesHosts()
        {
            return current != null && network != null && network.Mode == NetworkMode.Client && HostWorld.PlaysHostWorld;
        }

        // The host's picture, if it offered one and it is here whole; 0 if not.
        private static ulong HostsPicture()
        {
            IList<SharedFile> list = current.Files;
            if (list == null || list.Count == 0 || !current.IsHere(list[0].Id))
            {
                return 0;
            }

            string path;
            return files.TryGetPath(list[0].Id, out path) ? list[0].Id : 0;
        }

        // The importer of the world: the mod's action after its address is built, once per machine.
        private static void Hook()
        {
            GameObject root = GameObject.Find(ImporterRoot);
            Transform at = root == null ? null : root.transform.Find(ImporterPath);
            importer = at == null ? null : DoorSync.FindMachine(at, ImporterMachine);

            // Unity's null check is also true for a destroyed object.
            if (importer == null || importer.Fsm == null || !importer.Fsm.Initialized || !Hooked.Add(importer.GetInstanceID()))
            {
                return;
            }

            FsmState[] states = importer.FsmStates;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                if (states[s].Name != LoadState)
                {
                    continue;
                }

                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    if (actions[a] is BuildString)
                    {
                        importer.FsmInject(LoadState, OnAddress, false, a + 1, false);
                        log.Info(LogCategory.General, "Car picture: the game's importer found; a guest's \"Art\" paint shows the host's picture.");
                        return;
                    }
                }
            }

            log.Warning(LogCategory.General, "Car picture: '" + ImporterRoot.Substring(1) + "/" + ImporterPath + " :: " + ImporterMachine + "' has no address to change; each game shows its own picture.");
        }

        // After the game built the address of its own picture: in a guest, the host's instead.
        private static void OnAddress()
        {
            if (Failed || importer == null || !UsesHosts())
            {
                return;
            }

            try
            {
                FsmString address = importer.FsmVariables.FindFsmString(AddressVariable);
                if (address == null)
                {
                    return;
                }

                string path;
                IList<SharedFile> list = current.Files;
                if (list == null || list.Count == 0 || !current.IsHere(list[0].Id) || !files.TryGetPath(list[0].Id, out path))
                {
                    path = NeutralPicture();
                }

                address.Value = "file:///" + Path.GetFullPath(path).Replace('\\', '/');
            }
            catch (Exception exception)
            {
                Failed = true;
                log.Error(LogCategory.General, "Showing the host's car picture failed; each game reads its own until it is closed. " + exception);
            }
        }

        // One grey pixel, which the game's detail map takes as no picture at all (what it shows without one): written once.
        private static string NeutralPicture()
        {
            string path = Path.Combine(cacheFolder, Neutral);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(cacheFolder);
                var pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                pixel.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
                pixel.Apply();
                File.WriteAllBytes(path, pixel.EncodeToPNG());
                UnityEngine.Object.Destroy(pixel);
            }

            return path;
        }

        // The game loads the picture again, as when a part is painted "Art".
        private static bool Reload(string why)
        {
            // Unity's null check is also true for a destroyed object.
            FsmBool activate = importer == null || !Hooked.Contains(importer.GetInstanceID()) ? null : importer.FsmVariables.FindFsmBool(ActivateVariable);
            if (activate == null)
            {
                return false;
            }

            activate.Value = true;
            log.Info(LogCategory.General, "Car picture: " + why + "; the game loads it again.");
            return true;
        }
    }
}
