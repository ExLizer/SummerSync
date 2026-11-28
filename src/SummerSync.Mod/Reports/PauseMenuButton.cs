using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Unity.World;
using UnityEngine;

namespace SummerSync.Unity.Reports
{
    /// <summary>
    /// "REPORT A BUG" in the game's own Esc menu, under RESUME and the settings, above QUIT TO MENU.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "El menú de Esc"): the menu is "Systems/OptionsMenu", off until
    /// Esc, drawn by its own orthographic camera; its "Menu" has the buttons one under the other ("Btn_Resume", the settings
    /// in "Table 5", "Btn_Quit" in "Table 4"), each a label "GUITextLabel" with its shadow and a "Button" with a box and the
    /// machine "Button": on the mouse it grows ("Wait"), a click goes to "State 2", which for QUIT TO MENU switches on
    /// "Btn_ConfirmQuit", then "State 1" waits and goes back. The panel behind them is "BoxBG". The button is a copy of
    /// QUIT TO MENU with its table, put where QUIT was; QUIT, its table and the two "ARE YOU SURE?" boxes go one row down,
    /// and the panel grows by that row. The copy's "State 2" only tells the mod, and lets the machine go on only in a later
    /// frame with the mouse's button up (the game's own buttons do that by waiting; see <see cref="Menu.MenuColumn"/>).
    ///
    /// It is made the first time the menu shows in a world: before that its machines have never run, and touching a machine
    /// that never started can leave it dead (docs/GAME_NOTES.md).
    /// </summary>
    internal sealed class PauseMenuButton
    {
        public const string Label = "REPORT A BUG";

        private const string SystemsPath = "Systems";
        private const string MenuPath = "OptionsMenu";
        private const string ButtonsPath = "Menu";
        private const string Template = "Btn_Quit";
        private const string TemplateTable = "Table 4";
        private const string Panel = "BoxBG";
        private const string MachineHolder = "Button";
        private const string MachineName = "Button";
        private const string ClickState = "State 2";

        // One row of the menu, as the game spaces its tables: the settings' table ends 0.2 above QUIT's, whose own table is
        // 1.1 tall around it (level file: QUIT at -3.75, the settings' table from 0 to -3).
        private const float Row = 1.25f;

        // The confirmations that show under QUIT TO MENU and RESET TO DEFAULTS.
        private static readonly string[] Below = { "Btn_ConfirmQuit", "Btn_ConfirmReset" };

        private static bool clicked;

        private readonly Logger log;
        private readonly List<Collider> held = new List<Collider>();
        private GameObject menu;
        private bool built;
        private bool failed;
        private bool holding;

        public PauseMenuButton(Logger log)
        {
            this.log = log;
        }

        /// <summary>Whether the game's Esc menu is up.</summary>
        public bool Shown
        {
            get { return menu != null && menu.activeInHierarchy; }
        }

        /// <summary>Once per frame. True in the frame after REPORT A BUG was clicked.</summary>
        public bool Update(bool inWorld)
        {
            bool now = clicked;
            clicked = false;
            if (!inWorld)
            {
                menu = null;
                built = false;
                held.Clear();
                holding = false;
                return false;
            }

            if (menu == null && !failed)
            {
                GameObject systems = GameObject.Find(SystemsPath);
                Transform found = systems == null ? null : systems.transform.Find(MenuPath);
                menu = found == null ? null : found.gameObject;
            }

            if (!built && !failed && menu != null && menu.activeInHierarchy)
            {
                Build();
            }

            return now;
        }

        /// <summary>
        /// While a window of the mod's is open over the Esc menu, the menu's buttons take no clicks: they find the mouse by a
        /// ray from their camera, whatever is drawn over them.
        /// </summary>
        public void HoldClicks(bool hold)
        {
            if (hold == holding)
            {
                return;
            }

            holding = hold;
            if (hold)
            {
                if (menu == null)
                {
                    return;
                }

                Collider[] all = menu.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].enabled)
                    {
                        all[i].enabled = false;
                        held.Add(all[i]);
                    }
                }

                return;
            }

            for (int i = 0; i < held.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (held[i] != null)
                {
                    held[i].enabled = true;
                }
            }

            held.Clear();
        }

        private void Build()
        {
            built = true;
            Transform buttons = menu.transform.Find(ButtonsPath);
            Transform quit = buttons == null ? null : buttons.Find(Template);
            Transform table = buttons == null ? null : buttons.Find(TemplateTable);
            Transform panel = buttons == null ? null : buttons.Find(Panel);
            Transform resume = buttons == null ? null : buttons.Find("Btn_Resume");
            if (quit == null || table == null || panel == null || resume == null)
            {
                Fail("'" + MenuPath + "/" + ButtonsPath + "' does not have " + Template + ", " + TemplateTable + ", " + Panel + " and Btn_Resume");
                return;
            }

            float row = Row;
            var copy = (GameObject)Object.Instantiate(quit.gameObject);
            copy.name = "SummerSync Btn_Report";
            copy.transform.parent = buttons;
            copy.transform.localPosition = quit.localPosition;
            copy.transform.localRotation = quit.localRotation;
            copy.transform.localScale = quit.localScale;
            foreach (TextMesh text in copy.GetComponentsInChildren<TextMesh>(true))
            {
                text.text = Label;
            }

            Transform holder = copy.transform.Find(MachineHolder);
            PlayMakerFSM machine = holder == null ? null : DoorSync.FindMachine(holder, MachineName);
            FsmState state = machine == null ? null : machine.GetState(ClickState);
            if (state == null)
            {
                Object.Destroy(copy);
                Fail("the copy of " + Template + " has no machine '" + MachineName + "' with '" + ClickState + "'");
                return;
            }

            // Nothing of QUIT's own (it switches on its "ARE YOU SURE?"): only the mod's.
            state.Actions = new FsmStateAction[0];
            if (!machine.FsmInject(ClickState, new Clicked(), -1, false))
            {
                Object.Destroy(copy);
                Fail("the mod's action could not be put in the copy's '" + ClickState + "'");
                return;
            }

            var tableCopy = (GameObject)Object.Instantiate(table.gameObject);
            tableCopy.name = "SummerSync Table Report";
            tableCopy.transform.parent = buttons;
            tableCopy.transform.localPosition = table.localPosition;
            tableCopy.transform.localRotation = table.localRotation;
            tableCopy.transform.localScale = table.localScale;

            // QUIT and what shows under it, one row down; the panel one row longer, downwards.
            Down(quit, row);
            Down(table, row);
            for (int i = 0; i < Below.Length; i++)
            {
                Down(buttons.Find(Below[i]), row);
            }

            Vector3 scale = panel.localScale;
            panel.localScale = new Vector3(scale.x, scale.y + row, scale.z);
            Down(panel, row * 0.5f);
            log.Info(LogCategory.General, "Esc menu: '" + Label + "' added above QUIT TO MENU (a copy of it, one row of " + row + "; the panel " + panel.localScale.y + " tall).");
        }

        private static void Down(Transform part, float by)
        {
            if (part != null)
            {
                Vector3 at = part.localPosition;
                part.localPosition = new Vector3(at.x, at.y - by, at.z);
            }
        }

        private void Fail(string why)
        {
            failed = true;
            log.Warning(LogCategory.General, "Esc menu: " + why + "; '" + Label + "' is not added (the report's key, 'ss report' and '/report' in the chat still open it).");
        }

        // The mod's work in the copy's "State 2": it tells the mod at once, and lets the machine go on only in a later frame
        // with the mouse's button up, so that one click is one click.
        private sealed class Clicked : FsmStateAction
        {
            private int frame;

            public override void OnEnter()
            {
                frame = Time.frameCount;
                clicked = true;
            }

            public override void OnUpdate()
            {
                if (Time.frameCount > frame && !Input.GetMouseButton(0))
                {
                    Finish();
                }
            }
        }
    }
}
