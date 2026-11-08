using System;
using System.Collections.Generic;
using System.IO;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// The looks for the main menu, taken from the game's world (<see cref="LookCache"/>): a while after a world loads,
    /// each look the cache does not have yet is made as a copy (<see cref="CharacterModel"/>), and its bones, meshes,
    /// materials, textures and standing loop are written down, one look a frame. Then the file in the mod's folder is
    /// written again. A look whose person this world does not have waits for another world.
    ///
    /// The game's meshes of its people can be read (their level files say so: m_IsReadable); a texture is read back
    /// through a render texture, which needs nothing of it.
    /// </summary>
    internal sealed class LookCapture
    {
        private const double StartAfter = 8.0;
        private const float FrameRate = 15f;
        private const float LongestLoop = 8f;
        private const int LargestTexture = 1024;
        private const float Still = 0.0001f;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private Transform world;
        private double startAt = double.NaN;
        private int next;
        private bool done;
        private bool changed;
        private GameObject holder;

        public LookCapture(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null with no game world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            if (localPlayer != world)
            {
                world = localPlayer;
                startAt = localPlayer == null ? double.NaN : now + StartAfter;
                next = 0;
                done = false;
                changed = false;
            }

            if (done || world == null || now < startAt)
            {
                return;
            }

            string gameBuild = manager.GameVersion ?? string.Empty;
            LookCache cache = LookStore.Current;
            if (cache == null || cache.GameBuild != gameBuild || cache.ListShape != LookStore.ListShape)
            {
                cache = new LookCache(gameBuild, LookStore.ListShape);
                LookStore.Current = cache;
                changed = true;
            }

            // One look a frame, among those not kept yet.
            while (next < Characters.All.Length && cache.Find(Characters.All[next].Index) != null)
            {
                next++;
            }

            if (next < Characters.All.Length)
            {
                Characters.Look look = Characters.All[next++];
                CachedLook taken = null;
                try
                {
                    taken = Take(look, cache);
                }
                catch (Exception exception)
                {
                    log.Warning(LogCategory.Player, "Looks for the menu: '" + look.Name + "' could not be taken: " + exception.Message);
                }

                if (taken != null)
                {
                    cache.Looks.Add(taken);
                    changed = true;
                }

                return;
            }

            done = true;
            if (holder != null)
            {
                UnityEngine.Object.Destroy(holder);
                holder = null;
            }

            if (changed)
            {
                LookStore.Save(log);
            }
        }

        private CachedLook Take(Characters.Look look, LookCache cache)
        {
            if (holder == null)
            {
                holder = new GameObject("SummerSync looks for the menu");
                holder.transform.position = new Vector3(0f, -500f, 0f);
            }

            CharacterModel model = CharacterModel.Build(look, holder.transform, "Look " + look.Index, -1, null);
            if (model == null)
            {
                return null;
            }

            try
            {
                Transform root = model.Root;
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.identity;
                root.gameObject.SetActive(true);

                // Every object of the copy, parents first; the copy itself is the first.
                var bones = new List<Transform>();
                var parents = new List<int>();
                var index = new Dictionary<Transform, int>();
                Gather(root, -1, bones, parents, index);

                var result = new CachedLook { Index = look.Index, Name = look.Name };

                // The standing loop: every bone in every frame, then only those that move.
                float length = Mathf.Min(LongestLoop, model.StandLength);
                int frames = length > 0f ? Mathf.Max(2, Mathf.RoundToInt(length * FrameRate)) : 1;
                var rotations = new Quaternion[bones.Count, frames];
                var positions = new Vector3[bones.Count, frames];
                for (int f = 0; f < frames; f++)
                {
                    model.SampleStanding(f / FrameRate);
                    for (int b = 0; b < bones.Count; b++)
                    {
                        rotations[b, f] = bones[b].localRotation;
                        positions[b, f] = bones[b].localPosition;
                    }
                }

                // The pose at rest is the first frame.
                model.SampleStanding(0f);
                result.Bones = new CachedBone[bones.Count];
                for (int b = 0; b < bones.Count; b++)
                {
                    Transform bone = bones[b];
                    result.Bones[b] = new CachedBone
                    {
                        Name = bone.name,
                        Parent = parents[b],
                        Position = b == 0 ? new float[3] : Floats(bone.localPosition),
                        Rotation = b == 0 ? new[] { 0f, 0f, 0f, 1f } : Floats(bone.localRotation),
                        Scale = Floats(bone.localScale),
                    };
                }

                result.Idle = frames > 1 ? Loop(rotations, positions, bones.Count, frames) : null;

                var materials = new List<CachedMaterial>();
                var materialIndex = new Dictionary<Material, int>();
                var meshes = new List<CachedMesh>();
                SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(false);
                for (int i = 0; i < skinned.Length; i++)
                {
                    CachedMesh mesh = Skinned(skinned[i], index, cache, materials, materialIndex);
                    if (mesh != null)
                    {
                        meshes.Add(mesh);
                    }
                }

                MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(false);
                for (int i = 0; i < filters.Length; i++)
                {
                    CachedMesh mesh = Rigid(filters[i], index, cache, materials, materialIndex);
                    if (mesh != null)
                    {
                        meshes.Add(mesh);
                    }
                }

                result.Meshes = meshes.ToArray();
                result.Materials = materials.ToArray();
                int vertices = 0;
                for (int i = 0; i < result.Meshes.Length; i++)
                {
                    vertices += result.Meshes[i].Vertices.Length / 3;
                }

                log.Info(
                    LogCategory.Player,
                    "Looks for the menu: '" + look.Name + "' taken: " + bones.Count + " bone(s), " + result.Meshes.Length + " mesh(es) of " + vertices + " vertices, "
                    + result.Materials.Length + " material(s) (" + Shaders(result.Materials) + "), standing " + frames + " frame(s)"
                    + (result.Idle == null ? string.Empty : ", " + result.Idle.Bones.Length + " bone(s) moving") + ".");
                return result;
            }
            finally
            {
                model.Destroy();
            }
        }

        private static void Gather(Transform at, int parent, List<Transform> bones, List<int> parents, Dictionary<Transform, int> index)
        {
            int self = bones.Count;
            bones.Add(at);
            parents.Add(parent);
            index[at] = self;
            for (int i = 0; i < at.childCount; i++)
            {
                Gather(at.GetChild(i), self, bones, parents, index);
            }
        }

        private static CachedClip Loop(Quaternion[,] rotations, Vector3[,] positions, int bones, int frames)
        {
            var moving = new List<int>();
            for (int b = 1; b < bones; b++)
            {
                for (int f = 1; f < frames; f++)
                {
                    if (Quaternion.Angle(rotations[b, f], rotations[b, 0]) > 0.05f || (positions[b, f] - positions[b, 0]).sqrMagnitude > Still)
                    {
                        moving.Add(b);
                        break;
                    }
                }
            }

            var clip = new CachedClip
            {
                Rate = FrameRate,
                Frames = frames,
                Bones = moving.ToArray(),
                Rotations = new float[moving.Count * frames * 4],
                Positions = new float[moving.Count * frames * 3],
            };
            for (int m = 0; m < moving.Count; m++)
            {
                for (int f = 0; f < frames; f++)
                {
                    Quaternion rotation = rotations[moving[m], f];
                    Vector3 position = positions[moving[m], f];
                    int r = ((m * frames) + f) * 4;
                    clip.Rotations[r] = rotation.x;
                    clip.Rotations[r + 1] = rotation.y;
                    clip.Rotations[r + 2] = rotation.z;
                    clip.Rotations[r + 3] = rotation.w;
                    int p = ((m * frames) + f) * 3;
                    clip.Positions[p] = position.x;
                    clip.Positions[p + 1] = position.y;
                    clip.Positions[p + 2] = position.z;
                }
            }

            return clip;
        }

        private CachedMesh Skinned(SkinnedMeshRenderer renderer, Dictionary<Transform, int> index, LookCache cache, List<CachedMaterial> materials, Dictionary<Material, int> materialIndex)
        {
            Mesh shared = renderer.sharedMesh;
            int self;
            if (shared == null || !index.TryGetValue(renderer.transform, out self))
            {
                return null;
            }

            Transform[] bones = renderer.bones;
            var boneIndices = new int[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || !index.TryGetValue(bones[i], out boneIndices[i]))
                {
                    return null;
                }
            }

            int root;
            if (renderer.rootBone == null || !index.TryGetValue(renderer.rootBone, out root))
            {
                root = boneIndices.Length > 0 ? boneIndices[0] : self;
            }

            CachedMesh mesh = Geometry(shared, self, renderer.sharedMaterials, cache, materials, materialIndex);
            mesh.Skinned = true;
            mesh.RootBone = root;
            mesh.Bones = boneIndices;
            BoneWeight[] weights = shared.boneWeights;
            mesh.BoneIndices = new int[weights.Length * 4];
            mesh.BoneWeights = new float[weights.Length * 4];
            for (int v = 0; v < weights.Length; v++)
            {
                mesh.BoneIndices[(v * 4) + 0] = weights[v].boneIndex0;
                mesh.BoneIndices[(v * 4) + 1] = weights[v].boneIndex1;
                mesh.BoneIndices[(v * 4) + 2] = weights[v].boneIndex2;
                mesh.BoneIndices[(v * 4) + 3] = weights[v].boneIndex3;
                mesh.BoneWeights[(v * 4) + 0] = weights[v].weight0;
                mesh.BoneWeights[(v * 4) + 1] = weights[v].weight1;
                mesh.BoneWeights[(v * 4) + 2] = weights[v].weight2;
                mesh.BoneWeights[(v * 4) + 3] = weights[v].weight3;
            }

            Matrix4x4[] bindposes = shared.bindposes;
            mesh.Bindposes = new float[bindposes.Length * 16];
            for (int b = 0; b < bindposes.Length; b++)
            {
                for (int k = 0; k < 16; k++)
                {
                    mesh.Bindposes[(b * 16) + k] = bindposes[b][k];
                }
            }

            return mesh.BoneIndices.Length == (mesh.Vertices.Length / 3) * 4 && bindposes.Length == boneIndices.Length ? mesh : null;
        }

        private CachedMesh Rigid(MeshFilter filter, Dictionary<Transform, int> index, LookCache cache, List<CachedMaterial> materials, Dictionary<Material, int> materialIndex)
        {
            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            int self;
            if (filter.sharedMesh == null || renderer == null || !index.TryGetValue(filter.transform, out self))
            {
                return null;
            }

            return Geometry(filter.sharedMesh, self, renderer.sharedMaterials, cache, materials, materialIndex);
        }

        private CachedMesh Geometry(Mesh shared, int bone, Material[] sharedMaterials, LookCache cache, List<CachedMaterial> materials, Dictionary<Material, int> materialIndex)
        {
            var mesh = new CachedMesh { Bone = bone };
            Vector3[] vertices = shared.vertices;
            Vector3[] normals = shared.normals;
            Vector2[] uv = shared.uv;
            mesh.Vertices = new float[vertices.Length * 3];
            for (int v = 0; v < vertices.Length; v++)
            {
                mesh.Vertices[v * 3] = vertices[v].x;
                mesh.Vertices[(v * 3) + 1] = vertices[v].y;
                mesh.Vertices[(v * 3) + 2] = vertices[v].z;
            }

            if (normals.Length == vertices.Length)
            {
                mesh.Normals = new float[normals.Length * 3];
                for (int v = 0; v < normals.Length; v++)
                {
                    mesh.Normals[v * 3] = normals[v].x;
                    mesh.Normals[(v * 3) + 1] = normals[v].y;
                    mesh.Normals[(v * 3) + 2] = normals[v].z;
                }
            }

            if (uv.Length == vertices.Length)
            {
                mesh.Uv = new float[uv.Length * 2];
                for (int v = 0; v < uv.Length; v++)
                {
                    mesh.Uv[v * 2] = uv[v].x;
                    mesh.Uv[(v * 2) + 1] = uv[v].y;
                }
            }

            mesh.Submeshes = new int[shared.subMeshCount][];
            mesh.Materials = new int[shared.subMeshCount];
            for (int s = 0; s < shared.subMeshCount; s++)
            {
                mesh.Submeshes[s] = shared.GetTriangles(s);
                Material material = sharedMaterials != null && sharedMaterials.Length > 0 ? sharedMaterials[Mathf.Min(s, sharedMaterials.Length - 1)] : null;
                mesh.Materials[s] = MaterialIndex(material, cache, materials, materialIndex);
            }

            return mesh;
        }

        private int MaterialIndex(Material material, LookCache cache, List<CachedMaterial> materials, Dictionary<Material, int> materialIndex)
        {
            int found;
            if (material != null && materialIndex.TryGetValue(material, out found))
            {
                return found;
            }

            var taken = new CachedMaterial { Shader = material != null && material.shader != null ? material.shader.name : string.Empty };
            if (material != null)
            {
                Color color = material.HasProperty("_Color") ? material.color : Color.white;
                taken.Color = new[] { color.r, color.g, color.b, color.a };
                Texture texture = material.HasProperty("_MainTex") ? material.mainTexture : null;
                if (texture != null)
                {
                    Vector2 scale = material.mainTextureScale;
                    Vector2 offset = material.mainTextureOffset;
                    taken.TextureScale = new[] { scale.x, scale.y };
                    taken.TextureOffset = new[] { offset.x, offset.y };
                    taken.Texture = cache.TextureIndex(texture.name + "#" + texture.width + "x" + texture.height, () => Png(texture));
                }
            }

            materials.Add(taken);
            if (material != null)
            {
                materialIndex[material] = materials.Count - 1;
            }

            return materials.Count - 1;
        }

        // Any texture, read back through a render texture: the game's are not readable as they are.
        private static byte[] Png(Texture texture)
        {
            int width = texture.width;
            int height = texture.height;
            while (width > LargestTexture || height > LargestTexture)
            {
                width = Mathf.Max(1, width / 2);
                height = Mathf.Max(1, height / 2);
            }

            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture before = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                copy = new Texture2D(width, height, TextureFormat.ARGB32, false);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                return copy.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(target);
                if (copy != null)
                {
                    UnityEngine.Object.Destroy(copy);
                }
            }
        }

        private static string Shaders(CachedMaterial[] materials)
        {
            var names = new List<string>();
            for (int i = 0; i < materials.Length; i++)
            {
                if (!names.Contains(materials[i].Shader))
                {
                    names.Add(materials[i].Shader);
                }
            }

            return string.Join(", ", names.ToArray());
        }

        private static float[] Floats(Vector3 value)
        {
            return new[] { value.x, value.y, value.z };
        }

        private static float[] Floats(Quaternion value)
        {
            return new[] { value.x, value.y, value.z, value.w };
        }
    }

    /// <summary>The cache of looks on this machine: read once, written when a world adds to it.</summary>
    internal static class LookStore
    {
        private const string FileName = "looks.cache";
        private static bool loaded;
        private static LookCache current;

        /// <summary>The mod's list of looks, by name: a cache made for another list is made again.</summary>
        public static string ListShape
        {
            get
            {
                var names = new string[Characters.All.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    names[i] = Characters.All[i].Name;
                }

                return string.Join("|", names);
            }
        }

        /// <summary>What this machine has, read from its file the first time; null if it has nothing yet.</summary>
        public static LookCache Current
        {
            get
            {
                if (!loaded)
                {
                    loaded = true;
                    current = Load();
                }

                return current;
            }

            set
            {
                loaded = true;
                current = value;
            }
        }

        private static string Path
        {
            get { return string.IsNullOrEmpty(ModRuntime.SettingsFolder) ? null : System.IO.Path.Combine(ModRuntime.SettingsFolder, FileName); }
        }

        public static void Save(Logger log)
        {
            string path = Path;
            if (path == null || current == null)
            {
                return;
            }

            try
            {
                byte[] bytes = current.Write();
                File.WriteAllBytes(path, bytes);
                log.Info(LogCategory.Player, "Looks for the menu: " + current.Looks.Count + " of " + Characters.All.Length + " kept in '" + path + "' (" + (bytes.Length / 1024) + " kB).");
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Player, "Looks for the menu could not be written to '" + path + "': " + exception.Message);
            }
        }

        private static LookCache Load()
        {
            string path = Path;
            try
            {
                if (path == null || !File.Exists(path))
                {
                    return null;
                }

                return LookCache.Read(File.ReadAllBytes(path));
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Warning(LogCategory.Player, "Looks for the menu in '" + path + "' could not be read (" + exception.Message + "); they are taken again in the next world.");
                return null;
            }
        }
    }
}
