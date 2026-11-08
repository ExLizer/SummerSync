using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SummerSync.Core.Players
{
    /// <summary>
    /// The game's people a player can look like, kept on this machine so that the main menu can show them: the menu has
    /// none of them (they live in the game's world), and players meet there before any world is loaded. Each look is
    /// what a copy of the person is made of (bones, meshes, materials and textures) and how it stands (a loop sampled
    /// from the game's own clips). It is made from this machine's own game, kept in the mod's folder, and never goes
    /// anywhere else.
    /// </summary>
    public sealed class LookCache
    {
        /// <summary>Changes whenever what is written changes: an older file is made again.</summary>
        public const int Format = 1;

        // Far more than any person of the game has: a broken file says so instead of asking for gigabytes.
        private const int MostItems = 4 * 1024 * 1024;
        private const int MostBytes = 64 * 1024 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SSLOOKS");

        public LookCache(string gameBuild, string listShape)
        {
            GameBuild = gameBuild ?? string.Empty;
            ListShape = listShape ?? string.Empty;
        }

        /// <summary>The game build the looks were taken from: another build makes them again.</summary>
        public string GameBuild { get; private set; }

        /// <summary>The mod's list of looks they were taken for (their names, in order): another list makes them again.</summary>
        public string ListShape { get; private set; }

        public List<CachedLook> Looks { get; } = new List<CachedLook>();

        /// <summary>The textures of every look, each once.</summary>
        public List<CachedTexture> Textures { get; } = new List<CachedTexture>();

        /// <summary>The look with that index of the mod's list, or null.</summary>
        public CachedLook Find(int index)
        {
            for (int i = 0; i < Looks.Count; i++)
            {
                if (Looks[i].Index == index)
                {
                    return Looks[i];
                }
            }

            return null;
        }

        /// <summary>The index of the texture with that name, added if it is not there yet with what <paramref name="png"/> makes.</summary>
        public int TextureIndex(string name, Func<byte[]> png)
        {
            for (int i = 0; i < Textures.Count; i++)
            {
                if (Textures[i].Name == name)
                {
                    return i;
                }
            }

            byte[] bytes = png();
            if (bytes == null)
            {
                return -1;
            }

            Textures.Add(new CachedTexture { Name = name, Png = bytes });
            return Textures.Count - 1;
        }

        public byte[] Write()
        {
            using (var stream = new MemoryStream())
            {
                var writer = new BinaryWriter(stream, Encoding.UTF8);
                writer.Write(Magic);
                writer.Write(Format);
                writer.Write(GameBuild);
                writer.Write(ListShape);
                writer.Write(Textures.Count);
                for (int i = 0; i < Textures.Count; i++)
                {
                    writer.Write(Textures[i].Name ?? string.Empty);
                    WriteBytes(writer, Textures[i].Png);
                }

                writer.Write(Looks.Count);
                for (int i = 0; i < Looks.Count; i++)
                {
                    Looks[i].Write(writer);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>A cache read back; throws <see cref="FormatException"/> for anything that is not one this build wrote.</summary>
        public static LookCache Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length < Magic.Length + 4)
            {
                throw new FormatException("Not a cache of looks: too short.");
            }

            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var reader = new BinaryReader(stream, Encoding.UTF8);
                    byte[] magic = reader.ReadBytes(Magic.Length);
                    for (int i = 0; i < Magic.Length; i++)
                    {
                        if (magic[i] != Magic[i])
                        {
                            throw new FormatException("Not a cache of looks.");
                        }
                    }

                    int format = reader.ReadInt32();
                    if (format != Format)
                    {
                        throw new FormatException("A cache of looks of format " + format + ", not " + Format + ".");
                    }

                    var cache = new LookCache(reader.ReadString(), reader.ReadString());
                    int textures = Count(reader);
                    for (int i = 0; i < textures; i++)
                    {
                        cache.Textures.Add(new CachedTexture { Name = reader.ReadString(), Png = ReadBytes(reader) });
                    }

                    int looks = Count(reader);
                    for (int i = 0; i < looks; i++)
                    {
                        cache.Looks.Add(CachedLook.Read(reader, cache.Textures.Count));
                    }

                    return cache;
                }
            }
            catch (EndOfStreamException)
            {
                throw new FormatException("A cache of looks cut short.");
            }
        }

        internal static int Count(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MostItems)
            {
                throw new FormatException("A cache of looks with a count of " + count + ".");
            }

            return count;
        }

        internal static void WriteFloats(BinaryWriter writer, float[] values)
        {
            values = values ?? new float[0];
            writer.Write(values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                writer.Write(values[i]);
            }
        }

        internal static float[] ReadFloats(BinaryReader reader)
        {
            var values = new float[Count(reader)];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = reader.ReadSingle();
            }

            return values;
        }

        internal static void WriteInts(BinaryWriter writer, int[] values)
        {
            values = values ?? new int[0];
            writer.Write(values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                writer.Write(values[i]);
            }
        }

        internal static int[] ReadInts(BinaryReader reader)
        {
            var values = new int[Count(reader)];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = reader.ReadInt32();
            }

            return values;
        }

        private static void WriteBytes(BinaryWriter writer, byte[] bytes)
        {
            bytes = bytes ?? new byte[0];
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static byte[] ReadBytes(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > MostBytes)
            {
                throw new FormatException("A cache of looks with a texture of " + length + " bytes.");
            }

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new FormatException("A cache of looks cut short.");
            }

            return bytes;
        }
    }

    /// <summary>A texture of the game, as a PNG.</summary>
    public sealed class CachedTexture
    {
        public string Name;
        public byte[] Png;
    }

    /// <summary>One person: their bones, what is drawn on them, and how they stand.</summary>
    public sealed class CachedLook
    {
        public int Index;
        public string Name;

        /// <summary>Every object of the person, the first being the person itself; a parent comes before its children.</summary>
        public CachedBone[] Bones = new CachedBone[0];

        public CachedMesh[] Meshes = new CachedMesh[0];
        public CachedMaterial[] Materials = new CachedMaterial[0];

        /// <summary>Standing, as a loop; null if the person had no clip to stand with.</summary>
        public CachedClip Idle;

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Index);
            writer.Write(Name ?? string.Empty);
            writer.Write(Bones.Length);
            for (int i = 0; i < Bones.Length; i++)
            {
                Bones[i].Write(writer);
            }

            writer.Write(Meshes.Length);
            for (int i = 0; i < Meshes.Length; i++)
            {
                Meshes[i].Write(writer);
            }

            writer.Write(Materials.Length);
            for (int i = 0; i < Materials.Length; i++)
            {
                Materials[i].Write(writer);
            }

            writer.Write(Idle != null);
            if (Idle != null)
            {
                Idle.Write(writer);
            }
        }

        internal static CachedLook Read(BinaryReader reader, int textures)
        {
            var look = new CachedLook { Index = reader.ReadInt32(), Name = reader.ReadString() };
            look.Bones = new CachedBone[LookCache.Count(reader)];
            for (int i = 0; i < look.Bones.Length; i++)
            {
                look.Bones[i] = CachedBone.Read(reader);
                if (look.Bones[i].Parent >= i || (i > 0 && look.Bones[i].Parent < 0))
                {
                    throw new FormatException("A look whose bone " + i + " hangs from " + look.Bones[i].Parent + ".");
                }
            }

            if (look.Bones.Length == 0)
            {
                throw new FormatException("A look with no bones.");
            }

            look.Meshes = new CachedMesh[LookCache.Count(reader)];
            for (int i = 0; i < look.Meshes.Length; i++)
            {
                look.Meshes[i] = CachedMesh.Read(reader);
            }

            look.Materials = new CachedMaterial[LookCache.Count(reader)];
            for (int i = 0; i < look.Materials.Length; i++)
            {
                look.Materials[i] = CachedMaterial.Read(reader);
                if (look.Materials[i].Texture >= textures)
                {
                    throw new FormatException("A material with texture " + look.Materials[i].Texture + " of " + textures + ".");
                }
            }

            look.Idle = reader.ReadBoolean() ? CachedClip.Read(reader) : null;
            look.Check();
            return look;
        }

        // Every index points at something there is.
        private void Check()
        {
            for (int m = 0; m < Meshes.Length; m++)
            {
                CachedMesh mesh = Meshes[m];
                int vertices = mesh.Vertices.Length / 3;
                if (mesh.Vertices.Length % 3 != 0 || mesh.Normals.Length != 0 && mesh.Normals.Length != mesh.Vertices.Length || mesh.Uv.Length != 0 && mesh.Uv.Length != vertices * 2)
                {
                    throw new FormatException("A mesh whose vertices do not add up.");
                }

                if (!InRange(mesh.Bone, Bones.Length) || mesh.Skinned && !InRange(mesh.RootBone, Bones.Length) || !AllInRange(mesh.Bones, Bones.Length) || !AllInRange(mesh.Materials, Materials.Length))
                {
                    throw new FormatException("A mesh pointing at a bone or material there is not.");
                }

                if (mesh.Skinned && (mesh.BoneIndices.Length != vertices * 4 || mesh.BoneWeights.Length != vertices * 4 || mesh.Bindposes.Length != mesh.Bones.Length * 16 || !AllInRange(mesh.BoneIndices, mesh.Bones.Length)))
                {
                    throw new FormatException("A skinned mesh whose weights do not add up.");
                }

                for (int s = 0; s < mesh.Submeshes.Length; s++)
                {
                    if (!AllInRange(mesh.Submeshes[s], vertices))
                    {
                        throw new FormatException("A mesh with a triangle on a vertex it does not have.");
                    }
                }
            }

            if (Idle != null && (!AllInRange(Idle.Bones, Bones.Length) || Idle.Rotations.Length != Idle.Bones.Length * Idle.Frames * 4
                || Idle.Positions.Length != Idle.Bones.Length * Idle.Frames * 3))
            {
                throw new FormatException("A standing loop that does not add up.");
            }
        }

        private static bool InRange(int index, int count)
        {
            return index >= 0 && index < count;
        }

        private static bool AllInRange(int[] indices, int count)
        {
            for (int i = 0; i < indices.Length; i++)
            {
                if (!InRange(indices[i], count))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>One object of a person, where it hangs from its parent.</summary>
    public sealed class CachedBone
    {
        public string Name;

        /// <summary>The index of its parent among the person's bones; -1 for the person itself.</summary>
        public int Parent;

        public float[] Position = new float[3];
        public float[] Rotation = { 0f, 0f, 0f, 1f };
        public float[] Scale = { 1f, 1f, 1f };

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Name ?? string.Empty);
            writer.Write(Parent);
            for (int i = 0; i < 3; i++)
            {
                writer.Write(Position[i]);
            }

            for (int i = 0; i < 4; i++)
            {
                writer.Write(Rotation[i]);
            }

            for (int i = 0; i < 3; i++)
            {
                writer.Write(Scale[i]);
            }
        }

        internal static CachedBone Read(BinaryReader reader)
        {
            var bone = new CachedBone { Name = reader.ReadString(), Parent = reader.ReadInt32() };
            for (int i = 0; i < 3; i++)
            {
                bone.Position[i] = reader.ReadSingle();
            }

            for (int i = 0; i < 4; i++)
            {
                bone.Rotation[i] = reader.ReadSingle();
            }

            for (int i = 0; i < 3; i++)
            {
                bone.Scale[i] = reader.ReadSingle();
            }

            return bone;
        }
    }

    /// <summary>A mesh drawn on one of the person's objects: skinned to their bones, or carried by one (a cap).</summary>
    public sealed class CachedMesh
    {
        /// <summary>The bone it is drawn on.</summary>
        public int Bone;

        public bool Skinned;
        public int RootBone;

        /// <summary>The person's bones it is skinned to, in the order of its weights and bind poses.</summary>
        public int[] Bones = new int[0];

        public float[] Vertices = new float[0];
        public float[] Normals = new float[0];
        public float[] Uv = new float[0];

        /// <summary>Four bone indices (among <see cref="Bones"/>) per vertex, and their four weights.</summary>
        public int[] BoneIndices = new int[0];

        public float[] BoneWeights = new float[0];

        /// <summary>Sixteen numbers per bone, column after column.</summary>
        public float[] Bindposes = new float[0];

        public int[][] Submeshes = new int[0][];

        /// <summary>A material (of the look) per submesh.</summary>
        public int[] Materials = new int[0];

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Bone);
            writer.Write(Skinned);
            writer.Write(RootBone);
            LookCache.WriteInts(writer, Bones);
            LookCache.WriteFloats(writer, Vertices);
            LookCache.WriteFloats(writer, Normals);
            LookCache.WriteFloats(writer, Uv);
            LookCache.WriteInts(writer, BoneIndices);
            LookCache.WriteFloats(writer, BoneWeights);
            LookCache.WriteFloats(writer, Bindposes);
            writer.Write(Submeshes.Length);
            for (int i = 0; i < Submeshes.Length; i++)
            {
                LookCache.WriteInts(writer, Submeshes[i]);
            }

            LookCache.WriteInts(writer, Materials);
        }

        internal static CachedMesh Read(BinaryReader reader)
        {
            var mesh = new CachedMesh
            {
                Bone = reader.ReadInt32(),
                Skinned = reader.ReadBoolean(),
                RootBone = reader.ReadInt32(),
                Bones = LookCache.ReadInts(reader),
                Vertices = LookCache.ReadFloats(reader),
                Normals = LookCache.ReadFloats(reader),
                Uv = LookCache.ReadFloats(reader),
                BoneIndices = LookCache.ReadInts(reader),
                BoneWeights = LookCache.ReadFloats(reader),
                Bindposes = LookCache.ReadFloats(reader),
            };
            mesh.Submeshes = new int[LookCache.Count(reader)][];
            for (int i = 0; i < mesh.Submeshes.Length; i++)
            {
                mesh.Submeshes[i] = LookCache.ReadInts(reader);
            }

            mesh.Materials = LookCache.ReadInts(reader);
            return mesh;
        }
    }

    /// <summary>How a part of the person is drawn: the game's shader by name, a color and a texture.</summary>
    public sealed class CachedMaterial
    {
        public string Shader;
        public float[] Color = { 1f, 1f, 1f, 1f };

        /// <summary>The main texture, an index of the cache's textures, or -1.</summary>
        public int Texture = -1;

        public float[] TextureScale = { 1f, 1f };
        public float[] TextureOffset = { 0f, 0f };

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Shader ?? string.Empty);
            for (int i = 0; i < 4; i++)
            {
                writer.Write(Color[i]);
            }

            writer.Write(Texture);
            for (int i = 0; i < 2; i++)
            {
                writer.Write(TextureScale[i]);
            }

            for (int i = 0; i < 2; i++)
            {
                writer.Write(TextureOffset[i]);
            }
        }

        internal static CachedMaterial Read(BinaryReader reader)
        {
            var material = new CachedMaterial { Shader = reader.ReadString() };
            for (int i = 0; i < 4; i++)
            {
                material.Color[i] = reader.ReadSingle();
            }

            material.Texture = reader.ReadInt32();
            for (int i = 0; i < 2; i++)
            {
                material.TextureScale[i] = reader.ReadSingle();
            }

            for (int i = 0; i < 2; i++)
            {
                material.TextureOffset[i] = reader.ReadSingle();
            }

            return material;
        }
    }

    /// <summary>A loop of frames: for some bones, where each is and how it is turned in each frame.</summary>
    public sealed class CachedClip
    {
        public float Rate = 15f;
        public int Frames;
        public int[] Bones = new int[0];

        /// <summary>Four numbers per bone per frame: bone after bone, frame after frame within a bone.</summary>
        public float[] Rotations = new float[0];

        /// <summary>Three numbers per bone per frame, in the same order.</summary>
        public float[] Positions = new float[0];

        /// <summary>How long the loop lasts, in seconds.</summary>
        public float Length
        {
            get { return Frames <= 1 || Rate <= 0f ? 0f : Frames / Rate; }
        }

        /// <summary>
        /// Where <paramref name="time"/> falls in the loop: the frame before, the one after (the first again after the last)
        /// and how far between them, from 0 to 1.
        /// </summary>
        public void Between(double time, out int before, out int after, out float blend)
        {
            if (Frames <= 1 || Rate <= 0f)
            {
                before = after = 0;
                blend = 0f;
                return;
            }

            double frame = time * Rate;
            frame -= Math.Floor(frame / Frames) * Frames;
            before = Math.Min(Frames - 1, (int)Math.Floor(frame));
            after = (before + 1) % Frames;
            blend = (float)(frame - before);
        }

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Rate);
            writer.Write(Frames);
            LookCache.WriteInts(writer, Bones);
            LookCache.WriteFloats(writer, Rotations);
            LookCache.WriteFloats(writer, Positions);
        }

        internal static CachedClip Read(BinaryReader reader)
        {
            var clip = new CachedClip { Rate = reader.ReadSingle(), Frames = reader.ReadInt32() };
            if (clip.Frames < 0 || clip.Frames > 10000 || clip.Rate <= 0f || clip.Rate > 120f)
            {
                throw new FormatException("A standing loop of " + clip.Frames + " frames at " + clip.Rate + " a second.");
            }

            clip.Bones = LookCache.ReadInts(reader);
            clip.Rotations = LookCache.ReadFloats(reader);
            clip.Positions = LookCache.ReadFloats(reader);
            return clip;
        }
    }
}
