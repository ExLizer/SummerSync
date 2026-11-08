using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using SummerSync.Core.World;

namespace SummerSync.Core.Net
{
    /// <summary>Turns the state of the network into the lines shown in the debug overlay and by "ss status".</summary>
    public static class NetworkStatus
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>Appends one or more lines separated by '\n', without a trailing one.</summary>
        public static void Describe(NetworkManager manager, StringBuilder text)
        {
            NetSession session = manager.Session;
            if (session == null)
            {
                text.Append("Net: offline");
                AppendSimulator(manager, text);
                return;
            }

            text.Append("Net: ");
            switch (manager.Mode)
            {
                case NetworkMode.LoopbackTest:
                    text.Append("loopback self-test");
                    break;
                case NetworkMode.Host:
                    text.Append("host over ").Append(manager.TransportName)
                        .Append(" | ").Append(session.Players.Count).Append('/').Append(manager.MaxPlayers).Append(" players");
                    break;
                default:
                    text.Append("client over ").Append(manager.TransportName);
                    break;
            }

            if (session.State == SessionState.Connecting)
            {
                text.Append(" | connecting...");
            }
            else
            {
                if (session.Role == SessionRole.Client)
                {
                    text.Append(" | player #").Append(session.LocalPlayerId).Append(" of ").Append(session.Players.Count);
                }

                if (session.IsClockSynced)
                {
                    text.Append(" | tick ").Append(session.Tick);
                }

                if (session.Role == SessionRole.Client)
                {
                    text.Append(" | clock ");
                    text.Append(session.IsClockSynced ? Milliseconds(session.ClockOffset * 1000.0, true) : "not synced");
                }
            }

            AppendSimulator(manager, text);

            NetSession loopbackHost = manager.LoopbackHostSession;
            if (loopbackHost != null)
            {
                AppendPeers(loopbackHost, " host sees ", text);
                AppendPeers(session, " client sees ", text);
                AppendTraffic(Sum(loopbackHost.Traffic, session.Traffic), session.MalformedDatagrams + loopbackHost.MalformedDatagrams, text);
            }
            else
            {
                AppendPeers(session, " ", text);
                if (session.State == SessionState.Connected)
                {
                    AppendPlayers(session, text);
                    AppendTraffic(session.Traffic, session.MalformedDatagrams, text);
                }
            }

            if (session.State == SessionState.Connected)
            {
                AppendBodies(manager, text);
                AppendObjects(manager, text);
                AppendVehicles(manager, text);
                AppendSwitches(manager, text);
                AppendWorld(manager, text);
                WorldShare savedWorld = manager.SavedWorld;
                string worldLine = savedWorld == null ? null : savedWorld.Describe();
                if (worldLine != null)
                {
                    text.Append("\n ").Append(worldLine);
                }

                FileTransfer songs = manager.Songs;
                string songLine = songs == null ? null : songs.Describe();
                if (songLine != null)
                {
                    text.Append("\n ").Append(songLine);
                }

                FileTransfer cds = manager.Cds;
                string cdLine = cds == null ? null : cds.Describe();
                if (cdLine != null)
                {
                    text.Append("\n ").Append(cdLine);
                }

                FileTransfer art = manager.Art;
                string artLine = art == null ? null : art.Describe();
                if (artLine != null)
                {
                    text.Append("\n ").Append(artLine);
                }

                WalletReplication wallet = manager.Wallet;
                string walletLine = wallet == null ? null : wallet.Describe();
                if (walletLine != null)
                {
                    text.Append("\n ").Append(walletLine);
                }

                SpawnReplication spawns = manager.Spawns;
                string spawnLine = spawns == null ? null : spawns.Describe();
                if (spawnLine != null)
                {
                    text.Append("\n ").Append(spawnLine);
                }

                TallyReplication tallies = manager.Tallies;
                string tallyLine = tallies == null ? null : tallies.Describe();
                if (tallyLine != null)
                {
                    text.Append("\n ").Append(tallyLine);
                }

                ItemReplication items = manager.Items;
                string itemLine = items == null ? null : items.Describe();
                if (itemLine != null)
                {
                    text.Append("\n ").Append(itemLine);
                }

                AssemblyReplication assembly = manager.Assembly;
                string assemblyLine = assembly == null ? null : assembly.Describe();
                if (assemblyLine != null)
                {
                    text.Append("\n ").Append(assemblyLine);
                }

                ChecksumReplication checks = manager.Checks;
                string checkLine = checks == null ? null : checks.Describe();
                if (checkLine != null)
                {
                    text.Append("\n ").Append(checkLine);
                }
            }
        }

        /// <summary>"200 ms +-30 ms, loss 5 %", or "off".</summary>
        public static string DescribeConditions(NetworkConditions conditions)
        {
            if (conditions.IsPerfect)
            {
                return "off";
            }

            var text = new StringBuilder();
            text.Append(conditions.LatencyMs.ToString("0", Invariant)).Append(" ms");
            if (conditions.JitterMs > 0f)
            {
                text.Append(" +-").Append(conditions.JitterMs.ToString("0", Invariant)).Append(" ms");
            }

            text.Append(", loss ").Append(conditions.LossPercent.ToString("0.#", Invariant)).Append(" %");
            if (conditions.DuplicatePercent > 0f)
            {
                text.Append(", duplicates ").Append(conditions.DuplicatePercent.ToString("0.#", Invariant)).Append(" %");
            }

            return text.ToString();
        }

        private static void AppendSimulator(NetworkManager manager, StringBuilder text)
        {
            if (manager.Conditions.IsPerfect)
            {
                return;
            }

            text.Append("\nSim (what this side sends): ").Append(DescribeConditions(manager.Conditions));
            if (manager.IsActive)
            {
                text.Append(" | ").Append(manager.SimulatedInFlight).Append(" in flight, ").Append(manager.SimulatedDrops).Append(" dropped");
            }
        }

        private static void AppendPeers(NetSession session, string prefix, StringBuilder text)
        {
            IList<PeerStats> peers = session.GetPeerStats();
            for (int i = 0; i < peers.Count; i++)
            {
                PeerStats peer = peers[i];
                text.Append('\n').Append(prefix).Append('#').Append(peer.PlayerId).Append(' ').Append(peer.Name).Append(": rtt ");
                text.Append(peer.HasRoundTripTime ? Milliseconds(peer.RoundTripMs, false) : "?");
                text.Append(", loss ").Append(peer.LossPercent.ToString("0.0", Invariant)).Append(" %");
                text.Append(", in ");
                AppendRate(peer.PacketsInPerSecond, peer.BytesInPerSecond, text);
                text.Append(", out ");
                AppendRate(peer.PacketsOutPerSecond, peer.BytesOutPerSecond, text);
                text.Append(", queue ").Append(peer.ReliableQueue).Append(", resent ").Append(peer.ReliableResends);
            }
        }

        private static void AppendPlayers(NetSession session, StringBuilder text)
        {
            IList<PlayerInfo> players = session.Players;
            text.Append("\n Players: ");
            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(", ");
                }

                text.Append('#').Append(players[i].Id).Append(' ').Append(players[i].Name);
                if (players[i].IsHost && players[i].IsLocal)
                {
                    text.Append(" (host, you)");
                }
                else if (players[i].IsHost)
                {
                    text.Append(" (host)");
                }
                else if (players[i].IsLocal)
                {
                    text.Append(" (you)");
                }
            }
        }

        // Where the local player is, and how each of the others is coming through.
        private static void AppendBodies(NetworkManager manager, StringBuilder text)
        {
            PlayerReplication own = manager.LocalPlayers;
            PlayerReplication seen = manager.Players;
            if (own == null || seen == null)
            {
                return;
            }

            text.Append("\n You: ");
            if (own.LocalInWorld)
            {
                PlayerPose pose = own.LocalPose;
                text.Append("at (").Append(pose.X.ToString("0.0", Invariant)).Append(", ").Append(pose.Y.ToString("0.0", Invariant))
                    .Append(", ").Append(pose.Z.ToString("0.0", Invariant)).Append("), looking ").Append(pose.Yaw.ToString("0", Invariant))
                    .Append('/').Append(pose.Pitch.ToString("0", Invariant)).Append(", eyes ").Append(pose.EyeHeight.ToString("0.00", Invariant))
                    .Append(" m");
                AppendRide(pose, text);
            }
            else
            {
                text.Append("not in the game world");
            }

            IList<RemotePlayer> remotes = seen.Remotes;
            for (int i = 0; i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                text.Append("\n ");
                if (manager.Mode == NetworkMode.LoopbackTest)
                {
                    text.Append("Echo of you");
                }
                else
                {
                    text.Append('#').Append(remote.Id).Append(' ').Append(remote.Name);
                }

                text.Append(": ");
                if (!remote.HasState)
                {
                    text.Append("no state yet");
                }
                else if (!remote.InWorld)
                {
                    text.Append("not in the game world");
                }
                else
                {
                    // Two positions measured from different things are not a distance apart.
                    if (own.LocalInWorld && remote.Visible && own.LocalPose.Parent == remote.Shown.Parent)
                    {
                        text.Append(PlayerPose.Distance(own.LocalPose, remote.Shown).ToString("0.0", Invariant)).Append(" m away, ");
                    }

                    if (remote.Visible && remote.Shown.Parent != 0)
                    {
                        text.Append(remote.Shown.Driving ? "drives vehicle " : "rides vehicle ").Append(remote.Shown.Parent).Append(", ");
                    }

                    if (remote.Visible && remote.Shown.Pushing)
                    {
                        text.Append("pushing, ");
                    }

                    text.Append(PlayerReplication.DescribeQuality(remote));
                }
            }
        }

        // The loose objects: how many are shared, and whether the two worlds are the same.
        private static void AppendObjects(NetworkManager manager, StringBuilder text)
        {
            ObjectReplication objects = manager.Objects;
            if (objects == null || (objects.Shared == 0 && objects.MissingHere == 0 && objects.Locals.Count == 0))
            {
                return;
            }

            text.Append("\n Objects: ").Append(objects.Shared).Append(" shared, ").Append(objects.Moving).Append(" moving, ")
                .Append(objects.Simulated).Append(" simulated here");
            SharedBody inHand = objects.InHand;
            if (inHand != null)
            {
                text.Append(", '").Append(inHand.Name).Append("' in hand");
            }

            if (objects.MissingHere > 0 || objects.OnlyHere > 0)
            {
                text.Append(" | WORLDS DIFFER: ").Append(objects.MissingHere).Append(" of the host's missing here, ")
                    .Append(objects.OnlyHere).Append(" only here");
            }
        }

        // After a player's own position: the vehicle it is measured from, and what a driver is doing with it.
        private static void AppendRide(PlayerPose pose, StringBuilder text)
        {
            if (pose.Parent == 0)
            {
                return;
            }

            text.Append(pose.Driving ? " | driving vehicle " : " | riding vehicle ").Append(pose.Parent).Append(" (position is inside it)");
            if (pose.Driving)
            {
                text.Append(", steering ").Append(pose.Controls.Steering.ToString("0.00", Invariant))
                    .Append(", throttle ").Append(pose.Controls.Throttle.ToString("0.00", Invariant))
                    .Append(", brake ").Append(pose.Controls.Brake.ToString("0.00", Invariant))
                    .Append(", ").Append(pose.Controls.Rpm.ToString("0", Invariant)).Append(" rpm, gear ").Append(pose.Controls.Gear);
            }
        }

        // The vehicles: how many are shared, and who is at the wheel of each one that has a driver.
        private static void AppendVehicles(NetworkManager manager, StringBuilder text)
        {
            ObjectReplication vehicles = manager.Vehicles;
            if (vehicles == null || (vehicles.Shared == 0 && vehicles.MissingHere == 0 && vehicles.Locals.Count == 0))
            {
                return;
            }

            text.Append("\n Vehicles: ").Append(vehicles.Shared).Append(" shared, ").Append(vehicles.Moving).Append(" moving, ")
                .Append(vehicles.Simulated).Append(" simulated here");
            IList<SharedBody> locals = vehicles.Locals;
            for (int i = 0; i < locals.Count; i++)
            {
                SharedBody body = locals[i];
                if (body.HasId && body.Held)
                {
                    text.Append(", '").Append(body.Name).Append("' (").Append(body.Id).Append(") driven by ");
                    if (body.Role == BodyRole.Simulate)
                    {
                        text.Append("you");
                    }
                    else
                    {
                        text.Append("player ").Append(body.Owner);
                    }
                }
            }

            if (vehicles.MissingHere > 0 || vehicles.OnlyHere > 0)
            {
                text.Append(" | WORLDS DIFFER: ").Append(vehicles.MissingHere).Append(" of the host's missing here, ")
                    .Append(vehicles.OnlyHere).Append(" only here");
            }
        }

        private static void AppendSwitches(NetworkManager manager, StringBuilder text)
        {
            SwitchReplication switches = manager.Switches;
            if (switches == null || switches.Count == 0)
            {
                return;
            }

            text.Append("\n Doors: ").Append(switches.Count);
            if (switches.Mismatch && switches.Shared)
            {
                text.Append(" | WORLDS DIFFER: ").Append(switches.SharedCount).Append(" shared by name, ").Append(switches.OnlyHere)
                    .Append(" only here, ").Append(switches.OnlyHost).Append(" only the host's");
            }
            else if (switches.Mismatch)
            {
                text.Append(" | WORLDS DIFFER: not shared");
            }
            else
            {
                text.Append(switches.Shared ? " shared" : " waiting for the host");
            }
        }

        // The time and the weather this machine's game shows, and on a client how far that is from the host's.
        private static void AppendWorld(NetworkManager manager, StringBuilder text)
        {
            WorldReplication world = manager.World;
            if (world == null || (!world.ShowsClock && !world.ShowsWeather))
            {
                return;
            }

            text.Append("\n World: ");
            if (world.ShowsClock)
            {
                WorldClock shown = world.ShownClock;
                float into = shown.Elapsed < 0f ? 0f : shown.Elapsed > WorldClock.BlockSeconds ? WorldClock.BlockSeconds : shown.Elapsed;
                float half = WorldClock.BlockSeconds / 2f;
                int hour = ((shown.Hour % 24) + (into >= half ? 1 : 0)) % 24;
                int minute = (int)((into % half) / (half / 60f));
                text.Append("day ").Append(shown.Day).Append(", ").Append(hour.ToString("00", Invariant)).Append(':')
                    .Append((minute > 59 ? 59 : minute).ToString("00", Invariant));
                WorldClock host;
                if (world.IsAuthority)
                {
                    text.Append(" (the host's)");
                }
                else if (world.TryGetClock(out host))
                {
                    double ahead = shown.Ahead(host);
                    text.Append(", ").Append((ahead < 0 ? -ahead : ahead).ToString("0.0", Invariant)).Append(ahead < 0 ? " s behind the host" : " s ahead of the host");
                }
                else
                {
                    text.Append(", the host has not said what time it has");
                }
            }
            else
            {
                text.Append("no clock found");
            }

            if (world.ShowsWeather)
            {
                WeatherState weather = world.ShownWeather;
                text.Append(" | ").Append(weather.Kind == WeatherState.Thunder ? "thunder" : weather.Kind == WeatherState.Rain ? "rain" : "dry")
                    .Append(" front at (").Append(weather.X.ToString("0", Invariant)).Append(", ").Append(weather.Z.ToString("0", Invariant)).Append(')');
                WeatherState host;
                double age;
                if (!world.IsAuthority && world.TryGetWeather(out host, out age) && !host.SameFront(weather))
                {
                    text.Append(" | NOT THE HOST'S");
                }
            }
        }

        private static void AppendTraffic(TrafficTestStats traffic, long malformed, StringBuilder text)
        {
            if (traffic.ReliableSent == 0 && traffic.ReliableReceived == 0 && traffic.UnreliableSent == 0 && traffic.UnreliableReceived == 0)
            {
                return;
            }

            text.Append("\n Test traffic: reliable ").Append(traffic.ReliableSent).Append(" sent, ").Append(traffic.ReliableReceived)
                .Append(" received, ").Append(traffic.ReliableErrors).Append(" errors | unreliable ").Append(traffic.UnreliableSent)
                .Append(" sent, ").Append(traffic.UnreliableReceived).Append(" received");
            if (malformed > 0)
            {
                text.Append(" | ").Append(malformed).Append(" malformed");
            }
        }

        private static TrafficTestStats Sum(TrafficTestStats a, TrafficTestStats b)
        {
            return new TrafficTestStats
            {
                ReliableSent = a.ReliableSent + b.ReliableSent,
                ReliableReceived = a.ReliableReceived + b.ReliableReceived,
                ReliableErrors = a.ReliableErrors + b.ReliableErrors,
                UnreliableSent = a.UnreliableSent + b.UnreliableSent,
                UnreliableReceived = a.UnreliableReceived + b.UnreliableReceived,
            };
        }

        private static void AppendRate(double packetsPerSecond, double bytesPerSecond, StringBuilder text)
        {
            text.Append(packetsPerSecond.ToString("0", Invariant)).Append("/s ")
                .Append((bytesPerSecond / 1024.0).ToString("0.0", Invariant)).Append(" kB/s");
        }

        private static string Milliseconds(double value, bool signed)
        {
            return value.ToString(signed ? "+0.0;-0.0" : "0", Invariant) + " ms";
        }
    }
}
