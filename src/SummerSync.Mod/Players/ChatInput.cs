using System;
using System.Reflection;
using Harmony;
using SummerSync.Core.Logging;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// Keeps the keyboard away from the game while the chat is being typed in, so that a "w" does not walk and an "f" does
    /// not pick anything up. The game reads keys two ways (docs/GAME_NOTES.md, "El teclado del juego"): through cInput,
    /// whose state of every key and axis it fills once a frame in <c>_CheckInputs</c>, and through PlayMaker's
    /// <c>GetKeyDown</c>, <c>GetKey</c> and <c>AnyKey</c>, which read Unity's input directly. While held, cInput's state is
    /// emptied right after it is filled, and those three actions see no key of the keyboard. The mouse still looks around.
    /// A key let go while held is not seen going up: nothing is seen held either.
    /// </summary>
    internal static class ChatInput
    {
        private static readonly string[] CInputState =
        {
            "_getKeyArray", "_getKeyDownArray", "_getKeyUpArray", "_getAxis", "_getAxisRaw", "_getAxisArray", "_getAxisArrayRaw",
        };

        private static bool tried;
        private static Array[] state;

        /// <summary>Whether the game's keyboard can be held; without it the chat can be read but not typed in.</summary>
        public static bool Ready { get; private set; }

        /// <summary>While true, the game sees no key of the keyboard.</summary>
        public static bool Holding { get; set; }

        /// <summary>Patches the game's input once per process. Safe to call every time.</summary>
        public static void Install(Logger log)
        {
            if (tried)
            {
                return;
            }

            tried = true;
            try
            {
                const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
                MethodInfo check = typeof(cInput).GetMethod("_CheckInputs", Instance);
                MethodInfo keyDown = typeof(HutongGames.PlayMaker.Actions.GetKeyDown).GetMethod("OnUpdate", Instance);
                MethodInfo keyEnter = typeof(HutongGames.PlayMaker.Actions.GetKey).GetMethod("OnEnter", Instance);
                MethodInfo keyUpdate = typeof(HutongGames.PlayMaker.Actions.GetKey).GetMethod("OnUpdate", Instance);
                MethodInfo anyKey = typeof(HutongGames.PlayMaker.Actions.AnyKey).GetMethod("OnUpdate", Instance);
                if (check == null || keyDown == null || keyEnter == null || keyUpdate == null || anyKey == null)
                {
                    throw new InvalidOperationException("a method of cInput or of PlayMaker's key actions is missing");
                }

                var arrays = new Array[CInputState.Length];
                for (int i = 0; i < CInputState.Length; i++)
                {
                    FieldInfo field = typeof(cInput).GetField(CInputState[i], Static);
                    arrays[i] = field == null ? null : field.GetValue(null) as Array;
                    if (arrays[i] == null)
                    {
                        throw new InvalidOperationException("cInput has no " + CInputState[i]);
                    }
                }

                state = arrays;
                HarmonyInstance harmony = HarmonyInstance.Create("summersync.chat");
                harmony.Patch(check, null, Hook("AfterCheckInputs"));
                harmony.Patch(keyDown, Hook("BeforeKeyDown"), null);
                harmony.Patch(keyEnter, null, Hook("AfterKey"));
                harmony.Patch(keyUpdate, null, Hook("AfterKey"));
                harmony.Patch(anyKey, Hook("BeforeAnyKey"), null);
                Ready = true;
                log.Info(LogCategory.Player, "Chat: the game's keyboard is held while the chat is typed in.");
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Player, "Chat: the game's input is not as this build expects (" + exception.Message + "); the chat can be read but not typed in.");
            }
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(ChatInput).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        }

        // Keys and buttons of a joystick come after the mouse's.
        private static bool IsKeyboard(KeyCode key)
        {
            return key != KeyCode.None && key < KeyCode.Mouse0;
        }

        // cInput's _CheckInputs: what it just read of every key and axis, forgotten.
        private static void AfterCheckInputs()
        {
            if (!Holding)
            {
                return;
            }

            for (int i = 0; i < state.Length; i++)
            {
                Array.Clear(state[i], 0, state[i].Length);
            }
        }

        // PlayMaker's GetKeyDown.OnUpdate: no event and a false result for a key of the keyboard.
        private static bool BeforeKeyDown(HutongGames.PlayMaker.Actions.GetKeyDown __instance)
        {
            if (!Holding || !IsKeyboard(__instance.key))
            {
                return true;
            }

            if (__instance.storeResult != null)
            {
                __instance.storeResult.Value = false;
            }

            return false;
        }

        // PlayMaker's GetKey.OnEnter and OnUpdate: a key of the keyboard is up.
        private static void AfterKey(HutongGames.PlayMaker.Actions.GetKey __instance)
        {
            if (Holding && IsKeyboard(__instance.key) && __instance.storeResult != null)
            {
                __instance.storeResult.Value = false;
            }
        }

        // PlayMaker's AnyKey.OnUpdate: nothing was pressed.
        private static bool BeforeAnyKey()
        {
            return !Holding;
        }
    }
}
