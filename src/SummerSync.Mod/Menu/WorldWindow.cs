using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using SummerSync.Unity.Net;
using SummerSync.Unity.World;
using UnityEngine;

namespace SummerSync.Unity.Menu
{
    /// <summary>
    /// The main menu's windows for saved worlds, and the bar of a host in the menu.
    ///
    /// "LOAD SAVE": a card for each world (<see cref="SaveWorlds"/>), with the photo of its Satsuma, the player's name, the day
    /// and money of its save and when it was saved; a click loads it, as "Continue" does. Each card has its copies (the last
    /// three, to go back to one) and, for a world of the mod's, a way to delete it.
    ///
    /// "HOST": the same cards and "+ PARTIDA NUEVA" first; with one chosen, "Hostear" opens a Steam lobby (unless this game
    /// already hosts a session), offers the invite of Steam, and makes that world the one the guests get. A new world is the
    /// game's own licence, with its intro skipped (<see cref="NewWorld"/>). While hosting in the menu, a bar says which world
    /// it is, and has "Invitar amigos", "Jugar", "Cambiar partida" and "Cerrar lobby".
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Los botones del menú"): "Continue" is
    /// "Interface/Buttons/ButtonContinue", whose machine "SetSize" loads the world from "Reset globals 2" on (the globals of
    /// the player, the save's permanent death, Steam, then "GAME"). Loading a world is sending that machine there, once the
    /// menu's own check has shown the button for the folder chosen.
    /// </summary>
    internal sealed class WorldWindow
    {
        private const string ContinuePath = "Interface/Buttons/ButtonContinue";
        private const string ContinueMachine = "SetSize";
        private const string ContinueState = "Reset globals 2";
        private const string ContinueEvent = "SUMMERSYNC LOAD WORLD";
        private const double ContinueWaitSeconds = 4.0;
        private const double InviteWaitSeconds = 30.0;

        private readonly Logger log;
        private readonly NetworkController network;
        private readonly NewWorld creating;
        private readonly Dictionary<string, Texture2D> photos = new Dictionary<string, Texture2D>();
        private Mode mode;
        private Panel panel;
        private List<SavedWorld> worlds = new List<SavedWorld>();
        private int selected = -1;
        private SavedWorld focus;
        private List<WorldCopy> copies = new List<WorldCopy>();
        private ulong focusHash;
        private WorldCopy restoring;
        private string messageEnglish;
        private string messageSpanish;
        private bool messageBad;
        private Vector2 scroll;
        private double now;
        private bool continuing;
        private double continueUntil;
        private bool inviteWanted;
        private double inviteUntil;
        private bool inMenu;
        private float stylesFor;
        private Texture2D panelTexture;
        private Texture2D cardTexture;
        private Texture2D cardHotTexture;
        private Texture2D chosenTexture;
        private Texture2D greenTexture;
        private Texture2D redTexture;
        private Texture2D shadeTexture;
        private GUIStyle panelStyle;
        private GUIStyle titleStyle;
        private GUIStyle titleShadowStyle;
        private GUIStyle nameStyle;
        private GUIStyle lineStyle;
        private GUIStyle smallStyle;
        private GUIStyle tagStyle;
        private GUIStyle buttonStyle;
        private GUIStyle chooseStyle;
        private GUIStyle dangerStyle;
        private GUIStyle smallButtonStyle;
        private GUIStyle messageStyle;
        private GUIStyle plusStyle;
        private GUIStyle roundedStyle;
        private GUIStyle lobbyTitleStyle;
        private GUIStyle lobbyCountStyle;
        private GUIStyle messageLeftStyle;
        private Texture2D roundedTexture;
        private string statusShown;
        private GUIContent statusContent;
        private float statusHeight;

        public WorldWindow(Logger log, NetworkController network)
        {
            this.log = log;
            this.network = network;
            creating = new NewWorld(log);
        }

        private enum Mode
        {
            Closed,
            Load,
            Host,
        }

        private enum Panel
        {
            Cards,
            Copies,
            Delete,
            Restore,
        }

        /// <summary>Whether the lobby's box was drawn in the last frame: it says what the host's banner would.</summary>
        public static bool LobbyShown { get; private set; }

        /// <summary>Whether a window is open (the menu's buttons are held meanwhile).</summary>
        public bool IsOpen
        {
            get { return mode != Mode.Closed; }
        }

        /// <summary>Whether the licence of a new world is up.</summary>
        public bool Creating
        {
            get { return creating.Active; }
        }

        public void OpenLoad()
        {
            Open(Mode.Load);
        }

        public void OpenHost()
        {
            Open(Mode.Host);
        }

        public void Close()
        {
            if (mode == Mode.Closed)
            {
                return;
            }

            mode = Mode.Closed;
            panel = Panel.Cards;
            focus = null;
            restoring = null;
            Hush();
            ClearPhotos();
        }

        /// <summary>Once per frame.</summary>
        public void Update(double clock, bool menu)
        {
            now = clock;
            inMenu = menu;
            creating.Update(menu);
            if (!menu)
            {
                Close();
                continuing = false;
                inviteWanted = false;
                return;
            }

            if (continuing)
            {
                TryContinue();
            }

            if (inviteWanted)
            {
                if (network.SteamLobbyReady)
                {
                    inviteWanted = false;
                    network.InviteFriend();
                }
                else if (now >= inviteUntil || network.Manager.Mode != NetworkMode.Host)
                {
                    inviteWanted = false;
                }
            }
        }

        // ---- Opening and choosing ----

        private void Open(Mode which)
        {
            mode = which;
            panel = Panel.Cards;
            Hush();
            scroll = Vector2.zero;
            Refresh();
            selected = -1;
            if (which == Mode.Host)
            {
                // The world in use is chosen already when hosting it.
                for (int i = 0; i < worlds.Count && network.Manager.Mode == NetworkMode.Host; i++)
                {
                    if (SaveWorlds.IsCurrent(worlds[i]))
                    {
                        selected = i + 1;
                    }
                }
            }

            log.Info(LogCategory.General, "Main menu: '" + (which == Mode.Load ? "LOAD SAVE" : "HOST") + "' open with " + worlds.Count + " saved world(s).");
        }

        private void Refresh()
        {
            try
            {
                worlds = SaveWorlds.List();
            }
            catch (Exception exception)
            {
                worlds = new List<SavedWorld>();
                Say("The saves could not be read: " + exception.Message, "No se pudieron leer las partidas: " + exception.Message, true);
                log.Warning(LogCategory.General, "Main menu: the saved worlds could not be listed. " + exception);
            }

            ClearPhotos();
            for (int i = 0; i < worlds.Count; i++)
            {
                LoadPhoto(worlds[i].Photo);
            }
        }

        /// <summary>A card of "LOAD SAVE": that world, now.</summary>
        private void Load(SavedWorld world)
        {
            if (!world.HasSave)
            {
                Say(
                    "\"" + world.Title + "\" has nothing saved. If you died in it, try one of its copies.",
                    "\"" + world.Title + "\" no tiene nada guardado. Si murió, probá con una de sus copias.",
                    true);
                return;
            }

            string problem;
            if (!SaveWorlds.Use(world, out problem))
            {
                Say("\"" + world.Title + "\" could not be loaded: " + problem + ".", "No se pudo cargar \"" + world.Title + "\": " + problem + ".", true);
                return;
            }

            log.Info(LogCategory.General, "Main menu: loading '" + world.Title + "' (" + (world.IsGame ? "the game's own save" : world.Id) + ").");
            Close();
            Play();
        }

        /// <summary>"Hostear": a lobby, if there is none, with the world chosen (or a new one).</summary>
        private void Host()
        {
            bool fresh = selected == 0;
            SavedWorld world = selected > 0 && selected <= worlds.Count ? worlds[selected - 1] : null;
            if (!fresh && world == null)
            {
                Say("Choose a save, or \"New game\".", "Elegí una partida, o \"Partida nueva\".", true);
                return;
            }

            if (!fresh && !world.HasSave)
            {
                Say(
                    "\"" + world.Title + "\" has nothing saved: choose another, or restore a copy from LOAD SAVE.",
                    "\"" + world.Title + "\" no tiene nada guardado: elegí otra, o restaurá una copia desde LOAD SAVE.",
                    true);
                return;
            }

            NetworkManager manager = network.Manager;
            if (manager.Mode != NetworkMode.Host)
            {
                if (manager.IsActive)
                {
                    Say("You are in someone else's session already: leave it to host.", "Ya estás en una sesión de otro: salí de ella para hostear.", true);
                    return;
                }

                network.HostSteam();
                if (manager.Mode != NetworkMode.Host)
                {
                    Say(
                        "The Steam lobby could not be opened. See the console (F12 of MSCLoader) for why.",
                        "No se pudo abrir el lobby de Steam. Mirá la consola (F12 de MSCLoader) para ver por qué.",
                        true);
                    return;
                }

                inviteWanted = true;
                inviteUntil = now + InviteWaitSeconds;
            }

            string problem;
            if (fresh)
            {
                SavedWorld previous = SaveWorlds.Current;
                if (!creating.Begin(previous, out problem))
                {
                    Say("A new game could not be started: " + problem + ".", "No se pudo empezar una partida nueva: " + problem + ".", true);
                    return;
                }

                log.Info(LogCategory.General, "Main menu: hosting a new world.");
                Close();
                return;
            }

            if (!SaveWorlds.Use(world, out problem))
            {
                Say("\"" + world.Title + "\" could not be used: " + problem + ".", "No se pudo usar \"" + world.Title + "\": " + problem + ".", true);
                return;
            }

            log.Info(LogCategory.General, "Main menu: hosting '" + world.Title + "' (" + (world.IsGame ? "the game's own save" : world.Id) + ").");
            Close();
        }

        // Into the world chosen, the way "Continue" goes: once the menu's own check shows it for that folder.
        private void Play()
        {
            continuing = true;
            continueUntil = now + ContinueWaitSeconds;
        }

        /// <summary>
        /// Back into the world after a death without permanent death, as the player would click "Continue": once the menu
        /// shows it (a guest's once the host's world is here), within a minute.
        /// </summary>
        public void ReturnAfterDeath()
        {
            continuing = true;
            continueUntil = now + 60.0;
            log.Info(LogCategory.General, "Main menu: back into the world after a death without permanent death, as 'Continue' would.");
        }

        private void TryContinue()
        {
            GameObject buttons = GameObject.Find("Interface/Buttons");
            Transform button = buttons == null ? null : buttons.transform.Find("ButtonContinue");
            PlayMakerFSM machine = button == null || !button.gameObject.activeInHierarchy ? null : DoorSync.FindMachine(button, ContinueMachine);
            if (machine != null && machine.enabled && !string.IsNullOrEmpty(machine.ActiveStateName))
            {
                continuing = false;
                if (!DoorSync.HasGlobalTransition(machine, ContinueEvent))
                {
                    machine.AddGlobalTransition(ContinueEvent, ContinueState);
                }

                machine.SendEvent(ContinueEvent);
                log.Info(LogCategory.General, "Main menu: '" + ContinuePath + "' sent to '" + ContinueState + "' (now '" + machine.ActiveStateName + "') for the world chosen.");
                return;
            }

            if (now >= continueUntil)
            {
                continuing = false;
                Say("The game did not offer CONTINUE for that save. Try again.", "El juego no ofreció CONTINUE para esa partida. Probá de nuevo.", true);
                log.Warning(LogCategory.General, "Main menu: the game did not show '" + ContinuePath + "' for the world chosen within " + ContinueWaitSeconds + " s; nothing loaded.");
            }
        }

        // What the window says, in the player's language; null for nothing.
        private string Message
        {
            get { return Texts.Pick(messageEnglish, messageSpanish); }
        }

        private void Say(string english, string spanish, bool bad)
        {
            messageEnglish = english;
            messageSpanish = spanish;
            messageBad = bad;
        }

        private void Hush()
        {
            messageEnglish = null;
            messageSpanish = null;
        }

        // ---- Drawing ----

        /// <summary>The window, or the host's bar. Only from OnGUI.</summary>
        public void OnGUI(bool menuShown, bool pickerOpen)
        {
            if (!inMenu)
            {
                return;
            }

            float k = Mathf.Max(1f, Screen.height / 1080f);
            Styles(k);
            if (Event.current.type == EventType.Layout)
            {
                LobbyShown = false;
            }

            if (mode == Mode.Closed)
            {
                bool lobby = menuShown && !pickerOpen && !creating.Active && MenuColumn.LobbyMode;
                if (lobby)
                {
                    LobbyShown = true;
                    LobbyBox(k);
                }

                // With the lobby's box, a word goes in it.
                if (Message != null && menuShown && !pickerOpen && !lobby)
                {
                    // A word left from a window that closed (loading failed): under the column.
                    var area = new Rect((Screen.width - (640f * k)) * 0.5f, Screen.height * 0.2f, 640f * k, 44f * k);
                    GUI.Box(area, GUIContent.none, panelStyle);
                    GUI.Label(area, Message, messageStyle);
                    if (GUI.Button(new Rect(area.xMax - (30f * k), area.y + (8f * k), 24f * k, 24f * k), "x", smallButtonStyle))
                    {
                        Hush();
                    }
                }

                return;
            }

            float width = Mathf.Min(1180f * k, Screen.width - (40f * k));
            float height = Mathf.Min(820f * k, Screen.height - (40f * k));
            var window = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
            GUI.Box(window, GUIContent.none, panelStyle);
            string title = mode == Mode.Load ? Texts.Pick("LOAD SAVE", "CARGAR PARTIDA")
                : (network.Manager.Mode == NetworkMode.Host ? Texts.Pick("CHANGE SAVE", "CAMBIAR PARTIDA") : Texts.Pick("HOST", "HOSTEAR"));
            var titleRect = new Rect(window.x, window.y + (14f * k), window.width, 52f * k);
            GUI.Label(new Rect(titleRect.x + (3f * k), titleRect.y + (3f * k), titleRect.width, titleRect.height), title, titleShadowStyle);
            GUI.Label(titleRect, title, titleStyle);
            float y = titleRect.yMax + (4f * k);
            GUI.Label(new Rect(window.x + (24f * k), y, window.width - (48f * k), 24f * k), Subtitle(), lineStyle);
            y += 34f * k;

            var body = new Rect(window.x + (24f * k), y, window.width - (48f * k), window.yMax - y - (96f * k));
            switch (panel)
            {
                case Panel.Copies:
                    DrawCopies(body, k);
                    break;
                case Panel.Delete:
                case Panel.Restore:
                    DrawConfirm(body, k);
                    break;
                default:
                    DrawCards(body, k);
                    break;
            }

            if (mode == Mode.Closed)
            {
                // Something drawn above closed it (a world loading).
                return;
            }

            if (Message != null)
            {
                GUI.Label(new Rect(window.x + (24f * k), window.yMax - (88f * k), window.width - (48f * k), 26f * k), Message, messageStyle);
            }

            float buttonWidth = 230f * k;
            float buttonY = window.yMax - (58f * k);
            if (mode == Mode.Host && panel == Panel.Cards)
            {
                GUI.enabled = selected >= 0;
                bool hosting = network.Manager.Mode == NetworkMode.Host;
                string go = selected == 0
                    ? (hosting ? Texts.Pick("Start a new game", "Empezar partida nueva") : Texts.Pick("Host a new game", "Hostear partida nueva"))
                    : (hosting ? Texts.Pick("Use this save", "Usar esta partida") : Texts.Pick("Host this save", "Hostear esta partida"));
                if (GUI.Button(new Rect(window.center.x - buttonWidth - (8f * k), buttonY, buttonWidth, 44f * k), go, chooseStyle))
                {
                    Host();
                }

                GUI.enabled = true;
                if (mode != Mode.Closed && GUI.Button(new Rect(window.center.x + (8f * k), buttonY, buttonWidth, 44f * k), Texts.Pick("Cancel", "Cancelar"), buttonStyle))
                {
                    Close();
                }
            }
            else if (panel == Panel.Cards)
            {
                if (GUI.Button(new Rect(window.center.x - (buttonWidth * 0.5f), buttonY, buttonWidth, 44f * k), Texts.Pick("Close", "Cerrar"), buttonStyle))
                {
                    Close();
                }
            }

            if (mode != Mode.Closed && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Event.current.Use();
                if (panel == Panel.Cards)
                {
                    Close();
                }
                else
                {
                    panel = panel == Panel.Restore ? Panel.Copies : Panel.Cards;
                }
            }

            // Last: a click no control of the window took stays in the window.
            if (mode != Mode.Closed)
            {
                GUI.Button(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none, GUIStyle.none);
            }
        }

        private string Subtitle()
        {
            bool hosting = network.Manager.Mode == NetworkMode.Host;
            if (panel == Panel.Copies && focus != null)
            {
                return Texts.Spanish
                    ? "Copias de \"" + focus.Title + "\": se guarda una cada vez que la cargás (quedan las últimas " + WorldCopies.Kept + ")."
                    : "Copies of \"" + focus.Title + "\": one is kept each time you load it (the last " + WorldCopies.Kept + " stay).";
            }

            if (mode == Mode.Load)
            {
                return hosting
                    ? Texts.Pick(
                        "Choose a save to play it now. You are hosting: your guests will play the one you load.",
                        "Elegí una partida para jugarla ya. Estás hosteando: tus invitados van a jugar la que cargues.")
                    : Texts.Pick("Choose a save to play it now.", "Elegí una partida para jugarla ya.");
            }

            return Texts.Pick(
                "Choose which save to play with your friends, or start a new one (without the intro).",
                "Elegí con qué partida jugar con tus amigos, o empezá una nueva (sin la intro).");
        }

        private void DrawCards(Rect body, float k)
        {
            float gap = 16f * k;
            float cardWidth = 300f * k;
            float photoHeight = (cardWidth - (16f * k)) * 9f / 16f;
            float cardHeight = photoHeight + ((mode == Mode.Load ? 134f : 100f) * k);
            int count = worlds.Count + (mode == Mode.Host ? 1 : 0);
            if (count == 0)
            {
                GUI.Label(
                    new Rect(body.x, body.y + (40f * k), body.width, 60f * k),
                    Texts.Pick("There are no saves yet. Start one with NEW GAME, or from HOST.", "Todavía no hay partidas guardadas. Empezá una con NEW GAME, o desde HOST."),
                    lineStyle);
                return;
            }

            int columns = Mathf.Max(1, Mathf.Min(count, Mathf.FloorToInt((body.width + gap) / (cardWidth + gap))));
            int rows = (count + columns - 1) / columns;
            float gridWidth = (columns * cardWidth) + ((columns - 1) * gap);
            float contentHeight = (rows * cardHeight) + ((rows - 1) * gap);
            bool scrolls = contentHeight > body.height;
            var content = new Rect(0f, 0f, body.width - (scrolls ? 20f * k : 0f), contentHeight);
            scroll = GUI.BeginScrollView(body, scroll, content);
            float left = (content.width - gridWidth) * 0.5f;
            for (int i = 0; i < count && mode != Mode.Closed; i++)
            {
                int row = i / columns;
                int column = i % columns;
                var card = new Rect(left + (column * (cardWidth + gap)), row * (cardHeight + gap), cardWidth, cardHeight);
                if (mode == Mode.Host && i == 0)
                {
                    DrawNewCard(card, photoHeight, k);
                }
                else
                {
                    DrawCard(worlds[i - (mode == Mode.Host ? 1 : 0)], i, card, photoHeight, k);
                }
            }

            GUI.EndScrollView();
        }

        private void DrawNewCard(Rect card, float photoHeight, float k)
        {
            bool chosen = selected == 0;
            bool hot = card.Contains(Event.current.mousePosition);
            GUI.DrawTexture(card, chosen ? chosenTexture : (hot ? cardHotTexture : cardTexture));
            var inner = new Rect(card.x + (4f * k), card.y + (4f * k), card.width - (8f * k), card.height - (8f * k));
            GUI.DrawTexture(inner, cardTexture);
            var photo = new Rect(card.x + (8f * k), card.y + (8f * k), card.width - (16f * k), photoHeight);
            GUI.DrawTexture(photo, shadeTexture);
            GUI.Label(photo, "+", plusStyle);
            float y = photo.yMax + (8f * k);
            GUI.Label(new Rect(card.x + (12f * k), y, card.width - (24f * k), 28f * k), Texts.Pick("NEW GAME", "PARTIDA NUEVA"), nameStyle);
            GUI.Label(
                new Rect(card.x + (12f * k), y + (30f * k), card.width - (24f * k), 40f * k),
                Texts.Pick("Fill in the licence and start at home, without the intro.", "Pone nombre en la licencia y empieza en casa, sin la intro."),
                smallStyle);
            if (GUI.Button(card, GUIContent.none, GUIStyle.none))
            {
                selected = 0;
                Hush();
            }
        }

        private void DrawCard(SavedWorld world, int index, Rect card, float photoHeight, float k)
        {
            bool chosen = mode == Mode.Host && selected == index;
            bool current = SaveWorlds.IsCurrent(world) && network.Manager.Mode == NetworkMode.Host;
            bool hot = card.Contains(Event.current.mousePosition);
            GUI.DrawTexture(card, chosen ? chosenTexture : (hot ? cardHotTexture : cardTexture));
            var inner = new Rect(card.x + (4f * k), card.y + (4f * k), card.width - (8f * k), card.height - (8f * k));
            GUI.DrawTexture(inner, cardTexture);
            var photo = new Rect(card.x + (8f * k), card.y + (8f * k), card.width - (16f * k), photoHeight);
            Texture2D picture = Photo(world.Photo);
            if (picture != null)
            {
                GUI.DrawTexture(photo, picture, ScaleMode.ScaleAndCrop);
            }
            else
            {
                GUI.DrawTexture(photo, shadeTexture);
                GUI.Label(photo, Texts.Pick("No photo yet:\nit is taken when saving", "Sin foto todavía:\nse saca al guardar"), smallStyle);
            }

            if (world.IsGame || current || world.PermaDeath)
            {
                string tag = current ? Texts.Pick("IN THE LOBBY", "LA DEL LOBBY") : (world.IsGame ? Texts.Pick("THE GAME'S SAVE", "PARTIDA DEL JUEGO") : string.Empty);
                if (world.PermaDeath)
                {
                    tag += (tag.Length > 0 ? " · " : string.Empty) + Texts.Pick("PERMANENT DEATH", "MUERTE PERMANENTE");
                }

                var tagRect = new Rect(photo.x, photo.y, photo.width, 22f * k);
                GUI.DrawTexture(tagRect, shadeTexture);
                GUI.Label(tagRect, tag, tagStyle);
            }

            float y = photo.yMax + (8f * k);
            GUI.Label(new Rect(card.x + (12f * k), y, card.width - (24f * k), 28f * k), world.Title, nameStyle);
            y += 28f * k;
            GUI.Label(new Rect(card.x + (12f * k), y, card.width - (24f * k), 22f * k), Facts(world), lineStyle);
            y += 22f * k;
            GUI.Label(new Rect(card.x + (12f * k), y, card.width - (24f * k), 20f * k), Saved(world.SavedAt, world.HasSave), smallStyle);
            y += 26f * k;

            if (mode == Mode.Load)
            {
                // The game's own save is not the mod's to delete: its card has only its copies.
                float small = world.IsGame ? card.width - (24f * k) : (card.width - (32f * k)) * 0.5f;
                GUI.enabled = world.Copies.Count > 0;
                if (GUI.Button(new Rect(card.x + (12f * k), y, small, 28f * k), Texts.Pick("Copies (", "Copias (") + world.Copies.Count + ")", smallButtonStyle))
                {
                    ShowCopies(world);
                }

                GUI.enabled = true;
                if (!world.IsGame && GUI.Button(new Rect(card.xMax - (12f * k) - small, y, small, 28f * k), Texts.Pick("Delete", "Borrar"), smallButtonStyle))
                {
                    focus = world;
                    panel = Panel.Delete;
                    Hush();
                }
            }
            else if (world.HasSave)
            {
                // The game's own switch, as its licence leaves it in the save: with it a death deletes the world; without
                // it the player comes back at home.
                string text = Texts.Pick("Permanent death: ", "Muerte permanente: ") + (world.PermaDeath ? Texts.Pick("YES", "SÍ") : "NO");
                if (GUI.Button(new Rect(card.x + (12f * k), y, card.width - (24f * k), 28f * k), text, smallButtonStyle))
                {
                    string problem;
                    if (SaveWorlds.SetPermaDeath(world, !world.PermaDeath, out problem))
                    {
                        log.Info(LogCategory.General, "Main menu: '" + world.Title + "' now has " + (world.PermaDeath ? "permanent death." : "no permanent death: a player who dies comes back at home."));
                    }
                    else
                    {
                        Say(
                            "The permanent death of \"" + world.Title + "\" could not be changed: " + problem + ".",
                            "No se pudo cambiar la muerte permanente de \"" + world.Title + "\": " + problem + ".",
                            true);
                    }
                }
            }

            if (GUI.Button(new Rect(card.x, card.y, card.width, y - card.y), GUIContent.none, GUIStyle.none))
            {
                Hush();
                if (mode == Mode.Load)
                {
                    Load(world);
                }
                else
                {
                    selected = index;
                }
            }
        }

        private void ShowCopies(SavedWorld world)
        {
            focus = world;
            panel = Panel.Copies;
            Hush();
            scroll = Vector2.zero;
            try
            {
                copies = SaveWorlds.Copies(world);
                focusHash = SaveWorlds.HashNow(world);
            }
            catch (Exception exception)
            {
                copies = new List<WorldCopy>();
                Say("The copies could not be read: " + exception.Message, "No se pudieron leer las copias: " + exception.Message, true);
            }

            for (int i = 0; i < copies.Count; i++)
            {
                LoadPhoto(copies[i].Photo);
            }
        }

        private void DrawCopies(Rect body, float k)
        {
            float row = 112f * k;
            float y = body.y;
            if (copies.Count == 0)
            {
                GUI.Label(new Rect(body.x, y + (20f * k), body.width, 40f * k), Texts.Pick("This save has no copies yet.", "Esta partida no tiene copias todavía."), lineStyle);
            }

            for (int i = 0; i < copies.Count && y + row <= body.yMax; i++)
            {
                WorldCopy copy = copies[i];
                var area = new Rect(body.x, y, body.width, row - (10f * k));
                GUI.DrawTexture(area, cardTexture);
                var photo = new Rect(area.x + (8f * k), area.y + (6f * k), 160f * k, 90f * k);
                Texture2D picture = Photo(copy.Photo);
                GUI.DrawTexture(photo, picture != null ? picture : (Texture)shadeTexture, ScaleMode.ScaleAndCrop);
                float x = photo.xMax + (16f * k);
                SavedWorld summary = copy.Summary;
                GUI.Label(new Rect(x, area.y + (8f * k), area.width - x + area.x - (200f * k), 28f * k), summary == null ? copy.Name : summary.Title, nameStyle);
                GUI.Label(new Rect(x, area.y + (38f * k), area.width - x + area.x - (200f * k), 22f * k), summary == null ? string.Empty : Facts(summary), lineStyle);
                GUI.Label(new Rect(x, area.y + (62f * k), area.width - x + area.x - (200f * k), 20f * k), Saved(copy.SavedAt, true), smallStyle);
                if (copy.Hash == focusHash)
                {
                    GUI.Label(new Rect(area.xMax - (190f * k), area.y + (30f * k), 180f * k, 30f * k), Texts.Pick("Same as now", "Es la de ahora"), lineStyle);
                }
                else if (GUI.Button(new Rect(area.xMax - (190f * k), area.y + (30f * k), 180f * k, 36f * k), Texts.Pick("Restore", "Restaurar"), chooseStyle))
                {
                    restoring = copy;
                    panel = Panel.Restore;
                    Hush();
                }

                y += row;
            }

            if (GUI.Button(new Rect(body.center.x - (115f * k), body.yMax + (38f * k), 230f * k, 44f * k), Texts.Pick("Back", "Volver"), buttonStyle))
            {
                panel = Panel.Cards;
                Refresh();
            }
        }

        private void DrawConfirm(Rect body, float k)
        {
            if (focus == null || (panel == Panel.Restore && restoring == null))
            {
                panel = Panel.Cards;
                return;
            }

            string question;
            if (panel == Panel.Delete)
            {
                question = Texts.Spanish
                    ? "¿Borrar \"" + focus.Title + "\"?\nSe borra con su foto y sus copias, y no se puede deshacer."
                    : "Delete \"" + focus.Title + "\"?\nIt goes with its photo and its copies, and it cannot be undone.";
            }
            else
            {
                question = Texts.Spanish
                    ? "¿Volver \"" + focus.Title + "\" a como estaba " + Saved(restoring.SavedAt, true).ToLowerInvariant() + "?\nLo que tiene ahora queda como una copia más."
                    : "Restore \"" + focus.Title + "\" to how it was when " + Saved(restoring.SavedAt, true).ToLowerInvariant() + "?\nWhat it has now is kept as one more copy.";
            }

            var area = new Rect(body.x + (body.width * 0.15f), body.y + (body.height * 0.25f), body.width * 0.7f, 150f * k);
            GUI.DrawTexture(area, cardTexture);
            GUI.Label(new Rect(area.x + (16f * k), area.y + (16f * k), area.width - (32f * k), 70f * k), question, lineStyle);
            float buttonWidth = 200f * k;
            string confirm = panel == Panel.Delete ? Texts.Pick("Delete", "Borrar") : Texts.Pick("Restore", "Restaurar");
            if (GUI.Button(new Rect(area.center.x - buttonWidth - (8f * k), area.yMax - (56f * k), buttonWidth, 42f * k), confirm, panel == Panel.Delete ? dangerStyle : chooseStyle))
            {
                string problem;
                if (panel == Panel.Delete)
                {
                    if (SaveWorlds.Delete(focus, out problem))
                    {
                        Say("\"" + focus.Title + "\" was deleted.", "\"" + focus.Title + "\" se borró.", false);
                    }
                    else
                    {
                        Say("It could not be deleted: " + problem, "No se pudo borrar: " + problem, true);
                    }

                    panel = Panel.Cards;
                    focus = null;
                    Refresh();
                }
                else
                {
                    if (SaveWorlds.Restore(focus, restoring, out problem))
                    {
                        Say(
                            "\"" + focus.Title + "\" is back to how it was when " + Saved(restoring.SavedAt, true, false).ToLowerInvariant() + ".",
                            "\"" + focus.Title + "\" volvió a como estaba " + Saved(restoring.SavedAt, true, true).ToLowerInvariant() + ".",
                            false);
                    }
                    else
                    {
                        Say("It could not be restored: " + problem, "No se pudo restaurar: " + problem, true);
                    }

                    restoring = null;
                    panel = Panel.Cards;
                    focus = null;
                    Refresh();
                }

                return;
            }

            if (GUI.Button(new Rect(area.center.x + (8f * k), area.yMax - (56f * k), buttonWidth, 42f * k), Texts.Pick("Cancel", "Cancelar"), buttonStyle))
            {
                panel = panel == Panel.Restore ? Panel.Copies : Panel.Cards;
            }
        }

        /// <summary>INVITE: Steam's invite, if this game has a Steam lobby.</summary>
        public void Invite()
        {
            if (network.SteamLobbyReady)
            {
                Hush();
                network.InviteFriend();
            }
            else
            {
                if (inviteWanted)
                {
                    Say("The Steam lobby is still being made: try again in a moment.", "El lobby de Steam todavía se está creando: probá en un momento.", true);
                }
                else
                {
                    Say("This session has no Steam lobby: there is no one to invite.", "Esta sesión no tiene lobby de Steam: no hay a quién invitar.", true);
                }
            }
        }

        /// <summary>LEAVE LOBBY: a guest leaves the host's session; its own save is the menu's again.</summary>
        public void LeaveLobby()
        {
            log.Info(LogCategory.General, "Main menu: this guest left the lobby.");
            Hush();
            network.Leave();
        }

        /// <summary>CLOSE LOBBY: the session ends, and the menu is the game's own save's again.</summary>
        public void CloseLobby()
        {
            log.Info(LogCategory.General, "Main menu: the host closed the lobby.");
            Hush();
            inviteWanted = false;
            network.Leave();
        }

        // The lobby's box, over the column: which world the guests get and from when, who is in, and what is going on.
        private void LobbyBox(float k)
        {
            Rect column = MenuColumn.ColumnArea;
            if (column.width <= 0f)
            {
                return;
            }

            NetworkManager manager = network.Manager;
            NetSession session = manager.Session;
            IList<PlayerInfo> players = session == null ? null : session.Players;
            int count = players == null ? 0 : players.Count;
            bool guest = MenuColumn.GuestLobby;

            // A guest says the host's world (its name once it is here) and how it is coming along.
            string title = guest ? HostWorld.GuestWorldTitle : SaveWorlds.CurrentTitle;
            string saved = guest ? HostWorld.GuestSaved() : HostWorld.HostSaved();
            string status = Message;
            if (status == null)
            {
                status = guest ? HostWorld.GuestStatus()
                    : (string.IsNullOrEmpty(title) ? Texts.Pick("This save is empty: choose another in LOBBY SETTINGS.", "Esta partida está vacía: elegí otra en LOBBY SETTINGS.")
                        : (network.SteamLobbyReady ? null
                            : (inviteWanted ? Texts.Pick("Making the Steam lobby...", "Creando el lobby de Steam...")
                                : Texts.Pick("A session without a Steam lobby: no one can be invited.", "Sesión sin lobby de Steam: no se puede invitar."))));
            }

            float pad = 16f * k;
            float width = 360f * k;
            if (!ReferenceEquals(status, statusShown))
            {
                statusShown = status;
                statusContent = status == null ? null : new GUIContent(status);
                statusHeight = status == null ? 0f : smallStyle.CalcHeight(statusContent, width - (2f * pad));
            }

            float height = pad + (30f * k) + (26f * k) + (saved != null ? 22f * k : 0f) + (10f * k) + (count * 24f * k) + (status != null ? statusHeight + (8f * k) : 0f) + pad;
            float right = Mathf.Min(Screen.width - (16f * k), column.xMax + (6f * k));
            var box = new Rect(right - width, Mathf.Max(16f * k, column.y - (18f * k) - height), width, height);
            GUI.Box(box, GUIContent.none, roundedStyle);

            float x = box.x + pad;
            float inner = box.width - (2f * pad);
            float y = box.y + pad;
            GUI.Label(new Rect(x, y, inner, 30f * k), "LOBBY", lobbyTitleStyle);
            GUI.Label(new Rect(x, y, inner, 30f * k), count + " / " + manager.MaxPlayers + Texts.Pick(" players", " jugadores"), lobbyCountStyle);
            y += 30f * k;
            string world;
            if (!string.IsNullOrEmpty(title))
            {
                world = Texts.Pick("Save: \"", "Partida: \"") + title + "\"";
            }
            else if (guest)
            {
                world = Texts.Spanish ? "Partida de " + HostWorld.HostNameShown : HostWorld.HostNameShown + "'s save";
            }
            else
            {
                world = Texts.Pick("Empty save", "Partida vacía");
            }

            GUI.Label(new Rect(x, y, inner, 26f * k), world, nameStyle);
            y += 26f * k;
            if (saved != null)
            {
                GUI.Label(new Rect(x, y, inner, 22f * k), saved, smallStyle);
                y += 22f * k;
            }

            y += 10f * k;
            for (int i = 0; i < count; i++)
            {
                PlayerInfo player = players[i];
                GUI.Label(new Rect(x, y, inner, 24f * k), "•  " + player.Name + (player.IsHost ? Texts.Pick("   (host)", "   (anfitrión)") : string.Empty), lineStyle);
                y += 24f * k;
            }

            if (status != null)
            {
                y += 8f * k;
                GUI.Label(new Rect(x, y, inner, statusHeight), statusContent, Message != null ? messageLeftStyle : smallStyle);
            }
        }

        private static string Facts(SavedWorld world)
        {
            return Facts(world, Texts.Spanish);
        }

        private static string Facts(SavedWorld world, bool spanish)
        {
            if (!world.HasSave)
            {
                return spanish ? "Sin nada guardado" : "Nothing saved";
            }

            string when = SavedWorldText.When(world.Day, world.Hour, spanish);
            string money = float.IsNaN(world.Money) ? string.Empty : SavedWorldText.Money(world.Money);
            return when.Length > 0 && money.Length > 0 ? when + "  ·  " + money : when + money;
        }

        private static string Saved(DateTime at, bool has)
        {
            return Saved(at, has, Texts.Spanish);
        }

        private static string Saved(DateTime at, bool has, bool spanish)
        {
            if (!has || at == DateTime.MinValue)
            {
                return spanish ? "Nunca guardada" : "Never saved";
            }

            DateTime today = DateTime.Today;
            string time = at.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (at.Date == today)
            {
                return (spanish ? "Guardada hoy a las " : "Saved today at ") + time;
            }

            if (at.Date == today.AddDays(-1))
            {
                return (spanish ? "Guardada ayer a las " : "Saved yesterday at ") + time;
            }

            string day = at.ToString("dd/MM", CultureInfo.InvariantCulture);
            return spanish ? "Guardada el " + day + " a las " + time : "Saved on " + day + " at " + time;
        }

        // ---- Photos ----

        private void LoadPhoto(string path)
        {
            if (path == null || photos.ContainsKey(path))
            {
                return;
            }

            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    UnityEngine.Object.Destroy(texture);
                    texture = null;
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.General, "Main menu: the photo '" + path + "' could not be read: " + exception.Message);
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }

                texture = null;
            }

            photos[path] = texture;
        }

        private Texture2D Photo(string path)
        {
            Texture2D texture;
            return path != null && photos.TryGetValue(path, out texture) ? texture : null;
        }

        private void ClearPhotos()
        {
            foreach (Texture2D texture in photos.Values)
            {
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }

            photos.Clear();
        }

        // ---- Styles ----

        private void Styles(float k)
        {
            if (panelStyle != null && stylesFor == k)
            {
                return;
            }

            stylesFor = k;
            if (panelTexture == null)
            {
                panelTexture = Solid(new Color(0.07f, 0.08f, 0.1f, 0.95f));
                cardTexture = Solid(new Color(0.13f, 0.14f, 0.17f, 1f));
                cardHotTexture = Solid(new Color(0.55f, 0.5f, 0.3f, 1f));
                chosenTexture = Solid(new Color(0.98f, 0.86f, 0.2f, 1f));
                greenTexture = Solid(new Color(0.18f, 0.55f, 0.32f, 1f));
                redTexture = Solid(new Color(0.62f, 0.16f, 0.14f, 1f));
                shadeTexture = Solid(new Color(0f, 0f, 0f, 0.55f));
                roundedTexture = Rounded(new Color(0.06f, 0.07f, 0.09f, 0.9f), new Color(0.98f, 0.86f, 0.2f, 0.9f));
            }

            roundedStyle = new GUIStyle(GUI.skin.box) { border = new RectOffset(22, 22, 22, 22) };
            roundedStyle.normal.background = roundedTexture;

            Font font = MenuColumn.GameFont != null && MenuColumn.GameFont.dynamic ? MenuColumn.GameFont : null;
            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = panelTexture;
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(40 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            if (font != null)
            {
                titleStyle.font = font;
                titleStyle.fontStyle = FontStyle.Normal;
            }

            titleStyle.normal.textColor = new Color(0.98f, 0.86f, 0.2f);
            titleShadowStyle = new GUIStyle(titleStyle);
            titleShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            nameStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(19 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            nameStyle.normal.textColor = Color.white;
            lineStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * k), alignment = TextAnchor.MiddleLeft, wordWrap = true };
            lineStyle.normal.textColor = new Color(0.9f, 0.91f, 0.93f);
            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(13 * k), alignment = TextAnchor.MiddleLeft, wordWrap = true };
            smallStyle.normal.textColor = new Color(0.68f, 0.7f, 0.74f);
            tagStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(12 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            tagStyle.normal.textColor = new Color(0.98f, 0.86f, 0.2f);
            plusStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(72 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            if (font != null)
            {
                plusStyle.font = font;
            }

            plusStyle.normal.textColor = new Color(0.98f, 0.86f, 0.2f);
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(17 * k), fontStyle = FontStyle.Bold };
            smallButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(13 * k) };
            chooseStyle = Colored(buttonStyle, greenTexture);
            dangerStyle = Colored(buttonStyle, redTexture);
            messageStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            messageStyle.normal.textColor = new Color(1f, 0.82f, 0.4f);
            messageLeftStyle = new GUIStyle(messageStyle) { alignment = TextAnchor.UpperLeft, fontSize = Mathf.RoundToInt(13 * k) };
            lobbyTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(22 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            lobbyTitleStyle.normal.textColor = new Color(0.98f, 0.86f, 0.2f);
            lobbyCountStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * k), alignment = TextAnchor.MiddleRight };
            lobbyCountStyle.normal.textColor = new Color(0.9f, 0.91f, 0.93f);
        }

        private static GUIStyle Colored(GUIStyle from, Texture2D texture)
        {
            var style = new GUIStyle(from);
            style.normal.background = texture;
            style.hover.background = texture;
            style.active.background = texture;
            style.normal.textColor = Color.white;
            style.hover.textColor = Color.white;
            style.active.textColor = new Color(0.85f, 0.85f, 0.85f);
            return style;
        }

        // A box with round corners and a thin rim, drawn stretched by its borders: the lobby's.
        private static Texture2D Rounded(Color fill, Color rim)
        {
            const int Size = 64;
            const float Radius = 18f;
            const float Rim = 2f;
            var texture = new Texture2D(Size, Size, TextureFormat.ARGB32, false) { wrapMode = TextureWrapMode.Clamp };
            float half = Size * 0.5f;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    // How far the pixel's middle is outside the rounded rectangle (inside is below 0).
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - Radius);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - Radius);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - Radius;
                    Color color = Color.Lerp(fill, rim, Mathf.Clamp01(outside + Rim + 0.5f));
                    color.a *= Mathf.Clamp01(0.5f - outside);
                    texture.SetPixel(x, y, color);
                }
            }

            texture.Apply();
            return texture;
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        // ---- The console ----

        /// <summary>
        /// "ss worlds": the saved worlds, and what the windows do, by number (1 is the first card of "LOAD SAVE").
        /// </summary>
        public string Command(string[] args)
        {
            string verb = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            if (verb == "photo")
            {
                return WorldPhoto.Tune(args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null);
            }

            if (!inMenu)
            {
                return "Saved worlds: only from the main menu (and 'ss worlds photo [around up]' in a world).";
            }

            List<SavedWorld> list = SaveWorlds.List();
            int number;
            SavedWorld world = args.Length > 2 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= list.Count
                ? list[number - 1] : null;
            switch (verb)
            {
                case "list":
                    return Describe(list);
                case "open":
                    string opened = args.Length > 2 ? args[2].ToLowerInvariant() : "load";
                    if (opened == "host")
                    {
                        OpenHost();
                        return "Saved worlds: the window is open.";
                    }

                    OpenLoad();
                    SavedWorld shown = args.Length > 3 && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= worlds.Count
                        ? worlds[number - 1] : null;
                    if (opened == "copies" && shown != null)
                    {
                        ShowCopies(shown);
                    }
                    else if (opened == "delete" && shown != null && !shown.IsGame)
                    {
                        focus = shown;
                        panel = Panel.Delete;
                    }

                    return "Saved worlds: the window is open.";
                case "load":
                    if (world == null)
                    {
                        return "Saved worlds: 'ss worlds load <n>', n from 'ss worlds'.";
                    }

                    worlds = list;
                    Load(world);
                    return "Saved worlds: " + (messageEnglish ?? "loading '" + world.Title + "'.");
                case "host":
                    if (args.Length > 2 && args[2].ToLowerInvariant() == "new")
                    {
                        worlds = list;
                        selected = 0;
                    }
                    else if (world != null)
                    {
                        worlds = list;
                        selected = list.IndexOf(world) + 1;
                    }
                    else
                    {
                        return "Saved worlds: 'ss worlds host <n|new>'.";
                    }

                    mode = Mode.Host;
                    Host();
                    string said = messageEnglish;
                    Close();
                    return "Saved worlds: " + (said ?? "hosting " + (selected == 0 ? "a new world" : "'" + world.Title + "'") + ".");
                case "play":
                    Play();
                    return "Saved worlds: into '" + SaveWorlds.CurrentTitle + "'.";
                case "copies":
                    if (world == null)
                    {
                        return "Saved worlds: 'ss worlds copies <n>'.";
                    }

                    return DescribeCopies(world, SaveWorlds.Copies(world));
                case "restore":
                    int which;
                    List<WorldCopy> kept = world == null ? null : SaveWorlds.Copies(world);
                    if (kept == null || args.Length < 4 || !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out which) || which < 1 || which > kept.Count)
                    {
                        return "Saved worlds: 'ss worlds restore <n> <copy>', the copy's number from 'ss worlds copies <n>'.";
                    }

                    string problem;
                    return SaveWorlds.Restore(world, kept[which - 1], out problem) ? "Saved worlds: '" + world.Title + "' restored from " + kept[which - 1].Name + "." : "Saved worlds: " + problem;
                case "delete":
                    if (world == null)
                    {
                        return "Saved worlds: 'ss worlds delete <n>'.";
                    }

                    string why;
                    return SaveWorlds.Delete(world, out why) ? "Saved worlds: '" + world.Title + "' deleted." : "Saved worlds: " + why;
                case "close":
                    Close();
                    return "Saved worlds: the window is closed.";
                case "permadeath":
                    string onOff = args.Length > 3 ? args[3].ToLowerInvariant() : null;
                    if (world == null || (onOff != "on" && onOff != "off"))
                    {
                        return "Saved worlds: 'ss worlds permadeath <n> on|off'.";
                    }

                    string notWritten;
                    return SaveWorlds.SetPermaDeath(world, onOff == "on", out notWritten)
                        ? "Saved worlds: '" + world.Title + "' now has " + (world.PermaDeath ? "permanent death." : "no permanent death.")
                        : "Saved worlds: " + notWritten;
                default:
                    return "ss worlds [list|open [load|host|copies <n>|delete <n>]|load <n>|host <n|new>|play|copies <n>|restore <n> <copy>|delete <n>|close|permadeath <n> on|off|photo [around up]]";
            }
        }

        private static string Describe(List<SavedWorld> list)
        {
            var text = new StringBuilder("Saved worlds (" + list.Count + "), in use: " + (SaveWorlds.UsesModWorld ? SaveWorlds.Current.Id : "the game's own save") + ".");
            for (int i = 0; i < list.Count; i++)
            {
                SavedWorld world = list[i];
                text.Append("\n").Append(i + 1).Append(". ").Append(world.Title).Append(world.IsGame ? " (the game's own save)" : " (" + world.Id + ")")
                    .Append(" - ").Append(Facts(world, false)).Append(" - ").Append(Saved(world.SavedAt, world.HasSave, false)).Append(" - ")
                    .Append(world.Copies.Count).Append(" copies").Append(world.Photo != null ? ", photo" : ", no photo")
                    .Append(world.PermaDeath ? ", permanent death" : ", no permanent death")
                    .Append(SaveWorlds.IsCurrent(world) ? " [in use]" : string.Empty);
            }

            return text.ToString();
        }

        private static string DescribeCopies(SavedWorld world, List<WorldCopy> kept)
        {
            ulong now = SaveWorlds.HashNow(world);
            var text = new StringBuilder("Copies of '" + world.Title + "' (" + kept.Count + "):");
            for (int i = 0; i < kept.Count; i++)
            {
                text.Append("\n").Append(i + 1).Append(". ").Append(kept[i].Name).Append(" - ")
                    .Append(kept[i].Summary == null ? string.Empty : kept[i].Summary.Title + ", " + Facts(kept[i].Summary, false))
                    .Append(kept[i].Hash == now ? " [same as now]" : string.Empty);
            }

            return text.ToString();
        }
    }
}
