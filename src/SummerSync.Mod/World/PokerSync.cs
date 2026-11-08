using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The video poker in Teimo's pub: the game of whoever plays it runs it, and its screen, lights and sounds are the
    /// same in every game (<see cref="PanelReplication"/>).
    ///
    /// What is assumed about the game, from the dumps of 2026-10-14 and the game with the test bridge
    /// (docs/GAME_NOTES.md, "El video póker"): the machine is "STORE/LOD/VideoPoker". Its buttons are
    /// "GameLogic/Buttons/&lt;name&gt;" (state machine "Use"): a click goes to "Button", which sends "ButtonPressed" to the
    /// game's own button, the object in its "LogicButton". The game is "GameLogic/Rami-Pokeri", made of state machines
    /// that switch each other on and off: a coin ("Pokeri-Buttons-InsertCoin") takes a mark of "PlayerMoney" and adds a
    /// credit ("Pokeri-Money", "Money_int"); the cards are five fixed objects whose materials and textures change, and
    /// a camera of its own films them onto the screen; a win is paid into "PlayerMoney". With no credit left the game
    /// goes back to its menu ("Pokeri-MainMenu", state "Menu"): the machine waits for someone.
    ///
    /// In a session the game runs only where it is played: in every other game its state machines are off and its
    /// buttons lead nowhere. A click on a machine nobody plays makes the clicker's game the one that runs it (the host
    /// decides who, if two click at once); a click anywhere else is made in that game. That game reads, about ten
    /// times a second, what each part of the machine shows (on or off, material, texture, text, a sound started) and
    /// the others show the same. Back at its menu with no credit, it lets go.
    ///
    /// The money inside the machine ("Cash" of its state machine "Data", what breaking it open gives) is drawn at random
    /// the first time a world has the machine and kept in the save from then on, so two games loaded from one save may
    /// have different sums until the host's game saves it. It is one: the host tells its own once its machine has
    /// loaded, a client takes it, and a change made in any game after that is told.
    /// </summary>
    internal sealed class PokerSync
    {
        private const int Panel = 0;
        private const string MachinePath = "STORE/LOD/VideoPoker";
        private const string GamePath = "GameLogic/Rami-Pokeri";
        private const string ButtonsPath = "GameLogic/Buttons/";
        private const string ClickedState = "Button";
        private const string PressEvent = "ButtonPressed";
        private const string MenuState = "Menu";
        private const double SearchInterval = 2.0;
        private const double ReadInterval = 0.1;
        private const double IdleSeconds = 2.0;
        private const string CashKey = "poker/cash";

        // The machine's buttons, in the order they travel.
        private static readonly string[] ButtonNames = { "InsertCoin", "Deal", "TakeWin", "Bet", "Double", "DoubleHigh", "DoubleLow", "Hold1", "Hold2", "Hold3", "Hold4", "Hold5" };

        // Parts that are data, not anything shown: the decks the cards are drawn from, and the machine's insides.
        private static readonly string[] NotShown = { "Pokeri-Deck-Unused", "Pokeri-DoublingDeck-Unused", "Pokeri-Deck-Used", "Pokeri-DoublingDeck-Used", "Disabled", "HookSlot", "Hatch", "Buttons" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Part> parts = new List<Part>();
        private readonly List<PlayMakerFSM> machines = new List<PlayMakerFSM>();
        private readonly List<bool> wereOn = new List<bool>();
        private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Texture> textures = new Dictionary<string, Texture>();
        private readonly char[] separator = { '|' };
        private PanelReplication current;
        private double nextSearch;
        private bool found;
        private bool searchReported;
        private bool assetsListed;

        private Button[] buttons;
        private PlayMakerFSM menu;
        private FsmInt credits;
        private FsmInt wins;
        private bool switchedOff;
        private bool working;
        private double idleSince = double.NaN;
        private double nextRead;
        private int pressesSent;
        private int pressesMade;
        private int partsShown;
        private FsmInt cash;
        private FsmBool loaded;
        private TallyReplication cashTallies;
        private int knownCash = int.MinValue;
        private int[] heardCash;

        public PokerSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            PanelReplication panels = localPlayer == null ? null : manager.Panels;
            if (panels != current)
            {
                if (current != null)
                {
                    current.PressWanted -= OnPressWanted;
                    current.ShowWanted -= OnShowWanted;
                    current.OperatorChanged -= OnOperatorChanged;
                }

                Release();
                current = panels;
                if (current != null)
                {
                    current.PressWanted += OnPressWanted;
                    current.ShowWanted += OnShowWanted;
                    current.OperatorChanged += OnOperatorChanged;
                }

                found = false;
                working = false;
                nextSearch = now;
            }

            if (current == null)
            {
                return;
            }

            if (!found && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            // Unity's null check is also true for a destroyed object.
            if (!found || menu == null)
            {
                return;
            }

            WatchCash();
            if (current.IsMine(Panel))
            {
                if (!working)
                {
                    Work(true);
                }

                if (now >= nextRead)
                {
                    nextRead = now + ReadInterval;
                    Read();
                }

                WatchIdle(now);
            }
            else
            {
                if (working || !switchedOff)
                {
                    Work(false);
                }

                WatchClicks();
            }
        }

        /// <summary>The machine here goes back to how the game had it.</summary>
        public void Release()
        {
            if (switchedOff)
            {
                for (int i = 0; i < machines.Count; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    if (machines[i] != null)
                    {
                        machines[i].enabled = wereOn[i];
                    }
                }
            }

            switchedOff = false;
            for (int i = 0; buttons != null && i < buttons.Length; i++)
            {
                if (buttons[i] != null && buttons[i].Target != null)
                {
                    buttons[i].Target.Value = buttons[i].Logic;
                }
            }
        }

        private void Search()
        {
            GameObject store = GameObject.Find("/STORE");
            Transform machine = store == null ? null : store.transform.Find(MachinePath.Substring("STORE/".Length));
            Transform game = machine == null ? null : machine.Find(GamePath);
            Transform menuAt = game == null ? null : game.Find("Pokeri-MainMenu");
            Transform moneyAt = game == null ? null : game.Find("Pokeri-Money");
            menu = menuAt == null ? null : DoorSync.FindMachine(menuAt, "Menu");
            PlayMakerFSM money = moneyAt == null ? null : DoorSync.FindMachine(moneyAt, "Money");
            credits = money == null ? null : money.FsmVariables.FindFsmInt("Money_int");
            PlayMakerFSM data = machine == null ? null : DoorSync.FindMachine(machine, "Data");
            cash = data == null ? null : data.FsmVariables.FindFsmInt("Cash");
            loaded = data == null ? null : data.FsmVariables.FindFsmBool("Loaded");
            wins = money == null ? null : money.FsmVariables.FindFsmInt("Wins_int");
            buttons = new Button[ButtonNames.Length];
            int buttonsFound = 0;
            for (int i = 0; machine != null && i < ButtonNames.Length; i++)
            {
                Transform at = machine.Find(ButtonsPath + ButtonNames[i]);
                PlayMakerFSM use = at == null ? null : DoorSync.FindMachine(at, "Use");
                FsmGameObject target = use == null ? null : use.FsmVariables.FindFsmGameObject("LogicButton");
                GameObject logic = target == null ? null : target.Value;
                PlayMakerFSM[] logicMachines = logic == null ? new PlayMakerFSM[0] : logic.GetComponents<PlayMakerFSM>();
                if (use != null && logicMachines.Length == 1)
                {
                    buttons[i] = new Button { Use = use, Target = target, Logic = logic, LogicMachine = logicMachines[0], LastState = use.ActiveStateName };
                    buttonsFound++;
                }
            }

            if (menu == null || credits == null || wins == null || buttonsFound != ButtonNames.Length || !DoorSync.HasState(menu, MenuState))
            {
                if (!searchReported)
                {
                    searchReported = true;
                    log.Warning(LogCategory.Fsm, "Video poker: '" + MachinePath + "' (its game, its menu, its " + ButtonNames.Length + " buttons) is not as this build expects, or the machine is no longer in the pub: each game's is its own.");
                }

                menu = null;
                return;
            }

            // The game's state machines, switched off where it does not run.
            machines.Clear();
            wereOn.Clear();
            machines.AddRange(game.GetComponentsInChildren<PlayMakerFSM>(true));
            for (int i = 0; i < machines.Count; i++)
            {
                wereOn.Add(machines[i].enabled);
            }

            // What is shown: every part of the machine but the decks and its insides, in the order of the scene.
            parts.Clear();
            List(machine, machine);
            uint shape = 2166136261;
            for (int i = 0; i < parts.Count; i++)
            {
                shape = Hash(shape, parts[i].Path);
            }

            current.Define(Panel, parts.Count, shape);
            current.RequestAll();
            found = true;
            log.Info(LogCategory.Fsm, "Video poker: shared; the game of whoever plays it runs it, and its " + parts.Count + " parts are shown in every game (shape "
                + shape.ToString("x8", CultureInfo.InvariantCulture) + ")" + (cash != null && loaded != null ? "; the money inside it is one." : "; the money inside it was not found and is each game's own."));

            // A game that is in the middle of being played when the session starts goes on being played here.
            if (!Idle() && manager.Mode == NetworkMode.Host)
            {
                current.Claim(Panel);
            }
        }

        // The money inside the machine: the host's once its machine has loaded it (from the save, or drawn), then whatever
        // any game makes of it. The machine loads it when its player first comes near: nothing is read or written before.
        private void WatchCash()
        {
            TallyReplication tallies = manager.Tallies;
            if (tallies != cashTallies)
            {
                if (cashTallies != null)
                {
                    cashTallies.StateWanted -= OnCashWanted;
                }

                cashTallies = tallies;
                if (tallies != null)
                {
                    tallies.StateWanted += OnCashWanted;
                }

                knownCash = int.MinValue;
                heardCash = null;
            }

            if (tallies == null || cash == null || loaded == null || !loaded.Value)
            {
                return;
            }

            if (heardCash != null)
            {
                int value = heardCash[0];
                heardCash = null;
                if (cash.Value != value)
                {
                    log.Info(LogCategory.Fsm, "Video poker: the money inside it is the session's, " + value + " (it was " + cash.Value + " here).");
                    cash.Value = value;
                }

                knownCash = value;
                return;
            }

            // A client tells only a change after it has the session's sum: what its world drew or loaded is not news.
            bool tell = manager.Mode == NetworkMode.Host ? cash.Value != knownCash : knownCash != int.MinValue && cash.Value != knownCash;
            if (tell)
            {
                knownCash = cash.Value;
                tallies.Set(CashKey, new[] { knownCash });
            }
        }

        private void OnCashWanted(string key, int[] values)
        {
            if (key == CashKey && values != null && values.Length >= 1)
            {
                heardCash = values;
            }
        }

        private void List(Transform machine, Transform at)
        {
            if (Array.IndexOf(NotShown, at.name) >= 0)
            {
                return;
            }

            var part = new Part
            {
                Path = PathOf(machine, at),
                Thing = at.gameObject,
                Renderer = at.GetComponent<Renderer>(),
                Text = at.GetComponent<TextMesh>(),
                Sound = at.GetComponent<AudioSource>(),
            };
            parts.Add(part);
            for (int i = 0; i < at.childCount; i++)
            {
                List(machine, at.GetChild(i));
            }
        }

        private static string PathOf(Transform machine, Transform at)
        {
            string path = at.name;
            for (Transform up = at.parent; up != null && at != machine && up != machine.parent; up = up.parent)
            {
                path = up.name + "/" + path;
            }

            return path;
        }

        private static uint Hash(uint hash, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                hash = (hash ^ text[i]) * 16777619;
            }

            return (hash ^ '\n') * 16777619;
        }

        // This game runs the machine, or shows another's: its state machines on or off, its buttons to the game or nowhere.
        private void Work(bool on)
        {
            working = on;
            if (on)
            {
                Release();
                for (int i = 0; i < parts.Count; i++)
                {
                    // What the others last showed is known: only changes from it are said.
                    parts[i].Known = null;
                }

                log.Info(LogCategory.Fsm, "Video poker: played here; this game runs it for everyone.");
                return;
            }

            if (!switchedOff)
            {
                for (int i = 0; i < machines.Count; i++)
                {
                    if (machines[i] != null)
                    {
                        wereOn[i] = machines[i].enabled;
                        machines[i].enabled = false;
                    }
                }

                switchedOff = true;
            }

            for (int i = 0; i < buttons.Length; i++)
            {
                // A click is made where the machine runs: here it goes to the button itself, which has no such machine.
                buttons[i].Target.Value = buttons[i].Use.gameObject;
                buttons[i].LastState = buttons[i].Use.ActiveStateName;
            }
        }

        // Not running it: a click here goes to whoever does, or makes this game the one that does.
        private void WatchClicks()
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                string state = button.Use.ActiveStateName;
                if (state == button.LastState)
                {
                    continue;
                }

                button.LastState = state;
                if (state == ClickedState)
                {
                    pressesSent++;
                    current.Press(Panel, i);
                }
            }
        }

        // Running it: a click of another player, made as if pressed here.
        private void OnPressWanted(int panel, int button)
        {
            if (panel != Panel || buttons == null || button < 0 || button >= buttons.Length || buttons[button] == null)
            {
                return;
            }

            if (!working)
            {
                Work(true);
            }

            pressesMade++;
            buttons[button].LogicMachine.SendEvent(PressEvent);
            log.Info(LogCategory.Fsm, "Video poker: '" + ButtonNames[button] + "' pressed while this game was not playing it; made here.");
        }

        private void OnOperatorChanged(int panel, byte player)
        {
            if (panel == Panel)
            {
                log.Info(LogCategory.Fsm, player == PanelWire.NoOperator ? "Video poker: nobody plays it now." : "Video poker: player " + player + " plays it now.");
            }
        }

        // With no credit and back at its menu for a moment, the machine is let go of.
        private void WatchIdle(double now)
        {
            if (!Idle())
            {
                idleSince = double.NaN;
                return;
            }

            if (double.IsNaN(idleSince))
            {
                idleSince = now;
            }
            else if (now - idleSince >= IdleSeconds)
            {
                idleSince = double.NaN;
                Read();
                current.Release(Panel);
            }
        }

        // Its own: the pub is switched off with its player far away, and that does not make the machine busy. A menu
        // that never started (the pub never on since the world loaded) is a machine nobody has played.
        private bool Idle()
        {
            string state = menu.ActiveStateName;
            return menu.gameObject.activeSelf && (state == MenuState || string.IsNullOrEmpty(state)) && credits.Value == 0 && wins.Value == 0;
        }

        // What each part shows now, said where it changed.
        private void Read()
        {
            for (int i = 0; i < parts.Count; i++)
            {
                Part part = parts[i];

                // Unity's null check is also true for a destroyed object.
                if (part.Thing == null)
                {
                    continue;
                }

                bool active = part.Thing.activeSelf;
                Material material = part.Renderer == null ? null : part.Renderer.sharedMaterial;
                Texture texture = material == null ? null : material.mainTexture;
                string text = part.Text == null ? null : part.Text.text;
                if (part.Sound != null)
                {
                    bool playing = part.Sound.isPlaying;
                    float time = playing ? part.Sound.time : 0f;
                    if (playing && (!part.WasPlaying || time + 0.05f < part.LastTime))
                    {
                        part.Plays++;
                    }

                    part.WasPlaying = playing;
                    part.LastTime = time;
                }

                if (part.Known != null && active == part.Active && material == part.Material && texture == part.Texture && text == part.LastText && part.Plays == part.ToldPlays)
                {
                    continue;
                }

                part.Active = active;
                part.Material = material;
                part.Texture = texture;
                part.LastText = text;
                part.ToldPlays = part.Plays;
                string value = (active ? "1" : "0") + "|" + (material == null ? string.Empty : material.name) + "|" + (texture == null ? string.Empty : texture.name)
                    + "|" + part.Plays.ToString(CultureInfo.InvariantCulture) + "|" + (text ?? string.Empty);
                if (value != part.Known)
                {
                    part.Known = value;
                    current.Show(Panel, i, value);
                    partsShown++;
                }
            }
        }

        // Another game's machine: a part shows this now.
        private void OnShowWanted(int panel, int slot, string value)
        {
            if (panel != Panel || !found || slot < 0 || slot >= parts.Count || value == null)
            {
                return;
            }

            Part part = parts[slot];

            // Unity's null check is also true for a destroyed object.
            if (part.Thing == null)
            {
                return;
            }

            string[] fields = value.Split(separator, 5);
            if (fields.Length < 5)
            {
                return;
            }

            part.Known = value;
            bool active = fields[0] == "1";
            if (part.Renderer != null && fields[1].Length > 0)
            {
                Material material = FindMaterial(fields[1]);
                if (material != null && part.Renderer.sharedMaterial != material)
                {
                    part.Renderer.sharedMaterial = material;
                }

                Material shown = part.Renderer.sharedMaterial;
                Texture texture = fields[2].Length > 0 ? FindTexture(fields[2]) : null;
                if (shown != null && texture != null && shown.mainTexture != texture)
                {
                    // As the game does it: on the material itself.
                    shown.mainTexture = texture;
                }
            }

            if (part.Text != null && part.Text.text != fields[4])
            {
                part.Text.text = fields[4];
            }

            int plays;
            bool replay = int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out plays) && plays != part.Plays;
            part.Plays = plays;
            if (part.Thing.activeSelf != active)
            {
                part.Thing.SetActive(active);
            }

            if (replay && active && part.Sound != null && part.Thing.activeInHierarchy)
            {
                part.Sound.Play();
            }
        }

        private Material FindMaterial(string name)
        {
            Material found;
            if (materials.TryGetValue(name, out found) && found != null)
            {
                return found;
            }

            ListAssets();
            return materials.TryGetValue(name, out found) ? found : null;
        }

        private Texture FindTexture(string name)
        {
            Texture found;
            if (textures.TryGetValue(name, out found) && found != null)
            {
                return found;
            }

            ListAssets();
            return textures.TryGetValue(name, out found) ? found : null;
        }

        // The game's materials and textures by name, once (and again if one is missing).
        private void ListAssets()
        {
            if (assetsListed)
            {
                return;
            }

            assetsListed = true;
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(Material));
            for (int i = 0; i < all.Length; i++)
            {
                var material = all[i] as Material;
                if (material != null && !materials.ContainsKey(material.name))
                {
                    materials[material.name] = material;
                }
            }

            all = Resources.FindObjectsOfTypeAll(typeof(Texture));
            for (int i = 0; i < all.Length; i++)
            {
                var texture = all[i] as Texture;
                if (texture != null && !textures.ContainsKey(texture.name))
                {
                    textures[texture.name] = texture;
                }
            }
        }

        /// <summary>One line for the overlay, or null before anything happened.</summary>
        public string Describe()
        {
            if (current == null || !found)
            {
                return null;
            }

            byte worker = current.Operator(Panel);
            return "Video poker: " + (worker == PanelWire.NoOperator ? "nobody plays" : worker == manager.Session.LocalPlayerId ? "played here" : "player " + worker + " plays")
                + ", " + pressesSent + " click(s) sent, " + pressesMade + " made here, " + partsShown + " part(s) shown";
        }

        private sealed class Button
        {
            public PlayMakerFSM Use;
            public FsmGameObject Target;
            public GameObject Logic;
            public PlayMakerFSM LogicMachine;
            public string LastState;
        }

        private sealed class Part
        {
            public string Path;
            public GameObject Thing;
            public Renderer Renderer;
            public TextMesh Text;
            public AudioSource Sound;
            public string Known;
            public bool Active;
            public Material Material;
            public Texture Texture;
            public string LastText;
            public int Plays;
            public int ToldPlays;
            public bool WasPlaying;
            public float LastTime;
        }
    }
}
