using SummerSync.Core.Players;

namespace SummerSync.Core.Tests.Players;

/// <summary>The looks kept on this machine for the main menu: written and read back whole, and nothing else read as one.</summary>
public class LookCacheTests
{
    private static LookCache Sample()
    {
        var cache = new LookCache("5.0.0f4/e30ef95a", "Kale|Alpo");
        int skin = cache.TextureIndex("kale_skin", () => new byte[] { 137, 80, 78, 71, 1, 2, 3 });
        Assert.Equal(skin, cache.TextureIndex("kale_skin", () => throw new InvalidOperationException("made twice")));
        var look = new CachedLook
        {
            Index = 0,
            Name = "Kale",
            Bones = new[]
            {
                new CachedBone { Name = "Char", Parent = -1 },
                new CachedBone { Name = "skeleton", Parent = 0, Position = new[] { 0f, 0.1f, 0f } },
                new CachedBone { Name = "pelvis", Parent = 1, Rotation = new[] { 0f, 0.7071068f, 0f, 0.7071068f } },
                new CachedBone { Name = "bodymesh", Parent = 0, Scale = new[] { 2f, 2f, 2f } },
            },
            Materials = new[] { new CachedMaterial { Shader = "Legacy Shaders/Diffuse", Texture = skin, Color = new[] { 1f, 0.9f, 0.8f, 1f } } },
            Meshes = new[]
            {
                new CachedMesh
                {
                    Bone = 3,
                    Skinned = true,
                    RootBone = 2,
                    Bones = new[] { 1, 2 },
                    Vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f },
                    Normals = new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f },
                    Uv = new[] { 0f, 0f, 1f, 0f, 0f, 1f },
                    BoneIndices = new[] { 0, 1, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0 },
                    BoneWeights = new[] { 0.5f, 0.5f, 0f, 0f, 1f, 0f, 0f, 0f, 0.25f, 0.75f, 0f, 0f },
                    Bindposes = Enumerable.Repeat(1f, 32).ToArray(),
                    Submeshes = new[] { new[] { 0, 1, 2 } },
                    Materials = new[] { 0 },
                },
            },
            Idle = new CachedClip
            {
                Rate = 10f,
                Frames = 2,
                Bones = new[] { 2 },
                Rotations = new[] { 0f, 0f, 0f, 1f, 0f, 0.1f, 0f, 0.995f },
                Positions = new[] { 0f, 0f, 0f, 0f, 0.01f, 0f },
            },
        };
        cache.Looks.Add(look);
        return cache;
    }

    [Fact]
    public void A_cache_reads_back_as_it_was_written()
    {
        LookCache read = LookCache.Read(Sample().Write());

        Assert.Equal("5.0.0f4/e30ef95a", read.GameBuild);
        Assert.Equal("Kale|Alpo", read.ListShape);
        CachedTexture texture = Assert.Single(read.Textures);
        Assert.Equal("kale_skin", texture.Name);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 1, 2, 3 }, texture.Png);

        CachedLook look = read.Find(0)!;
        Assert.Equal("Kale", look.Name);
        Assert.Null(read.Find(1));
        Assert.Equal(4, look.Bones.Length);
        Assert.Equal(1, look.Bones[2].Parent);
        Assert.Equal(0.7071068f, look.Bones[2].Rotation[1]);
        Assert.Equal(2f, look.Bones[3].Scale[0]);
        Assert.Equal("Legacy Shaders/Diffuse", look.Materials[0].Shader);
        Assert.Equal(0.9f, look.Materials[0].Color[1]);

        CachedMesh mesh = Assert.Single(look.Meshes);
        Assert.True(mesh.Skinned);
        Assert.Equal(new[] { 1, 2 }, mesh.Bones);
        Assert.Equal(9, mesh.Vertices.Length);
        Assert.Equal(0.75f, mesh.BoneWeights[9]);
        Assert.Equal(new[] { 0, 1, 2 }, Assert.Single(mesh.Submeshes));

        Assert.Equal(2, look.Idle!.Frames);
        Assert.Equal(0.2f, look.Idle.Length, 5);
        Assert.Equal(0.01f, look.Idle.Positions[4]);
    }

    [Fact]
    public void Something_else_is_not_read_as_a_cache()
    {
        Assert.Throws<FormatException>(() => LookCache.Read(new byte[] { 1, 2, 3 }));
        Assert.Throws<FormatException>(() => LookCache.Read(System.Text.Encoding.ASCII.GetBytes("SSLOOKS and more of anything")));

        byte[] whole = Sample().Write();
        Assert.Throws<FormatException>(() => LookCache.Read(whole.Take(whole.Length - 10).ToArray()));

        // Another format: made again, never misread.
        byte[] other = (byte[])whole.Clone();
        other[7] = 99;
        Assert.Throws<FormatException>(() => LookCache.Read(other));
    }

    [Fact]
    public void A_look_pointing_at_what_it_does_not_have_is_refused()
    {
        LookCache cache = Sample();
        cache.Looks[0].Meshes[0].Submeshes[0][2] = 7;
        Assert.Throws<FormatException>(() => LookCache.Read(cache.Write()));

        cache = Sample();
        cache.Looks[0].Bones[1].Parent = 3;
        Assert.Throws<FormatException>(() => LookCache.Read(cache.Write()));

        cache = Sample();
        cache.Looks[0].Materials[0].Texture = 5;
        Assert.Throws<FormatException>(() => LookCache.Read(cache.Write()));
    }

    [Fact]
    public void The_standing_loop_goes_round()
    {
        var clip = new CachedClip { Rate = 10f, Frames = 4 };
        clip.Between(0.0, out int before, out int after, out float blend);
        Assert.Equal((0, 1, 0f), (before, after, blend));

        clip.Between(0.35, out before, out after, out blend);
        Assert.Equal(3, before);
        Assert.Equal(0, after);
        Assert.Equal(0.5f, blend, 4);

        clip.Between(0.45, out before, out after, out blend);
        Assert.Equal(0, before);
        Assert.Equal(0.5f, blend, 4);
        Assert.Equal(0.4f, clip.Length, 5);
    }
}
