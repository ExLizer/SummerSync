using System;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Imports the songs of the game's "Radio" folder by itself, once each time the game is opened, so that nobody
    /// has to press the main menu's "Import music files" button.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "La radio"): the button ("Interface/Songs/Button",
    /// machine "Button") marks its variable "SongImported" and switches on the machine "LoadSongs" of
    /// "Radio/Folk", which starts switched off in the menu's level file; that machine loads "Radio/track1.ogg" to
    /// "track200.ogg" and then the CDs. The radio's objects stay from the menu into the world. This does the same,
    /// in the menu, a moment after it loads, unless the player already did it or switched it off in the settings.
    /// </summary>
    internal static class MusicImport
    {
        private const double WaitSeconds = 1.5;

        private static bool done;
        private static double menuSince = -1.0;

        public static void Update(bool inMenu, bool wanted, double now, Logger log)
        {
            if (done)
            {
                return;
            }

            if (!inMenu)
            {
                menuSince = -1.0;
                return;
            }

            if (menuSince < 0.0)
            {
                menuSince = now;
                return;
            }

            if (now - menuSince < WaitSeconds)
            {
                return;
            }

            done = true;
            if (!wanted)
            {
                log.Info(LogCategory.General, "The music of the Radio folder is not imported by the mod: switched off in its settings.");
                return;
            }

            try
            {
                Import(log);
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.General, "Importing the music of the Radio folder failed; the menu's button still does it. " + exception.Message);
            }
        }

        private static void Import(Logger log)
        {
            GameObject folk = GameObject.Find("Radio/Folk");
            PlayMakerFSM loader = Machine(folk, "LoadSongs");
            if (loader == null)
            {
                log.Warning(LogCategory.General, "The radio's 'Radio/Folk' or its machine 'LoadSongs' was not found: the music is left for the menu's button.");
                return;
            }

            if (loader.enabled)
            {
                log.Info(LogCategory.General, "The music of the Radio folder is already being imported (the menu's button was pressed).");
                return;
            }

            // What the button does when pressed: it marks that the songs are imported, and switches the loading on.
            PlayMakerFSM button = Machine(GameObject.Find("Interface/Songs/Button"), "Button");
            FsmBool imported = button == null ? null : button.FsmVariables.FindFsmBool("SongImported");
            if (imported == null)
            {
                // Not the button's own: one of the game's global variables.
                imported = FsmVariables.GlobalVariables.FindFsmBool("SongImported");
            }

            if (imported != null)
            {
                imported.Value = true;
            }

            loader.enabled = true;
            log.Info(
                LogCategory.General,
                "Importing the music of the Radio folder, as the menu's button does" + (imported == null ? " (its own mark was not found)." : "."));
        }

        private static PlayMakerFSM Machine(GameObject owner, string name)
        {
            if (owner == null)
            {
                return null;
            }

            PlayMakerFSM[] machines = owner.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i] != null && machines[i].FsmName == name)
                {
                    return machines[i];
                }
            }

            return null;
        }
    }
}
