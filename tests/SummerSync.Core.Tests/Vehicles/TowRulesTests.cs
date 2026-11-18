using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class TowRulesTests
{
    private static RopeEnd Vehicle(bool rear, bool runsHere) => new RopeEnd { OnVehicle = true, Rear = rear, Front = !rear, RunsHere = runsHere };

    private static RopeEnd Thing(bool runsHere, bool anchored = false) => new RopeEnd { RunsHere = runsHere, Anchored = anchored };

    [Fact]
    public void The_vehicle_tied_at_its_rear_tows_and_its_simulating_machine_holds_the_other()
    {
        // The van tows the Satsuma, both on this machine: the rope pulls here and the Satsuma is held here.
        RopePull here = TowRules.Decide(Vehicle(rear: false, runsHere: true), Vehicle(rear: true, runsHere: true));
        Assert.False(here.FirstTows);
        Assert.True(here.Pulls);
        Assert.True(here.HoldsTowed);

        // On a machine that only watches, both are copies: slack, and nothing held.
        RopePull watching = TowRules.Decide(Vehicle(rear: true, runsHere: false), Vehicle(rear: false, runsHere: false));
        Assert.True(watching.FirstTows);
        Assert.False(watching.Pulls);
        Assert.False(watching.HoldsTowed);
    }

    [Fact]
    public void A_towed_vehicle_driven_on_another_machine_is_pulled_there_and_not_here()
    {
        // This machine runs the towing van; the Satsuma is driven elsewhere: tied to its copy, the van would be held still.
        RopePull towing = TowRules.Decide(Vehicle(rear: true, runsHere: true), Vehicle(rear: false, runsHere: false));
        Assert.False(towing.Pulls);
        Assert.True(towing.HoldsTowed);

        // The Satsuma's driver's machine: the rope drags it after the copy of the van.
        RopePull driven = TowRules.Decide(Vehicle(rear: true, runsHere: false), Vehicle(rear: false, runsHere: true));
        Assert.True(driven.Pulls);
        Assert.False(driven.HoldsTowed);
    }

    [Fact]
    public void Something_that_is_not_a_vehicle_is_towed_by_the_vehicle()
    {
        // The video poker's slot tied first and the van's front hook second: the van tows all the same.
        RopePull pull = TowRules.Decide(Thing(runsHere: true, anchored: true), Vehicle(rear: false, runsHere: true));
        Assert.False(pull.FirstTows);
        Assert.True(pull.Pulls);
        Assert.True(pull.HoldsTowed);
    }

    [Fact]
    public void What_is_held_to_the_world_is_pulled_only_where_the_towing_vehicle_runs()
    {
        // A machine that watches the van: its own video poker is held to the world, and a copy that cannot give would
        // tear it out (or snap the rope) before the machine whose physics it is.
        Assert.False(TowRules.Decide(Vehicle(rear: true, runsHere: false), Thing(runsHere: true, anchored: true)).Pulls);

        // A junk car is each game's own: every game drags its own after the copy of the van.
        Assert.True(TowRules.Decide(Vehicle(rear: true, runsHere: false), Thing(runsHere: true)).Pulls);
    }

    [Fact]
    public void A_junk_car_dragged_after_a_copy_of_the_towing_vehicle_does_not_decide_when_the_rope_snaps()
    {
        // Every game drags its own junk car, but only the game that runs the van's physics has the rope as it really is:
        // a break there snaps the rope for everyone.
        RopePull towing = TowRules.Decide(Vehicle(rear: true, runsHere: true), Thing(runsHere: true));
        Assert.True(towing.Pulls);
        Assert.True(towing.Decides);

        // Where the van is a copy, the junk car stands where it stands in that game alone: its joint breaking there (tied
        // longer than it is here, or yanked by the copy) is only that game's.
        RopePull watching = TowRules.Decide(Vehicle(rear: true, runsHere: false), Thing(runsHere: true));
        Assert.True(watching.Pulls);
        Assert.False(watching.Decides);

        // The same with the junk car's hook tied first.
        Assert.False(TowRules.Decide(Thing(runsHere: true), Vehicle(rear: false, runsHere: false)).Decides);
        Assert.True(TowRules.Decide(Thing(runsHere: true), Vehicle(rear: false, runsHere: true)).Decides);
    }

    [Fact]
    public void A_towed_vehicle_decides_where_it_is_pulled_as_before()
    {
        // Held by the machine that runs the van.
        Assert.True(TowRules.Decide(Vehicle(rear: true, runsHere: true), Vehicle(rear: false, runsHere: true)).Decides);

        // Driven on another machine than the van's: the rope pulls only there, after the copy of the van, and only there it
        // can snap.
        Assert.True(TowRules.Decide(Vehicle(rear: true, runsHere: false), Vehicle(rear: false, runsHere: true)).Decides);
        Assert.False(TowRules.Decide(Vehicle(rear: true, runsHere: true), Vehicle(rear: false, runsHere: false)).Decides);

        // Where nothing pulls, nothing decides.
        Assert.False(TowRules.Decide(Vehicle(rear: true, runsHere: false), Vehicle(rear: false, runsHere: false)).Decides);
        Assert.False(TowRules.Decide(Vehicle(rear: true, runsHere: false), Thing(runsHere: true, anchored: true)).Decides);
        Assert.True(TowRules.Decide(Vehicle(rear: true, runsHere: true), Thing(runsHere: true, anchored: true)).Decides);
    }

    [Theory]
    [InlineData(4.0f, 3.0f, 4.0f)] // closer here than where it was tied: as tied, slack until it stretches to that
    [InlineData(4.0f, 4.0f, 4.0f)]
    [InlineData(4.0f, 6.5f, 6.5f)] // the junk car stands farther here: as long as it is here, not stretched
    [InlineData(4.0f, 8.0f, 8.0f)]
    [InlineData(4.0f, 8.01f, -1f)] // beyond a rope's reach here: not tied in this game
    [InlineData(4.0f, float.NaN, -1f)]
    public void A_rope_tied_for_another_player_is_never_stretched_when_it_is_tied(float told, float apart, float length)
    {
        Assert.Equal(length, TowRules.LengthHere(told, apart));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, false, false, true)]
    public void Between_two_vehicles_the_rear_hook_tows_then_the_other_of_a_front_one_then_the_first(bool firstRear, bool firstFront, bool secondRear, bool secondFront, bool firstTows)
    {
        var first = new RopeEnd { OnVehicle = true, Rear = firstRear, Front = firstFront };
        var second = new RopeEnd { OnVehicle = true, Rear = secondRear, Front = secondFront };
        Assert.Equal(firstTows, TowRules.FirstTows(first, second));
    }
}
