using System;
using System.Globalization;

namespace SummerSync.Core.Net
{
    public enum NetCommandKind
    {
        Status,
        Help,
        Loopback,
        HostSteam,
        Invite,
        HostUdp,
        JoinUdp,
        Leave,
        Simulate,
    }

    /// <summary>
    /// The arguments of the "ss" console command, parsed. Parsing lives here, away from the game, so that a
    /// typo is answered with a message instead of an exception and the whole thing can be tested.
    /// </summary>
    public sealed class NetCommand
    {
        public const string DefaultAddress = "127.0.0.1";

        public const string Usage =
            "ss status | loopback | host | invite | leave\n"
            + "ss udphost [port] [lan]     host on UDP; 'lan' also accepts other machines\n"
            + "ss udpjoin [address] [port] join a UDP host; default is this machine\n"
            + "ss sim <latency ms> [jitter ms] [loss %] | ss sim off";

        private NetCommand(NetCommandKind kind)
        {
            Kind = kind;
            Address = DefaultAddress;
        }

        public NetCommandKind Kind { get; }

        /// <summary>Null when the command was understood; otherwise what is wrong with it.</summary>
        public string Error { get; private set; }

        public int Port { get; private set; }

        public string Address { get; private set; }

        /// <summary>For <see cref="NetCommandKind.HostUdp"/>: listen on every network interface, not only this machine.</summary>
        public bool Lan { get; private set; }

        public float LatencyMs { get; private set; }

        public float JitterMs { get; private set; }

        public float LossPercent { get; private set; }

        public static NetCommand Parse(string[] args, int defaultPort)
        {
            if (args == null || args.Length == 0)
            {
                return new NetCommand(NetCommandKind.Status);
            }

            switch (args[0].ToLowerInvariant())
            {
                case "status":
                    return new NetCommand(NetCommandKind.Status);
                case "help":
                case "?":
                    return new NetCommand(NetCommandKind.Help);
                case "loopback":
                    return new NetCommand(NetCommandKind.Loopback);
                case "host":
                    return new NetCommand(NetCommandKind.HostSteam);
                case "invite":
                    return new NetCommand(NetCommandKind.Invite);
                case "leave":
                case "disconnect":
                    return new NetCommand(NetCommandKind.Leave);
                case "udphost":
                    return ParseHostUdp(args, defaultPort);
                case "udpjoin":
                    return ParseJoinUdp(args, defaultPort);
                case "sim":
                    return ParseSimulate(args);
                default:
                    return Fail(NetCommandKind.Help, "Unknown command '" + args[0] + "'.");
            }
        }

        private static NetCommand ParseHostUdp(string[] args, int defaultPort)
        {
            var command = new NetCommand(NetCommandKind.HostUdp) { Port = defaultPort };
            for (int i = 1; i < args.Length; i++)
            {
                int port;
                if (string.Equals(args[i], "lan", StringComparison.OrdinalIgnoreCase))
                {
                    command.Lan = true;
                }
                else if (TryParsePort(args[i], out port))
                {
                    command.Port = port;
                }
                else
                {
                    return Fail(NetCommandKind.HostUdp, "'" + args[i] + "' is neither a port (1 to 65535) nor 'lan'.");
                }
            }

            return command;
        }

        private static NetCommand ParseJoinUdp(string[] args, int defaultPort)
        {
            var command = new NetCommand(NetCommandKind.JoinUdp) { Port = defaultPort };
            if (args.Length > 3)
            {
                return Fail(NetCommandKind.JoinUdp, "Too many arguments: expected an address and a port at most.");
            }

            for (int i = 1; i < args.Length; i++)
            {
                int port;
                if (args[i].IndexOf('.') >= 0)
                {
                    command.Address = args[i];
                }
                else if (TryParsePort(args[i], out port))
                {
                    command.Port = port;
                }
                else
                {
                    return Fail(NetCommandKind.JoinUdp, "'" + args[i] + "' is neither an IPv4 address nor a port (1 to 65535).");
                }
            }

            return command;
        }

        private static NetCommand ParseSimulate(string[] args)
        {
            var command = new NetCommand(NetCommandKind.Simulate);
            if (args.Length == 2 && string.Equals(args[1], "off", StringComparison.OrdinalIgnoreCase))
            {
                return command;
            }

            if (args.Length < 2 || args.Length > 4)
            {
                return Fail(NetCommandKind.Simulate, "Expected: ss sim <latency ms> [jitter ms] [loss %], or ss sim off.");
            }

            var values = new float[3];
            for (int i = 1; i < args.Length; i++)
            {
                if (!float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i - 1]) || !(values[i - 1] >= 0f))
                {
                    return Fail(NetCommandKind.Simulate, "'" + args[i] + "' is not a number of zero or more.");
                }
            }

            command.LatencyMs = values[0];
            command.JitterMs = values[1];
            command.LossPercent = Math.Min(values[2], 100f);
            return command;
        }

        private static bool TryParsePort(string text, out int port)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port >= 1 && port <= 65535;
        }

        private static NetCommand Fail(NetCommandKind kind, string error)
        {
            return new NetCommand(kind) { Error = error };
        }
    }
}
