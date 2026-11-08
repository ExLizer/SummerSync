using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SummerSync.Core;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using SummerSync.Core.World;

namespace SummerSync.NetProbe;

/// <summary>
/// Runs one end of a SummerSync session from a console, over UDP or in loopback, and prints what the game's
/// debug overlay would show. Exit code 0 means the session connected and no reliable message was lost or
/// reordered.
/// </summary>
internal static class Program
{
    private const int DefaultPort = 27016;
    private const double FrameSeconds = 1.0 / 60.0;

    private const string Usage =
        """
        netprobe loopback                 host and client inside this process
        netprobe udphost [port] [lan]     host on UDP; 'lan' also accepts other machines
        netprobe udpjoin [address] [port] join a UDP host; default is this machine

        Options, after the command:
          --seconds <n>           stop after n seconds (default: run until Ctrl+C or disconnected)
          --sim <ms> [ms] [%]     simulated latency, jitter and loss on what this side sends
          --name <name>           player name (default: probe)
          --game <identity>       game build to announce, as the mod logs it ("5.0.0f4/1a2b3c4d")
          --game-dir <folder>     work the game build out from a My Summer Car installation
          --bot                   be a player that walks by itself, in front of the first player it sees
          --bot-at <x> <y> <z>    the same, around a given point of the map
          --carry                 with a bot: pick up the nearest loose object, carry it, throw it, and again
          --objects <n>           as a host: put n crates around the bot's point, for others to pick up
          --objects-save <file>   as a client: at the end, write the host's loose objects (name and place) to the file
          --objects-load <file>   as a host: this world has the objects of such a file, so that a game whose world they
                                  come from pairs every one of them; with --bot-at and --carry, the bot picks them up
          --drive                 with a bot: take the wheel of the first vehicle, drive it in circles, get out, and again
          --van                   as a host: have a van, for another probe to drive
          --songs <n> <kB>        as a host: offer n made-up songs of that size; as a client: n have to arrive whole
          --world <n> <kB>        as a host: offer a made-up saved world of n files of that size; as a client: the
                                  host's last world has to be here, whole, by the end
          --resave <at>           with --world: the host saves its world again at that second (one file changes);
                                  a client has to end with the second one
          --money <start> <spend> this game starts with that money and, every 2 s, spends that much; both ends
                                  have to end with the same money, everything spent by anyone taken from the host's
          --made <n>              this game makes n things, one every half second; with two players, each end has
                                  to hear of the other's n
          --shop <n>              this game takes n things off a shelf, one every half second; with two players, both
                                  ends have to end with 2n on the counter
          --order <1-5>           as a client: order at Teimo's bar (1 beer, 2 vodka, 3 sausage and fries, 4 coffee,
                                  5 cigarettes); the host's game serves it
          --lotto                 as a client: hear the host's lottery draw, then buy a ticket; it has to come back
          --phone                 as a client: pick up the host's phone two seconds after it starts ringing
          --phone-ring            as a host: make the phone ring (a call about a sewage well); someone has to take it
          --sleep                 say this player sleeps, for a real game's player to sleep with
          --bus-rider <bits> [<paid at>]
                                  say what this player needs of the host's bus: 1 in it, 2 sitting, 4 a stop asked from
                                  inside, 8 waiting at Loppe, 16 at Kesseli, 32 at Rykipohja; and at that second, a ticket
          --bus-door <at>         as a host: the bus's door opens at that second and closes ten seconds later
          --police <place>        as a host: a checkpoint at that place (1 Airport, 2 Skihill, 3 Fields; 0 none)
          --tally <name> <values> <at>   say that a tally of that name is those values (comma separated), at that second
          --vehicles-save <file>  as a client: at the end, write the host's vehicles (name and place) to the file
          --vehicles-load <file>  as a host: have the vehicles of such a file, standing where they were
          --activity <cycle|drink|smoke|pee> [n]   with a bot: it stands still at its point and drinks (from drink n), smokes or
                                  pees, or all three in turn (at the wheel too, with --drive-only); --bot-face <degrees>,
                                  where it faces; --bot-eyes <m>, how high its eyes are (0.85 crouched)
          --throw <s> <x> <y> <z> at that second, throw an empty beer bottle from that point, as a game does after a drink
          --show-ids <name>       print the shared number of each object of that name (to name one in --tally)
          --switches-load <file>  the doors and switches of a game's world, as "ss doors" wrote them (so the lists agree)
          --switch "<s>|<part of a name>|<0|1>;…"   with --switches-load: open (1) or close (0) those at their seconds;
                                  every one someone else flips is printed
          --say "<s>|<name>|<v,v>;…"   say those tallies, each at its second ("#<name>": the newest shared number of
                                  an object of that name then)
          --hear <prefix>         print every tally heard whose name starts with that ("tool/")
                                  (a test aid for what the game side hears: "npc/state/kuski/0" 1, "job/wood1/s0" 0,3…)
          --tally-add <name> <change> <at>   as a client: say that this game changed a tally by that much, at that
                                  second (a job's amount: "job/shit1/0" -2000 is a well pumped by 2)
          --poker <n> <parts> <shape>
                                  as a client: once the host's game plays the video poker, press its buttons n times;
                                  what it shows has to arrive. <parts> and <shape> (hex) are the ones the game logs
          --traffic <cars> <shape> <highway|dirt>
                                  as a client: say this player is on the highway or the dirt roads, and watch the
                                  host's traffic; its cars have to arrive and move. <cars> and <shape> (hex) are the
                                  ones the game logs
          --people <n> <shape>    as a client: watch the people who walk the roads in the host's game; they have to
                                  arrive and move. <n> and <shape> (hex) are the ones the game logs
          --people-show <n> <shape> <x> <y> <z> [<name> <at>]
                                  as a host: made-up walkers by that point, for a real game to show; with a name, that
                                  one is said to be run over at that second
          --npcs <n> <shape>      as a client: watch the rest of what moves by itself in the host's game (train,
                                  boats, moose…); something has to arrive and move. <n> and <shape> (hex) are the
                                  ones the game logs ("NPCs: N listed (shape …)")
          --npcs-show <n> <shape> <x> <y> <z> [<name> <at>]
                                  as a host: made-up NPCs going round points 30 m apart from that one, for a real game
                                  to show; with a name ("moose#0"), that one is said to be destroyed at that second
          --npcs-still <s>        with --npcs-show: they stand still for that many seconds first (a rider can get on)
          --traffic-show <cars> <shape> <x> <y> <z>
                                  as a host: a made-up traffic for a real game to show: three cars driving round
                                  that point
          --satsuma <n> [<seed>]  put a made-up car together with the other probes: n random steps here, five a second
                                  (parts on and off, bolts, wires, paint, outcomes, oil poured into it); the host hands
                                  the car's numbers to a client a third of the way and takes them back at two thirds.
                                  Every probe has to end with the same car and the same numbers (the "Satsuma" lines)
          --car [full]            the real Satsuma against a game, listed from data/satsuma/parts.json: as host with
                                  "full", every part on, every bolt tight and every wire made, for a joining game to
                                  build; otherwise it follows the host's car. Its game has to end as the session's car
          --car-steps <script>    with --car: steps its player makes, "<s>,<verb>,<entry>[,<n>[,<times>]]" separated by
                                  ';' (install, remove, tighten, loosen, tightall, wire), e.g.
                                  "4,install,DatabaseBody/Subframe;6,tightall,DatabaseBody/Subframe"
          --car-parts <file>      another catalogue than the repository's data/satsuma/parts.json
          --drive-only <name>     like --drive, but only a vehicle whose name starts with that ("SATSUMA"), left
                                  where it was taken when the bot gets out
          --drive-circle <m> <m/s>   the circle the bot drives (12 m) and its top speed (8 m/s)
          --drive-seat <x> <y> <z> <eyes>   where the bot sits at the wheel: its feet in the vehicle's coordinates and its
                                  eyes above them (the line "At the wheel here" of "ss car" in the game); without it,
                                  the "seat" of the vehicle's file in data/vehicles
          --watch-vehicles        print every vehicle another machine moves, as it moves
          --vehicle-steps <file>  write every state of a vehicle another machine moves, as it arrives, to that file
                                  (name, the sender's clock, x, y, z): to measure how evenly it moves
          --drive-lesson          the bot works the controls as a person: the wheel from side to side, each pedal in
                                  turn, the clutch down to change gear
          --car-save <file>       with --car: at the end, write the session's car to that file
          --car-load <file>       with --car, as host: the car is the one in that file (written by --car-save)
          --stall <at> <seconds>  stop running for a while, like a game on a loading screen
          --quiet                 only print the summary

        To talk to the real game, both ends must announce the same game build: use --game or --game-dir.
        """;

    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.Command.Kind == NetCommandKind.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var log = new Logger { MinimumLevel = options.Quiet ? LogLevel.Warning : LogLevel.Info };
        log.AddSink(new ConsoleSink());
        var manager = new NetworkManager(log) { ModVersion = BuildInfo.Version, GameVersion = options.GameVersion };
        manager.Conditions.LatencyMs = options.Simulate.LatencyMs;
        manager.Conditions.JitterMs = options.Simulate.JitterMs;
        manager.Conditions.LossPercent = options.Simulate.LossPercent;

        string? ending = null;
        manager.Stopped += (_, reason, text) => ending = reason + ". " + text;
        if (!options.Quiet)
        {
            Console.WriteLine("mod " + manager.ModVersion + ", game " + manager.GameVersion + ", protocol " + Protocol.Version);
        }

        try
        {
            Start(manager, options);
        }
        catch (SocketException exception)
        {
            Console.Error.WriteLine("Could not open the UDP socket: " + exception.Message);
            return 2;
        }

        bool stopRequested = false;
        Console.CancelKeyPress += (_, cancel) =>
        {
            // Leave politely instead of dying mid-session.
            cancel.Cancel = true;
            stopRequested = true;
        };

        var summary = new Summary();
        PlayerPose? botCentre = options.BotCentre;
        if (botCentre == null && options.Command.Kind == NetCommandKind.Loopback)
        {
            // Nobody else to stand in front of: both ends are this process.
            botCentre = new PlayerPose(0, 0, 0, 0, 0, BotPath.StandingEyes);
        }

        Bot? bot = options.Bot ? new Bot(botCentre) { Activity = options.Activity, ActivityDetail = options.ActivityDetail, Facing = options.BotFace, Eyes = options.BotEyes } : null;
        var world = new ProbeWorld(options.Carry);
        ProbeSwitches? switches = options.SwitchesFile == null ? null : new ProbeSwitches(options.SwitchesFile, options.SwitchScript);
        var driver = new ProbeDriver(options.Drive, options.DriveOnly, options.DriveRadius, options.DriveSpeed, options.DriveSeat, options.DriveLesson) { WatchVehicles = options.WatchVehicles, StepsFile = options.VehicleSteps };
        if (options.Van && manager.Vehicles != null && manager.Mode == NetworkMode.Host)
        {
            driver.AddVan(manager.Vehicles, botCentre ?? new PlayerPose(0, 0, 0, 0, 0, BotPath.StandingEyes));
            Console.WriteLine("Parked a van.");
        }
        if (options.Objects > 0 && manager.Objects != null && manager.Mode == NetworkMode.Host)
        {
            world.AddCrates(manager.Objects, botCentre ?? new PlayerPose(0, 0, 0, 0, 0, BotPath.StandingEyes), options.Objects);
            Console.WriteLine("Put " + options.Objects + " crate(s) on the floor.");
        }

        if (options.VehiclesLoad != null && manager.Vehicles != null && manager.Mode == NetworkMode.Host)
        {
            Console.WriteLine("Has the " + driver.Load(manager.Vehicles, options.VehiclesLoad) + " vehicle(s) of '" + options.VehiclesLoad + "'.");
        }

        if (options.ObjectsLoad != null && manager.Objects != null && manager.Mode == NetworkMode.Host)
        {
            Console.WriteLine("Has the " + world.Load(manager.Objects, options.ObjectsLoad) + " object(s) of '" + options.ObjectsLoad + "'.");
        }

        var songs = new ProbeSongs();
        if (manager.Songs != null)
        {
            manager.Songs.SetStore(songs);
            if (options.Songs > 0 && manager.Mode == NetworkMode.Host)
            {
                manager.Songs.Offer(songs.MakeUp(options.Songs, options.SongKilobytes));
                Console.WriteLine("Offering " + options.Songs + " made-up song(s) of " + options.SongKilobytes + " kB.");
            }
        }

        summary.ExpectSongs(manager.Mode == NetworkMode.Host ? 0 : options.Songs, songs);
        var savedWorld = new ProbeSongs();
        bool resaved = false;
        if (manager.SavedWorld != null)
        {
            manager.SavedWorld.SetStore(savedWorld);
            if (options.WorldFiles > 0 && manager.Mode == NetworkMode.Host)
            {
                manager.SavedWorld.Offer(savedWorld.MakeUpWorld(options.WorldFiles, options.WorldKilobytes, 0), 1_760_000_000);
                Console.WriteLine("Offering a made-up world of " + options.WorldFiles + " file(s) of " + options.WorldKilobytes + " kB.");
            }
        }

        summary.ExpectWorld(manager.Mode == NetworkMode.Host || options.WorldFiles == 0 ? 0u : (options.ResaveAt > 0 ? 2u : 1u));
        ProbeMoney? money = options.MoneyStart == null ? null : new ProbeMoney(options.MoneyStart.Value, options.MoneySpend, options.Seconds);
        summary.ExpectMoney(money);
        ProbeMade? made = options.Made > 0 ? new ProbeMade(options.Made, options.Seconds) : null;
        summary.ExpectMade(made);
        ProbeShop? shop = options.Shop > 0 ? new ProbeShop(options.Shop, options.Seconds) : null;
        summary.ExpectShop(shop);
        ProbePub? pub = options.Order > 0 || options.Lotto || options.PokerPresses > 0 || options.Phone || options.PhoneRing || options.Sleep || options.BusBits != 0 || options.BusDoorAt > 0 || options.PolicePlace >= 0 || options.TallyName != null || options.AddName != null || options.Hear != null || options.Say != null
            ? new ProbePub(options.Order, options.Lotto, options.PokerPresses, options.PokerParts, options.PokerShape, options.Phone, options.PhoneRing, options.Sleep, options.BusBits, options.BusPaidAt, options.BusDoorAt, options.PolicePlace, options.TallyName, options.TallyValue, options.TallyAt, options.AddName, options.AddValue, options.AddAt) { Hear = options.Hear, Say = options.Say, IdOf = world.NewestId }
            : null;
        ProbeTraffic? traffic = options.TrafficCars > 0 ? new ProbeTraffic(options.TrafficCars, options.TrafficShape, options.TrafficHighway) : null;
        ProbeSatsuma? satsuma = options.Satsuma > 0 ? new ProbeSatsuma(options.Satsuma, options.SatsumaSeed) : null;
        ProbeCar? realCar = options.Car ? new ProbeCar(options.CarParts, options.CarFull, options.CarSteps, options.CarLoad, options.CarSave) : null;
        TrafficCar[]? shownTraffic = null;
        TrafficCar[]? shownPeople = null;
        bool madeUpFall = false;
        ProbeTraffic? walkers = options.PeopleCount > 0 ? new ProbeTraffic(options.PeopleCount, options.PeopleShape, false, true) : null;
        ProbeTraffic? npcs = options.NpcCount > 0 ? new ProbeTraffic(options.NpcCount, options.NpcShape, false, false, true) : null;
        TrafficCar[]? shownNpcs = null;
        bool madeUpGone = false;
        var ridingSaid = new HashSet<byte>();

        var clock = Stopwatch.StartNew();
        double nextReport = 1;
        double nextFrame = 0;
        bool stalled = false;
        bool thrown = false;
        while (manager.IsActive)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (!stalled && options.StallSeconds > 0 && now >= options.StallAt)
            {
                stalled = true;
                Console.WriteLine("Not running for " + options.StallSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s, like a game on a loading screen.");
                Thread.Sleep(TimeSpan.FromSeconds(options.StallSeconds));
                now = clock.Elapsed.TotalSeconds;
            }

            if (bot != null)
            {
                bot.Move(manager, now);
            }
            else
            {
                manager.SetLocalPlayer(false, default);
            }

            if (options.ThrowFrom != null && !thrown && now >= options.ThrowAt && manager.Items != null)
            {
                thrown = true;
                float[] from = options.ThrowFrom;
                manager.Items.TellThrown(new ThrownItem
                {
                    Prefab = "BottleBeerFly",
                    Name = "empty bottle(Clone)",
                    Pose = BodyPose.At(from[0], from[1], from[2]),
                    Motion = new BodyMotion { VY = 2f, VZ = 1.5f },
                });
                Console.WriteLine("Threw an empty bottle from (" + from[0].ToString("0.0", CultureInfo.InvariantCulture) + ", " + from[1].ToString("0.0", CultureInfo.InvariantCulture) + ", "
                    + from[2].ToString("0.0", CultureInfo.InvariantCulture) + ").");
            }

            // At the wheel the bot is where the seat is, not where it would have walked; it still drinks, smokes or pees.
            driver.Step(manager, now, bot?.Pose != null);
            if (driver.Seat != null)
            {
                PlayerPose seat = driver.Seat.Value;
                if (bot?.Pose != null)
                {
                    seat.Activity = bot.Pose.Value.Activity;
                    seat.ActivityDetail = bot.Pose.Value.ActivityDetail;
                }

                manager.SetLocalPlayer(true, seat);
            }

            manager.Update(now);
            if (!manager.IsActive)
            {
                break;
            }

            // Watch the other players, and move the objects, the way a game does: once per frame, sixty frames a
            // second.
            if (now >= nextFrame)
            {
                nextFrame = now - nextFrame > 1 ? now + FrameSeconds : nextFrame + FrameSeconds;
                manager.Players!.Sample(now);
                world.Step(manager, now, FrameSeconds, bot?.Pose);
                if (options.ShowIds != null)
                {
                    world.ShowIds(options.ShowIds);
                }

                switches?.Step(manager, now);
                money?.Step(manager.Wallet, now);
                made?.Step(manager.Spawns, now);
                shop?.Step(manager.Tallies, now);
                pub?.Step(manager, now);
                traffic?.Step(manager, now);
                walkers?.Step(manager, now);
                npcs?.Step(manager, now);
                satsuma?.Step(manager, now);
                realCar?.Step(manager, now);
                if (options.NpcAt != null && manager.Mode == NetworkMode.Host)
                {
                    ProbeTraffic.Show(manager, now, options.NpcShowCount, options.NpcShowShape, options.NpcAt[0], options.NpcAt[1], options.NpcAt[2], options.GoneName, options.GoneAt, options.NpcStill, ref shownNpcs, ref madeUpGone);
                }

                // A player who rides one of the NPCs (the bus) says where they are on it.
                if (manager.Players != null)
                {
                    foreach (RemotePlayer remote in manager.Players.Remotes)
                    {
                        bool riding = remote.Visible && remote.Shown.Parent >= PlayerPose.NpcParentBase;
                        if (riding != ridingSaid.Contains(remote.Id))
                        {
                            if (riding)
                            {
                                ridingSaid.Add(remote.Id);
                                Console.WriteLine("Player " + remote.Id + " rides NPC " + (remote.Shown.Parent - PlayerPose.NpcParentBase) + ", at ("
                                    + remote.Shown.X.ToString("0.0", CultureInfo.InvariantCulture) + ", " + remote.Shown.Y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                                    + remote.Shown.Z.ToString("0.0", CultureInfo.InvariantCulture) + ") on it.");
                            }
                            else
                            {
                                ridingSaid.Remove(remote.Id);
                                Console.WriteLine("Player " + remote.Id + " is off the NPC.");
                            }
                        }
                    }
                }
                if (options.WalkAt != null && manager.Mode == NetworkMode.Host)
                {
                    ProbeTraffic.Walk(manager, now, options.WalkCount, options.WalkShape, options.WalkAt[0], options.WalkAt[1], options.WalkAt[2], options.FallName, options.FallAt, ref shownPeople, ref madeUpFall);
                }

                if (options.ShowAt != null && manager.Mode == NetworkMode.Host)
                {
                    ProbeTraffic.Drive(manager, now, options.ShowCars, options.ShowShape, options.ShowAt[0], options.ShowAt[1], options.ShowAt[2], ref shownTraffic);
                }
                if (!resaved && options.ResaveAt > 0 && now >= options.ResaveAt && manager.Mode == NetworkMode.Host && manager.SavedWorld != null && options.WorldFiles > 0)
                {
                    resaved = true;
                    manager.SavedWorld.Offer(savedWorld.MakeUpWorld(options.WorldFiles, options.WorldKilobytes, 1), 1_760_000_600);
                    Console.WriteLine("Saved the world again.");
                }
            }

            summary.Observe(manager, world);
            if (now >= nextReport)
            {
                nextReport = Math.Max(nextReport + 1, now);
                if (!options.Quiet)
                {
                    Console.WriteLine(Describe(manager));
                }
            }

            if (stopRequested || (options.Seconds > 0 && now >= options.Seconds))
            {
                Console.WriteLine(Describe(manager));
                manager.Stop();
                break;
            }

            Thread.Sleep(2);
        }

        Console.WriteLine("Ended: " + (ending ?? "unknown"));
        if (options.VehiclesSave != null)
        {
            Console.WriteLine("Wrote " + driver.Save(options.VehiclesSave) + " vehicle(s) of the host's to '" + options.VehiclesSave + "'.");
        }

        if (options.ObjectsSave != null)
        {
            Console.WriteLine("Wrote " + world.Save(options.ObjectsSave) + " object(s) of the host's to '" + options.ObjectsSave + "'.");
        }
        if (driver.Count > 0 || options.Drive)
        {
            Console.WriteLine(driver.Describe());
        }

        driver.CloseSteps();

        if (pub != null)
        {
            Console.Write(pub.Describe());
        }

        if (traffic != null)
        {
            Console.Write(traffic.Describe());
        }

        if (walkers != null)
        {
            Console.Write(walkers.Describe());
        }

        if (npcs != null)
        {
            Console.Write(npcs.Describe());
        }

        if (satsuma != null)
        {
            Console.Write(satsuma.Describe());
        }

        if (realCar != null)
        {
            Console.Write(realCar.Describe());
        }

        Console.WriteLine(summary.ToString());
        bool passed = summary.Passed && (pub == null || pub.Passed) && (traffic == null || traffic.Passed) && (walkers == null || walkers.Passed) && (npcs == null || npcs.Passed)
            && (satsuma == null || satsuma.Passed) && (realCar == null || realCar.Passed);
        if (summary.Passed && !passed)
        {
            Console.WriteLine("FAIL (" + (pub != null && !pub.Passed ? "the pub" : traffic != null && !traffic.Passed ? "the traffic" : walkers != null && !walkers.Passed ? "the people"
                : npcs != null && !npcs.Passed ? "the NPCs" : satsuma != null && !satsuma.Passed ? "the Satsuma" : "the real car") + ")");
        }

        return passed ? 0 : 1;
    }

    private static void Start(NetworkManager manager, Options options)
    {
        NetCommand command = options.Command;
        switch (command.Kind)
        {
            case NetCommandKind.Loopback:
                manager.StartLoopbackTest(options.Name);
                break;

            case NetCommandKind.HostUdp:
                var listener = new UdpTransport(command.Lan ? IPAddress.Any : IPAddress.Loopback, command.Port);
                manager.StartHost(listener, "udp", options.Name);
                Console.WriteLine("Hosting on UDP port " + listener.LocalPort + (command.Lan ? " (LAN)." : " (this machine only)."));
                break;

            case NetCommandKind.JoinUdp:
                IPAddress host = IPAddress.Parse(command.Address);
                var socket = new UdpTransport(IPAddress.IsLoopback(host) ? IPAddress.Loopback : IPAddress.Any, 0);
                manager.StartClient(socket, "udp", UdpTransport.PeerId(host, command.Port), options.Name);
                Console.WriteLine("Joining " + host + ":" + command.Port + "...");
                break;

            default:
                throw new InvalidOperationException("Unexpected command " + command.Kind + ".");
        }
    }

    private static string Describe(NetworkManager manager)
    {
        var text = new StringBuilder();
        NetworkStatus.Describe(manager, text);
        return text.ToString();
    }

    private sealed class Options
    {
        public NetCommand Command { get; private set; } = NetCommand.Parse([], DefaultPort);

        public NetCommand Simulate { get; private set; } = NetCommand.Parse(["sim", "off"], DefaultPort);

        public double Seconds { get; private set; }

        public string Name { get; private set; } = "probe";

        public string GameVersion { get; private set; } = "probe";

        public bool Quiet { get; private set; }

        public bool Bot { get; private set; }

        public bool Carry { get; private set; }

        public int Objects { get; private set; }

        /// <summary>As a client: the file the host's list of loose objects is written to at the end.</summary>
        public string? ObjectsSave { get; private set; }

        /// <summary>As a host: a file written by <see cref="ObjectsSave"/>, whose objects this "world" has.</summary>
        public string? ObjectsLoad { get; private set; }

        /// <summary>The same for the vehicles: written as a client, had as a host.</summary>
        public string? VehiclesSave { get; private set; }

        public string? VehiclesLoad { get; private set; }

        /// <summary>As a host: how many songs to make up and offer, and how big. As a client: how many have to arrive.</summary>
        public int Songs { get; private set; }

        public int SongKilobytes { get; private set; } = 300;

        /// <summary>As a host: how many files the made-up world has, and how big. As a client: that it has to arrive.</summary>
        public int WorldFiles { get; private set; }

        public int WorldKilobytes { get; private set; } = 500;

        /// <summary>As a host: the second at which it saves its world again, or 0.</summary>
        public double ResaveAt { get; private set; }

        /// <summary>The money this game starts with, or null for a probe that has none.</summary>
        public float? MoneyStart { get; private set; }

        public float MoneySpend { get; private set; }

        /// <summary>How many things this game makes, and hears of from the other end.</summary>
        public int Made { get; private set; }

        /// <summary>How many things this game takes off the shelf.</summary>
        public int Shop { get; private set; }

        /// <summary>What this guest orders at the bar (1 to 5), or 0.</summary>
        public int Order { get; private set; }

        public bool Lotto { get; private set; }

        public bool Phone { get; private set; }

        public bool PhoneRing { get; private set; }

        public bool Sleep { get; private set; }

        public int BusBits { get; private set; }

        public double BusPaidAt { get; private set; }

        public double BusDoorAt { get; private set; }

        public int PolicePlace { get; private set; } = -1;

        public string? TallyName { get; private set; }

        /// <summary>With --say: several tallies, each at its second.</summary>
        public string? Say { get; private set; }

        /// <summary>Tallies whose name starts with this are printed as they are heard.</summary>
        public string? Hear { get; private set; }

        /// <summary>With a bot: it stands still and drinks, smokes or pees ("drink", "smoke", "pee"), or all three in turn ("cycle").</summary>
        public string? Activity { get; private set; }

        public byte ActivityDetail { get; private set; }

        /// <summary>With --activity: where the bot faces, in degrees.</summary>
        public float BotFace { get; private set; }

        /// <summary>With --throw: when, and from where, an empty beer bottle is thrown as a game throws it after a drink.</summary>
        public double ThrowAt { get; private set; }

        public float[]? ThrowFrom { get; private set; }

        /// <summary>With --show-ids: the name of the objects whose shared numbers are printed.</summary>
        public string? ShowIds { get; private set; }

        /// <summary>With --switches-load: the list of doors and switches a game wrote with "ss doors".</summary>
        public string? SwitchesFile { get; private set; }

        /// <summary>With --switch: flips at their seconds.</summary>
        public string? SwitchScript { get; private set; }

        /// <summary>With --activity: how high its eyes are, in metres (standing if not said).</summary>
        public float BotEyes { get; private set; } = BotPath.StandingEyes;

        public int[]? TallyValue { get; private set; }

        public double TallyAt { get; private set; }

        public string? AddName { get; private set; }

        public int AddValue { get; private set; }

        public double AddAt { get; private set; }

        public int PokerPresses { get; private set; }

        public int PokerParts { get; private set; }

        public uint PokerShape { get; private set; }

        public int TrafficCars { get; private set; }

        public uint TrafficShape { get; private set; }

        public bool TrafficHighway { get; private set; }

        public int PeopleCount { get; private set; }

        public int NpcCount { get; private set; }

        public uint NpcShape { get; private set; }

        public int NpcShowCount { get; private set; }

        public uint NpcShowShape { get; private set; }

        public float[]? NpcAt { get; private set; }

        public string? GoneName { get; private set; }

        public double GoneAt { get; private set; }

        public double NpcStill { get; private set; }

        public uint PeopleShape { get; private set; }

        public int WalkCount { get; private set; }

        public uint WalkShape { get; private set; }

        public float[]? WalkAt { get; private set; }

        public string? FallName { get; private set; }

        public double FallAt { get; private set; }

        public int ShowCars { get; private set; }

        public uint ShowShape { get; private set; }

        public float[]? ShowAt { get; private set; }

        public bool Drive { get; private set; }

        public bool Van { get; private set; }

        /// <summary>How many steps of the made-up car this probe makes, or 0; and the seed of its choices.</summary>
        public int Satsuma { get; private set; }

        public int SatsumaSeed { get; private set; } = 1;

        /// <summary>Whether this probe works on the real Satsuma, whether its car is the whole car, its script, and the catalogue.</summary>
        public bool Car { get; private set; }

        public bool CarFull { get; private set; }

        public string? CarSteps { get; private set; }

        public string CarParts { get; private set; } = ProbeCar.DefaultParts;

        public string? CarSave { get; private set; }

        /// <summary>With --drive: only a vehicle whose name starts with this; the circle's radius and the top speed.</summary>
        public string? DriveOnly { get; private set; }

        public double DriveRadius { get; private set; } = 12.0;

        public double DriveSpeed { get; private set; } = 8.0;

        public float[]? DriveSeat { get; private set; }

        public bool DriveLesson { get; private set; }

        public bool WatchVehicles { get; private set; }

        public string? VehicleSteps { get; private set; }

        public string? CarLoad { get; private set; }

        /// <summary>Where the bot walks; null to walk in front of the first player it sees.</summary>
        public PlayerPose? BotCentre { get; private set; }

        public double StallAt { get; private set; }

        public double StallSeconds { get; private set; }

        public static Options Parse(string[] args)
        {
            int firstOption = Array.FindIndex(args, argument => argument.StartsWith("--", StringComparison.Ordinal));
            string[] commandArgs = firstOption < 0 ? args : args[..firstOption];
            if (commandArgs.Length == 0)
            {
                commandArgs = ["help"];
            }

            var options = new Options { Command = Checked(NetCommand.Parse(commandArgs, DefaultPort)) };
            for (int i = firstOption < 0 ? args.Length : firstOption; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--quiet":
                        options.Quiet = true;
                        break;
                    case "--bot":
                        options.Bot = true;
                        break;
                    case "--carry":
                        options.Bot = true;
                        options.Carry = true;
                        break;
                    case "--drive-circle":
                        options.DriveRadius = Number(args, ref i, "--drive-circle", true);
                        options.DriveSpeed = Number(args, ref i, "--drive-circle", true);
                        break;
                    case "--drive-seat":
                        options.DriveSeat = new[]
                        {
                            (float)Number(args, ref i, "--drive-seat", false), (float)Number(args, ref i, "--drive-seat", false),
                            (float)Number(args, ref i, "--drive-seat", false), (float)Number(args, ref i, "--drive-seat", true),
                        };
                        break;
                    case "--drive-lesson":
                        options.DriveLesson = true;
                        break;
                    case "--watch-vehicles":
                        options.WatchVehicles = true;
                        break;
                    case "--vehicle-steps":
                        options.VehicleSteps = Value(args, ref i);
                        break;
                    case "--drive-only":
                        options.Bot = true;
                        options.Drive = true;
                        options.DriveOnly = Value(args, ref i);
                        break;
                    case "--drive":
                        options.Bot = true;
                        options.Drive = true;
                        break;
                    case "--van":
                        options.Van = true;
                        break;
                    case "--objects":
                        options.Objects = (int)Math.Min(500, Number(args, ref i, "--objects", true));
                        break;
                    case "--objects-save":
                        options.ObjectsSave = Value(args, ref i);
                        break;
                    case "--objects-load":
                        options.ObjectsLoad = Value(args, ref i);
                        break;
                    case "--vehicles-save":
                        options.VehiclesSave = Value(args, ref i);
                        break;
                    case "--vehicles-load":
                        options.VehiclesLoad = Value(args, ref i);
                        break;
                    case "--songs":
                        options.Songs = (int)Math.Min(200, Number(args, ref i, "--songs", true));
                        options.SongKilobytes = (int)Math.Min(40000, Math.Max(1, Number(args, ref i, "--songs", true)));
                        break;
                    case "--world":
                        options.WorldFiles = (int)Math.Min(6, Math.Max(1, Number(args, ref i, "--world", true)));
                        options.WorldKilobytes = (int)Math.Min(90000, Math.Max(1, Number(args, ref i, "--world", true)));
                        break;
                    case "--resave":
                        options.ResaveAt = Number(args, ref i, "--resave", true);
                        break;
                    case "--money":
                        options.MoneyStart = (float)Number(args, ref i, "--money", true);
                        options.MoneySpend = (float)Number(args, ref i, "--money", true);
                        break;
                    case "--shop":
                        options.Shop = (int)Math.Min(100, Number(args, ref i, "--shop", true));
                        break;
                    case "--order":
                        options.Order = (int)Math.Min(5, Number(args, ref i, "--order", true));
                        break;
                    case "--lotto":
                        options.Lotto = true;
                        break;
                    case "--phone":
                        options.Phone = true;
                        break;
                    case "--phone-ring":
                        options.PhoneRing = true;
                        break;
                    case "--sleep":
                        options.Sleep = true;
                        break;
                    case "--tally-add":
                        options.AddName = Value(args, ref i);
                        options.AddValue = (int)Number(args, ref i, "--tally-add", false);
                        options.AddAt = Number(args, ref i, "--tally-add", false);
                        break;
                    case "--say":
                        options.Say = Value(args, ref i);
                        break;
                    case "--tally":
                        options.TallyName = Value(args, ref i);
                        options.TallyValue = Value(args, ref i).Split(',').Select(part => int.Parse(part, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
                        options.TallyAt = Number(args, ref i, "--tally", false);
                        break;
                    case "--activity":
                        options.Activity = Value(args, ref i);
                        if (i + 1 < args.Length && byte.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte detail))
                        {
                            options.ActivityDetail = detail;
                            i++;
                        }

                        break;
                    case "--bot-face":
                        options.BotFace = (float)Number(args, ref i, "--bot-face", false);
                        break;
                    case "--throw":
                        options.ThrowAt = Number(args, ref i, "--throw", true);
                        options.ThrowFrom = new[]
                        {
                            (float)Number(args, ref i, "--throw", false), (float)Number(args, ref i, "--throw", false), (float)Number(args, ref i, "--throw", false),
                        };
                        break;
                    case "--show-ids":
                        options.ShowIds = Value(args, ref i);
                        break;
                    case "--switches-load":
                        options.SwitchesFile = Value(args, ref i);
                        break;
                    case "--switch":
                        options.SwitchScript = Value(args, ref i);
                        break;
                    case "--bot-eyes":
                        options.BotEyes = (float)Number(args, ref i, "--bot-eyes", true);
                        break;
                    case "--hear":
                        options.Hear = Value(args, ref i);
                        break;
                    case "--police":
                        options.PolicePlace = (int)Number(args, ref i, "--police", false);
                        break;
                    case "--bus-door":
                        options.BusDoorAt = Number(args, ref i, "--bus-door", false);
                        break;
                    case "--bus-rider":
                        options.BusBits = (int)Number(args, ref i, "--bus-rider", true);
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            options.BusPaidAt = Number(args, ref i, "--bus-rider", false);
                        }

                        break;
                    case "--npcs-still":
                        options.NpcStill = Number(args, ref i, "--npcs-still", false);
                        break;
                    case "--npcs":
                        options.NpcCount = (int)Math.Min(TrafficWire.MaxCars, Number(args, ref i, "--npcs", true));
                        options.NpcShape = uint.Parse(Value(args, ref i), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        break;
                    case "--npcs-show":
                        options.NpcShowCount = (int)Math.Min(TrafficWire.MaxCars, Number(args, ref i, "--npcs-show", true));
                        options.NpcShowShape = uint.Parse(Value(args, ref i), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        options.NpcAt = new[]
                        {
                            (float)Number(args, ref i, "--npcs-show", false),
                            (float)Number(args, ref i, "--npcs-show", false),
                            (float)Number(args, ref i, "--npcs-show", false),
                        };
                        if (i + 2 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            options.GoneName = Value(args, ref i);
                            options.GoneAt = Number(args, ref i, "--npcs-show", true);
                        }

                        break;
                    case "--people":
                        options.PeopleCount = (int)Math.Min(TrafficWire.MaxCars, Number(args, ref i, "--people", true));
                        options.PeopleShape = uint.Parse(Value(args, ref i), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        break;
                    case "--people-show":
                        options.WalkCount = (int)Math.Min(TrafficWire.MaxCars, Number(args, ref i, "--people-show", true));
                        options.WalkShape = uint.Parse(Value(args, ref i), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        options.WalkAt = new[]
                        {
                            (float)Number(args, ref i, "--people-show", false),
                            (float)Number(args, ref i, "--people-show", false),
                            (float)Number(args, ref i, "--people-show", false),
                        };
                        if (i + 2 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            options.FallName = Value(args, ref i);
                            options.FallAt = Number(args, ref i, "--people-show", true);
                        }

                        break;
                    case "--traffic-show":
                        options.ShowCars = (int)Math.Min(TrafficWire.MaxCars, Number(args, ref i, "--traffic-show", true));
                        options.ShowShape = uint.Parse(Value(args, ref i), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        options.ShowAt = new[]
                        {
                            (float)Number(args, ref i, "--traffic-show", false),
                            (float)Number(args, ref i, "--traffic-show", false),
                            (float)Number(args, ref i, "--traffic-show", false),
                        };
                        break;
                    case "--traffic":
                        options.TrafficCars = (int)Math.Min(TrafficWire.MaxCars, Number(args, ref i, "--traffic", true));
                        options.TrafficShape = uint.Parse(Value(args, ref i), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        options.TrafficHighway = Value(args, ref i) == "highway";
                        break;
                    case "--poker":
                        options.PokerPresses = (int)Math.Min(50, Number(args, ref i, "--poker", true));
                        options.PokerParts = (int)Math.Min(PanelWire.MaxSlots, Number(args, ref i, "--poker", true));
                        options.PokerShape = uint.Parse(Value(args, ref i), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        break;
                    case "--made":
                        options.Made = (int)Math.Min(200, Number(args, ref i, "--made", true));
                        break;
                    case "--car":
                        options.Car = true;
                        if (i + 1 < args.Length && args[i + 1] == "full")
                        {
                            options.CarFull = true;
                            i++;
                        }

                        break;
                    case "--car-steps":
                        options.Car = true;
                        options.CarSteps = Value(args, ref i);
                        break;
                    case "--car-parts":
                        options.CarParts = Value(args, ref i);
                        break;
                    case "--car-save":
                        options.Car = true;
                        options.CarSave = Value(args, ref i);
                        break;
                    case "--car-load":
                        options.Car = true;
                        options.CarLoad = Value(args, ref i);
                        break;
                    case "--satsuma":
                        options.Satsuma = (int)Math.Min(5000, Number(args, ref i, "--satsuma", true));
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            options.SatsumaSeed = (int)Number(args, ref i, "--satsuma", false);
                        }

                        break;
                    case "--bot-at":
                        options.Bot = true;
                        options.BotCentre = new PlayerPose(
                            (float)Number(args, ref i, "--bot-at", false),
                            (float)Number(args, ref i, "--bot-at", false),
                            (float)Number(args, ref i, "--bot-at", false),
                            0,
                            0,
                            BotPath.StandingEyes);
                        break;
                    case "--stall":
                        options.StallAt = Number(args, ref i, "--stall", true);
                        options.StallSeconds = Number(args, ref i, "--stall", true);
                        break;
                    case "--seconds":
                        options.Seconds = Number(args, ref i, "--seconds", true);
                        break;
                    case "--name":
                        options.Name = Value(args, ref i);
                        break;
                    case "--game":
                        options.GameVersion = Value(args, ref i);
                        break;
                    case "--game-dir":
                        options.GameVersion = IdentifyGame(Value(args, ref i));
                        break;
                    case "--sim":
                        var simulate = new List<string> { "sim" };
                        while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            simulate.Add(args[++i]);
                        }

                        options.Simulate = Checked(NetCommand.Parse(simulate.ToArray(), DefaultPort));
                        break;
                    default:
                        throw new ArgumentException("Unknown option '" + args[i] + "'.");
                }
            }

            return options;
        }

        private static NetCommand Checked(NetCommand command)
        {
            if (command.Error != null)
            {
                throw new ArgumentException(command.Error);
            }

            switch (command.Kind)
            {
                case NetCommandKind.Help:
                case NetCommandKind.Loopback:
                case NetCommandKind.HostUdp:
                case NetCommandKind.Simulate:
                    return command;
                case NetCommandKind.JoinUdp:
                    return IPAddress.TryParse(command.Address, out _)
                        ? command
                        : throw new ArgumentException("'" + command.Address + "' is not an IPv4 address.");
                default:
                    throw new ArgumentException("That command needs the game: only loopback, udphost and udpjoin work here.");
            }
        }

        private static double Number(string[] args, ref int index, string option, bool positive)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException(option + " needs " + (positive ? "a positive number." : "a number."));
            }

            string text = args[++index];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || double.IsNaN(parsed)
                || double.IsInfinity(parsed) || (positive && !(parsed > 0)))
            {
                throw new ArgumentException(option + " needs " + (positive ? "a positive number" : "a number") + ", not '" + text + "'.");
            }

            return parsed;
        }

        private static string Value(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException(args[index] + " needs a value.");
            }

            return args[++index];
        }

        private static string IdentifyGame(string gameFolder)
        {
            string[] candidates =
            [
                Path.Combine(gameFolder, "mysummercar_Data", "Managed", GameBuild.MainAssemblyFileName),
                Path.Combine(gameFolder, GameBuild.MainAssemblyFileName),
            ];
            string? assembly = candidates.FirstOrDefault(File.Exists);
            if (assembly == null)
            {
                throw new ArgumentException("No " + GameBuild.MainAssemblyFileName + " under '" + gameFolder + "'.");
            }

            return GameBuild.Identify(GameBuild.ExpectedUnityVersion, File.ReadAllBytes(assembly));
        }
    }

    /// <summary>A player that walks <see cref="BotPath"/> by itself, so that a game has someone to look at.</summary>
    private sealed class Bot
    {
        private const double MetresAhead = 6.0;

        private PlayerPose? centre;
        private double startedAt;

        /// <summary>Where the bot is right now, once it has somewhere to walk.</summary>
        public PlayerPose? Pose { get; private set; }

        public Bot(PlayerPose? centre)
        {
            this.centre = centre;
            if (centre != null)
            {
                Console.WriteLine("The bot walks around " + Describe(centre.Value) + ".");
            }
            else
            {
                Console.WriteLine("The bot waits for another player to be in the game world, to walk in front of them.");
            }
        }

        /// <summary>"drink", "smoke", "pee" or "cycle": it stands still and does that; null to walk.</summary>
        public string? Activity { get; init; }

        public byte ActivityDetail { get; init; }

        public float Facing { get; init; }

        public float Eyes { get; init; } = BotPath.StandingEyes;

        public void Move(NetworkManager manager, double now)
        {
            if (centre == null)
            {
                foreach (RemotePlayer remote in manager.Players!.Remotes)
                {
                    if (remote.Visible)
                    {
                        centre = BotPath.CentreInFrontOf(remote.Shown, MetresAhead);
                        startedAt = now;
                        Console.WriteLine("The bot walks around " + Describe(centre.Value) + ", " + MetresAhead.ToString("0", CultureInfo.InvariantCulture)
                            + " m in front of #" + remote.Id + " " + remote.Name + ".");
                        break;
                    }
                }
            }

            if (centre == null)
            {
                manager.SetLocalPlayer(false, default);
            }
            else if (Activity != null)
            {
                PlayerPose standing = centre.Value;
                standing.Yaw = Facing;
                standing.Pitch = 0f;
                standing.EyeHeight = Eyes;
                Act(ref standing, now - startedAt);
                Pose = standing;
                manager.SetLocalPlayer(true, standing);
            }
            else
            {
                Pose = BotPath.Evaluate(now - startedAt, centre.Value);
                manager.SetLocalPlayer(true, Pose.Value);
            }
        }

        // A 36-second round: drinks (each round another drink), smokes two puffs, pees; or only one of them, on and on.
        private void Act(ref PlayerPose pose, double elapsed)
        {
            int round = (int)(elapsed / 36.0);
            double t = elapsed % 36.0;
            string what = Activity!;
            if (what == "cycle")
            {
                what = t >= 2 && t < 7 ? "drink" : t >= 9 && t < 21 ? "smoke" : t >= 23 && t < 31 ? "pee" : "none";
                t = what == "smoke" ? t - 9 : t;
            }
            else
            {
                t %= 12.0;
            }

            switch (what)
            {
                case "drink":
                    pose.Activity = PlayerActivity.Drinking;
                    pose.ActivityDetail = ActivityDetail != 0 ? ActivityDetail : (byte)((round % 10) + 1);
                    break;
                case "smoke":
                    pose.Activity = PlayerActivity.Smoking;
                    pose.ActivityDetail = (t >= 3 && t < 4.5) || (t >= 8 && t < 9.5) ? PlayerPose.SmokeDrawing
                        : (t >= 4.5 && t < 6.5) || (t >= 9.5 && t < 11.5) ? PlayerPose.SmokeBreathing : PlayerPose.SmokeHolding;
                    break;
                case "pee":
                    pose.Activity = PlayerActivity.Peeing;
                    break;
            }
        }

        private static string Describe(PlayerPose pose)
        {
            return "(" + pose.X.ToString("0.0", CultureInfo.InvariantCulture) + ", " + pose.Y.ToString("0.0", CultureInfo.InvariantCulture)
                + ", " + pose.Z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>What the session achieved, kept from the last moment it was alive.</summary>
    private sealed class Summary
    {
        // A player watched for less than this has not been watched enough to judge.
        private const double JudgeAfterSeconds = 5.0;
        private const double MostStarved = 0.05;

        private readonly SortedDictionary<byte, Watched> watched = new();
        private string objects = string.Empty;
        private string songs = string.Empty;
        private long songsMalformed;
        private int songsExpected;
        private ProbeSongs? songStore;
        private uint worldExpected;
        private bool worldSettled;
        private string worldLine = string.Empty;
        private ProbeMoney? moneyGame;
        private ProbeMade? madeGame;
        private ProbeShop? shopGame;
        private string shopLine = string.Empty;
        private bool shopSettled;
        private string madeLine = string.Empty;
        private bool madeSettled;
        private string money = string.Empty;
        private bool moneySettled;
        private long objectMalformed;
        private bool connected;
        private int mostPlayers;
        private long reliableSent;
        private long reliableReceived;
        private long reliableErrors;
        private long unreliableSent;
        private long unreliableReceived;
        private long malformed;

        public bool Passed => connected && reliableErrors == 0 && objectMalformed == 0 && watched.Values.All(player => player.Smooth)
            && (songStore == null || (songStore.Arrived >= songsExpected && songStore.Wrong == 0 && songsMalformed == 0))
            && (moneyGame == null || moneySettled)
            && (madeGame == null || madeSettled)
            && (shopGame == null || shopSettled)
            && (worldExpected == 0 || worldSettled);

        /// <summary>That both ends have to end with everything both took on the counter.</summary>
        public void ExpectShop(ProbeShop? game)
        {
            shopGame = game;
        }

        /// <summary>That this game's things have to be told, and as many of the other end's heard, by the end.</summary>
        public void ExpectMade(ProbeMade? game)
        {
            madeGame = game;
        }

        /// <summary>That this game's money has to be shared, and settled, by the end.</summary>
        public void ExpectMoney(ProbeMoney? game)
        {
            moneyGame = game;
        }

        /// <summary>As a client: the host's world has to be here, whole, by the end, and be the one it offered that many-th.</summary>
        public void ExpectWorld(uint serial)
        {
            worldExpected = serial;
        }

        /// <summary>As a client: that many of the host's songs have to be here, whole, by the end.</summary>
        public void ExpectSongs(int count, ProbeSongs store)
        {
            songsExpected = count;
            songStore = store;
        }

        public void Observe(NetworkManager manager, ProbeWorld world)
        {
            ObjectReplication? shared = manager.Objects;
            if (shared != null && (shared.Shared > 0 || shared.MissingHere > 0 || world.Count > 0))
            {
                objectMalformed = shared.Malformed + (manager.Switches?.Malformed ?? 0);
                objects = "Objects: " + shared.Shared + " shared, " + shared.MissingHere + " of the host's missing here, " + shared.OnlyHere
                    + " only here, " + shared.StatesSent + " state(s) sent, " + shared.StatesReceived + " received, " + objectMalformed
                    + " malformed. " + world.Describe();
            }

            if (manager.Songs != null)
            {
                songs = manager.Songs.Describe() ?? string.Empty;
                songsMalformed = manager.Songs.Malformed;
            }

            WorldShare? savedWorld = manager.SavedWorld;
            if (savedWorld != null)
            {
                worldLine = savedWorld.Describe() ?? string.Empty;
                WorldManifest? heard = savedWorld.Manifest;
                worldSettled = worldExpected > 0 && heard != null && heard.Serial == worldExpected && savedWorld.IsReady
                    && savedWorld.Malformed == 0 && savedWorld.Files.Malformed == 0;
                if (heard != null)
                {
                    worldLine += " (offer " + heard.Serial + ", id " + SharedFile.Name(heard.Id) + ")";
                }
            }

            WalletReplication? wallet = manager.Wallet;
            if (moneyGame != null && wallet != null)
            {
                // Settled: shared, nothing of this game's still on its way, and the game has what the wallet says.
                moneySettled = wallet.Shared && wallet.Waiting == 0 && wallet.Malformed == 0 && Math.Abs(moneyGame.Money - wallet.Balance) < 0.01f;
                money = "Money here " + WalletReplication.Text(moneyGame.Money) + " (" + wallet.Describe() + "); this game spent "
                    + WalletReplication.Text(moneyGame.Spend) + " " + moneyGame.Spent + " time(s), " + wallet.Malformed + " malformed";
            }

            TallyReplication? tallies = manager.Tallies;
            if (shopGame != null && tallies != null)
            {
                shopSettled = shopGame.Took == shopGame.Count && shopGame.Counter == 2 * shopGame.Count && shopGame.Shelf == 2 * shopGame.Count && tallies.Malformed == 0;
                shopLine = "Shop here: took " + shopGame.Took + " of " + shopGame.Count + ", " + shopGame.Counter + " on the counter and " + shopGame.Shelf + " off the shelf, "
                    + tallies.Malformed + " malformed";
            }

            SpawnReplication? spawns = manager.Spawns;
            if (madeGame != null && spawns != null)
            {
                madeSettled = madeGame.Told == madeGame.Count && madeGame.Heard.Count == madeGame.Count && madeGame.InOrder && spawns.Malformed == 0;
                madeLine = "Made here " + madeGame.Told + " of " + madeGame.Count + ", heard " + madeGame.Heard.Count + " of the other's" + (madeGame.InOrder ? " in order" : " OUT OF ORDER")
                    + ", " + spawns.Malformed + " malformed";
            }

            NetSession session = manager.Session!;
            NetSession? other = manager.LoopbackHostSession;
            connected |= session.State == SessionState.Connected;
            mostPlayers = Math.Max(mostPlayers, session.Players.Count);
            reliableSent = session.Traffic.ReliableSent + (other?.Traffic.ReliableSent ?? 0);
            reliableReceived = session.Traffic.ReliableReceived + (other?.Traffic.ReliableReceived ?? 0);
            reliableErrors = session.Traffic.ReliableErrors + (other?.Traffic.ReliableErrors ?? 0);
            unreliableSent = session.Traffic.UnreliableSent + (other?.Traffic.UnreliableSent ?? 0);
            unreliableReceived = session.Traffic.UnreliableReceived + (other?.Traffic.UnreliableReceived ?? 0);
            malformed = session.MalformedDatagrams + (other?.MalformedDatagrams ?? 0) + manager.Players!.Malformed;
            foreach (RemotePlayer remote in manager.Players.Remotes)
            {
                if (remote.StatesReceived > 0)
                {
                    PoseBuffer buffer = remote.Buffer;
                    watched[remote.Id] = new Watched(
                        "#" + remote.Id + " " + remote.Name + ": " + remote.StatesReceived + " state(s), shown for "
                        + buffer.PlayedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s, " + PlayerReplication.DescribeQuality(remote)
                        + ", " + PlayerReplication.DescribeDelivery(remote),
                        buffer.Snaps == 0 && (buffer.PlayedSeconds < JudgeAfterSeconds || buffer.StarvedSeconds <= MostStarved * buffer.PlayedSeconds));
                }
            }
        }

        // The verdict stays on the last line: scripts and people look for it there.
        public override string ToString()
        {
            var text = new StringBuilder();
            if (objects.Length > 0)
            {
                text.Append(objects).Append('\n');
            }

            if (songs.Length > 0 || songsExpected > 0)
            {
                text.Append(songs.Length > 0 ? songs : "Songs: none heard of").Append(songsExpected > 0 ? " (" + songsExpected + " expected, " + (songStore?.Arrived ?? 0) + " arrived whole, "
                    + (songStore?.Wrong ?? 0) + " wrong)" : string.Empty).Append('\n');
            }

            if (worldLine.Length > 0 || worldExpected > 0)
            {
                text.Append(worldLine.Length > 0 ? worldLine : "World: never heard of").Append(worldExpected > 0 && !worldSettled ? "  <-- not settled" : string.Empty).Append('\n');
            }

            if (shopGame != null)
            {
                text.Append(shopLine.Length > 0 ? shopLine : "Shop: never shared").Append(shopSettled ? string.Empty : "  <-- not settled").Append('\n');
            }

            if (madeGame != null)
            {
                text.Append(madeLine.Length > 0 ? madeLine : "Made: never shared").Append(madeSettled ? string.Empty : "  <-- not settled").Append('\n');
            }

            if (moneyGame != null)
            {
                text.Append(money.Length > 0 ? money : "Money: never shared").Append(moneySettled ? string.Empty : "  <-- not settled").Append('\n');
            }

            foreach (Watched player in watched.Values)
            {
                text.Append("Saw ").Append(player.Description).Append(player.Smooth ? string.Empty : "  <-- not smooth").Append('\n');
            }

            return text.Append(Passed ? "PASS" : "FAIL").Append(": connected ").Append(connected ? "yes" : "no").Append(", most players ").Append(mostPlayers)
                .Append(", reliable ").Append(reliableSent).Append(" sent / ").Append(reliableReceived).Append(" received / ").Append(reliableErrors).Append(" errors")
                .Append(", unreliable ").Append(unreliableSent).Append(" sent / ").Append(unreliableReceived).Append(" received, malformed ").Append(malformed)
                .ToString();
        }

        private sealed record Watched(string Description, bool Smooth);
    }

    /// <summary>A game's money, which the probe spends now and then, the way a player shops.</summary>
    private sealed class ProbeMoney
    {
        private const double FirstSpend = 3.0;
        private const double SpendEvery = 2.0;

        // Nothing is spent in the last seconds, so that both ends can settle before they compare.
        private const double QuietSeconds = 4.0;

        private readonly double until;
        private double nextSpend = FirstSpend;

        public ProbeMoney(float start, float spend, double seconds)
        {
            Money = start;
            Spend = spend;
            until = seconds > 0 ? seconds - QuietSeconds : double.MaxValue;
        }

        public float Money { get; private set; }

        public float Spend { get; }

        public int Spent { get; private set; }

        public void Step(WalletReplication? wallet, double now)
        {
            if (wallet == null)
            {
                return;
            }

            if (wallet.Shared && now >= nextSpend && now < until)
            {
                Money -= Spend;
                Spent++;
                nextSpend = now + SpendEvery;
            }

            Money = wallet.Sync(Money);
        }
    }

    /// <summary>A game that makes things now and then, the way a till or a bag does, and hears of the others'.</summary>
    private sealed class ProbeMade
    {
        private const double FirstMade = 3.0;
        private const double MadeEvery = 0.5;
        private const double QuietSeconds = 4.0;

        private readonly double until;
        private double next = FirstMade;
        private SpawnReplication? hooked;

        public ProbeMade(int count, double seconds)
        {
            Count = count;
            until = seconds > 0 ? seconds - QuietSeconds : double.MaxValue;
        }

        public int Count { get; }

        public int Told { get; private set; }

        public List<int> Heard { get; } = new();

        public bool InOrder { get; private set; } = true;

        public void Step(SpawnReplication? spawns, double now)
        {
            if (spawns == null)
            {
                return;
            }

            if (spawns != hooked)
            {
                hooked = spawns;
                spawns.Wanted += made =>
                {
                    InOrder &= made.Number == Heard.Count + 1 && made.Spawner == "Probe :: Crate" && made.Pose.X == made.Number;
                    Heard.Add(made.Number);
                };
            }

            if (Told < Count && now >= next && now < until)
            {
                next = now + MadeEvery;
                Told++;
                spawns.Report(new SpawnEvent { Spawner = "Probe :: Crate", Number = Told, Pose = BodyPose.At(Told, 1f, 2f) });
            }
        }
    }

    /// <summary>A shop with one product, whose shelf this game takes from now and then, the way a player does.</summary>
    private sealed class ProbeShop
    {
        private const string Product = "store/Probe";
        private const double FirstTake = 3.0;
        private const double TakeEvery = 0.5;
        private const double QuietSeconds = 4.0;

        private readonly double until;
        private double next = FirstTake;
        private TallyReplication? hooked;

        public ProbeShop(int count, double seconds)
        {
            Count = count;
            until = seconds > 0 ? seconds - QuietSeconds : double.MaxValue;
        }

        public int Count { get; }

        public int Took { get; private set; }

        public int Counter { get; private set; }

        public int Shelf { get; private set; }

        public void Step(TallyReplication? tallies, double now)
        {
            if (tallies == null)
            {
                return;
            }

            if (tallies != hooked)
            {
                hooked = tallies;
                tallies.StateWanted += (name, values) =>
                {
                    Counter = values[0];
                    Shelf = values[1];
                };
                tallies.ChangeWanted += (name, change, player) =>
                {
                    int[] before = { Counter, Shelf };
                    Counter += change[0];
                    Shelf += change[1];
                    tallies.Tell(name, before, new[] { Counter, Shelf });
                };
            }

            if (Took < Count && now >= next && now < until)
            {
                next = now + TakeEvery;
                Took++;
                int[] before = { Counter, Shelf };
                Counter++;
                Shelf++;
                tallies.Tell(Product, before, new[] { Counter, Shelf });
            }
        }
    }

    private sealed class ConsoleSink : ILogSink
    {
        public void Write(LogEntry entry)
        {
            (entry.Level >= LogLevel.Warning ? Console.Error : Console.Out).WriteLine(LogFormatter.Format(entry));
        }
    }
}
