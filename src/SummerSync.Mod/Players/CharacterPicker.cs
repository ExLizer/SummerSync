using System;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// The window where the local player chooses which of the game's people they look like to the others
    /// (<see cref="Wardrobe"/>: the person turning slowly, and a grid of everyone's faces). It opens by itself the first
    /// time the player is in the game world in a session, with F7 at any time, and in the main menu with its button
    /// "Cambiar personaje" (there, the player's figure in a lobby changes as the choice moves).
    ///
    /// Its second page tunes how the people move and where their heads look (<see cref="CharacterTuning"/>): the
    /// person in the picture does what is chosen (crouch, run, lie down...) while the values are moved, and the
    /// head of each person can be set straight.
    ///
    /// The picture is a copy of the person far above the player, on a layer of its own that only a camera of its
    /// own looks at, drawn into a texture. While the window is open the cursor is free and the game's mouse look
    /// (the components "MouseLook" of the player and of the camera, docs/GAME_NOTES.md) is held.
    /// </summary>
    internal sealed class CharacterPicker
    {
        public const KeyCode Key = KeyCode.F7;

        private const float Above = 400f;
        private const int TextureSize = 512;
        private const float SpinPerSecond = 35f;
        private const string LookComponent = "MouseLook";
        private const float Row = 40f;

        private static readonly string[] KindNames = { "Standing", "Walking", "Running", "Crouching", "Crouch walking", "Lying down", "Sitting" };
        private static readonly string[] KindNamesSpanish = { "De pie", "Caminando", "Corriendo", "Agachado", "Agachado caminando", "Acostado", "Sentado" };
        private static readonly PoseKind[] Kinds = { PoseKind.Stand, PoseKind.Walk, PoseKind.Run, PoseKind.Crouch, PoseKind.CrouchWalk, PoseKind.Prone, PoseKind.Sit };
        private static readonly string[] ViewNames = { "View: turning", "View: from the front", "View: from the side" };
        private static readonly string[] ViewNamesSpanish = { "Vista: girando", "Vista: de frente", "Vista: de costado" };
        private static readonly string[] HeadNames = { "Head: turn (°)", "Head: up / down (°)", "Head: tilt (°)" };
        private static readonly string[] HeadNamesSpanish = { "Cabeza: giro (°)", "Cabeza: arriba / abajo (°)", "Cabeza: ladeo (°)" };

        private readonly Logger log;
        private readonly LookSync looks;
        private readonly Wardrobe wardrobe = new Wardrobe();
        private bool menuMode;
        private GUIStyle menuButtonStyle;
        private float menuButtonFor;
        private readonly string[] valueTexts = new string[16];
        private readonly float[] valueShown = new float[16];
        private bool open;
        private bool tuning;
        private int index;
        private int kind;
        private int view;
        private string status;
        private bool defaulted;
        private Transform player;
        private CharacterModel preview;
        private GameObject rig;
        private Camera camera;
        private RenderTexture picture;
        private int layer = -1;
        private int mainMaskBefore;
        private Camera mainCamera;
        private float spin;
        private Behaviour[] held = new Behaviour[0];
        private FsmBool inMenu;
        private bool inMenuBefore;
        private string countText;
        private int countIndex = -1;
        private bool countMissing;
        private bool countSpanish;
        private GUIStyle panelStyle;
        private GUIStyle titleStyle;
        private GUIStyle nameStyle;
        private GUIStyle smallStyle;
        private GUIStyle leftStyle;
        private GUIStyle buttonStyle;
        private GUIStyle chooseStyle;
        private GUIStyle tabStyle;
        private Texture2D panelTexture;
        private Texture2D chooseTexture;

        public CharacterPicker(Logger log, LookSync looks)
        {
            this.log = log;
            this.looks = looks;
        }

        public bool IsOpen
        {
            get { return open; }
        }

        /// <summary>
        /// Opens the window, on the person the player looks like now: in the game world, or in the main menu (with no
        /// page to tune how they move). False if there is neither.
        /// </summary>
        public bool Open(Transform localPlayer)
        {
            if (open)
            {
                return true;
            }

            menuMode = localPlayer == null;
            if (menuMode && !World.HostWorld.InMenu)
            {
                return false;
            }

            player = localPlayer;
            index = looks.Mine >= 0 ? looks.Mine : 0;
            open = true;
            status = null;
            defaulted = false;
            tuning = false;
            wardrobe.Begin(looks.HasChosen ? looks.Mine : 0);
            if (!menuMode)
            {
                layer = FreeLayer();
                BuildRig();
                ShowPerson();
                HoldTheGame(true);
            }

            log.Info(LogCategory.Player, "The character window is open " + (menuMode ? "in the main menu" : "(picture on layer " + layer + ")") + ".");
            return true;
        }

        /// <summary>Closes the window; with <paramref name="choose"/>, the person on it is the player's from now on.</summary>
        public void Close(bool choose)
        {
            if (!open)
            {
                return;
            }

            open = false;
            if (choose)
            {
                looks.Choose(wardrobe.Selected);
            }

            looks.Previewing = null;
            wardrobe.End();

            if (preview != null)
            {
                preview.Destroy();
                preview = null;
            }

            if (rig != null)
            {
                UnityEngine.Object.Destroy(rig);
            }

            if (picture != null)
            {
                picture.Release();
                UnityEngine.Object.Destroy(picture);
                picture = null;
            }

            if (!menuMode)
            {
                HoldTheGame(false);
            }
        }

        /// <summary>Once per frame: the picture follows the player, turns, and the keys are read.</summary>
        public void LateUpdate(Transform localPlayer)
        {
            if (!open)
            {
                return;
            }

            if (menuMode ? !World.HostWorld.InMenu : (localPlayer == null || localPlayer != player))
            {
                Close(false);
                return;
            }

            // Only the arrows: A and D still walk the player, whose movement is not held.
            if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                Step(-1);
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                Step(1);
            }
            else if (!tuning && Input.GetKeyDown(KeyCode.UpArrow))
            {
                Step(-Wardrobe.Columns);
            }
            else if (!tuning && Input.GetKeyDown(KeyCode.DownArrow))
            {
                Step(Wardrobe.Columns);
            }
            else if (!tuning && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                Close(true);
                return;
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close(false);
                return;
            }

            wardrobe.Update(Time.deltaTime);
            if (index != wardrobe.Selected && wardrobe.Selected >= 0)
            {
                // The page to tune shows the one chosen on the other.
                index = wardrobe.Selected;
                if (!menuMode)
                {
                    ShowPerson();
                }
            }

            if (menuMode)
            {
                looks.Previewing = wardrobe.Selected;
                return;
            }

            // The game takes the cursor back every frame it is not in a menu of its own.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Vector3 stage = player.position + (Vector3.up * Above);
            spin = (spin + (SpinPerSecond * Time.deltaTime)) % 360f;
            float facing = !tuning || view == 0 ? spin : (view == 1 ? 0f : 90f);
            if (preview != null)
            {
                PoseKind shown = tuning ? Kinds[kind] : PoseKind.Stand;
                preview.Forced = shown;
                float eyes = shown == PoseKind.Prone ? 0.55f : shown == PoseKind.Sit ? 0.85f : shown == PoseKind.Crouch || shown == PoseKind.CrouchWalk ? 1.1f : 1.65f;
                preview.Place(new PlayerPose(stage.x, stage.y, stage.z, facing, 0f, eyes), Vector3.zero, Time.deltaTime, float.NaN, null);
            }

            // The whole person, from the soles to above the cap.
            rig.transform.position = stage + new Vector3(0f, 1.0f, 3.1f);
            rig.transform.LookAt(stage + (Vector3.up * 0.9f));
            if (mainCamera != null && mainCamera != Camera.main)
            {
                mainCamera.cullingMask = mainMaskBefore;
                mainCamera = null;
            }

            if (mainCamera == null && Camera.main != null)
            {
                mainCamera = Camera.main;
                mainMaskBefore = mainCamera.cullingMask;
                mainCamera.cullingMask &= ~(1 << layer);
            }
        }

        /// <summary>
        /// Draws the window, or in the main menu its button (not while another window of the mod's is up, or the game's licence
        /// or credits cover the menu: <paramref name="otherWindow"/>). Only from OnGUI.
        /// </summary>
        public void OnGUI(bool otherWindow)
        {
            if (!open)
            {
                if (World.HostWorld.InMenu && !otherWindow && Menu.MenuColumn.InterfaceShown)
                {
                    MenuButton();
                }

                return;
            }

            Styles();
            float width = Mathf.Min(tuning ? 940f : 980f, Screen.width - 40f);
            float height = Mathf.Min(tuning ? 720f : 680f, Screen.height - 40f);
            var panel = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
            GUI.Box(panel, GUIContent.none, panelStyle);

            // The page that tunes how the people move is the developers' (Edition).
            float y = panel.y + 14f;
            if (!menuMode && Edition.Developer)
            {
                if (GUI.Button(new Rect(panel.x + 14f, y, 130f, 30f), Texts.Pick("Character", "Personaje"), tuning ? tabStyle : chooseStyle))
                {
                    tuning = false;
                }

                if (GUI.Button(new Rect(panel.x + 150f, y, 130f, 30f), Texts.Pick("Adjust", "Ajustar"), tuning ? chooseStyle : tabStyle))
                {
                    tuning = true;
                    status = null;
                    defaulted = false;
                }

                y += 38f;
            }

            GUI.Label(
                new Rect(panel.x, y, panel.width, 36f),
                tuning ? Texts.Pick("ADJUST HOW THEY MOVE", "AJUSTAR CÓMO SE MUEVEN") : Texts.Pick("CHANGE CHARACTER", "CAMBIAR PERSONAJE"),
                titleStyle);
            y += 42f;
            if (!tuning)
            {
                wardrobe.Draw(new Rect(panel.x + 24f, y, panel.width - 48f, panel.yMax - y - 90f), smallStyle);
                float choiceWidth = 190f;
                float buttons = panel.yMax - 82f;
                if (GUI.Button(new Rect(panel.x + (panel.width * 0.5f) - choiceWidth - 8f, buttons, choiceWidth, 46f), Texts.Pick("Choose", "Elegir"), chooseStyle))
                {
                    Close(true);
                    return;
                }

                if (GUI.Button(new Rect(panel.x + (panel.width * 0.5f) + 8f, buttons, choiceWidth, 46f), Texts.Pick("Cancel", "Cancelar"), buttonStyle))
                {
                    Close(false);
                    return;
                }

                GUI.Label(
                    new Rect(panel.x, panel.yMax - 30f, panel.width, 22f),
                    Texts.Pick("Click or arrows to choose  ·  Enter to keep it  ·  Esc to cancel", "Clic o flechas para elegir  ·  Enter para quedártelo  ·  Esc para cancelar"),
                    smallStyle);
                Block();
                return;
            }

            float side = tuning ? Mathf.Min(panel.width * 0.48f, height - 200f) : Mathf.Min(panel.width - 120f, height - 280f);
            float frameX = tuning ? panel.x + 60f : panel.x + ((panel.width - side) * 0.5f);
            var frame = new Rect(frameX, y, side, side);
            if (picture != null)
            {
                GUI.DrawTexture(frame, picture, ScaleMode.ScaleToFit, false);
            }

            if (GUI.Button(new Rect(frame.x - 50f, frame.y + (side * 0.5f) - 30f, 44f, 60f), "<", buttonStyle))
            {
                Step(-1);
            }

            if (GUI.Button(new Rect(frame.xMax + 6f, frame.y + (side * 0.5f) - 30f, 44f, 60f), ">", buttonStyle))
            {
                Step(1);
            }

            float under = frame.yMax + 8f;
            Characters.Look look = Characters.At(index);
            GUI.Label(new Rect(frame.x - 40f, under, side + 80f, 32f), look == null ? Texts.Pick("Capsule", "Cápsula") : look.Shown, nameStyle);
            under += 32f;
            bool missing = preview == null && look != null;
            if (countText == null || countIndex != index || countMissing != missing || countSpanish != Texts.Spanish)
            {
                countIndex = index;
                countMissing = missing;
                countSpanish = Texts.Spanish;
                countText = (index + 1).ToString(CultureInfo.InvariantCulture) + Texts.Pick(" of ", " de ") + Characters.All.Length
                    + (missing ? Texts.Pick("  ·  not in this world", "  ·  no está en este mundo") : string.Empty);
            }

            GUI.Label(new Rect(frame.x - 40f, under, side + 80f, 22f), countText, smallStyle);
            under += 30f;

            if (tuning)
            {
                DrawTuning(new Rect(frame.xMax + 70f, y, panel.xMax - frame.xMax - 90f, panel.yMax - y - 50f), look);
                if (GUI.Button(new Rect(frame.x, under, side, 40f), Texts.Pick("Close", "Cerrar"), buttonStyle))
                {
                    Close(false);
                    return;
                }

                string said = status;
                if (said == null)
                {
                    said = defaulted
                        ? Texts.Pick("Default values for what is shown (press Save to keep them).", "Valores de fábrica para lo que se ve (falta Guardar para que quede).")
                        : Texts.Pick(
                            "Changes show in the view and on the other players. \"Save\" keeps them for next time.",
                            "Los cambios se ven en la vista y en los demás jugadores. \"Guardar\" los deja para la próxima vez.");
                }

                GUI.Label(new Rect(panel.x, panel.yMax - 30f, panel.width, 22f), said, smallStyle);
            }

            Block();
        }

        // Last of all: a click no control of the window took is taken here, so that it does not reach what else the GUI draws.
        // The game's own menu does not look at the GUI (its buttons take the mouse by a ray): in the menu its buttons are
        // switched off while the window is open (Menu.MenuClicks).
        private static void Block()
        {
            GUI.Button(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none, GUIStyle.none);
        }

        // The values of what is being shown, and the head of the person on the picture.
        private void DrawTuning(Rect area, Characters.Look look)
        {
            PoseSettings settings = CharacterTuning.Settings;
            float y = area.y;
            if (GUI.Button(new Rect(area.x, y, 36f, 32f), "<", buttonStyle))
            {
                kind = (kind + Kinds.Length - 1) % Kinds.Length;
            }

            GUI.Label(new Rect(area.x + 40f, y, area.width - 80f, 32f), (Texts.Spanish ? KindNamesSpanish : KindNames)[kind], nameStyle);
            if (GUI.Button(new Rect(area.xMax - 36f, y, 36f, 32f), ">", buttonStyle))
            {
                kind = (kind + 1) % Kinds.Length;
            }

            y += 40f;
            if (GUI.Button(new Rect(area.x, y, area.width, 28f), (Texts.Spanish ? ViewNamesSpanish : ViewNames)[view], tabStyle))
            {
                view = (view + 1) % ViewNames.Length;
            }

            y += 38f;
            PoseValue[] group = GroupOf(Kinds[kind]);
            int slot = 0;
            for (int i = 0; group != null && i < group.Length; i++, slot++)
            {
                PoseValue value = group[i];
                float now = settings[value];
                float moved = Slider(new Rect(area.x, y, area.width, Row), value.Label, now, value.Least, value.Most, slot);
                if (moved != now)
                {
                    settings.Set(value, moved);
                    status = null;
                    defaulted = false;
                }

                y += Row;
            }

            if (group == null)
            {
                GUI.Label(
                    new Rect(area.x, y, area.width, 44f),
                    Texts.Pick("This animation is the game's: it has no values of its own.", "Esta animación es del juego: no tiene valores propios."),
                    leftStyle);
                y += 44f;
            }

            y += 8f;
            if (look != null)
            {
                for (int axis = 0; axis < 3; axis++, slot++)
                {
                    float now = settings.Head(look.Index, axis);
                    float moved = Slider(new Rect(area.x, y, area.width, Row), (Texts.Spanish ? HeadNamesSpanish : HeadNames)[axis], now, -PoseSettings.HeadTune, PoseSettings.HeadTune, slot);
                    if (moved != now)
                    {
                        settings.SetHead(look.Index, axis, Mathf.Round(moved * 2f) * 0.5f);
                        status = null;
                        defaulted = false;
                    }

                    y += Row;
                }
            }

            y += 10f;
            float half = (area.width - 10f) * 0.5f;
            if (GUI.Button(new Rect(area.x, y, half, 40f), Texts.Pick("Save", "Guardar"), chooseStyle))
            {
                status = CharacterTuning.Save();
                defaulted = false;
            }

            if (GUI.Button(new Rect(area.x + half + 10f, y, half, 40f), Texts.Pick("Defaults", "De fábrica"), buttonStyle))
            {
                if (group != null)
                {
                    settings.Reset(group);
                }

                if (look != null)
                {
                    settings.ResetHead(look.Index);
                }

                status = null;
                defaulted = true;
            }
        }

        private float Slider(Rect area, string label, float value, float least, float most, int slot)
        {
            // The number is written again only when it changes: OnGUI runs several times a frame.
            if (valueTexts[slot] == null || valueShown[slot] != value)
            {
                valueShown[slot] = value;
                valueTexts[slot] = value.ToString(Mathf.Abs(most - least) <= 2f ? "0.00" : "0.#", CultureInfo.InvariantCulture);
            }

            GUI.Label(new Rect(area.x, area.y, area.width - 60f, 20f), label, leftStyle);
            GUI.Label(new Rect(area.xMax - 60f, area.y, 60f, 20f), valueTexts[slot], leftStyle);
            return GUI.HorizontalSlider(new Rect(area.x, area.y + 22f, area.width, 14f), value, least, most);
        }

        private static PoseValue[] GroupOf(PoseKind shown)
        {
            switch (shown)
            {
                case PoseKind.Crouch:
                    return PoseSettings.Groups[0];
                case PoseKind.CrouchWalk:
                    return PoseSettings.Groups[1];
                case PoseKind.Run:
                    return PoseSettings.Groups[2];
                case PoseKind.Prone:
                    return PoseSettings.Groups[3];
                case PoseKind.Walk:
                case PoseKind.Stand:
                    return PoseSettings.Groups[4];
                default:
                    return null;
            }
        }

        private void Step(int by)
        {
            wardrobe.Step(by);
        }

        // In the main menu, when the window is closed: the way to open it.
        private void MenuButton()
        {
            float k = Mathf.Max(1f, Screen.height / 1080f);
            if (menuButtonStyle == null || menuButtonFor != k)
            {
                Styles();
                menuButtonFor = k;
                menuButtonStyle = new GUIStyle(chooseStyle) { fontSize = Mathf.RoundToInt(17 * k) };
            }

            if (GUI.Button(new Rect(24f * k, Screen.height - (52f * k), 240f * k, 40f * k), Texts.Pick("Change character", "Cambiar personaje"), menuButtonStyle))
            {
                Open(null);
            }
        }

        private void ShowPerson()
        {
            if (preview != null)
            {
                preview.Destroy();
                preview = null;
            }

            Characters.Look look = Characters.At(index);
            if (look != null)
            {
                preview = CharacterModel.Build(look, null, "Character window: " + look.Name, layer, CharacterTuning.Settings);
                if (preview == null)
                {
                    log.Warning(LogCategory.Player, "The character window could not find " + look.Name + " in this world (" + look.Where + ").");
                }
                else
                {
                    log.Info(LogCategory.Player, "The character window shows " + look.Name + " " + preview.Describe() + ".");
                }
            }
        }

        private void BuildRig()
        {
            rig = new GameObject("SummerSync character window");
            rig.transform.position = player.position;
            picture = new RenderTexture(TextureSize, TextureSize, 16);
            camera = rig.AddComponent<Camera>();
            camera.targetTexture = picture;
            camera.cullingMask = 1 << layer;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
            camera.fieldOfView = 38f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            camera.depth = -50f;
            Light light = rig.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = 1 << layer;
            light.intensity = 0.9f;
        }

        // The game's mouse look held while the cursor is in use, and the game told it is in a menu.
        private void HoldTheGame(bool hold)
        {
            if (hold)
            {
                var found = new System.Collections.Generic.List<Behaviour>();
                Behaviour[] behaviours = player.GetComponentsInChildren<Behaviour>(true);
                for (int i = 0; i < behaviours.Length; i++)
                {
                    if (behaviours[i] != null && behaviours[i].GetType().Name == LookComponent && behaviours[i].enabled)
                    {
                        behaviours[i].enabled = false;
                        found.Add(behaviours[i]);
                    }
                }

                held = found.ToArray();
                inMenu = FsmVariables.GlobalVariables.FindFsmBool("PlayerInMenu");
                if (inMenu != null)
                {
                    inMenuBefore = inMenu.Value;
                    inMenu.Value = true;
                }

                return;
            }

            for (int i = 0; i < held.Length; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (held[i] != null)
                {
                    held[i].enabled = true;
                }
            }

            held = new Behaviour[0];
            if (inMenu != null)
            {
                inMenu.Value = inMenuBefore;
                inMenu = null;
            }

            if (mainCamera != null)
            {
                mainCamera.cullingMask = mainMaskBefore;
                mainCamera = null;
            }

            // Closed because the world went away, the cursor stays the menu's.
            if (Application.loadedLevelName == "GAME")
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        // A layer no one in the game has named: the picture's own.
        private static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                {
                    return i;
                }
            }

            return 31;
        }

        private void Styles()
        {
            if (panelStyle != null)
            {
                return;
            }

            panelTexture = Solid(new Color(0.07f, 0.08f, 0.1f, 0.94f));
            chooseTexture = Solid(new Color(0.18f, 0.55f, 0.32f, 1f));
            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = panelTexture;
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            titleStyle.normal.textColor = new Color(0.95f, 0.85f, 0.55f);
            nameStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            nameStyle.normal.textColor = Color.white;
            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            smallStyle.normal.textColor = new Color(0.7f, 0.72f, 0.75f);
            leftStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            leftStyle.normal.textColor = new Color(0.88f, 0.9f, 0.92f);
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
            tabStyle = new GUIStyle(GUI.skin.button) { fontSize = 15 };
            chooseStyle = new GUIStyle(buttonStyle) { fontSize = 16 };
            chooseStyle.normal.background = chooseTexture;
            chooseStyle.hover.background = chooseTexture;
            chooseStyle.normal.textColor = Color.white;
            chooseStyle.hover.textColor = Color.white;
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
    }
}
