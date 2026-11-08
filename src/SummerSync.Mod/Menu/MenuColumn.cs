using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Unity.World;
using UnityEngine;

namespace SummerSync.Unity.Menu
{
    /// <summary>
    /// The main menu's column with buttons of the mod's, made from the game's own. Without a lobby it
    /// reads CONTINUE, NEW GAME, LOAD SAVE, HOST, CREDITS, QUIT; while this game hosts a lobby, ENTER GAME (the game's own
    /// CONTINUE, in green), INVITE, LOBBY SETTINGS, CLOSE LOBBY, CREDITS, QUIT; while it is a guest in one, ENTER GAME (once
    /// the host's world is here), LEAVE LOBBY, CREDITS, QUIT.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Los botones del menú"): the column is "Interface/Buttons", tilted,
    /// one button under the other at the same step; each button is a trigger box with its words as a text mesh ("GFX", and
    /// "GFX/GFXShadow" under it, the colour of the words in the text mesh: QUIT's red is that, on the same material as the
    /// others) and a machine "SetSize" that shrinks the words and plays a sound while the mouse is over it, and on a click goes
    /// to the state that does the button's work. "NEW GAME"'s is "State 1": the sound of buying, then the licence. A copy of
    /// "NEW GAME" is made for each new button, its words changed, and its "State 1" keeps only the sound and then does the
    /// mod's work: the game's own look, sound and feel, nothing of the licence. ENTER GAME is the game's CONTINUE itself,
    /// with other words and colour while the lobby is open: it loads the world in use the game's way, and the game shows it
    /// only when that world has a save.
    ///
    /// "State 1" goes back to "Mouse" when it is done, and "Mouse" and "Action" look at the mouse as they are entered: a
    /// state that ends in the frame of the click finds the mouse over the button and its click still down, and clicks again,
    /// a thousand times in that frame with a sound each, until PlayMaker stops the machine for good ("Loop count exceeded",
    /// seen on 2026-10-16: very loud, then dead). The game's NEW GAME never comes back that way (it hides the
    /// menu). So the mod's work is an action that ends only in a later frame, once the mouse's button is up.
    /// </summary>
    internal sealed class MenuColumn
    {
        public const int LoadSave = 1;
        public const int Host = 2;
        public const int Invite = 3;
        public const int LobbySettings = 4;
        public const int CloseLobby = 5;
        public const int LeaveLobby = 6;

        private const string ButtonsPath = "Interface/Buttons";
        private const string InterfacePath = "Interface";
        private const string Template = "ButtonNewgame";
        private const string ClickState = "State 1";
        private const string KeptAction = "PlaySound";
        private const string EnterGame = "ENTER GAME";

        // Top to bottom; the ones that show stack up from the last, with no gap.
        private const int Continue = 0;
        private const int NewGame = 1;
        private const int Credits = 8;
        private const int Quit = 9;

        private static readonly Color EnterGreen = new Color(0.3f, 0.86f, 0.22f, 1f);
        private static int clicked;

        private readonly Logger log;
        private readonly Transform[] column = new Transform[10];
        private readonly Vector3[] corners = new Vector3[8];
        private GameObject buttons;
        private GameObject menuInterface;
        private Vector3 step;
        private Vector3 bottom;
        private bool built;
        private bool failed;
        private int fitFrames;

        // How the copies and CONTINUE were before their words changed: the width of the words and the box.
        private float templateWidth;
        private Vector3 templateSize;
        private Vector3 templateCenter;
        private float continueWidth;
        private Vector3 continueSize;
        private Vector3 continueCenter;
        private TextMesh[] continueWords;
        private string[] continueTexts;
        private Color continueColor;
        private bool entering;

        public MenuColumn(Logger log)
        {
            this.log = log;
        }

        /// <summary>The words of the game's menu (the font of its buttons), once seen; null before.</summary>
        public static Font GameFont { get; private set; }

        /// <summary>Whether the menu's own column shows (not the licence or the credits over it). True when not known.</summary>
        public static bool InterfaceShown { get; private set; }

        /// <summary>Whether the column is the lobby's: this game hosts a session or is a guest in one, in the main menu.</summary>
        public static bool LobbyMode { get; private set; }

        /// <summary>Whether this game is in the lobby as a guest (not its host).</summary>
        public static bool GuestLobby { get; private set; }

        /// <summary>Where the words of the buttons that show are on the screen, in GUI coordinates; empty when not known.</summary>
        public static Rect ColumnArea { get; private set; }

        /// <summary>
        /// Once per frame. <paramref name="guest"/>: this game is in a session as a guest, or plays in a host's world, where
        /// LOAD SAVE and HOST mean nothing; <paramref name="hosting"/>: it hosts a session. <paramref name="clickedNow"/> is the
        /// button clicked since the last frame (0 for none).
        /// </summary>
        public void Update(bool inMenu, bool guest, bool hosting, out int clickedNow)
        {
            clickedNow = clicked;
            clicked = 0;
            if (!inMenu)
            {
                built = false;
                buttons = null;
                menuInterface = null;
                InterfaceShown = false;
                LobbyMode = false;
                GuestLobby = false;
                ColumnArea = new Rect();
                return;
            }

            if (menuInterface == null)
            {
                menuInterface = GameObject.Find(InterfacePath);
            }

            if (!built && !failed)
            {
                Build();
            }

            InterfaceShown = menuInterface == null || menuInterface.activeInHierarchy;
            if (!built)
            {
                LobbyMode = false;
                GuestLobby = false;
                return;
            }

            bool host = hosting && !guest;
            bool lobby = host || guest;
            LobbyMode = lobby;
            GuestLobby = guest;
            Show(column[2], !lobby);
            Show(column[3], !lobby);
            for (int i = 4; i <= 6; i++)
            {
                Show(column[i], host);
            }

            Show(column[7], guest);

            Enter(lobby);
            Stack();
            if (fitFrames > 0 && --fitFrames == 0)
            {
                for (int i = 2; i <= 7; i++)
                {
                    Fit(column[i], templateWidth, templateSize, templateCenter);
                }

                if (entering)
                {
                    Fit(column[Continue], continueWidth, continueSize, continueCenter);
                }
            }

            Measure();
        }

        private void Build()
        {
            buttons = GameObject.Find(ButtonsPath);
            menuInterface = GameObject.Find(InterfacePath);
            if (buttons == null)
            {
                // The menu's column shows only when the menu has started; looked for again next frame.
                return;
            }

            Transform template = buttons.transform.Find(Template);
            string[] game = { "ButtonContinue", "ButtonNewgame", "ButtonCredits", "ButtonQuit" };
            int[] places = { Continue, NewGame, Credits, Quit };
            for (int i = 0; i < game.Length; i++)
            {
                column[places[i]] = buttons.transform.Find(game[i]);
                if (column[places[i]] == null)
                {
                    Fail("'" + ButtonsPath + "/" + game[i] + "' is not there");
                    return;
                }
            }

            TextMesh words = template == null ? null : template.GetComponentInChildren<TextMesh>();
            if (template == null || words == null || !Measure(template, out templateWidth, out templateSize, out templateCenter)
                || !Measure(column[Continue], out continueWidth, out continueSize, out continueCenter))
            {
                Fail("'" + ButtonsPath + "/" + Template + "' or 'ButtonContinue' is not as expected (words, box)");
                return;
            }

            GameFont = words.font;
            string[] names = { "LoadSave", "Host", "Invite", "LobbySettings", "CloseLobby", "LeaveLobby" };
            string[] labels = { "LOAD SAVE", "HOST", "INVITE", "LOBBY SETTINGS", "CLOSE LOBBY", "LEAVE LOBBY" };
            for (int i = 0; i < names.Length; i++)
            {
                GameObject made = Make(template, "SummerSync " + names[i], labels[i], i + 1);
                if (made == null)
                {
                    for (int j = 2; j < 2 + i; j++)
                    {
                        UnityEngine.Object.Destroy(column[j].gameObject);
                        column[j] = null;
                    }

                    Fail("the copies of '" + Template + "' could not be made to do the mod's work");
                    return;
                }

                made.SetActive(false);
                column[2 + i] = made.transform;
            }

            continueWords = column[Continue].GetComponentsInChildren<TextMesh>(true);
            continueTexts = new string[continueWords.Length];
            for (int i = 0; i < continueWords.Length; i++)
            {
                continueTexts[i] = continueWords[i].text;
            }

            continueColor = continueWords.Length > 0 ? continueWords[0].color : Color.white;

            // One step between buttons, as the game has them; the column grows upwards from QUIT.
            step = (column[Continue].localPosition - column[Quit].localPosition) / 3f;
            bottom = column[Quit].localPosition;
            built = true;
            Stack();
            fitFrames = 2;
            log.Info(
                LogCategory.General,
                "Main menu: the mod's buttons added to the column (a step of " + step + "; words in '" + (GameFont != null ? GameFont.name : "no font")
                + "', " + (GameFont != null && GameFont.dynamic ? "dynamic" : "fixed") + ").");
        }

        // A copy of the template that says other words and, when clicked, only makes its sound and tells the mod.
        private GameObject Make(Transform template, string name, string label, int which)
        {
            var copy = (GameObject)UnityEngine.Object.Instantiate(template.gameObject);
            copy.name = name;
            copy.transform.parent = template.parent;
            copy.transform.localPosition = template.localPosition;
            copy.transform.localRotation = template.localRotation;
            copy.transform.localScale = template.localScale;
            foreach (TextMesh text in copy.GetComponentsInChildren<TextMesh>(true))
            {
                text.text = label;
            }

            PlayMakerFSM machine = DoorSync.FindMachine(copy.transform, "SetSize");
            FsmState state = machine == null ? null : machine.GetState(ClickState);
            if (state == null)
            {
                UnityEngine.Object.Destroy(copy);
                return null;
            }

            var kept = new List<FsmStateAction>();
            FsmStateAction[] actions = state.Actions;
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i] != null && actions[i].GetType().Name == KeptAction)
                {
                    kept.Add(actions[i]);
                }
            }

            state.Actions = kept.ToArray();
            if (!machine.FsmInject(ClickState, new Clicked(which), -1, false))
            {
                UnityEngine.Object.Destroy(copy);
                return null;
            }

            return copy;
        }

        // CONTINUE as ENTER GAME while the lobby is open, and as the game has it otherwise.
        private void Enter(bool lobby)
        {
            if (lobby == entering)
            {
                return;
            }

            entering = lobby;
            for (int i = 0; i < continueWords.Length; i++)
            {
                if (continueWords[i] == null)
                {
                    continue;
                }

                continueWords[i].text = lobby ? EnterGame : continueTexts[i];

                // The shadow keeps its colour: only the words themselves turn green.
                if (continueWords[i].transform.parent == column[Continue])
                {
                    continueWords[i].color = lobby ? EnterGreen : continueColor;
                }
            }

            if (lobby)
            {
                fitFrames = 2;
            }
            else
            {
                var box = column[Continue].GetComponent<BoxCollider>();
                if (box != null)
                {
                    box.size = continueSize;
                    box.center = continueCenter;
                }
            }
        }

        private static bool Measure(Transform button, out float width, out Vector3 size, out Vector3 center)
        {
            Transform words = button.Find("GFX");
            Renderer renderer = words == null ? null : words.GetComponent<Renderer>();
            var box = button.GetComponent<BoxCollider>();
            width = renderer == null ? 0f : renderer.bounds.size.x;
            size = box == null ? Vector3.zero : box.size;
            center = box == null ? Vector3.zero : box.center;
            return box != null && width > 0f;
        }

        // The box the mouse finds the button by, as wide as its words, from what it was for the words it was made for.
        private static void Fit(Transform button, float width, Vector3 size, Vector3 center)
        {
            Transform words = button == null ? null : button.Find("GFX");
            var box = button == null ? null : button.GetComponent<BoxCollider>();
            Renderer ours = words == null ? null : words.GetComponent<Renderer>();
            if (box == null || ours == null || width <= 0f || ours.bounds.size.x <= 0f)
            {
                return;
            }

            // The words end at the same place on the right (the game's anchor): the box keeps its right side.
            float ratio = ours.bounds.size.x / width;
            float right = center.x + (size.x * 0.5f);
            size.x *= ratio;
            center.x = right - (size.x * 0.5f);
            box.size = size;
            box.center = center;
        }

        // The buttons that show, one under the other with no gap: the game hides "Continue" without a save (and, for a
        // guest, until the host's world is here), a guest has no "New game", and the lobby has its own.
        private void Stack()
        {
            int place = 0;
            for (int i = column.Length - 1; i >= 0; i--)
            {
                Transform button = column[i];
                if (button == null || !button.gameObject.activeSelf)
                {
                    continue;
                }

                Vector3 wanted = bottom + (step * place);
                if (button.localPosition != wanted)
                {
                    button.localPosition = wanted;
                }

                place++;
            }
        }

        // Where the words of the buttons that show are on the screen, for what is drawn next to the column.
        private void Measure()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                ColumnArea = new Rect();
                return;
            }

            float left = float.MaxValue;
            float right = float.MinValue;
            float top = float.MaxValue;
            float under = float.MinValue;
            for (int i = 0; i < column.Length; i++)
            {
                Transform button = column[i];
                Transform words = button == null || !button.gameObject.activeInHierarchy ? null : button.Find("GFX");
                Renderer renderer = words == null ? null : words.GetComponent<Renderer>();
                if (renderer == null)
                {
                    continue;
                }

                Bounds bounds = renderer.bounds;
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                corners[0] = new Vector3(min.x, min.y, min.z);
                corners[1] = new Vector3(max.x, min.y, min.z);
                corners[2] = new Vector3(min.x, max.y, min.z);
                corners[3] = new Vector3(max.x, max.y, min.z);
                corners[4] = new Vector3(min.x, min.y, max.z);
                corners[5] = new Vector3(max.x, min.y, max.z);
                corners[6] = new Vector3(min.x, max.y, max.z);
                corners[7] = new Vector3(max.x, max.y, max.z);
                for (int c = 0; c < corners.Length; c++)
                {
                    Vector3 screen = camera.WorldToScreenPoint(corners[c]);
                    float y = Screen.height - screen.y;
                    left = Mathf.Min(left, screen.x);
                    right = Mathf.Max(right, screen.x);
                    top = Mathf.Min(top, y);
                    under = Mathf.Max(under, y);
                }
            }

            ColumnArea = left <= right ? new Rect(left, top, right - left, under - top) : new Rect();
        }

        private static void Show(Transform button, bool shown)
        {
            if (button != null && button.gameObject.activeSelf != shown)
            {
                button.gameObject.SetActive(shown);
            }
        }

        private void Fail(string why)
        {
            failed = true;
            log.Warning(LogCategory.General, "Main menu: " + why + "; the mod's buttons are not added (the console's 'ss worlds' still does what LOAD SAVE and HOST do).");
        }

        // The mod's work in a button's "State 1": it tells the mod at once, and lets the machine go back to "Mouse" only in a
        // later frame with the mouse's button up, so that one click is one click.
        private sealed class Clicked : FsmStateAction
        {
            private readonly int which;
            private int frame;

            public Clicked(int which)
            {
                this.which = which;
            }

            public override void OnEnter()
            {
                frame = Time.frameCount;
                clicked = which;
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
