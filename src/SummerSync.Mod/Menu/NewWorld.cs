using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Unity.World;
using UnityEngine;

namespace SummerSync.Unity.Menu
{
    /// <summary>
    /// A new world from "HOST", without the game's introduction: the game's own licence, in a new folder of the mod's,
    /// whose "BEGIN" goes straight into the world.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "La licencia"): "NEW GAME"'s "State 1" (machine "SetSize") shows the
    /// licence ("Licence") and hides the menu ("Interface", "Interface/Credits"); on the licence, "BEGIN" deletes the world
    /// files of the folder Easy Save uses, writes the player's names, sets the globals of a new game and loads the scene
    /// "Intro" ("Licence/Buttons/ButtonBegin", "SetSize", "State 1", a "LoadLevel"); at the end of the intro the game loads
    /// "GAME". "CANCEL" shows the menu again. Here the folder is a new, empty one (so that nothing is deleted), the licence is
    /// shown as "NEW GAME" shows it, and its "LoadLevel" loads "GAME". Left with "CANCEL", the folder goes and the licence is
    /// as the game had it.
    /// </summary>
    internal sealed class NewWorld
    {
        private const string NewGamePath = "Interface/Buttons/ButtonNewgame";
        private const string Machine = "SetSize";
        private const string ShowState = "State 1";
        private const string BeginPath = "Buttons/ButtonBegin";
        private const string LoadState = "State 1";
        private const string WarningPath = "Newgame";
        private const string WorldLevel = "GAME";
        private const string WarningEnglish = "NEW GAME: YOUR OTHER SAVES ARE NOT TOUCHED";
        private const string WarningSpanish = "PARTIDA NUEVA: TUS OTRAS PARTIDAS NO SE TOCAN";

        private readonly Logger log;
        private SavedWorld world;
        private SavedWorld before;
        private GameObject licence;
        private GameObject menu;
        private FsmString level;
        private string levelBefore;
        private readonly List<KeyValuePair<TextMesh, string>> warnings = new List<KeyValuePair<TextMesh, string>>();
        private bool warningSpanish;

        public NewWorld(Logger log)
        {
            this.log = log;
        }

        /// <summary>Whether the licence of a new world is up.</summary>
        public bool Active
        {
            get { return world != null; }
        }

        /// <summary>
        /// The licence for a new world, over the menu. <paramref name="previous"/> is the world to go back to if it is left
        /// (null: the game's own save).
        /// </summary>
        public bool Begin(SavedWorld previous, out string problem)
        {
            problem = null;
            if (world != null)
            {
                return true;
            }

            var shows = new List<KeyValuePair<GameObject, bool>>();
            if (!FindLicence(shows, out problem))
            {
                log.Warning(LogCategory.General, "New world: " + problem + "; no licence shown.");
                problem = Texts.Pick("the game's licence was not found", "no se encontró la licencia del juego");
                return false;
            }

            SavedWorld made = SaveWorlds.Create(out problem);
            if (made == null)
            {
                return false;
            }

            if (!SaveWorlds.Use(made, out problem))
            {
                SaveWorlds.Abandon(made);
                return false;
            }

            world = made;
            before = previous;
            SaveWorlds.Pinned = true;
            levelBefore = level.Value;
            level.Value = WorldLevel;
            warnings.Clear();
            warningSpanish = Texts.Spanish;
            Transform warning = licence.transform.Find(WarningPath);
            if (warning != null)
            {
                foreach (TextMesh text in warning.GetComponentsInChildren<TextMesh>(true))
                {
                    warnings.Add(new KeyValuePair<TextMesh, string>(text, text.text));
                    text.text = Texts.Pick(WarningEnglish, WarningSpanish);
                }
            }

            for (int i = 0; i < shows.Count; i++)
            {
                shows[i].Key.SetActive(shows[i].Value);
            }

            log.Info(LogCategory.General, "New world: the game's licence is up for '" + made.Folder + "'; its BEGIN loads '" + WorldLevel + "' instead of '" + levelBefore + "'.");
            return true;
        }

        /// <summary>Once per frame: a licence left with "CANCEL" leaves no world behind; one begun is a world.</summary>
        public void Update(bool inMenu)
        {
            if (world == null)
            {
                return;
            }

            if (!inMenu)
            {
                log.Info(LogCategory.General, "New world: begun, in '" + world.Folder + "'.");
                Clear();
                return;
            }

            if (warningSpanish != Texts.Spanish)
            {
                warningSpanish = Texts.Spanish;
                for (int i = 0; i < warnings.Count; i++)
                {
                    if (warnings[i].Key != null)
                    {
                        warnings[i].Key.text = Texts.Pick(WarningEnglish, WarningSpanish);
                    }
                }
            }

            if (licence != null && !licence.activeInHierarchy && menu != null && menu.activeInHierarchy)
            {
                log.Info(LogCategory.General, "New world: the licence was left; the empty world goes.");
                if (level != null)
                {
                    level.Value = levelBefore;
                }

                for (int i = 0; i < warnings.Count; i++)
                {
                    if (warnings[i].Key != null)
                    {
                        warnings[i].Key.text = warnings[i].Value;
                    }
                }

                SavedWorld left = world;
                SavedWorld back = before;
                Clear();
                SaveWorlds.Abandon(left);
                string problem;
                if (back != null && !SaveWorlds.Use(back, out problem))
                {
                    log.Warning(LogCategory.General, "New world: the world before it could not be used again (" + problem + "); the game's own save is.");
                }
            }
        }

        private void Clear()
        {
            SaveWorlds.Pinned = false;
            world = null;
            before = null;
            licence = null;
            menu = null;
            level = null;
            warnings.Clear();
        }

        // What "NEW GAME" shows and hides, from its own actions, and the licence's "LoadLevel".
        private bool FindLicence(List<KeyValuePair<GameObject, bool>> shows, out string problem)
        {
            problem = null;
            GameObject buttons = GameObject.Find("Interface/Buttons");
            Transform newGame = buttons == null ? null : buttons.transform.Find("ButtonNewgame");
            PlayMakerFSM machine = newGame == null ? null : DoorSync.FindMachine(newGame, Machine);
            FsmState state = machine == null ? null : machine.GetState(ShowState);
            if (state == null)
            {
                problem = "'" + NewGamePath + " :: " + Machine + "' has no '" + ShowState + "'";
                return false;
            }

            menu = GameObject.Find("Interface");
            licence = null;
            FsmStateAction[] actions = state.Actions;
            for (int i = 0; i < actions.Length; i++)
            {
                FsmStateAction action = actions[i];
                if (action == null || action.GetType().Name != "ActivateGameObject")
                {
                    continue;
                }

                var owner = Field(action, "gameObject") as FsmOwnerDefault;
                var on = Field(action, "activate") as FsmBool;
                GameObject target = owner == null || owner.OwnerOption != OwnerDefaultOption.SpecifyGameObject || owner.GameObject == null ? null : owner.GameObject.Value;
                if (target == null || on == null)
                {
                    continue;
                }

                shows.Add(new KeyValuePair<GameObject, bool>(target, on.Value));
                if (on.Value && licence == null)
                {
                    licence = target;
                }
            }

            if (licence == null || menu == null)
            {
                problem = "'" + NewGamePath + "' does not show a licence";
                return false;
            }

            Transform begin = licence.transform.Find(BeginPath);
            PlayMakerFSM beginMachine = begin == null ? null : DoorSync.FindMachine(begin, Machine);
            FsmState load = beginMachine == null ? null : beginMachine.GetState(LoadState);
            level = null;
            FsmStateAction[] loads = load == null ? new FsmStateAction[0] : load.Actions;
            for (int i = 0; i < loads.Length && level == null; i++)
            {
                if (loads[i] != null && loads[i].GetType().Name == "LoadLevel")
                {
                    level = Field(loads[i], "levelName") as FsmString;
                }
            }

            if (level == null)
            {
                problem = "the licence's '" + BeginPath + " :: " + Machine + " :: " + LoadState + "' has no level to load";
                return false;
            }

            return true;
        }

        private static object Field(object action, string name)
        {
            FieldInfo field = action.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(action);
        }
    }
}
