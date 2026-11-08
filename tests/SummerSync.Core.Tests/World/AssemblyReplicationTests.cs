using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;
using static SummerSync.Core.Tests.World.AssemblyModelTests;

namespace SummerSync.Core.Tests.World;

public class AssemblyReplicationTests
{
    // One machine's garage: its game's car, which the steps handed to it change as a game would, the replication,
    // and the game's numbers of the car.
    private sealed class Garage
    {
        public Garage(SessionNet.Node node, AssemblyModel? saved = null, AssemblyCatalog? catalog = null)
        {
            Node = node;
            Catalog = catalog ?? Satsuma();
            Car = new AssemblyReplication(node.Session, node.Logger);
            Car.Define(Catalog);
            Game = new AssemblyModel(Catalog);
            if (saved != null)
            {
                Copy(saved, Game);
            }

            Values = new float[Catalog.ValueCount];
            Car.StepWanted += step =>
            {
                Handed.Add(step);
                Game.Apply(step);
            };
            Car.ValueWanted += (index, value) => Values[index] = value;
            Car.ValueDeltaWanted += (index, amount) => Values[index] += amount;
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                if (Car.OwnsValues)
                {
                    for (int i = 0; i < Values.Length; i++)
                    {
                        Car.SetValue(i, Values[i]);
                    }
                }

                Car.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public AssemblyCatalog Catalog { get; }

        public AssemblyReplication Car { get; }

        public AssemblyModel Game { get; }

        public float[] Values { get; }

        public List<AssemblyStep> Handed { get; } = new();

        public byte Id => Node.Session.LocalPlayerId;

        // The world is ready, with the car its save has.
        public void Load(bool ask = true)
        {
            Car.Load(Game);
            if (ask)
            {
                Car.RequestAll();
            }
        }

        // The player does something in this game, which tells the others.
        public void Do(AssemblyStep step)
        {
            Game.Apply(step);
            Assert.True(Car.Report(step));
        }

        public void Pour(int index, float amount)
        {
            Values[index] += amount;
            Assert.True(Car.AddToValue(index, amount));
        }

        // A model of another catalogue instance, entry by entry, as a save read by another machine would be.
        public static void Copy(AssemblyModel from, AssemblyModel to)
        {
            for (int entry = 0; entry < from.Catalog.Count; entry++)
            {
                to.SetInstalled(entry, from.IsInstalled(entry), from.Variant(entry), from.PartRef(entry));
                to.SetWired(entry, from.IsWired(entry));
                to.SetDamaged(entry, from.IsDamaged(entry));
                if (from.IsPainted(entry))
                {
                    to.SetPaint(entry, from.PaintType(entry), from.Color(entry));
                }

                for (int bolt = 0; bolt < from.Catalog.Bolts(entry); bolt++)
                {
                    to.SetStage(entry, bolt, from.Stage(entry, bolt));
                }
            }
        }
    }

    private static void AssertSameCar(params Garage[] garages)
    {
        ulong digest = garages[0].Car.Model.Digest();
        foreach (Garage garage in garages)
        {
            Assert.True(garage.Car.Synced, "Player " + garage.Id + " never got the car.");
            Assert.Equal(0, garage.Car.PendingCount);
            Assert.Equal(garages[0].Car.Sequence, garage.Car.Sequence);
            Assert.Equal(AssemblyReplication.DigestText(digest), AssemblyReplication.DigestText(garage.Car.Model.Digest()));
            Assert.Equal(AssemblyReplication.DigestText(digest), AssemblyReplication.DigestText(garage.Game.Digest()));
            Assert.Equal(0, garage.Car.Malformed);
        }
    }

    private static (SessionNet net, Garage host, Garage first, Garage second) Session(NetworkConditions? conditions = null, int seed = 42)
    {
        var net = new SessionNet(conditions ?? new NetworkConditions { LatencyMs = 60, JitterMs = 15, LossPercent = 2 }, seed);
        var host = new Garage(net.Host);
        var first = new Garage(net.AddClient("Julian"));
        var second = new Garage(net.AddClient("Second"));
        net.Run(1);
        host.Load();
        first.Load();
        second.Load();
        net.Run(1);
        return (net, host, first, second);
    }

    [Fact]
    public void The_messages_follow_the_last_ones_in_use()
    {
        Assert.Equal(88, AssemblyWire.MessageSteps);
        Assert.Equal(89, AssemblyWire.MessageSequenced);
        Assert.Equal(90, AssemblyWire.MessageRequest);
        Assert.Equal(91, AssemblyWire.MessageSnapshot);
        Assert.Equal(92, AssemblyWire.MessageDigest);
        Assert.Equal(93, AssemblyWire.MessageValues);
        Assert.Equal(94, AssemblyWire.MessagePoured);
        Assert.Equal(95, AssemblyWire.MessageAbsorb);
        Assert.Equal(96, AssemblyWire.MessageOwner);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_player_s_steps_reach_every_game_in_the_host_s_order(int seed)
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 80, JitterMs = 40, LossPercent = 10, DuplicatePercent = 5 }, seed);

        host.Do(AssemblyStep.Install(Subframe, 0, 100));
        net.Run(0.05);
        first.Do(AssemblyStep.Install(DoorLeft, 0, 101));
        second.Do(AssemblyStep.Install(Hood, 0, 102));
        second.Do(AssemblyStep.Paint(Hood, 1, 0x336699ff));
        net.Run(0.5);
        first.Do(AssemblyStep.Install(WishboneFL, 0, 103));
        first.Do(AssemblyStep.Tighten(WishboneFL, 0));
        host.Do(AssemblyStep.Tighten(Subframe, 2));
        second.Do(AssemblyStep.Wire(Wiring, true));
        net.Run(0.2);
        host.Do(AssemblyStep.Outcome(Hood, AssemblyWire.OutcomeDamaged));
        first.Do(AssemblyStep.Remove(DoorLeft));
        net.Run(3);

        AssertSameCar(host, first, second);
        AssemblyModel car = host.Car.Model;
        Assert.True(car.IsInstalled(Subframe));
        Assert.Equal(100, car.PartRef(Subframe));
        Assert.True(car.IsInstalled(WishboneFL));
        Assert.Equal(1, car.Stage(WishboneFL, 0));
        Assert.False(car.IsInstalled(DoorLeft));
        Assert.True(car.IsWired(Wiring));
        Assert.True(car.IsDamaged(Hood));
        Assert.Equal(0x336699ff, car.Color(Hood));
        Assert.Equal(10u, host.Car.Sequence);

        // Nobody's game had to be corrected: every step fitted.
        Assert.All(new[] { host, first, second }, garage => Assert.Equal(0, garage.Car.Corrections));

        // Each game was handed the others' steps, never its own.
        Assert.Equal(7, host.Handed.Count);
        Assert.DoesNotContain(first.Handed, step => step.Player == first.Id);
        Assert.Contains(first.Handed, step => step.Equals(AssemblyStep.Install(Hood, 0, 102)) && step.Player == second.Id);
        Assert.Contains(second.Handed, step => step.Equals(AssemblyStep.Outcome(Hood, AssemblyWire.OutcomeDamaged)) && step.Player == host.Id);
    }

    [Fact]
    public void Two_players_installing_into_the_same_slot_at_once_end_with_the_first_one_s_part()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 100 });

        // Two doors of the same kind, one in each player's hand, into the same slot in the same moment.
        first.Do(AssemblyStep.Install(DoorLeft, 0, 11));
        net.Run(0.05);
        second.Do(AssemblyStep.Install(DoorLeft, 0, 22));
        net.Run(2);

        AssertSameCar(host, first, second);
        Assert.Equal(11, host.Car.Model.PartRef(DoorLeft));
        Assert.All(new[] { host, first, second }, garage => Assert.Equal(11, garage.Game.PartRef(DoorLeft)));

        // The second player's door comes out of the slot and the first one's goes in, in its game only.
        Assert.Equal(1, second.Car.Refused);
        Assert.Equal(new[] { AssemblyStep.Remove(DoorLeft), AssemblyStep.Install(DoorLeft, 0, 11) }, second.Handed);
        Assert.All(second.Handed, step => Assert.Equal(AssemblyWire.NoPlayer, step.Player));
        Assert.Empty(first.Handed);
        Assert.Equal(new[] { AssemblyStep.Install(DoorLeft, 0, 11) }, host.Handed);
        Assert.Equal(1, host.Car.Refused);
    }

    [Fact]
    public void A_step_of_the_host_goes_before_a_client_s_that_crosses_it()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 100 });

        host.Do(AssemblyStep.Install(Hood, 0, 5));
        first.Do(AssemblyStep.Install(Hood, 0, 7));
        first.Do(AssemblyStep.Tighten(Hood, 0));
        net.Run(2);

        // The host's hood is in; the client's turn of a bolt still counts, on the hood that is there.
        AssertSameCar(host, first, second);
        Assert.Equal(5, first.Game.PartRef(Hood));
        Assert.Equal(1, host.Car.Model.Stage(Hood, 0));
        Assert.Equal(new[] { AssemblyStep.Remove(Hood), AssemblyStep.Install(Hood, 0, 5) }, first.Handed);
        Assert.Equal(new[] { AssemblyStep.Install(Hood, 0, 5), AssemblyStep.Tighten(Hood, 0) }, second.Handed);
        Assert.Equal(1, first.Car.Refused);
    }

    [Fact]
    public void A_refused_install_is_undone_with_a_remove()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 50 });
        host.Do(AssemblyStep.Install(Hood, 0, 5));
        net.Run(1);

        // This game did not make the host's step (it failed, as a game can): the slot is empty here, and its player
        // puts a hood in.
        first.Game.Apply(AssemblyStep.Remove(Hood));
        first.Car.Shown.Apply(AssemblyStep.Remove(Hood));
        first.Handed.Clear();
        first.Do(AssemblyStep.Install(Hood, 0, 9));
        net.Run(1);

        Assert.Equal(1, first.Car.Refused);
        Assert.Equal(new[] { AssemblyStep.Remove(Hood), AssemblyStep.Install(Hood, 0, 5) }, first.Handed);
        AssertSameCar(host, first, second);
    }

    [Fact]
    public void Nothing_is_undone_when_the_host_s_car_already_agrees()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 100 });
        host.Do(AssemblyStep.Install(DoorRight, 0, 3));
        net.Run(1);

        host.Do(AssemblyStep.Remove(DoorRight));
        first.Do(AssemblyStep.Remove(DoorRight));
        net.Run(1);

        Assert.Equal(1, first.Car.Refused);
        Assert.Empty(first.Handed.Skip(1));
        Assert.Equal(0, first.Car.Corrections);
        AssertSameCar(host, first, second);
    }

    [Fact]
    public void Bolts_of_two_players_on_different_entries_go_together_and_in_few_messages()
    {
        var (net, host, first, second) = Session();
        host.Do(AssemblyStep.Install(Subframe, 0, 1));
        host.Do(AssemblyStep.Install(Hood, 0, 2));
        net.Run(1);

        // Two players turning wrenches at once, several turns a frame.
        for (int turn = 0; turn < 8; turn++)
        {
            for (int bolt = 0; bolt < 4; bolt++)
            {
                first.Do(AssemblyStep.Tighten(Subframe, bolt));
                second.Do(AssemblyStep.Tighten(Hood, bolt));
            }

            net.Run(1.0 / 60.0);
        }

        net.Run(2);

        AssertSameCar(host, first, second);
        for (int bolt = 0; bolt < 4; bolt++)
        {
            Assert.Equal(8, host.Car.Model.Stage(Subframe, bolt));
            Assert.Equal(8, first.Game.Stage(Hood, bolt));
            Assert.Equal(8, second.Game.Stage(Subframe, bolt));
        }

        Assert.All(new[] { host, first, second }, garage => Assert.Equal(0, garage.Car.Corrections));
        int stepMessages = net.Host.Messages.Count(message => message.From == first.Id && message.Data[0] == AssemblyWire.MessageSteps);
        Assert.InRange(stepMessages, 1, 10);

        // The host's two parts, and the other player's 32 turns.
        Assert.Equal(34, first.Handed.Count);
        Assert.Equal(34, second.Handed.Count);
    }

    [Fact]
    public void A_bolt_never_turns_past_its_stages()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 100 });
        host.Do(AssemblyStep.Install(Hood, 0, 2));
        for (int i = 0; i < 7; i++)
        {
            host.Do(AssemblyStep.Tighten(Hood, 1));
        }

        net.Run(1);

        // Both turn the same bolt its last stage at once: one of the turns has nowhere to go.
        first.Do(AssemblyStep.Tighten(Hood, 1));
        second.Do(AssemblyStep.Tighten(Hood, 1));
        net.Run(1);

        AssertSameCar(host, first, second);
        Assert.Equal(8, host.Car.Model.Stage(Hood, 1));
        Assert.Equal(1, first.Car.Refused + second.Car.Refused);
        Assert.Equal(0, first.Car.Corrections + second.Car.Corrections);

        // Past the start the same way, and a bolt of a part that is not on does not turn at all.
        host.Do(AssemblyStep.Remove(Hood));
        first.Do(AssemblyStep.Loosen(Hood, 2));
        net.Run(1);
        AssertSameCar(host, first, second);
        Assert.Equal(0, first.Game.Stage(Hood, 2));
        Assert.Equal(2, first.Car.Refused + second.Car.Refused);

        // Except the block's, which bolts on while it hangs from the hoist.
        second.Do(AssemblyStep.Tighten(Block, 0));
        net.Run(1);
        AssertSameCar(host, first, second);
        Assert.Equal(1, first.Game.Stage(Block, 0));

        // A bolt the car does not have is not told at all.
        Assert.False(first.Car.Report(AssemblyStep.Tighten(Hood, 9)));
        Assert.False(first.Car.Report(AssemblyStep.Turn(Hood, 0, 3)));
    }

    [Fact]
    public void A_refused_bolt_turn_is_undone_in_the_game_that_made_it()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 100 });
        host.Do(AssemblyStep.Install(Hood, 0, 2));
        net.Run(1);
        first.Handed.Clear();

        // The host takes the hood off while the client is still tightening it.
        host.Do(AssemblyStep.Remove(Hood));
        first.Do(AssemblyStep.Tighten(Hood, 3));
        first.Do(AssemblyStep.Tighten(Hood, 3));
        net.Run(1);

        AssertSameCar(host, first, second);
        Assert.False(first.Game.IsInstalled(Hood));
        Assert.Equal(0, first.Game.Stage(Hood, 3));
        Assert.Equal(new[] { AssemblyStep.Loosen(Hood, 3), AssemblyStep.Loosen(Hood, 3), AssemblyStep.Remove(Hood) }, first.Handed);
        Assert.Equal(2, first.Car.Refused);
    }

    [Fact]
    public void A_player_who_comes_later_gets_the_whole_car_and_the_same_digest()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, JitterMs = 15, LossPercent = 2 });
        var host = new Garage(net.Host);
        var early = new Garage(net.AddClient("Julian"));
        net.Run(1);
        host.Load();
        early.Load();
        net.Run(1);
        host.Do(AssemblyStep.Install(Subframe, 0, 1));
        for (int i = 0; i < 8; i++)
        {
            host.Do(AssemblyStep.Tighten(Subframe, 0));
        }

        early.Do(AssemblyStep.Install(WishboneFL, 0, 2));
        early.Do(AssemblyStep.Tighten(WishboneFL, 1));
        early.Do(AssemblyStep.Paint(DoorRight, 4, 0x55667788));
        host.Do(AssemblyStep.Wire(Wiring, true));
        host.Do(AssemblyStep.Outcome(Subframe, AssemblyWire.OutcomeDamaged));
        host.Do(AssemblyStep.Tighten(Block, 2));
        net.Run(1);

        // The late one's save is older: a hood the others took off since, and less bolted.
        var saved = new AssemblyModel(Satsuma());
        saved.SetInstalled(Hood, true);
        saved.SetStage(Hood, 0, 2);
        saved.SetInstalled(Subframe, true);
        saved.SetStage(Subframe, 0, 3);
        var late = new Garage(net.AddClient("Late"), saved);
        net.Run(1);
        late.Load();
        net.Run(1);

        AssertSameCar(host, early, late);
        Assert.Equal(1, late.Car.Snapshots);
        AssemblyStep[] expected =
        {
            AssemblyStep.Loosen(Hood, 0),
            AssemblyStep.Loosen(Hood, 0),
            AssemblyStep.Remove(Hood),
            AssemblyStep.Install(WishboneFL, 0, 2),
            AssemblyStep.Tighten(Subframe, 0),
            AssemblyStep.Tighten(Subframe, 0),
            AssemblyStep.Tighten(Subframe, 0),
            AssemblyStep.Tighten(Subframe, 0),
            AssemblyStep.Tighten(Subframe, 0),
            AssemblyStep.Tighten(WishboneFL, 1),
            AssemblyStep.Tighten(Block, 2),
            AssemblyStep.Wire(Wiring, true),
            AssemblyStep.Paint(DoorRight, 4, 0x55667788),
            AssemblyStep.Outcome(Subframe, AssemblyWire.OutcomeDamaged),
        };
        Assert.Equal(expected, late.Handed);

        // From then on it hears every step like the others.
        early.Do(AssemblyStep.Install(Hood, 0, 8));
        net.Run(1);
        AssertSameCar(host, early, late);
        Assert.Equal(AssemblyStep.Install(Hood, 0, 8), late.Handed.Last());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void A_player_who_joins_while_the_others_work_misses_nothing_and_hears_nothing_twice(int seed)
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 70, JitterMs = 30, LossPercent = 8 }, seed);
        var host = new Garage(net.Host);
        var worker = new Garage(net.AddClient("Julian"));
        net.Run(1);
        host.Load();
        worker.Load();
        net.Run(1);
        host.Do(AssemblyStep.Install(Subframe, 0, 1));
        host.Do(AssemblyStep.Install(Hood, 0, 2));
        net.Run(1);

        Garage? late = null;
        for (int frame = 0; frame < 240; frame++)
        {
            // Both turn bolts the whole time; the late one joins, loads and asks in the middle of it.
            int turn = frame / 4;
            if (frame % 4 == 0 && turn < 32)
            {
                worker.Do(AssemblyStep.Tighten(Subframe, turn % 4));
                host.Do(AssemblyStep.Tighten(Hood, turn % 4));
            }

            if (frame == 20)
            {
                late = new Garage(net.AddClient("Late"));
            }

            if (frame == 50)
            {
                late!.Load();
            }

            net.Run(1.0 / 60.0);
        }

        net.Run(2);
        AssertSameCar(host, worker, late!);
        Assert.Equal(8, late!.Game.Stage(Subframe, 3));
        Assert.Equal(8, late.Game.Stage(Hood, 0));
        Assert.Equal(66u, late.Car.Sequence);
    }

    [Fact]
    public void The_whole_car_comes_in_pieces_when_it_does_not_fit_in_one()
    {
        AssemblyCatalog Big()
        {
            var catalog = new AssemblyCatalog();
            for (int i = 0; i < 300; i++)
            {
                catalog.Add("Part" + i, 12);
            }

            for (int i = 0; i < 100; i++)
            {
                catalog.AddValue("Number" + i, 0.1f);
            }

            return catalog;
        }

        var net = new SessionNet(new NetworkConditions { LatencyMs = 40, LossPercent = 5 });
        var host = new Garage(net.Host, catalog: Big());
        net.Run(0.5);
        host.Load();
        for (int i = 0; i < 300; i++)
        {
            host.Do(AssemblyStep.Install(i, i % 3, 1000 + i));
            for (int bolt = 0; bolt < 12; bolt += 2)
            {
                host.Do(AssemblyStep.Tighten(i, bolt));
            }

            host.Do(AssemblyStep.Paint(i, 1, i * 77));
        }

        for (int i = 0; i < 100; i++)
        {
            host.Values[i] = i * 1.5f;
        }

        net.Run(1);
        var late = new Garage(net.AddClient("Late"), catalog: Big());
        net.Run(1);
        late.Load();
        net.Run(2);

        AssertSameCar(host, late);
        int pieces = late.Node.Messages.Count(message => message.Data[0] == AssemblyWire.MessageSnapshot);
        Assert.True(pieces > 3, pieces + " piece(s)");
        Assert.Equal(1000 + 299, late.Game.PartRef(299));
        Assert.Equal(host.Values, late.Values);
        Assert.Equal(host.Car.ValuesDigest(), late.Car.ValuesDigest());
    }

    [Fact]
    public void Steps_made_before_asking_for_the_car_are_told_after_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new Garage(net.Host);
        var guest = new Garage(net.AddClient("Julian"));
        net.Run(1);
        host.Load();
        host.Do(AssemblyStep.Install(Subframe, 0, 1));
        net.Run(0.5);

        // The guest's world loaded with a subframe of its own save, and its player bolted it before asking.
        guest.Game.SetInstalled(Subframe, true);
        guest.Load(ask: false);
        guest.Do(AssemblyStep.Tighten(Subframe, 1));
        net.Run(1);
        Assert.False(guest.Car.Synced);
        Assert.Equal(0, host.Car.Model.Stage(Subframe, 1));

        guest.Car.RequestAll();
        net.Run(1);

        AssertSameCar(host, guest);
        Assert.Equal(1, host.Car.Model.Stage(Subframe, 1));
        Assert.Empty(guest.Handed);
    }

    [Fact]
    public void A_client_already_in_its_world_who_asks_while_connecting_is_answered_once_connected()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Garage(net.Host);
        host.Load();
        host.Do(AssemblyStep.Install(Hood, 2, 4));

        var guest = new Garage(net.AddClient("Julian"));
        Assert.NotEqual(SessionState.Connected, guest.Node.Session.State);
        guest.Load();
        net.Run(1);

        AssertSameCar(host, guest);
        Assert.Equal(new[] { AssemblyStep.Install(Hood, 2, 4) }, guest.Handed);
    }

    [Fact]
    public void A_client_that_asks_before_the_host_s_world_is_ready_is_answered_when_it_is()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Garage(net.Host);
        var guest = new Garage(net.AddClient("Julian"));
        net.Run(1);
        guest.Load();
        net.Run(1);
        Assert.False(guest.Car.Synced);

        host.Game.SetInstalled(Hood, true, 0, 4);
        host.Load();
        net.Run(1);

        AssertSameCar(host, guest);
        Assert.True(guest.Game.IsInstalled(Hood));
    }

    [Fact]
    public void What_a_guest_does_while_the_host_s_world_is_still_loading_counts_once_it_is_ready()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, LossPercent = 5 });
        var host = new Garage(net.Host);
        var guest = new Garage(net.AddClient("Julian"));
        net.Run(1);

        // Both connected in the menu; the guest's world loads first and its player gets to work.
        guest.Load();
        guest.Do(AssemblyStep.Install(Subframe, 0, 7));
        guest.Do(AssemblyStep.Tighten(Subframe, 0));
        guest.Pour(1, 2f);
        net.Run(2);
        Assert.False(guest.Car.Synced);
        Assert.Equal(2, guest.Car.PendingCount);

        host.Values[1] = 3f;
        host.Load();
        net.Run(2);

        AssertSameCar(host, guest);
        Assert.True(host.Car.Model.IsInstalled(Subframe));
        Assert.Equal(1, host.Car.Model.Stage(Subframe, 0));
        Assert.Equal(5f, host.Values[1]);
        Assert.Equal(5f, guest.Values[1]);
        Assert.Empty(guest.Handed);
    }

    [Fact]
    public void A_host_out_of_its_world_refuses_steps_instead_of_leaving_them_unanswered()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 50 });
        host.Do(AssemblyStep.Install(Hood, 0, 5));
        net.Run(1);

        host.Car.ClearWorld();
        first.Do(AssemblyStep.Tighten(Hood, 2));
        net.Run(1);

        Assert.Equal(0, first.Car.PendingCount);
        Assert.Equal(1, first.Car.Refused);
        Assert.Equal(0, first.Game.Stage(Hood, 2));
        Assert.Equal(AssemblyStep.Loosen(Hood, 2), first.Handed.Last());
        Assert.Equal(first.Car.Model.Digest(), second.Car.Model.Digest());
    }

    [Fact]
    public void A_digest_that_differs_brings_the_whole_car_again()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 50 });
        host.Do(AssemblyStep.Install(Hood, 0, 5));
        net.Run(1);
        Assert.Equal(0, first.Car.DigestsDiffered);

        // Something went wrong on this machine (a bug): its car is not the host's any more.
        first.Car.Model.SetStage(Hood, 3, 4);
        first.Car.Shown.SetStage(Hood, 3, 4);
        first.Game.SetStage(Hood, 3, 4);
        net.Run(AssemblyReplication.DigestInterval + 1);

        Assert.Equal(1, first.Car.DigestsDiffered);
        Assert.Equal(2, first.Car.Snapshots);
        Assert.Contains(AssemblyStep.Loosen(Hood, 3), first.Handed);
        AssertSameCar(host, first, second);
        Assert.True(second.Car.DigestsAgreed >= 1);
        Assert.Equal(host.Car.Sequence, second.Car.HostDigestSequence);
        Assert.Contains(first.Node.Log, entry => entry.Message.Contains("asking for the whole car again"));
    }

    [Fact]
    public void Correct_hands_a_game_what_it_failed_to_make()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 50 });
        host.Do(AssemblyStep.Install(Hood, 0, 5));
        host.Do(AssemblyStep.Tighten(Hood, 0));
        net.Run(1);

        // The game of this client could not put the hood in when it was handed the step: its car, read, lacks it.
        first.Game.SetStage(Hood, 0, 0);
        first.Game.SetInstalled(Hood, false);
        first.Handed.Clear();

        Assert.Equal(2, first.Car.Correct(first.Game.Clone()));
        Assert.Equal(new[] { AssemblyStep.Install(Hood, 0, 5), AssemblyStep.Tighten(Hood, 0) }, first.Handed);
        Assert.Equal(0, first.Car.Correct(first.Game));
        Assert.Contains(first.Node.Log, entry => entry.Message.Contains("Hood (installed there, not here)"));
    }

    [Fact]
    public void A_step_of_the_host_that_its_car_refuses_is_undone_there()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 50 });
        host.Do(AssemblyStep.Install(Hood, 0, 5));
        net.Run(1);

        // The host's game put another hood over the first (a game side that did not notice the slot was full).
        host.Do(AssemblyStep.Install(Hood, 0, 6));
        net.Run(1);

        Assert.Equal(1, host.Car.Refused);
        Assert.Equal(new[] { AssemblyStep.Remove(Hood), AssemblyStep.Install(Hood, 0, 5) }, host.Handed);
        AssertSameCar(host, first, second);
        Assert.Equal(1u, host.Car.Sequence);
    }

    [Fact]
    public void The_car_s_numbers_are_the_owner_s_and_every_change_counts_once()
    {
        var (net, host, first, second) = Session(new NetworkConditions { LatencyMs = 60, JitterMs = 15, LossPercent = 2 });
        host.Values[0] = 3f;
        host.Values[1] = 5f;
        host.Values[2] = 12f;
        net.Run(1);
        Assert.Equal(new[] { 3f, 5f, 12f }, first.Values);
        Assert.Equal(new[] { 3f, 5f, 12f }, second.Values);
        Assert.True(host.Car.OwnsValues);
        Assert.False(first.Car.OwnsValues);

        // A guest pours oil: it shows at once there, and the host's game adds it.
        first.Pour(0, 0.5f);
        Assert.Equal(3.5f, first.Car.ShownValue(0));
        net.Run(1);
        Assert.Equal(new[] { 3.5f, 5f, 12f }, host.Values);
        Assert.Equal(new[] { 3.5f, 5f, 12f }, first.Values);
        Assert.Equal(new[] { 3.5f, 5f, 12f }, second.Values);

        // The host's game runs the engine: its water goes down by itself.
        host.Values[1] = 4f;
        net.Run(1);
        Assert.Equal(4f, second.Values[1]);

        // The first guest takes the car over while the second pours into it: nothing is lost or counted twice.
        second.Pour(2, 1f);
        Assert.True(host.Car.SetValuesOwner(first.Id));
        first.Pour(0, 0.25f);
        net.Run(1);
        Assert.True(first.Car.OwnsValues);
        Assert.False(host.Car.OwnsValues);
        Assert.Equal(first.Id, second.Car.ValuesOwner);
        Assert.Equal(new[] { 3.75f, 4f, 13f }, first.Values);
        Assert.Equal(first.Values, host.Values);
        Assert.Equal(first.Values, second.Values);

        // Its game runs the car now; the host pours, the second guest pours.
        first.Values[0] = 3.25f;
        host.Pour(1, 2f);
        second.Pour(1, 1f);
        net.Run(1);
        Assert.Equal(new[] { 3.25f, 7f, 13f }, first.Values);
        Assert.Equal(first.Values, host.Values);
        Assert.Equal(first.Values, second.Values);
        Assert.Equal(host.Car.ValuesDigest(), first.Car.ValuesDigest());

        // It leaves: the host runs the car from the last numbers it heard.
        second.Pour(0, 1f);
        net.Run(0.02);
        first.Node.Session.Close();
        net.Run(2);
        Assert.True(host.Car.OwnsValues);
        Assert.Equal(new[] { 4.25f, 7f, 13f }, host.Values);
        Assert.Equal(host.Values, second.Values);
        Assert.Equal(host.Car.ValuesDigest(), second.Car.ValuesDigest());
        Assert.Equal(0, host.Car.Malformed + second.Car.Malformed);
    }

    [Fact]
    public void The_owner_says_its_numbers_a_few_times_a_second_and_small_changes_later()
    {
        var (net, host, first, _) = Session(new NetworkConditions { LatencyMs = 20 });
        net.Run(1);
        int before = first.Node.Messages.Count(message => message.Data[0] == AssemblyWire.MessageValues);

        // A number that changes every frame: said about four times a second, not sixty.
        for (int frame = 0; frame < 120; frame++)
        {
            host.Values[0] += 1f;
            net.Run(1.0 / 60.0);
        }

        int words = first.Node.Messages.Count(message => message.Data[0] == AssemblyWire.MessageValues) - before;
        Assert.InRange(words, 6, 10);
        net.Run(1);
        Assert.Equal(120f, first.Values[0]);

        // Under its step of 0.5, the charge is said within the refresh anyway.
        host.Values[2] = 10.2f;
        net.Run(AssemblyReplication.ValueRefresh + 0.5);
        Assert.Equal(10.2f, first.Values[2]);
    }

    [Fact]
    public void Only_a_player_who_asked_for_the_car_can_run_its_numbers()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Garage(net.Host);
        var guest = new Garage(net.AddClient("Julian"));
        net.Run(1);
        host.Load();

        Assert.False(host.Car.SetValuesOwner(guest.Id));
        Assert.False(guest.Car.SetValuesOwner(guest.Id));
        Assert.Equal(host.Id, host.Car.ValuesOwner);
    }

    [Fact]
    public void Malformed_messages_are_counted_and_change_nothing()
    {
        var (net, host, first, second) = Session(new NetworkConditions());
        uint shape = host.Catalog.Shape;
        ulong digest = host.Car.Model.Digest();
        var writer = new PacketWriter(256);
        void Send(SessionNet.Node from, byte to, Action<PacketWriter> write)
        {
            writer.Reset();
            write(writer);
            from.Session.Send(to, writer.Buffer, 0, writer.Length, true);
        }

        // A step of an entry the car does not have, a bolt turned two ways, a kind of step there is not, a message
        // cut short, and messages only the host says, said by a client.
        Send(first.Node, Protocol.HostPlayerId, w => { w.WriteByte(AssemblyWire.MessageSteps); w.WriteUInt32(shape); w.WriteUInt32(1); w.WriteUInt16(1); w.WriteByte((byte)AssemblyStepKind.Install); w.WriteVarUInt32(500); w.WriteVarUInt32(0); w.WriteVarUInt32(0); });
        Send(first.Node, Protocol.HostPlayerId, w => { w.WriteByte(AssemblyWire.MessageSteps); w.WriteUInt32(shape); w.WriteUInt32(1); w.WriteUInt16(1); w.WriteByte((byte)AssemblyStepKind.Bolt); w.WriteVarUInt32(Hood); w.WriteVarUInt32(0); w.WriteByte(7); });
        Send(first.Node, Protocol.HostPlayerId, w => { w.WriteByte(AssemblyWire.MessageSteps); w.WriteUInt32(shape); w.WriteUInt32(1); w.WriteUInt16(1); w.WriteByte(42); w.WriteVarUInt32(Hood); });
        Send(first.Node, Protocol.HostPlayerId, w => { w.WriteByte(AssemblyWire.MessageSteps); w.WriteUInt32(shape); w.WriteUInt32(1); w.WriteUInt16(3); w.WriteByte((byte)AssemblyStepKind.Remove); w.WriteVarUInt32(Hood); });
        Send(first.Node, Protocol.HostPlayerId, w => { w.WriteByte(AssemblyWire.MessageSequenced); w.WriteUInt32(shape); w.WriteUInt32(1); w.WriteUInt16(0); });
        Send(first.Node, Protocol.HostPlayerId, w => { w.WriteByte(AssemblyWire.MessageOwner); w.WriteUInt32(shape); w.WriteByte(1); w.WriteUInt32(0); });
        Send(first.Node, Protocol.HostPlayerId, w => { w.WriteByte(AssemblyWire.MessagePoured); w.WriteUInt32(shape); w.WriteUInt32(1); w.WriteUInt16(1); w.WriteVarUInt32(0); w.WriteSingle(float.NaN); });

        // To a client: a step of the host's order with an entry past the end, and numbers of a value it does not have.
        Send(net.Host, first.Id, w => { w.WriteByte(AssemblyWire.MessageSequenced); w.WriteUInt32(shape); w.WriteUInt32(host.Car.Sequence + 1); w.WriteUInt16(1); w.WriteByte((byte)AssemblyStepKind.Remove); w.WriteByte(0); w.WriteVarUInt32(0); w.WriteVarUInt32(400); });
        Send(net.Host, first.Id, w => { w.WriteByte(AssemblyWire.MessageValues); w.WriteUInt32(shape); w.WriteByte(0); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteByte(0); w.WriteUInt16(1); w.WriteVarUInt32(40); w.WriteSingle(1f); });
        Send(net.Host, first.Id, w => { w.WriteByte(AssemblyWire.MessageSteps); w.WriteUInt32(shape); w.WriteUInt32(1); w.WriteUInt16(0); });
        net.Run(1);

        Assert.Equal(7, host.Car.Malformed);
        Assert.Equal(3, first.Car.Malformed);
        Assert.Equal(digest, host.Car.Model.Digest());
        Assert.Equal(digest, first.Car.Model.Digest());
        Assert.Empty(host.Handed);
        Assert.Empty(first.Handed);
        Assert.Contains(host.Node.Log, entry => entry.Message.Contains("Dropped a message about the car"));

        // And the car still works.
        first.Do(AssemblyStep.Install(Hood, 0, 1));
        net.Run(1);
        Assert.True(second.Game.IsInstalled(Hood));
        Assert.Equal(7, host.Car.Malformed);
        Assert.Equal(0, second.Car.Malformed);
    }

    [Fact]
    public void A_car_listed_differently_is_not_shared_and_its_messages_are_not_read()
    {
        AssemblyCatalog Other()
        {
            AssemblyCatalog catalog = Satsuma();
            catalog.Add("Spoiler", 2);
            return catalog;
        }

        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Garage(net.Host);
        var same = new Garage(net.AddClient("Julian"));
        var other = new Garage(net.AddClient("Other"), catalog: Other());
        net.Run(1);
        host.Load();
        same.Load();
        other.Load();
        net.Run(1);

        Assert.True(other.Car.Mismatch);
        Assert.False(other.Car.Synced);
        Assert.Equal(1, host.Car.Subscribers);
        Assert.False(other.Car.Report(AssemblyStep.Install(Hood, 0, 1)));

        // A message of the other list's shape: not read, and not counted as malformed.
        var writer = new PacketWriter(64);
        writer.WriteByte(AssemblyWire.MessageSteps);
        writer.WriteUInt32(other.Catalog.Shape);
        writer.WriteUInt32(1);
        writer.WriteUInt16(1);
        writer.WriteByte((byte)AssemblyStepKind.Install);
        writer.WriteVarUInt32(Hood);
        writer.WriteVarUInt32(0);
        writer.WriteVarUInt32(0);
        other.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        same.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(1);

        Assert.Equal(2, host.Car.OtherShape);
        Assert.Equal(0, host.Car.Malformed);
        Assert.False(host.Car.Model.IsInstalled(Hood));
        Assert.Contains(host.Node.Log, entry => entry.Message.Contains("lists the car differently"));

        host.Do(AssemblyStep.Install(Hood, 0, 2));
        net.Run(1);
        AssertSameCar(host, same);
        Assert.Empty(other.Handed);
    }

    [Fact]
    public void The_same_endpoints_hold_one_car_session_after_another()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 80, JitterMs = 60 });
        var host = new Garage(net.Host);
        var guest = new Garage(net.AddClient("Julian"));
        net.Run(1);
        host.Load();
        guest.Load();
        net.Run(1);
        host.Do(AssemblyStep.Install(Hood, 0, 1));
        guest.Do(AssemblyStep.Install(Subframe, 0, 2));
        guest.Do(AssemblyStep.Tighten(Subframe, 0));

        // The host closes with steps still on their way, and both start over on the same endpoints, as with Steam.
        net.Host.Session.Close();
        host.Car.Detach();
        guest.Car.Detach();
        net.Run(0.5);
        net.Host.Session.StartHost("Host");
        net.Run(2);
        guest.Node.Session.Connect(SessionNet.HostPeer, "Julian");
        net.Run(1);
        Assert.Equal(SessionState.Connected, guest.Node.Session.State);

        var host2 = new Garage(net.Host);
        var guest2 = new Garage(guest.Node);
        host2.Load();
        guest2.Load();
        net.Run(1);
        guest2.Do(AssemblyStep.Install(DoorLeft, 0, 3));
        host2.Do(AssemblyStep.Install(DoorRight, 0, 4));
        net.Run(1);

        AssertSameCar(host2, guest2);
        Assert.Equal(2u, host2.Car.Sequence);
        Assert.False(host2.Car.Model.IsInstalled(Hood));
        Assert.False(guest2.Game.IsInstalled(Subframe));
        Assert.Equal(0, host2.Car.Malformed + guest2.Car.Malformed + host2.Car.OtherShape + guest2.Car.OtherShape);
    }

    [Fact]
    public void The_overlay_line_says_where_the_car_is()
    {
        var (net, host, first, _) = Session(new NetworkConditions());
        host.Do(AssemblyStep.Install(Hood, 0, 1));
        net.Run(AssemblyReplication.DigestInterval + 0.5);

        Assert.Contains("Car: 1 step(s) here", host.Car.Describe());
        Assert.Contains("2 client(s)", host.Car.Describe());
        Assert.Contains("heard", first.Car.Describe());
        Assert.Contains("agreed", first.Car.Describe());
        Assert.Contains(AssemblyReplication.DigestText(host.Car.Digest()), first.Car.Describe());
    }
}
