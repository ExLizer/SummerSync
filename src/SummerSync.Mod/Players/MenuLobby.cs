using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// The players of a lobby standing in front of the car of the main menu, each as the person they chose, with their
    /// name above (<see cref="MenuFigure"/>, from the looks this machine kept: <see cref="LookCapture"/>).
    ///
    /// What is assumed about the game, from its level file (level0, the main menu) and a look at the menu: the car is
    /// "Scene/Car/MenuCar" under "Scene/Car", drawn twice the size of the world's and turning with an animation of its
    /// own; the camera ("Camera", the main one) looks at it from 11.8 m with a narrow view (33 degrees), so the car fills
    /// most of the screen (seen 2026-10-16). The figures are placed by where they show on the screen, not by metres: their
    /// feet at a height of the screen on the floor the car stands on, in a row across it, each as tall as a part of the
    /// screen, facing the camera. They are not children of the car. As it turns the car would pass through them: they are
    /// drawn by a camera of their own, on a layer nothing of the menu uses (31), after the menu's camera and before the one
    /// that draws its panels (depth 1 and 2: level0's cameras draw layers 0 and 18, and 14), so they always show in front
    /// of the car and behind the panels. A player whose look this machine has not kept (it has never been in a world with
    /// the mod) shows only their name. The figures have a light of their own, which lights only their layer.
    /// </summary>
    internal sealed class MenuLobby
    {
        private const string CarPath = "Scene/Car";
        private const int MostShown = 4;
        private const double ListEvery = 0.5;

        // How tall a person of the game stands, in the world's metres.
        private const float PersonHeight = 1.8f;

        // A layer nothing of the main menu uses: only the figures' camera draws it, and only their light lights it.
        private const int FigureLayer = 31;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly LookSync looks;
        private readonly List<Shown> shown = new List<Shown>();
        private readonly Dictionary<int, Texture2D> textures = new Dictionary<int, Texture2D>();
        private GameObject holder;
        private Shader fallback;
        private bool placed;
        private bool placeReported;
        private float ground;
        private Camera overlay;
        private Camera view;
        private double nextList;
        private double nextPlace;
        private GUIStyle nameStyle;
        private GUIStyle shadowStyle;

        public MenuLobby(Logger log, NetworkManager manager, LookSync looks)
        {
            this.log = log;
            this.manager = manager;
            this.looks = looks;
        }

        /// <summary>Where the feet show, from the bottom of the screen (0) to its top (1). "ss lobby" changes these.</summary>
        public static float Feet = 0.28f;

        /// <summary>How tall each figure shows, as a part of the screen's height.</summary>
        public static float Height = 0.42f;

        /// <summary>
        /// How much of the screen's width the row takes, and where its middle is: left of the host's lobby box, which goes over
        /// the column on the right.
        /// </summary>
        public static float Width = 0.44f;

        public static float Middle = 0.38f;

        // Changed from the console: the row is made again.
        private static bool retune;

        /// <summary>Once per frame. <paramref name="inMenu"/>: whether the main menu is the scene.</summary>
        public void LateUpdate(double now, bool inMenu)
        {
            NetSession session = manager.Session;
            bool lobby = inMenu && session != null && session.State == SessionState.Connected && (manager.Mode == NetworkMode.Host || manager.Mode == NetworkMode.Client);
            if (!lobby)
            {
                Clear();
                if (!inMenu)
                {
                    placed = false;
                }

                return;
            }

            if (!placed)
            {
                // The menu's car is looked for every couple of seconds until it is found.
                if (now < nextPlace)
                {
                    return;
                }

                nextPlace = now + 2.0;
                if (!Place())
                {
                    return;
                }
            }

            if (now >= nextList)
            {
                nextList = now + ListEvery;
                Follow(session, now);
            }

            for (int i = 0; i < shown.Count; i++)
            {
                if (shown[i].Figure != null)
                {
                    shown[i].Figure.Update(now);
                }
            }

            if (overlay != null)
            {
                Match(overlay);
            }

            // Not over the game's licence or credits: the menu's own column is hidden then.
            bool visible = Menu.MenuColumn.InterfaceShown;
            if (holder != null && holder.activeSelf != visible)
            {
                holder.SetActive(visible);
            }
        }

        // The figures' camera sees as the menu's does.
        private void Match(Camera camera)
        {
            camera.transform.position = view.transform.position;
            camera.transform.rotation = view.transform.rotation;
            camera.fieldOfView = view.fieldOfView;
            camera.nearClipPlane = view.nearClipPlane;
            camera.farClipPlane = view.farClipPlane;
        }

        // Every camera of the scene as it is: for the log.
        private static string Cameras()
        {
            var parts = new List<string>();
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                Camera camera = all[i];
                parts.Add("'" + camera.name + "' depth " + Show(camera.depth) + " " + camera.clearFlags + " mask " + camera.cullingMask.ToString("x8", CultureInfo.InvariantCulture)
                    + (camera.targetTexture != null ? " into '" + camera.targetTexture.name + "'" : string.Empty) + " " + camera.renderingPath + " view " + camera.rect);
            }

            return all.Length + ": " + string.Join("; ", parts.ToArray());
        }

        private static void SetLayer(Transform at)
        {
            at.gameObject.layer = FigureLayer;
            for (int i = 0; i < at.childCount; i++)
            {
                SetLayer(at.GetChild(i));
            }
        }

        public void OnGUI()
        {
            if (shown.Count == 0 || Event.current.type != EventType.Repaint || view == null || !Menu.MenuColumn.InterfaceShown)
            {
                return;
            }

            // Sized for 1080 lines, and grown with the screen.
            float k = Mathf.Max(1f, Screen.height / 1080f);
            if (nameStyle == null || nameStyle.fontSize != Mathf.RoundToInt(20 * k))
            {
                nameStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(20 * k), fontStyle = FontStyle.Bold };
                nameStyle.normal.textColor = Color.white;
                shadowStyle = new GUIStyle(nameStyle);
                shadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            }

            for (int i = 0; i < shown.Count; i++)
            {
                Vector3 top = shown[i].Figure != null ? shown[i].Figure.Top : shown[i].Root.position + (Vector3.up * PersonHeight * shown[i].Root.localScale.y);
                Vector3 screen = view.WorldToScreenPoint(top);
                if (screen.z <= 0f)
                {
                    continue;
                }

                var rect = new Rect(screen.x - (200f * k), Screen.height - screen.y - (32f * k), 400f * k, 30f * k);
                string label = Texts.Pick(shown[i].Label, shown[i].LabelSpanish);
                GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), label, shadowStyle);
                GUI.Label(rect, label, nameStyle);
            }
        }

        /// <summary>Everything made goes.</summary>
        public void Clear()
        {
            // Called in every frame out of a lobby: nothing to do, nothing made.
            if (shown.Count == 0 && textures.Count == 0 && holder == null)
            {
                return;
            }

            for (int i = 0; i < shown.Count; i++)
            {
                shown[i].Destroy();
            }

            shown.Clear();
            foreach (Texture2D texture in textures.Values)
            {
                if (texture != null)
                {
                    Object.Destroy(texture);
                }
            }

            textures.Clear();
            if (holder != null)
            {
                Object.Destroy(holder);
                holder = null;
                overlay = null;
            }
        }

        /// <summary>For the console: where the figures show. The row is made again with it.</summary>
        public static string Tune(string feet, string height, string width, string middle)
        {
            Feet = Read(feet, Feet);
            Height = Read(height, Height);
            Width = Read(width, Width);
            Middle = Read(middle, Middle);
            retune = true;
            return "Lobby figures: feet at " + Show(Feet) + " of the screen's height, " + Show(Height) + " of it tall, a row " + Show(Width) + " of its width around "
                + Show(Middle) + ".";
        }

        private static float Read(string text, float otherwise)
        {
            float value;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : otherwise;
        }

        private static string Show(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // The floor the car stands on, and the camera.
        private bool Place()
        {
            GameObject sceneRoot = GameObject.Find("/Scene");
            Transform car = sceneRoot == null ? null : sceneRoot.transform.Find(CarPath.Substring("Scene/".Length));
            Camera camera = Camera.main;
            Renderer[] renderers = car == null ? new Renderer[0] : car.GetComponentsInChildren<Renderer>(false);
            if (camera == null || renderers.Length == 0)
            {
                if (!placeReported)
                {
                    placeReported = true;
                    log.Warning(LogCategory.Player, "Lobby: the menu's car ('" + CarPath + "') or its camera was not found; the players are not shown in the menu.");
                }

                return false;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            ground = bounds.min.y;
            view = camera;

            fallback = MenuFigure.Fallback(renderers[0].sharedMaterial != null ? renderers[0].sharedMaterial.shader : null);
            placed = true;
            log.Info(
                LogCategory.Player,
                "Lobby: the menu's car found (" + renderers.Length + " part(s), floor at " + Show(ground) + ", camera at " + camera.transform.position + " with a view of "
                + Show(camera.fieldOfView) + " degrees); figures drawn with '" + (fallback != null ? fallback.name : "nothing") + "' where the menu lacks the game's shader.");
            return true;
        }

        // Where a figure stands to show its feet at that point of the screen: where the line of sight meets the car's floor.
        private bool Stand(float x, out Vector3 feet, out float size)
        {
            feet = Vector3.zero;
            size = 1f;
            Ray sight = view.ViewportPointToRay(new Vector3(x, Feet, 0f));
            if (sight.direction.y >= -0.001f)
            {
                return false;
            }

            float along = (ground - sight.origin.y) / sight.direction.y;
            feet = sight.origin + (sight.direction * along);

            // As tall as that part of the screen, at that distance.
            float depth = Vector3.Dot(feet - view.transform.position, view.transform.forward);
            float shownHeight = 2f * depth * Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
            size = (Height * shownHeight) / PersonHeight;
            return true;
        }

        // The players of the session, each shown as their look, in the order of their numbers.
        private void Follow(NetSession session, double now)
        {
            IList<PlayerInfo> players = session.Players;
            int count = Mathf.Min(players.Count, MostShown);
            bool same = count == shown.Count;
            for (int i = 0; same && i < count; i++)
            {
                same = shown[i].Player == players[i].Id && shown[i].Look == LookOf(players[i]) && shown[i].Name == players[i].Name;
            }

            if (same && !retune)
            {
                return;
            }

            retune = false;

            for (int i = 0; i < shown.Count; i++)
            {
                shown[i].Destroy();
            }

            shown.Clear();
            if (holder == null)
            {
                holder = new GameObject("SummerSync lobby");

                // Drawn after the menu's camera, before the panels', with what it sees: only the figures.
                overlay = new GameObject("SummerSync lobby camera").AddComponent<Camera>();
                overlay.transform.parent = holder.transform;
                overlay.clearFlags = CameraClearFlags.Depth;

                // As the menu's own camera: the game's default (deferred) paints over the whole screen in Unity 5.0 when only
                // its depth is cleared (seen 2026-10-16: the menu went black).
                overlay.renderingPath = RenderingPath.Forward;
                overlay.cullingMask = 1 << FigureLayer;
                overlay.depth = view.depth + 0.5f;
                Match(overlay);
                log.Info(LogCategory.Player, "Lobby: cameras " + Cameras() + ".");
            }

            LookCache cache = LookStore.Current;
            Vector3 lampAt = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                PlayerInfo player = players[i];
                int look = LookOf(player);
                float x = count == 1 ? Middle : Middle - (Width * 0.5f) + (Width * i / (count - 1));
                Vector3 feet;
                float size;
                if (!Stand(x, out feet, out size))
                {
                    continue;
                }

                Vector3 toCamera = view.transform.position - feet;
                toCamera.y = 0f;
                var slot = new GameObject("Player " + player.Id);
                slot.transform.parent = holder.transform;
                slot.transform.position = feet;
                slot.transform.rotation = Quaternion.LookRotation(toCamera.sqrMagnitude > 0.01f ? toCamera.normalized : Vector3.back, Vector3.up);
                slot.transform.localScale = Vector3.one * size;
                lampAt += feet + (toCamera.normalized * 2f * size) + (Vector3.up * 1.8f * size);
                var entry = new Shown { Player = player.Id, Look = look, Name = player.Name, Root = slot.transform, Label = Label(player, false), LabelSpanish = Label(player, true) };
                CachedLook kept = cache == null ? null : cache.Find(look);
                if (kept != null)
                {
                    entry.Figure = MenuFigure.Make(cache, kept, slot.transform, fallback, textures, now);
                }

                SetLayer(slot.transform);

                shown.Add(entry);
            }

            if (shown.Count > 0)
            {
                Light lamp = holder.GetComponentInChildren<Light>();
                if (lamp == null)
                {
                    lamp = new GameObject("Light").AddComponent<Light>();
                    lamp.transform.parent = holder.transform;
                    lamp.type = LightType.Point;
                    lamp.color = new Color(1f, 0.92f, 0.82f);
                    lamp.intensity = 1.4f;
                    lamp.cullingMask = 1 << FigureLayer;
                }

                lamp.gameObject.layer = FigureLayer;
                lamp.transform.position = lampAt / shown.Count;
                lamp.range = 6f * shown[0].Root.localScale.y;
            }

            log.Info(LogCategory.Player, "Lobby: " + shown.Count + " player(s) in front of the menu's car (" + Describe() + ").");
        }

        private int LookOf(PlayerInfo player)
        {
            return player.IsLocal ? (looks.Previewing ?? looks.Mine) : looks.LookOf(player.Id);
        }

        private static string Label(PlayerInfo player, bool spanish)
        {
            return player.Name + (player.IsHost ? (spanish ? " (anfitrión)" : " (host)") : string.Empty);
        }

        private string Describe()
        {
            var parts = new List<string>();
            for (int i = 0; i < shown.Count; i++)
            {
                Characters.Look look = Characters.At(shown[i].Look);
                parts.Add(shown[i].Name + (shown[i].Figure != null && look != null ? " as " + look.Name : " with no look kept here")
                    + " at " + shown[i].Root.position + " size " + Show(shown[i].Root.localScale.y));
            }

            return string.Join(", ", parts.ToArray());
        }

        private sealed class Shown
        {
            public byte Player;
            public int Look;
            public string Name;
            public string Label;
            public string LabelSpanish;
            public Transform Root;
            public MenuFigure Figure;

            public void Destroy()
            {
                if (Figure != null)
                {
                    Figure.Destroy();
                }

                if (Root != null)
                {
                    Object.Destroy(Root.gameObject);
                }
            }
        }
    }
}
