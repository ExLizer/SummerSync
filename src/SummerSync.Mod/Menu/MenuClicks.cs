using System.Collections.Generic;
using SummerSync.Core.Logging;
using UnityEngine;

namespace SummerSync.Unity.Menu
{
    /// <summary>
    /// While a window of the mod's is open in the main menu, the menu's own buttons do not take clicks.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Los botones del menú"): the menu's buttons react to the mouse by
    /// a ray from the main camera into their trigger boxes, every frame ("MousePickEvent", PlayMaker's mouse pick), whatever
    /// a window of Unity's GUI draws over them: a window cannot stop it by taking the click. So the menu's boxes are switched
    /// off while a window is open (each one that was on, to be switched on again when the window closes). Only in the main
    /// menu: in the world the same boxes are what things stand on.
    /// </summary>
    internal static class MenuClicks
    {
        private const float LookAgainSeconds = 0.5f;

        private static readonly List<Collider> Held = new List<Collider>();
        private static bool holding;
        private static float nextLook;

        /// <summary>Once per frame, in the main menu: whether a window of the mod's is open.</summary>
        public static void Hold(bool hold, Logger log)
        {
            if (!hold)
            {
                Release();
                return;
            }

            // Looked at again now and then: the game may switch a button on while the window is open ("Continue").
            if (holding && Time.realtimeSinceStartup < nextLook)
            {
                return;
            }

            bool first = !holding;
            holding = true;
            nextLook = Time.realtimeSinceStartup + LookAgainSeconds;
            Collider[] all = Object.FindObjectsOfType<Collider>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].enabled)
                {
                    all[i].enabled = false;
                    Held.Add(all[i]);
                }
            }

            if (first)
            {
                log.Info(LogCategory.General, "Main menu: " + Held.Count + " of its boxes switched off while a window of the mod's is open.");
            }
        }

        /// <summary>The menu's boxes as they were.</summary>
        public static void Release()
        {
            if (!holding)
            {
                return;
            }

            holding = false;
            for (int i = 0; i < Held.Count; i++)
            {
                if (Held[i] != null)
                {
                    Held[i].enabled = true;
                }
            }

            Held.Clear();
        }

        /// <summary>Another scene: what was held is gone with the menu.</summary>
        public static void Forget()
        {
            holding = false;
            Held.Clear();
        }
    }
}
