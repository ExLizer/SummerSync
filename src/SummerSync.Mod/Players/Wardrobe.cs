using System.Collections.Generic;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// The page where a player picks who they look like: on the left the chosen person turning slowly, on the right a
    /// grid with the face and name of each one. It works in the main menu as in the game world, because the people are
    /// made from the looks this machine kept (<see cref="LookCapture"/>, <see cref="MenuFigure"/>), not from the world.
    ///
    /// The people stand in a studio of their own, far below everything, on a layer nothing else uses, lit by its own lights
    /// and seen by its own camera, which draws into textures: the turning picture every frame, and each face once when the
    /// page opens. A look this machine has not kept yet (it has not been in a world with the mod where that person is)
    /// shows dimmed and cannot be picked.
    /// </summary>
    internal sealed class Wardrobe
    {
        /// <summary>The look that is the capsule (no person).</summary>
        public const int Capsule = -1;

        /// <summary>How many faces a row of the grid has.</summary>
        public const int Columns = 4;
        private const int PictureSize = 512;
        private const int FaceWidth = 128;
        private const int FaceHeight = 144;
        private const float SpinPerSecond = 30f;
        private static readonly Vector3 Studio = new Vector3(0f, -3000f, 0f);

        private readonly List<int> order = new List<int>();
        private readonly Dictionary<int, RenderTexture> faces = new Dictionary<int, RenderTexture>();
        private readonly Dictionary<int, Texture2D> textures = new Dictionary<int, Texture2D>();
        private GameObject studio;
        private Camera lens;
        private RenderTexture picture;
        private MenuFigure shown;
        private GameObject shownHolder;
        private int shownLook = int.MinValue;
        private int layer = -1;
        private float spin;
        private double clock;
        private LookCache cache;
        private Shader fallback;
        private GUIStyle tileName;
        private GUIStyle tileDim;
        private GUIStyle bigName;
        private Texture2D selectedTexture;
        private Texture2D tileTexture;

        /// <summary>The look picked on the page, an index of <see cref="Characters.All"/> or <see cref="Capsule"/>.</summary>
        public int Selected { get; private set; }

        public bool IsOpen
        {
            get { return studio != null; }
        }

        /// <summary>Opens on <paramref name="current"/>: the studio is set up and each face drawn once.</summary>
        public void Begin(int current)
        {
            End();
            cache = LookStore.Current;
            Selected = current;
            order.Clear();
            for (int i = 0; i < Characters.All.Length; i++)
            {
                order.Add(Characters.All[i].Index);
            }

            order.Add(Capsule);
            layer = StudioLayer();
            studio = new GameObject("SummerSync wardrobe");
            studio.transform.position = Studio;
            lens = new GameObject("Lens").AddComponent<Camera>();
            lens.transform.parent = studio.transform;
            lens.enabled = false;
            lens.cullingMask = 1 << layer;
            lens.clearFlags = CameraClearFlags.SolidColor;
            lens.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
            lens.nearClipPlane = 0.1f;
            lens.farClipPlane = 20f;
            lens.renderingPath = RenderingPath.Forward;
            AddLight(new Vector3(30f, 200f, 0f), 0.9f);
            AddLight(new Vector3(10f, 20f, 0f), 0.35f);
            fallback = MenuFigure.Fallback(null);
            picture = new RenderTexture(PictureSize, PictureSize, 16);
            for (int i = 0; i < order.Count; i++)
            {
                DrawFace(order[i]);
            }
        }

        /// <summary>Everything made goes.</summary>
        public void End()
        {
            Drop(ref shown, ref shownHolder);
            shownLook = int.MinValue;
            foreach (RenderTexture face in faces.Values)
            {
                if (face != null)
                {
                    face.Release();
                    Object.Destroy(face);
                }
            }

            faces.Clear();
            foreach (Texture2D texture in textures.Values)
            {
                if (texture != null)
                {
                    Object.Destroy(texture);
                }
            }

            textures.Clear();
            if (picture != null)
            {
                picture.Release();
                Object.Destroy(picture);
                picture = null;
            }

            if (studio != null)
            {
                Object.Destroy(studio);
                studio = null;
            }
        }

        /// <summary>Whether this machine has kept that look (the capsule always is).</summary>
        public bool Has(int look)
        {
            return look == Capsule || (cache != null && cache.Find(look) != null);
        }

        /// <summary>Moves the choice by <paramref name="by"/> places in the grid, over the looks that can be picked.</summary>
        public void Step(int by)
        {
            int at = Mathf.Max(0, order.IndexOf(Selected));
            for (int tries = 0; tries < order.Count; tries++)
            {
                at = (((at + by) % order.Count) + order.Count) % order.Count;
                if (Has(order[at]))
                {
                    Selected = order[at];
                    return;
                }

                by = by > 0 ? 1 : -1;
            }
        }

        /// <summary>Once per frame: the person on the picture turns and stands, and the picture is drawn.</summary>
        public void Update(float deltaTime)
        {
            if (studio == null)
            {
                return;
            }

            clock += deltaTime;
            if (shownLook != Selected)
            {
                Drop(ref shown, ref shownHolder);
                shownLook = Selected;
                shown = Make(Selected, Vector3.zero, out shownHolder);
            }

            spin = (spin + (SpinPerSecond * deltaTime)) % 360f;
            if (shown != null)
            {
                // A person looks along their +Z, at the lens.
                shown.Root.localRotation = Quaternion.Euler(0f, spin, 0f);
                shown.Update(clock);
            }

            // The whole person, from the soles to above the head.
            Aim(new Vector3(0f, 1.0f, 3.2f), new Vector3(0f, 0.92f, 0f), 36f, picture);
        }

        /// <summary>Draws the page in <paramref name="area"/>. Only from OnGUI.</summary>
        public void Draw(Rect area, GUIStyle small)
        {
            Styles();
            float side = Mathf.Min(area.height - 40f, area.width * 0.42f);
            var frame = new Rect(area.x, area.y, side, side);
            if (picture != null)
            {
                GUI.DrawTexture(frame, picture, ScaleMode.ScaleToFit, false);
            }

            Characters.Look look = Characters.At(Selected);
            GUI.Label(new Rect(frame.x, frame.yMax + 4f, side, 32f), look == null ? Texts.Pick("Capsule", "Cápsula") : look.Shown, bigName);
            int missing = 0;
            for (int i = 0; i < order.Count; i++)
            {
                missing += Has(order[i]) ? 0 : 1;
            }

            if (missing > 0)
            {
                GUI.Label(
                    new Rect(frame.x, frame.yMax + 40f, side, 44f),
                    missing + Texts.Pick(
                        " not kept yet: they are kept on their own when you enter a world where that person is.",
                        " sin guardar todavía: se guardan solos al entrar a un mundo donde está esa persona."),
                    small);
            }

            // The grid: as many rows as it takes, each tile as wide as fits.
            var grid = new Rect(frame.xMax + 20f, area.y, area.xMax - frame.xMax - 20f, area.height);
            int rows = (order.Count + Columns - 1) / Columns;
            float gap = 8f;
            float tileWidth = (grid.width - ((Columns - 1) * gap)) / Columns;
            float tileHeight = Mathf.Min(tileWidth * 1.3f, (grid.height - ((rows - 1) * gap)) / rows);
            for (int i = 0; i < order.Count; i++)
            {
                int column = i % Columns;
                int row = i / Columns;
                var tile = new Rect(grid.x + (column * (tileWidth + gap)), grid.y + (row * (tileHeight + gap)), tileWidth, tileHeight);
                int candidate = order[i];
                bool has = Has(candidate);
                GUI.DrawTexture(tile, candidate == Selected ? selectedTexture : tileTexture);
                var inner = new Rect(tile.x + 3f, tile.y + 3f, tile.width - 6f, tile.height - 26f);
                RenderTexture face;
                if (faces.TryGetValue(candidate, out face) && face != null)
                {
                    Color before = GUI.color;
                    if (!has)
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.3f);
                    }

                    GUI.DrawTexture(inner, face, ScaleMode.ScaleAndCrop, false);
                    GUI.color = before;
                }

                Characters.Look named = Characters.At(candidate);
                GUI.Label(new Rect(tile.x, tile.yMax - 24f, tile.width, 22f), named == null ? Texts.Pick("Capsule", "Cápsula") : named.Shown, has ? tileName : tileDim);
                if (has && GUI.Button(tile, GUIContent.none, GUIStyle.none))
                {
                    Selected = candidate;
                }
            }

        }

        private void DrawFace(int look)
        {
            var face = new RenderTexture(FaceWidth, FaceHeight, 16);
            faces[look] = face;
            GameObject holder;
            MenuFigure figure = Make(look, new Vector3(50f, 0f, 0f), out holder);
            if (figure == null)
            {
                // The capsule, or a look not kept: an empty face.
                Aim(new Vector3(50f, 1.6f, 1.35f), new Vector3(50f, 1.56f, 0f), 30f, face);
                return;
            }

            figure.Update(0.0);

            // Head and shoulders, from the front. Then out of sight at once: a destroyed object goes only at the end of
            // the frame, and the next face is drawn in this one.
            Aim(new Vector3(50f, 1.6f, 1.35f), new Vector3(50f, 1.56f, 0f), 30f, face);
            holder.SetActive(false);
            Drop(ref figure, ref holder);
        }

        // A person in the studio, on the object that holds them there; null (and no holder) for one not kept.
        private MenuFigure Make(int look, Vector3 offset, out GameObject holder)
        {
            holder = null;
            CachedLook kept = cache == null || look == Capsule ? null : cache.Find(look);
            if (kept == null)
            {
                return null;
            }

            holder = new GameObject("Look " + look);
            holder.transform.parent = studio.transform;
            holder.transform.localPosition = offset;
            holder.transform.localRotation = Quaternion.identity;
            MenuFigure figure = MenuFigure.Make(cache, kept, holder.transform, fallback, textures, clock);
            SetLayer(holder.transform, layer);
            return figure;
        }

        private static void Drop(ref MenuFigure figure, ref GameObject holder)
        {
            if (figure != null)
            {
                figure.Destroy();
                figure = null;
            }

            if (holder != null)
            {
                Object.Destroy(holder);
                holder = null;
            }
        }

        // The studio's camera, at a place and looking at another (both from the studio), drawn once into a texture.
        private void Aim(Vector3 from, Vector3 at, float fieldOfView, RenderTexture into)
        {
            lens.transform.position = Studio + from;
            lens.transform.LookAt(Studio + at);
            lens.fieldOfView = fieldOfView;
            lens.targetTexture = into;
            lens.Render();
            lens.targetTexture = null;
        }

        private void AddLight(Vector3 angles, float intensity)
        {
            var light = new GameObject("Light").AddComponent<Light>();
            light.transform.parent = studio.transform;
            light.transform.rotation = Quaternion.Euler(angles);
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.cullingMask = 1 << layer;
        }

        private static void SetLayer(Transform at, int layer)
        {
            at.gameObject.layer = layer;
            for (int i = 0; i < at.childCount; i++)
            {
                SetLayer(at.GetChild(i), layer);
            }
        }

        // A layer no one in the game has named, other than the menu lobby's (31).
        private static int StudioLayer()
        {
            for (int i = 30; i >= 8; i--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                {
                    return i;
                }
            }

            return 30;
        }

        private void Styles()
        {
            if (tileName != null)
            {
                return;
            }

            tileName = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            tileName.normal.textColor = Color.white;
            tileDim = new GUIStyle(tileName);
            tileDim.normal.textColor = new Color(0.5f, 0.5f, 0.52f);
            bigName = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            bigName.normal.textColor = Color.white;
            selectedTexture = Solid(new Color(0.85f, 0.7f, 0.2f, 1f));
            tileTexture = Solid(new Color(0.2f, 0.21f, 0.24f, 1f));
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
