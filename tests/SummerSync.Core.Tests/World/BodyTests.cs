using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class BodyTests
{
    [Fact]
    public void Every_bodies_file_in_the_repository_can_be_read()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "bodies");
        List<BodyDefinition> bodies = Directory.GetFiles(folder, "*.json").SelectMany(file => BodyDefinition.Parse(File.ReadAllText(file))).ToList();

        Assert.Equal(bodies.Count, bodies.Select(b => b.Name).Distinct().Count());
        BodyDefinition kale = bodies.Single(b => b.Name == "kale");
        Assert.Equal("HUMANS/Randomizer/Walkers/Kale/Pivot/RagDoll", kale.Paths.Single());
        Assert.Equal("HUMANS/Randomizer/Walkers/Kale", kale.Person.Single());
        Assert.Contains("Force", kale.Still);
        Assert.Equal(2, bodies.Single(b => b.Name == "pubfighter").Paths.Length);
        Assert.All(bodies, b => Assert.True(BodyWire.BoneKey(b.Name, BodyWire.MaxBones - 1).Length <= TallyWire.MaxNameBytes));
    }

    [Theory]
    [InlineData("""{ "format": 2, "bodies": [{ "name": "a", "paths": ["A"] }] }""")]
    [InlineData("""{ "format": 1 }""")]
    [InlineData("""{ "format": 1, "bodies": [] }""")]
    [InlineData("""{ "format": 1, "bodies": [{ "name": "a" }] }""")]
    [InlineData("""{ "format": 1, "bodies": [{ "name": "a", "paths": [] }] }""")]
    [InlineData("""{ "format": 1, "bodies": [{ "name": "Kale", "paths": ["A"] }] }""")]
    [InlineData("""{ "format": 1, "bodies": [{ "name": "a/b", "paths": ["A"] }] }""")]
    [InlineData("""{ "format": 1, "bodies": [{ "name": "abcdefghijklmnopqrstuvwxyz", "paths": ["A"] }] }""")]
    [InlineData("""{ "format": 1, "bodies": [{ "name": "a", "paths": ["A"] }, { "name": "a", "paths": ["B"] }] }""")]
    [InlineData("""{ "format": 1, "bodies": [{ "name": "a", "paths": ["A"], "still": [""] }] }""")]
    public void A_bodies_file_that_cannot_be_right_is_refused(string json)
    {
        Assert.Throws<FormatException>(() => BodyDefinition.Parse(json));
    }

    [Fact]
    public void A_bone_travels_to_within_a_millimetre_and_a_tenth_of_a_degree()
    {
        var turn = Normalized(0.3f, -0.5f, 0.2f, -0.78f);
        var pose = new BonePose(1544.1234f, -3.5f, -1182.6789f, turn.x, turn.y, turn.z, turn.w);

        int[] values = BodyWire.PackBone(7, pose);
        Assert.Equal(BodyWire.BoneValues, values.Length);
        Assert.True(BodyWire.TryUnpackBone(values, out int generation, out BonePose back));

        Assert.Equal(7, generation);
        Assert.InRange(Math.Abs(back.X - pose.X), 0f, 0.001f);
        Assert.InRange(Math.Abs(back.Y - pose.Y), 0f, 0.001f);
        Assert.InRange(Math.Abs(back.Z - pose.Z), 0f, 0.001f);
        Assert.InRange(BodyWire.Degrees(pose, back), 0f, 0.1f);
        Assert.False(BodyWire.Moved(pose, back, 0.002f, 0.2f));
    }

    [Fact]
    public void A_bone_with_no_turn_travels_as_no_turn()
    {
        Assert.True(BodyWire.TryUnpackBone(BodyWire.PackBone(1, new BonePose(1f, 2f, 3f, 0f, 0f, 0f, 0f)), out _, out BonePose back));
        Assert.Equal(1f, back.QW, 4);
    }

    [Theory]
    [InlineData(new[] { 1, 0, 0, 0, 0 })]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData(new[] { 1, 200000000, 0, 0, 0, 2147418112 })]
    [InlineData(new[] { 1, 0, 0, 0, 0, 0 })]
    public void Numbers_that_are_not_a_bone_are_refused(int[] values)
    {
        Assert.False(BodyWire.TryUnpackBone(values, out _, out _));
    }

    [Fact]
    public void A_bone_moved_or_turned_past_the_step_has_moved()
    {
        var a = new BonePose(0f, 0f, 0f, 0f, 0f, 0f, 1f);
        float half = (float)Math.Sin(Math.PI / 180.0 * 2.0 / 2.0);
        var turned = new BonePose(0f, 0f, 0f, 0f, half, 0f, (float)Math.Sqrt(1 - (half * half)));

        Assert.False(BodyWire.Moved(a, new BonePose(0.01f, 0f, 0f, 0f, 0f, 0f, 1f), 0.02f, 3f));
        Assert.True(BodyWire.Moved(a, new BonePose(0.03f, 0f, 0f, 0f, 0f, 0f, 1f), 0.02f, 3f));
        Assert.InRange(BodyWire.Degrees(a, turned), 1.99f, 2.01f);
        Assert.False(BodyWire.Moved(a, turned, 0.02f, 3f));
        Assert.True(BodyWire.Moved(a, turned, 0.02f, 1f));
    }

    [Fact]
    public void The_names_of_a_body_s_tallies_are_read_back()
    {
        Assert.True(BodyWire.TryParseKey(BodyWire.OwnerKey("kale"), out string body, out int bone));
        Assert.Equal("kale", body);
        Assert.Equal(-1, bone);
        Assert.True(BodyWire.TryParseKey(BodyWire.BoneKey("kale", 10), out body, out bone));
        Assert.Equal("kale", body);
        Assert.Equal(10, bone);
        Assert.False(BodyWire.TryParseKey("body/", out _, out _));
        Assert.False(BodyWire.TryParseKey("body/kale/", out _, out _));
        Assert.False(BodyWire.TryParseKey("body/kale/x", out _, out _));
        Assert.False(BodyWire.TryParseKey("body/kale/" + BodyWire.MaxBones, out _, out _));
        Assert.False(BodyWire.TryParseKey("people/fallen/Kale", out _, out _));
    }

    [Fact]
    public void An_owner_travels_and_nobody_is_not_an_owner()
    {
        Assert.True(BodyWire.TryUnpackOwner(BodyWire.PackOwner(2, 5), out byte owner, out int generation));
        Assert.Equal(2, owner);
        Assert.Equal(5, generation);
        Assert.False(BodyWire.TryUnpackOwner(new[] { 0, 5 }, out _, out _));
        Assert.False(BodyWire.TryUnpackOwner(new[] { 1, 0 }, out _, out _));
        Assert.False(BodyWire.TryUnpackOwner(new[] { 1 }, out _, out _));
    }

    [Fact]
    public void Two_players_who_take_a_body_at_once_end_with_the_same_owner_everywhere()
    {
        // Both said the next generation; every game hears the host's order: player 2's word first, then player 1's.
        var anywhere = new BodyClaim();
        Assert.True(anywhere.Hear(2, 1));
        Assert.False(anywhere.Hear(1, 1));
        Assert.Equal(2, anywhere.Owner);
        Assert.Equal(1, anywhere.Generation);

        // Then player 1 drags it: the generation after the one it heard.
        Assert.True(anywhere.Hear(1, anywhere.Next));
        Assert.Equal(1, anywhere.Owner);

        // A late word of the old generation counts for nothing, and its bones are not shown.
        Assert.False(anywhere.Hear(2, 1));
        Assert.True(anywhere.IsCurrent(2));
        Assert.False(anywhere.IsCurrent(1));
    }

    [Fact]
    public void A_body_nobody_has_taken_is_not_known_and_its_bones_are_not_current()
    {
        var claim = new BodyClaim();
        Assert.False(claim.Known);
        Assert.Equal(1, claim.Next);
        Assert.False(claim.IsCurrent(0));
        claim.Hear(0, 3);
        claim.Forget();
        Assert.False(claim.Known);
    }

    [Fact]
    public void The_poker_torn_out_says_who_tore_it_out()
    {
        Assert.True(PokerHeistWire.TryUnpackBroken(PokerHeistWire.PackBroken(3), out byte robber));
        Assert.Equal(3, robber);
        Assert.True(PokerHeistWire.TryUnpackBroken(new[] { 1 }, out robber));
        Assert.Equal(0, robber);
        Assert.False(PokerHeistWire.TryUnpackBroken(new[] { 0, 2 }, out _));
        Assert.False(PokerHeistWire.TryUnpackBroken(new int[0], out _));
    }

    private static (float x, float y, float z, float w) Normalized(float x, float y, float z, float w)
    {
        float length = (float)Math.Sqrt((x * x) + (y * y) + (z * z) + (w * w));
        return (x / length, y / length, z / length, w / length);
    }
}
