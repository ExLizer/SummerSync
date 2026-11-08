using System.Text;
using MSCLoader;
using SummerSync.Core;
using SummerSync.Core.Net;

namespace SummerSync.Unity.Net
{
    /// <summary>
    /// The "ss" console command: the network test from the keyboard. It keeps no state of its own, so the
    /// instance registered by one mod instance keeps working after MSCLoader replaces that instance.
    /// </summary>
    internal sealed class NetworkCommand : ConsoleCommand
    {
        private const string Prefix = "[" + BuildInfo.ProductName + "] ";

        public override string Name => "ss";

        private const string BringUsage = "ss bring <van|gifu|ruscko|ferndale|kekmet|jonnez|combine>   Test aid: gives you that vehicle's key and, if this game simulates it, parks it in front of you. 'ss van' is 'ss bring van'";

        private const string WorldUsage = "ss time <0-24>   ss weather <dry|rain|thunder>   Test aids, on the host: move the time to that hour's two-hour block; change what the clouds bring and put one over you";

        private const string FighterUsage = "ss fighter [punch [pub|dance] [part]]   How the men who pick a fight are shared here; with punch, a test aid: your fist hits him (part 0 the head)";

        private const string MuffleUsage = "ss muffle [<hertz>|off]   How muffled a vehicle's radio sounds from outside with its doors shut: the highest pitch that gets through (800 to begin with). Without a number, says what it is set to";

        private const string TravelUsage = "ss tp store   Test aid: takes you, on foot, to the front of Teimo's shop ('ss tp tienda' too)";

        private const string WoodUsage = "ss wood [n]   Test aid: n pieces of firewood (4 if not said, at most 20) in front of you, as if just chopped ('ss lena' too)";

        private const string LookUsage = "ss look [n|name|capsule]   The character the others see you as: without more, opens the window (F7 too); a number from 1, a name, or 'capsule'";

        private const string CarUsage = "ss car   How this game has the vehicle you drive, ride or stand next to: what holds it, its wheels, what is inside it. Also written to the log";

        private const string DoorsUsage = "ss doors   Test aid: writes the shared list of doors and switches, with their states, to switches.txt in the mod's settings folder";

        private const string LobbyUsage = "ss lobby [<feet> <height> <width> <middle>]   Test aid: where the players show in front of the main menu's car in a lobby, as parts of the screen: the height of their feet, how tall, how wide the row and where its middle is";

        private const string WorldsUsage = "ss worlds [list|open [load|host]|load <n>|host <n|new>|play|copies <n>|restore <n> <copy>|delete <n>|photo [around up]]   The saved worlds of the main menu's LOAD SAVE and HOST: n is a card's number from 'ss worlds' (1 is the first); 'photo', in a world, takes the Satsuma's photo now";

        private const string UpdateUsage = "ss update [install]   Whether the main menu offers a newer build of the mod; 'install' downloads and installs it as the menu's button does (used when the game starts again)";

        private const string OrderUsage = "ss order [<box>, <box>…|computer|now]   The mail orders: how they stand; with names of boxes ('fiberglass hood, fender flares'), test aid: this game posts that order as the post box does; 'computer' posts the computer's; 'now', on the host, has the order on its way at the post office in a moment";

        private const string SatsumaUsage = "ss satsuma [build|<entry>]   The Satsuma's assembly: this game's car against the session's, and what is being done to it; an entry ('DatabaseBody/Door_Left') bolt by bolt. 'build' (test aid) puts it together here the game's way";

        private const string ChatUsage = "ss chat [<text>]   The text chat (Y in the game, unless changed in the mod's keys): how it stands; with a text, says it as if typed";

        // What only helps testing, and changes the world or this game's connection: brings vehicles, moves the clock, the
        // weather or the player, makes wood, sits the player anywhere, pushes or knocks a vehicle, builds the car, writes
        // the door list, moves the menu's figures, degrades the network.
        private static bool IsTestAid(string[] args)
        {
            if (args.Length == 0)
            {
                return false;
            }

            string verb = args[0].ToLowerInvariant();
            string what = args.Length > 1 ? args[1].ToLowerInvariant() : string.Empty;
            switch (verb)
            {
                case "van":
                case "bring":
                case "time":
                case "weather":
                case "tp":
                case "wood":
                case "lena":
                case "le\u00f1a":
                case "cruise":
                case "drive":
                case "save":
                case "crash":
                case "doors":
                case "lobby":
                case "sim":
                    return true;
                case "order":
                    return what.Length > 0;
                case "seat":
                case "asiento":
                    return what.Length > 0 && what != "off" && what != "up" && what != "belt";
                case "satsuma":
                    return what == "build";
                case "fighter":
                    return what == "punch";
                default:
                    return false;
            }
        }

        public override string Help => "SummerSync network test: ss status, ss loopback, ss host... 'ss help' lists everything.";

        public override void Run(string[] args)
        {
            // The public edition: the test aids are the host's (or a game alone's), not a guest's.
            if (!Edition.Developer && IsTestAid(args) && ModRuntime.Network.Manager.Mode == NetworkMode.Client)
            {
                ModConsole.Error(Prefix + "Only the host can use 'ss " + args[0] + "'.");
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "van", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Summon("van"));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "bring", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(args.Length > 1 ? Prefix + RuntimeDriver.Summon(args[1]) : BringUsage);
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "time", System.StringComparison.OrdinalIgnoreCase))
            {
                int hour;
                ModConsole.Print(
                    args.Length > 1 && int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out hour)
                        ? Prefix + RuntimeDriver.TestHour(hour)
                        : WorldUsage);
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "muffle", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Muffle(args.Length > 1 ? args[1] : null));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "weather", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(args.Length > 1 ? Prefix + RuntimeDriver.TestWeather(args[1]) : WorldUsage);
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "tp", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(args.Length > 1 ? Prefix + RuntimeDriver.Travel(args[1]) : TravelUsage);
                return;
            }

            if (args.Length > 0 && (string.Equals(args[0], "wood", System.StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "lena", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "le\u00f1a", System.StringComparison.OrdinalIgnoreCase)))
            {
                ModConsole.Print(Prefix + RuntimeDriver.SpawnFirewood(args.Length > 1 ? args[1] : null));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "look", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Look(args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null));
                return;
            }

            if (args.Length > 0 && (string.Equals(args[0], "seat", System.StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "asiento", System.StringComparison.OrdinalIgnoreCase)))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Seat(args.Length > 1 ? args[1] : null));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "cruise", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Cruise(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "fighter", System.StringComparison.OrdinalIgnoreCase))
            {
                string what = args.Length > 1 ? args[1].ToLowerInvariant() : null;
                ModConsole.Print(Prefix + RuntimeDriver.Fighter(what, args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "save", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.SaveWorld());
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "drive", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Drive(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "order", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + World.OrderSync.Aid(args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null));
                return;
            }

            if (args.Length > 0 && (string.Equals(args[0], "dents", System.StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "abolladuras", System.StringComparison.OrdinalIgnoreCase)))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Dents());
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "crash", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Crash(args.Length > 1 ? args[1] : null));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "car", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.CarReport());
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "doors", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.DoorsReport());
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "lobby", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + Players.MenuLobby.Tune(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null, args.Length > 4 ? args[4] : null));
                return;
            }

            if (args.Length > 0 && (string.Equals(args[0], "worlds", System.StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "partidas", System.StringComparison.OrdinalIgnoreCase)))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Worlds(args));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "update", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + ModUpdater.Report(args.Length > 1 ? args[1] : null));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "chat", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.Chat(args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null));
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "satsuma", System.StringComparison.OrdinalIgnoreCase))
            {
                ModConsole.Print(Prefix + RuntimeDriver.SatsumaReport(args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null));
                return;
            }

            NetCommand command = NetCommand.Parse(args, NetworkController.DefaultUdpPort);
            if (command.Error != null)
            {
                ModConsole.Error(Prefix + command.Error);
                ModConsole.Print(NetCommand.Usage);
                return;
            }

            NetworkController network = ModRuntime.Network;
            switch (command.Kind)
            {
                case NetCommandKind.Help:
                    ModConsole.Print(NetCommand.Usage);
                    ModConsole.Print(BringUsage);
                    ModConsole.Print(CarUsage);
                    ModConsole.Print(SatsumaUsage);
                    ModConsole.Print(OrderUsage);
                    ModConsole.Print(DoorsUsage);
                    ModConsole.Print(WorldUsage);
                    ModConsole.Print(MuffleUsage);
                    ModConsole.Print(FighterUsage);
                    ModConsole.Print(TravelUsage);
                    ModConsole.Print(WoodUsage);
                    ModConsole.Print(LookUsage);
                    ModConsole.Print(ChatUsage);
                    ModConsole.Print(LobbyUsage);
                    ModConsole.Print(WorldsUsage);
                    ModConsole.Print(UpdateUsage);
                    return;
                case NetCommandKind.Loopback:
                    network.StartLoopbackTest();
                    break;
                case NetCommandKind.HostSteam:
                    network.HostSteam();
                    break;
                case NetCommandKind.Invite:
                    network.InviteFriend();
                    break;
                case NetCommandKind.HostUdp:
                    network.HostUdp(command.Port, command.Lan);
                    break;
                case NetCommandKind.JoinUdp:
                    network.JoinUdp(command.Address, command.Port);
                    break;
                case NetCommandKind.Leave:
                    network.Leave();
                    break;
                case NetCommandKind.Simulate:
                    ModRuntime.SetSimulator(command.LatencyMs, command.JitterMs, command.LossPercent);
                    break;
            }

            var status = new StringBuilder();
            network.AppendStatus(status);
            ModConsole.Print(status.ToString());
        }
    }
}
