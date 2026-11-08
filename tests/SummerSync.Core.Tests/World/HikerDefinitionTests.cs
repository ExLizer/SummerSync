using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class HikerDefinitionTests
{
    [Fact]
    public void The_files_the_mod_ships_say_who_hitchhikes_and_how()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "hikers");
        HikerDefinition[] hikers = Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal).Select(f => HikerDefinition.Parse(File.ReadAllText(f))).ToArray();

        Assert.Equal(new[] { "granny", "jokke", "jokkemoving", "suski" }, hikers.Select(h => h.Name));
        HikerDefinition jokke = hikers[1];
        Assert.Null(jokke.Animated);
        Assert.Equal("KILJUGUY/HikerPivot/JokkeHiker2", jokke.Logic.Path);
        Assert.Equal("Logic", jokke.Logic.Machine);
        Assert.Equal("Open door", jokke.Aboard);
        Assert.Equal("Start walking", jokke.Off);
        Assert.Equal("Activate", jokke.Appears);
        Assert.Contains("State 5", jokke.AppearsFrom);
        Assert.Equal("fat_get_van_in", jokke.ClipFor("HAYOSIKO(1500kg, 250)"));
        Assert.Equal("fat_get_car_in", jokke.ClipFor("SATSUMA(557kg, 248)"));
        Assert.Equal("DrunkStage", jokke.Keep.Variable);
        Assert.Equal(4, jokke.Paused.Length);
        Assert.Equal("CarHit", jokke.Hit.Machine);

        // The grandmother appears by herself in every game: her job's call is shared.
        HikerDefinition granny = hikers[0];
        Assert.Null(granny.Appears);
        Assert.Equal("State 10", granny.Dies);
        Assert.Equal("granny_get_car_in", granny.ClipFor("FERNDALE(1630kg)"));
        Assert.Equal("JOBS/Mummola", granny.Keep.Path);

        // Jokke of the moving job walks himself and has his clips on a child; his ride is the job's, which says nothing back.
        HikerDefinition moving = hikers[2];
        Assert.Equal(moving.Logic.Path, moving.Walker);
        Assert.Equal("JOBS/HouseDrunk/Moving/JokkeHiker1/Pivot", moving.Animated);
        Assert.Equal("Open door 2", moving.Aboard);
        Assert.Equal("Idle", moving.Off);
        Assert.Null(moving.Keep);

        // Suski rides the Satsuma and the Ferndale only, and her ride moves her story on (the job says it).
        HikerDefinition suski = hikers[3];
        Assert.Equal("woman_get_car_in", suski.ClipFor("SATSUMA(557kg, 248)"));
        Assert.Equal("GetInPivotSuski", suski.PivotFor("FERNDALE(1630kg)"));
        Assert.Equal("GetInPivot", suski.PivotFor("SATSUMA(557kg, 248)"));
        Assert.Equal("GetInPivot", jokke.PivotFor("FERNDALE(1630kg)"));
        Assert.Equal("State 9", suski.Dies);
        Assert.Null(suski.Appears);
    }

    [Theory]
    [InlineData("""{ "format": 2, "name": "x" }""", "format 2")]
    [InlineData("""{ "format": 1, "name": "a/b" }""", "without '/'")]
    [InlineData("""{ "format": 1, "name": "x", "logic": { "path": "A", "fsm": "Logic" }, "walker": "A", "body": "A/B", "aboard": "In", "off": "Out", "dies": "Dead", "pivot": "P", "passengerMass": "M", "appears": "Activate", "getIn": [{ "clip": "c" }] }""", "\"appears\" and \"appearsFrom\"")]
    [InlineData("""{ "format": 1, "name": "x", "logic": { "path": "A", "fsm": "Logic" }, "walker": "A", "body": "A/B", "aboard": "In", "off": "Out", "dies": "Dead", "pivot": "P", "passengerMass": "M", "getIn": [{ "vehicle": "V", "clip": "c" }] }""", "without a vehicle")]
    [InlineData("""{ "format": 1, "name": "x", "logic": { "path": "A", "fsm": "Logic" }, "walker": "A", "body": "A/B", "aboard": "In", "off": "Out", "dies": "Dead", "pivot": "P", "passengerMass": "M" }""", "needs \"getIn\"")]
    public void A_file_that_is_not_right_says_what_is_wrong(string json, string said)
    {
        var error = Assert.Throws<FormatException>(() => HikerDefinition.Parse(json));
        Assert.Contains(said, error.Message);
    }
}
