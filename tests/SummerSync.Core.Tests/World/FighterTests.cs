using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class FighterTests
{
    [Fact]
    public void Every_fighter_file_in_the_repository_can_be_read()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "fighters");
        List<FighterDefinition> fighters = Directory.GetFiles(folder, "*.json").Select(file => FighterDefinition.Parse(File.ReadAllText(file))).ToList();

        FighterDefinition pub = fighters.Single(f => f.Name == "pub");
        Assert.True(pub.DecidedByHost);
        Assert.Equal(new[] { "HUMANS/FighterPub/Fighter2", "HUMANS/Fighter2" }, pub.Paths);
        Assert.Equal(string.Empty, pub.Switch);
        Assert.Equal("HUMANS", pub.OutParent);
        Assert.Contains(pub.CalmEntry, pub.Calm);
        Assert.Equal(2, pub.Hits.Length);
        Assert.All(pub.Hits, hit => Assert.Equal("State 4", hit.State));
        Assert.Equal("State 5", pub.Down.State);
        Assert.Equal("ACTIVATE", pub.CallEvent);
        Assert.Equal("State 2", pub.Call.State);
        Assert.Equal("Fighter", pub.StruckBy);

        FighterDefinition dance = fighters.Single(f => f.Name == "dance");
        Assert.False(dance.DecidedByHost);
        Assert.Equal("DANCEHALL/Functions/FIGHTER", dance.Switch);
        Assert.Equal(string.Empty, dance.OutParent);
        Assert.Null(dance.Call);
        Assert.Single(dance.Shown);
        Assert.Equal(2, dance.Loose.Length);
    }

    [Theory]
    [InlineData("""{ "format": 2, "name": "x", "paths": ["A"], "decidedBy": "host", "brain": "Move", "calm": ["S"], "calmEntry": "S", "hits": [{ "path": "h", "fsm": "Hitbox", "state": "S" }], "fist": "F", "down": { "path": "h", "fsm": "Hitbox", "state": "D" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "paths": [], "decidedBy": "host", "brain": "Move", "calm": ["S"], "calmEntry": "S", "hits": [{ "path": "h", "fsm": "Hitbox", "state": "S" }], "fist": "F", "down": { "path": "h", "fsm": "Hitbox", "state": "D" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "paths": ["A"], "decidedBy": "anyone", "brain": "Move", "calm": ["S"], "calmEntry": "S", "hits": [{ "path": "h", "fsm": "Hitbox", "state": "S" }], "fist": "F", "down": { "path": "h", "fsm": "Hitbox", "state": "D" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "paths": ["A"], "decidedBy": "host", "brain": "Move", "calm": ["S"], "calmEntry": "T", "hits": [{ "path": "h", "fsm": "Hitbox", "state": "S" }], "fist": "F", "down": { "path": "h", "fsm": "Hitbox", "state": "D" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "paths": ["A"], "decidedBy": "host", "brain": "Move", "calm": ["S"], "calmEntry": "S", "hits": [], "fist": "F", "down": { "path": "h", "fsm": "Hitbox", "state": "D" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "paths": ["A"], "decidedBy": "host", "brain": "Move", "calm": ["S"], "calmEntry": "S", "hits": [{ "path": "h", "fsm": "Hitbox", "state": "" }], "fist": "F", "down": { "path": "h", "fsm": "Hitbox", "state": "D" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "paths": ["A"], "decidedBy": "host", "brain": "Move", "calm": ["S"], "calmEntry": "S", "hits": [{ "path": "h", "fsm": "Hitbox", "state": "S" }], "fist": "F" }""")]
    [InlineData("""{ "format": 1, "name": "x", "paths": ["A"], "decidedBy": "host", "brain": "Move", "calm": ["S"], "calmEntry": "S", "animations": ["a", "b", "c", "d", "e"], "hits": [{ "path": "h", "fsm": "Hitbox", "state": "S" }], "fist": "F", "down": { "path": "h", "fsm": "Hitbox", "state": "D" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "paths": ["A"], "decidedBy": "host", "brain": "Move", "calm": ["S"], "calmEntry": "S", "hits": [{ "path": "h", "fsm": "Hitbox", "state": "S" }], "fist": "F", "down": { "path": "h", "fsm": "Hitbox", "state": "D" }, "call": { "from": { "path": "b", "fsm": "B", "state": "S" }, "to": { "path": "a", "fsm": "A" } } }""")]
    [InlineData("""{ "format": 1, "name": "x", "paths": ["A"], "decidedBy": "host", "brain": "Move", "calm": ["S"], "calmEntry": "S", "hits": [{ "path": "h", "fsm": "Hitbox", "state": "S" }], "fist": "F", "down": { "path": "h", "fsm": "Hitbox", "state": "D" }, "struck": { "path": "p", "fsm": "Hitbox", "by": "", "at": "f" } }""")]
    public void A_fighter_file_that_cannot_be_right_is_refused(string json)
    {
        Assert.Throws<FormatException>(() => FighterDefinition.Parse(json));
    }

    [Fact]
    public void A_pose_travels_as_eight_numbers_and_comes_back_the_same()
    {
        var pose = new FighterPose
        {
            Runner = 3,
            X = -1544.245f,
            Y = 3.795f,
            Z = 1183.29f,
            Yaw = -90f,
            Clip0 = 2,
            Clip1 = 0,
            Clip2 = 5,
            Clip3 = 255,
            Calm = true,
            Out = true,
            Things = 0b1010_0001,
            Serial = 77,
        };

        int[] numbers = pose.Pack();
        Assert.Equal(FighterPose.Count, numbers.Length);
        Assert.True(FighterPose.TryUnpack(numbers, out FighterPose back));
        Assert.Equal(3, back.Runner);
        Assert.Equal(-1544.245f, back.X, 3);
        Assert.Equal(3.795f, back.Y, 3);
        Assert.Equal(1183.29f, back.Z, 3);
        Assert.Equal(270f, back.Yaw, 2);
        Assert.Equal(2, back.Clip(0));
        Assert.Equal(0, back.Clip(1));
        Assert.Equal(5, back.Clip(2));
        Assert.Equal(255, back.Clip(3));
        Assert.True(back.Calm);
        Assert.True(back.Out);
        Assert.Equal(0b1010_0001, back.Things);
        Assert.Equal(77, back.Serial);
    }

    [Fact]
    public void Numbers_that_are_not_a_pose_are_refused()
    {
        Assert.False(FighterPose.TryUnpack(null, out _));
        Assert.False(FighterPose.TryUnpack(new int[7], out _));
        Assert.False(FighterPose.TryUnpack(new int[8], out _));
        Assert.False(FighterPose.TryUnpack(new[] { 1, int.MaxValue, 0, 0, 0, 0, 0, 0 }, out _));
    }

    [Fact]
    public void The_nearest_player_runs_him_and_keeps_him_while_he_fights()
    {
        var candidates = new List<FighterCandidate> { new(0, 30f), new(1, 5f), new(2, 12f) };

        // Nobody runs him yet, or his runner left: the nearest.
        Assert.Equal(1, FighterOwnership.Choose(-1, true, candidates));
        Assert.Equal(1, FighterOwnership.Choose(4, false, candidates));

        // Fighting: whoever runs him keeps him, however near another is.
        Assert.Equal(0, FighterOwnership.Choose(0, false, candidates));

        // Calm: to one nearer by more than the margin.
        Assert.Equal(1, FighterOwnership.Choose(0, true, candidates));
        var close = new List<FighterCandidate> { new(0, 6f), new(1, 5f) };
        Assert.Equal(0, FighterOwnership.Choose(0, true, close));

        Assert.Equal(-1, FighterOwnership.Choose(0, true, new List<FighterCandidate>()));
    }

    [Fact]
    public void A_sound_name_fits_in_a_few_numbers()
    {
        var numbers = new int[8];
        Assert.True(TallyText.TryPack("fighter_ko", numbers, 3, 5));
        Assert.Equal("fighter_ko", TallyText.Unpack(numbers, 3, 5));
        Assert.True(TallyText.TryPack("12345678901234567890", numbers, 3, 5));
        Assert.Equal("12345678901234567890", TallyText.Unpack(numbers, 3, 5));
        Assert.False(TallyText.TryPack("123456789012345678901", numbers, 3, 5));
        Assert.False(TallyText.TryPack("ouché", numbers, 3, 5));
    }
}
