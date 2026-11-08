using System.Collections.Generic;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// One of the game's people standing in the main menu, made again from what <see cref="LookCapture"/> kept: its
    /// bones, its meshes skinned to them, its materials with the game's shader when the menu has it (the main menu
    /// loads only its own) or the one the menu's car is drawn with, and its standing loop, played by moving the bones.
    /// </summary>
    internal sealed class MenuFigure
    {
        private static readonly string[] FallbackShaders = { "Legacy Shaders/Diffuse", "Diffuse", "Standard" };

        private readonly GameObject root;
        private readonly Transform[] bones;
        private readonly CachedClip idle;
        private readonly Transform head;
        private readonly double startedAt;

        private MenuFigure(GameObject root, Transform[] bones, CachedClip idle, double startedAt)
        {
            this.root = root;
            this.bones = bones;
            this.idle = idle;
            this.startedAt = startedAt;
            for (int i = 0; i < bones.Length && head == null; i++)
            {
                if (bones[i].name == "head" || bones[i].name == "Head")
                {
                    head = bones[i];
                }
            }
        }

        public Transform Root
        {
            get { return root.transform; }
        }

        /// <summary>The top of the head, for the name above it.</summary>
        public Vector3 Top
        {
            get { return head != null ? head.position + (Vector3.up * 0.2f * root.transform.lossyScale.y) : root.transform.position + (Vector3.up * 1.9f * root.transform.lossyScale.y); }
        }

        /// <summary>
        /// The person of <paramref name="look"/> made in this scene, under <paramref name="parent"/>, its feet at the parent's
        /// origin. <paramref name="fallback"/> draws what the menu has no shader for. <paramref name="textures"/> keeps the
        /// textures made, shared by the figures and destroyed with them.
        /// </summary>
        public static MenuFigure Make(LookCache cache, CachedLook look, Transform parent, Shader fallback, Dictionary<int, Texture2D> textures, double now)
        {
            var made = new Transform[look.Bones.Length];
            for (int i = 0; i < made.Length; i++)
            {
                CachedBone bone = look.Bones[i];
                var gameObject = new GameObject(i == 0 ? "Look " + look.Name : bone.Name);
                Transform transform = gameObject.transform;
                transform.parent = i == 0 ? parent : made[bone.Parent];
                transform.localPosition = new Vector3(bone.Position[0], bone.Position[1], bone.Position[2]);
                transform.localRotation = new Quaternion(bone.Rotation[0], bone.Rotation[1], bone.Rotation[2], bone.Rotation[3]);
                transform.localScale = new Vector3(bone.Scale[0], bone.Scale[1], bone.Scale[2]);
                made[i] = transform;
            }

            var materials = new Material[look.Materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = MakeMaterial(cache, look.Materials[i], fallback, textures);
            }

            for (int m = 0; m < look.Meshes.Length; m++)
            {
                AddMesh(look.Meshes[m], made, materials);
            }

            return new MenuFigure(made[0].gameObject, made, look.Idle, now);
        }

        /// <summary>Plays its standing loop.</summary>
        public void Update(double now)
        {
            if (idle == null || idle.Frames < 2 || root == null)
            {
                return;
            }

            int before;
            int after;
            float blend;
            idle.Between(now - startedAt, out before, out after, out blend);
            for (int m = 0; m < idle.Bones.Length; m++)
            {
                Transform bone = bones[idle.Bones[m]];
                int a = (m * idle.Frames) + before;
                int b = (m * idle.Frames) + after;
                float[] r = idle.Rotations;
                float[] p = idle.Positions;
                bone.localRotation = Quaternion.Slerp(
                    new Quaternion(r[a * 4], r[(a * 4) + 1], r[(a * 4) + 2], r[(a * 4) + 3]),
                    new Quaternion(r[b * 4], r[(b * 4) + 1], r[(b * 4) + 2], r[(b * 4) + 3]),
                    blend);
                bone.localPosition = Vector3.Lerp(new Vector3(p[a * 3], p[(a * 3) + 1], p[(a * 3) + 2]), new Vector3(p[b * 3], p[(b * 3) + 1], p[(b * 3) + 2]), blend);
            }
        }

        public void Destroy()
        {
            if (root == null)
            {
                return;
            }

            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                Object.Destroy(filters[i].sharedMesh);
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var skinned = renderers[i] as SkinnedMeshRenderer;
                if (skinned != null)
                {
                    Object.Destroy(skinned.sharedMesh);
                }

                Material[] owned = renderers[i].sharedMaterials;
                for (int k = 0; k < owned.Length; k++)
                {
                    Object.Destroy(owned[k]);
                }
            }

            Object.Destroy(root);
        }

        /// <summary>The shader to draw with when the menu does not have the game's: the first of the usual ones it has, or <paramref name="seen"/>.</summary>
        public static Shader Fallback(Shader seen)
        {
            for (int i = 0; i < FallbackShaders.Length; i++)
            {
                Shader shader = Shader.Find(FallbackShaders[i]);
                if (shader != null)
                {
                    return shader;
                }
            }

            return seen;
        }

        private static Material MakeMaterial(LookCache cache, CachedMaterial taken, Shader fallback, Dictionary<int, Texture2D> textures)
        {
            Shader shader = string.IsNullOrEmpty(taken.Shader) ? null : Shader.Find(taken.Shader);
            var material = new Material(shader ?? fallback);
            if (material.HasProperty("_Color"))
            {
                material.color = new Color(taken.Color[0], taken.Color[1], taken.Color[2], taken.Color[3]);
            }

            if (taken.Texture >= 0 && taken.Texture < cache.Textures.Count && material.HasProperty("_MainTex"))
            {
                Texture2D texture;
                if (!textures.TryGetValue(taken.Texture, out texture) || texture == null)
                {
                    texture = new Texture2D(2, 2, TextureFormat.ARGB32, true);
                    texture.LoadImage(cache.Textures[taken.Texture].Png);
                    texture.name = cache.Textures[taken.Texture].Name;
                    textures[taken.Texture] = texture;
                }

                material.mainTexture = texture;
                material.mainTextureScale = new Vector2(taken.TextureScale[0], taken.TextureScale[1]);
                material.mainTextureOffset = new Vector2(taken.TextureOffset[0], taken.TextureOffset[1]);
            }

            return material;
        }

        private static void AddMesh(CachedMesh taken, Transform[] made, Material[] materials)
        {
            int count = taken.Vertices.Length / 3;
            var mesh = new Mesh();
            var vertices = new Vector3[count];
            for (int v = 0; v < count; v++)
            {
                vertices[v] = new Vector3(taken.Vertices[v * 3], taken.Vertices[(v * 3) + 1], taken.Vertices[(v * 3) + 2]);
            }

            mesh.vertices = vertices;
            if (taken.Normals.Length == taken.Vertices.Length)
            {
                var normals = new Vector3[count];
                for (int v = 0; v < count; v++)
                {
                    normals[v] = new Vector3(taken.Normals[v * 3], taken.Normals[(v * 3) + 1], taken.Normals[(v * 3) + 2]);
                }

                mesh.normals = normals;
            }

            if (taken.Uv.Length == count * 2)
            {
                var uv = new Vector2[count];
                for (int v = 0; v < count; v++)
                {
                    uv[v] = new Vector2(taken.Uv[v * 2], taken.Uv[(v * 2) + 1]);
                }

                mesh.uv = uv;
            }

            mesh.subMeshCount = taken.Submeshes.Length;
            for (int s = 0; s < taken.Submeshes.Length; s++)
            {
                mesh.SetTriangles(taken.Submeshes[s], s);
            }

            var shown = new Material[taken.Materials.Length];
            for (int s = 0; s < shown.Length; s++)
            {
                shown[s] = materials[taken.Materials[s]];
            }

            GameObject on = made[taken.Bone].gameObject;
            if (!taken.Skinned)
            {
                mesh.RecalculateBounds();
                on.AddComponent<MeshFilter>().sharedMesh = mesh;
                on.AddComponent<MeshRenderer>().sharedMaterials = shown;
                return;
            }

            var weights = new BoneWeight[count];
            for (int v = 0; v < count; v++)
            {
                weights[v] = new BoneWeight
                {
                    boneIndex0 = taken.BoneIndices[v * 4],
                    boneIndex1 = taken.BoneIndices[(v * 4) + 1],
                    boneIndex2 = taken.BoneIndices[(v * 4) + 2],
                    boneIndex3 = taken.BoneIndices[(v * 4) + 3],
                    weight0 = taken.BoneWeights[v * 4],
                    weight1 = taken.BoneWeights[(v * 4) + 1],
                    weight2 = taken.BoneWeights[(v * 4) + 2],
                    weight3 = taken.BoneWeights[(v * 4) + 3],
                };
            }

            mesh.boneWeights = weights;
            var bindposes = new Matrix4x4[taken.Bones.Length];
            for (int b = 0; b < bindposes.Length; b++)
            {
                var matrix = new Matrix4x4();
                for (int k = 0; k < 16; k++)
                {
                    matrix[k] = taken.Bindposes[(b * 16) + k];
                }

                bindposes[b] = matrix;
            }

            mesh.bindposes = bindposes;
            mesh.RecalculateBounds();
            var renderer = on.AddComponent<SkinnedMeshRenderer>();
            var skin = new Transform[taken.Bones.Length];
            for (int b = 0; b < skin.Length; b++)
            {
                skin[b] = made[taken.Bones[b]];
            }

            renderer.bones = skin;
            renderer.rootBone = made[taken.RootBone];
            renderer.sharedMesh = mesh;
            renderer.sharedMaterials = shown;
            renderer.updateWhenOffscreen = true;
        }
    }
}
