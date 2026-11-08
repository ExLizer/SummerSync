using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The bus, the host's, that every player can ride. <see cref="NpcSync"/> shows it in
    /// every game; this is what a rider and the ones waiting at a stop need from it.
    ///
    /// What is assumed about the game, from the dump of 2026-10-14 (docs/GAME_NOTES.md, "El colectivo"): the bus,
    /// "NPC_CARS/…/BUS", stops at a stop ("TRAFFIC/BusStopLoppe", "…Kesseli", "…Rykipohja") when its state machine
    /// "NavigationAI :: StopBus" finds that stop's flag ("BusStoppingLoppe"…) or "BusStopping" (a stop button inside) on;
    /// each stop's "Stopping" puts its flag on while the player is within 4 m of it. At a stop, "Route :: Start" waits
    /// while the player is getting on ("LOD/PlayerTrigger :: PlayerTrigger", "PlayerInBus") or sits without having paid
    /// ("LOD/PlayerTrigger/DriveTrigger :: PlayerTrigger", "PlayerSitsBus"; "Route :: Start", "Paid", which the ticket
    /// sets), and then drives on. "Route :: Door" opens and closes the door. A rider is hung from the bus by the game, and
    /// dies if the joint of "DriverHeadPivot" breaks (800 while riding).
    ///
    /// Each client says ("bus/rider/&lt;player&gt;") which stop it is waiting at, whether its player is in the bus or sits,
    /// and how many tickets it has paid; the host's game adds that to its own bus, the game's flags, so its bus stops,
    /// waits and leaves for every player as it does for its own. The host says when its door opens ("bus/door"), and the
    /// clients' doors open as theirs. On a client, a rider cannot get off while the host's bus moves (as in the game),
    /// and the joint that holds them does not break: there the bus is moved by the mod, not by a crash (both done for
    /// every NPC car by <see cref="NpcSync"/> and <see cref="Vehicles.FollowedCar"/>).
    /// </summary>
    internal sealed class BusSync
    {
        private const string BusKey = "bus";
        private const string RiderKey = "bus/rider/";
        private const string DoorKey = "bus/door";
        private const string OpenEvent = "OPENDOOR";
        private const string CloseEvent = "CLOSEDOOR";
        private const double SearchInterval = 2.0;
        private const float StandingSpeed = 0.15f;

        private const int InBus = 1;
        private const int Sits = 2;

        // The flags of "StopBus" that ask the bus to stop, each with its bit from 4 up.
        private static readonly string[] Wants = { "BusStopping", "BusStoppingLoppe", "BusStoppingKesseli", "BusStoppingRykipohja" };

        // The states of a seat's "PlayerTrigger" while its player rides.
        private static readonly string[] Riding = { "Player in car", "Death", "Reset view", "Check speed" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly NpcSync npcs;
        private readonly Dictionary<byte, int[]> riders = new Dictionary<byte, int[]>();
        private readonly Dictionary<byte, int> paidSeen = new Dictionary<byte, int>();
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();
        private readonly List<PlayMakerFSM> seats = new List<PlayMakerFSM>();
        private TallyReplication current;
        private bool host;
        private bool found;
        private double nextSearch;

        private Transform bus;
        private PlayMakerFSM door;
        private FsmBool doorOpen;
        private FsmBool paid;
        private FsmBool inBus;
        private FsmBool sits;
        private FsmBool[] wants = new FsmBool[0];
        private Overlay inBusOverlay;
        private Overlay sitsOverlay;
        private Overlay[] wantOverlays = new Overlay[0];

        private int toldBits = -1;
        private int toldPaid = -1;
        private int paidCount;
        private bool wasPaid;
        private int toldDoor = -1;
        private int doorWanted = -1;

        public BusSync(Logger log, NetworkManager manager, NpcSync npcs)
        {
            this.log = log;
            this.manager = manager;
            this.npcs = npcs;
        }

        /// <summary>
        /// Once per frame, after <see cref="NpcSync"/>. <paramref name="localPlayer"/> is the player's root object, or null
        /// while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }

                riders.Clear();
                paidSeen.Clear();
                heard.Clear();
                found = false;
                toldBits = -1;
                toldPaid = -1;
                paidCount = 0;
                toldDoor = -1;
                doorWanted = -1;
                nextSearch = now;
            }

            if (current == null)
            {
                return;
            }

            // Unity's null check is also true for a destroyed object.
            if ((!found || bus == null) && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            if (!found || bus == null)
            {
                return;
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value);
            }

            heard.Clear();
            if (host)
            {
                Gather();
                TellDoor();
            }
            else
            {
                ShowDoor();
                Tell();
            }
        }

        /// <summary>Everything this changed in the game goes back as the game had it.</summary>
        public void Release()
        {
            if (inBusOverlay != null)
            {
                inBusOverlay.Release();
            }

            if (sitsOverlay != null)
            {
                sitsOverlay.Release();
            }

            for (int i = 0; i < wantOverlays.Length; i++)
            {
                wantOverlays[i].Release();
            }

            found = false;
        }

        private void Search()
        {
            bus = npcs.Find(BusKey);
            if (bus == null)
            {
                return;
            }

            Transform routeAt = bus.Find("Route");
            Transform navigationAt = bus.Find("NavigationAI");
            Transform triggerAt = bus.Find("LOD/PlayerTrigger");
            PlayMakerFSM route = routeAt == null ? null : DoorSync.FindMachine(routeAt, "Start");
            door = routeAt == null ? null : DoorSync.FindMachine(routeAt, "Door");
            PlayMakerFSM stop = navigationAt == null ? null : DoorSync.FindMachine(navigationAt, "StopBus");
            PlayMakerFSM entrance = triggerAt == null ? null : DoorSync.FindMachine(triggerAt, "PlayerTrigger");
            Transform seatAt = triggerAt == null ? null : triggerAt.Find("DriveTrigger");
            PlayMakerFSM firstSeat = seatAt == null ? null : DoorSync.FindMachine(seatAt, "PlayerTrigger");
            seats.Clear();
            for (int i = 0; triggerAt != null && i < triggerAt.childCount; i++)
            {
                Transform child = triggerAt.GetChild(i);
                PlayMakerFSM seat = child.name == "DriveTrigger" ? DoorSync.FindMachine(child, "PlayerTrigger") : null;
                if (seat != null && seat.FsmVariables.FindFsmBool("PlayerSitsBus") != null)
                {
                    seats.Add(seat);
                }
            }

            paid = route == null ? null : route.FsmVariables.FindFsmBool("Paid");
            doorOpen = door == null ? null : door.FsmVariables.FindFsmBool("DoorOpen");
            inBus = entrance == null ? null : entrance.FsmVariables.FindFsmBool("PlayerInBus");
            sits = firstSeat == null ? null : firstSeat.FsmVariables.FindFsmBool("PlayerSitsBus");
            wants = new FsmBool[Wants.Length];
            bool all = paid != null && doorOpen != null && inBus != null && sits != null && seats.Count > 0
                && DoorSync.HasState(door, "State 2");
            for (int i = 0; i < Wants.Length; i++)
            {
                wants[i] = stop == null ? null : stop.FsmVariables.FindFsmBool(Wants[i]);
                all &= wants[i] != null;
            }

            if (!all)
            {
                log.Warning(LogCategory.Fsm, "Bus: '" + bus.name + "' (Route :: Start and Door, NavigationAI :: StopBus, LOD/PlayerTrigger and its seats) is not as this build expects: riding it is each game's own.");
                bus = null;
                found = true;
                return;
            }

            if (host)
            {
                inBusOverlay = new Overlay(inBus);
                sitsOverlay = new Overlay(sits);
                wantOverlays = new Overlay[wants.Length];
                for (int i = 0; i < wants.Length; i++)
                {
                    wantOverlays[i] = new Overlay(wants[i]);
                }
            }

            wasPaid = paid.Value;
            found = true;
            log.Info(LogCategory.Fsm, "Bus: found, " + seats.Count + " seat(s); " + (host ? "this game's bus stops and waits for every player." : "the host's bus stops and waits for this player too."));
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(RiderKey, StringComparison.Ordinal) || key == DoorKey)
            {
                heard.Add(new KeyValuePair<string, int[]>(key, values));
            }
        }

        private void Hear(string key, int[] values)
        {
            byte player;
            if (key == DoorKey)
            {
                if (!host && values.Length >= 1)
                {
                    doorWanted = values[0];
                }
            }
            else if (host && values.Length >= 2
                && byte.TryParse(key.Substring(RiderKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out player)
                && player != manager.Session.LocalPlayerId)
            {
                riders[player] = values;
                int seen;
                paidSeen.TryGetValue(player, out seen);
                if (values[1] > seen)
                {
                    // A ticket paid in that game: this bus has it paid too, for the trip it waits to start.
                    paidSeen[player] = values[1];
                    paid.Value = true;
                    log.Info(LogCategory.Fsm, "Bus: player " + player + " paid a ticket; the bus here has it paid.");
                }
            }
        }

        // The host: its bus's flags, as its own player has them or any other player needs them.
        private void Gather()
        {
            int needed = 0;
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                int[] values;
                if (remotes[i].InWorld && riders.TryGetValue(remotes[i].Id, out values))
                {
                    needed |= values[0];
                }
            }

            inBusOverlay.Update((needed & InBus) != 0);
            sitsOverlay.Update((needed & Sits) != 0);
            for (int i = 0; i < wantOverlays.Length; i++)
            {
                wantOverlays[i].Update((needed & (4 << i)) != 0);
            }
        }

        private void TellDoor()
        {
            int open = doorOpen.Value ? 1 : 0;
            if (open != toldDoor && manager.Session.State == SessionState.Connected)
            {
                toldDoor = open;
                current.Set(DoorKey, new[] { open });
            }
        }

        // A client: what its player needs of the bus.
        private void Tell()
        {
            int bits = (inBus.Value ? InBus : 0) | (AnyRiding() ? Sits : 0);
            for (int i = 0; i < wants.Length; i++)
            {
                if (wants[i].Value)
                {
                    bits |= 4 << i;
                }
            }

            // The ticket: the game sets "Paid" when it is bought and clears it when the player gets off.
            if (paid.Value && !wasPaid)
            {
                paidCount++;
                log.Info(LogCategory.Fsm, "Bus: this player paid a ticket; told.");
            }

            wasPaid = paid.Value;
            if ((bits != toldBits || paidCount != toldPaid) && manager.Session.State == SessionState.Connected)
            {
                toldBits = bits;
                toldPaid = paidCount;
                current.Set(RiderKey + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { bits, paidCount });
            }
        }

        private bool AnyRiding()
        {
            for (int i = 0; i < seats.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (seats[i] != null && Array.IndexOf(Riding, seats[i].ActiveStateName) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // A client's door: open or closed as the host's, by its own state machine, once it waits for an order.
        private void ShowDoor()
        {
            if (doorWanted < 0)
            {
                return;
            }

            bool open = doorWanted != 0;
            if (doorOpen.Value == open || door.ActiveStateName != "State 2")
            {
                return;
            }

            door.SendEvent(open ? OpenEvent : CloseEvent);
            if (open && wants.Length > 0 && wants[0].Value)
            {
                // The bus stopped: as the game's "StopBus" does when it stops, the stop asked for from inside is done.
                wants[0].Value = false;
            }
        }

        /// <summary>
        /// A flag of the host's bus that its own game writes when it changes, and that the mod also puts on while any other
        /// player needs it: what the game wrote last is its own, and it is what is left when the others no longer need it.
        /// </summary>
        private sealed class Overlay
        {
            private readonly FsmBool flag;
            private bool own;
            private bool written;
            private bool active;

            public Overlay(FsmBool flag)
            {
                this.flag = flag;
            }

            public void Update(bool others)
            {
                if (!active || flag.Value != written)
                {
                    own = flag.Value;
                    active = true;
                }

                bool wanted = own || others;
                if (flag.Value != wanted)
                {
                    flag.Value = wanted;
                }

                written = flag.Value;
            }

            public void Release()
            {
                if (active && flag.Value != own)
                {
                    flag.Value = own;
                }

                active = false;
            }
        }
    }
}
