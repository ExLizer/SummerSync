using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class MountDefinitionTests
{
    [Fact]
    public void A_file_says_where_the_thing_sits_and_how_the_game_mounts_and_takes_it_off()
    {
        MountDefinition drive = MountDefinition.Parse("""
            {
              "format": 1,
              "name": "diskette",
              "part": "diskette(itemx)",
              "into": "/YARD/COMPUTER/DiskDrive/Sled/pivot",
              "mount": { "path": "/YARD/COMPUTER/DiskDrive/TriggerDiskette", "fsm": "Assembly", "variable": "Diskette", "event": "DISKETTEIN" },
              "unmount": { "path": "/YARD/COMPUTER/DiskDrive/TriggerDiskette", "fsm": "Assembly", "state": "Sound" }
            }
            """);

        Assert.Equal("mount/diskette", drive.Key);
        Assert.Equal("diskette(itemx)", drive.Part);
        Assert.Equal("/YARD/COMPUTER/DiskDrive/Sled/pivot", drive.Into);
        Assert.Null(drive.Onto);
        Assert.Equal("Diskette", drive.Mount.Variable);
        Assert.Equal("DISKETTEIN", drive.Mount.Event);
        Assert.Null(drive.Mount.State);
        Assert.Equal("Sound", drive.Unmount!.State);
        Assert.Equal(0f, drive.Mount.Within);
    }

    [Fact]
    public void Something_put_onto_the_thing_and_a_game_that_lets_go_by_itself()
    {
        MountDefinition charger = MountDefinition.Parse("""
            {
              "format": 1,
              "name": "battery-charger",
              "part": "battery(Clone)",
              "onto": "/YARD/BatteryCharger/Clamps",
              "mount": { "path": "/YARD/BatteryCharger/TriggerCharger", "fsm": "Assembly", "variable": "Battery", "event": "PLACEBATT", "within": 0.5 }
            }
            """);

        Assert.Equal("/YARD/BatteryCharger/Clamps", charger.Onto);
        Assert.Null(charger.Into);
        Assert.Equal(0.5f, charger.Mount.Within);
        Assert.Null(charger.Unmount);
    }

    [Theory]
    [InlineData("""{ "format": 1, "name": "x", "part": "p", "mount": { "fsm": "A", "state": "S" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "part": "p", "into": "/a", "onto": "/b", "mount": { "fsm": "A", "state": "S" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "part": "p", "into": "a/b", "mount": { "fsm": "A", "state": "S" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "part": "p", "into": "/a" }""")]
    [InlineData("""{ "format": 1, "name": "x", "part": "p", "into": "/a", "mount": { "fsm": "A", "state": "S", "event": "E" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "part": "p", "into": "/a", "mount": { "fsm": "A" } }""")]
    [InlineData("""{ "format": 1, "name": "x/y", "part": "p", "into": "/a", "mount": { "fsm": "A", "state": "S" } }""")]
    [InlineData("""{ "format": 2, "name": "x", "part": "p", "into": "/a", "mount": { "fsm": "A", "state": "S" } }""")]
    public void A_file_that_does_not_say_one_place_and_one_way_is_refused(string json)
    {
        Assert.Throws<FormatException>(() => MountDefinition.Parse(json));
    }

    [Fact]
    public void Every_mount_file_in_the_repository_can_be_read()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "mounts");
        string[] files = Directory.GetFiles(folder, "*.json");

        Assert.NotEmpty(files);
        var mounts = files.Select(file => MountDefinition.Parse(File.ReadAllText(file))).ToList();
        Assert.Equal(mounts.Count, mounts.Select(m => m.Name).Distinct().Count());
        Assert.Contains(mounts, m => m.Name == "bucket-lid" && m.Unmount != null && m.Unmount.Path == null);
        Assert.Contains(mounts, m => m.Name == "diskette");
        Assert.Contains(mounts, m => m.Name == "battery-charger" && m.Onto != null);

        // A CD goes into its case or into the player, which hangs from wherever it is installed: found by its name.
        Assert.Contains(mounts, m => m.Name == "cd1-case" && m.Part == "cd(item1)" && m.Unmount!.Path == null);
        MountDefinition player = Assert.Single(mounts, m => m.Name == "cd3-player");
        Assert.Equal("*cd player(Clone)/Sled/cd_sled_pivot", player.Into);
        Assert.Equal("Part", player.Mount.Variable);
        Assert.Equal("*cd player(Clone)/ButtonsCD/Eject", player.Unmount!.Path);
    }
}
