using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// The traffic of the host's game, shown in every game (docs/DECISIONS.md).
    ///
    /// What is assumed about the game, from the dumps of 2026-10-14 and the game with the test bridge
    /// (docs/GAME_NOTES.md, "El tráfico"): the cars that drive by themselves are "TRAFFIC/VehiclesHighway/*" (ten, one
    /// of them pulling "CARAVAN") and the rally driver "TRAFFIC/VehiclesDirtRoad/Rally/FITTAN". Each has a body,
    /// "CarDynamics", a "MobileCarController" and a "Drivetrain", and drives with full physics wherever it is while
    /// its group is on: state machines "Steering", "Throttle", "Shifting", "Direction" and those of "NavigationAI"
    /// steer it along "TRAFFIC/Routes". Its "LOD" child (inside, people, sound) is on within 300 m of the player; its
    /// "BrakeLights" child is on while it brakes; its engine sound follows "Drivetrain.rpm". A group is switched on
    /// and off by the gateways between the dirt roads and the highway ("TRAFFIC/TriggerManager/&lt;place&gt;/TriggerIn"
    /// and "TriggerOut", state machine "Trigger"): passing one, the player's game switches each group to the
    /// gateway's "TrafficHighway" and "TrafficDirtroad".
    ///
    /// The host's game drives every car. A group is on while any player is where it drives: the gateways of every
    /// game are given, for what they switch, what someone needs; where the player is comes from the gateway they
    /// passed last (said by each client as "traffic/zone/&lt;player&gt;"). About fifteen times a second the host says
    /// where each car is (<see cref="TrafficReplication"/>). A client's cars drive nothing: they are kinematic, their
    /// controllers, engines and driving state machines are off, and each is moved, in every physics step, to where
    /// the host had it a moment ago; their wheels turn, their engines sound and their brake lights show as the host's.
    /// </summary>
    internal sealed class TrafficSync
    {
        private const string Root = "TRAFFIC";
        private const string HighwayPath = "VehiclesHighway";
        private const string DirtRoadsPath = "VehiclesDirtRoad";
        private const string RallyPath = "VehiclesDirtRoad/Rally";
        private const string GatewaysPath = "TriggerManager";
        private const string ZoneKey = "traffic/zone/";
        private const string GatewayMachine = "Trigger";
        private const string GatewayOn = "On";
        private const double SearchInterval = 2.0;
        private const float TeleportDistance = 20f;
        private const float ThrowFactor = 1.5f;
        private const float ThrowSlack = 1f;
        private const float ThrowReach = 1.5f;

        // The state machines that drive a car, switched off where it is only shown. The others (its lights, its
        // details near the player, its sound, its people) go on as the game has them.
        private static readonly string[] Driving =
        {
            "Steering", "Throttle", "Shifting", "ThrottleDrag", "Staging", "Direction", "Raycast", "Passing", "Navigation",
            "PassCheck", "Brakezones", "Lift", "Crash", "Speed", "Detach", "PlayerTrigger", "HelmetSim", "Achi",
        };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<FollowedCar> cars = new List<FollowedCar>();
        private readonly List<Gateway> gateways = new List<Gateway>();
        private readonly Dictionary<byte, bool> zones = new Dictionary<byte, bool>();
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();
        private TrafficCar[] report = new TrafficCar[0];
        private TrafficReplication current;
        private TallyReplication tallies;
        private bool host;
        private double nextSearch;
        private bool found;
        private bool searchReported;
        private GameObject highway;
        private GameObject dirtRoads;
        private GameObject rally;
        private bool onHighway;
        private int toldZone = -1;
        private double frameNow;
        private float frameTime;
        private bool frameKnown;
        private Transform player;
        private CharacterMotor playerMotor;
        private long throwsStopped;

        public TrafficSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TrafficReplication traffic = localPlayer == null ? null : manager.Traffic;
            TallyReplication zoneTallies = localPlayer == null ? null : manager.Tallies;
            if (traffic != current || zoneTallies != tallies)
            {
                if (tallies != null)
                {
                    tallies.StateWanted -= OnStateWanted;
                }

                Release();
                current = traffic;
                tallies = zoneTallies;
                host = manager.Mode == NetworkMode.Host;
                if (tallies != null)
                {
                    tallies.StateWanted += OnStateWanted;
                }

                found = false;
                frameKnown = false;
                zones.Clear();
                heard.Clear();
                toldZone = -1;
                nextSearch = now;
            }

            player = localPlayer;
            if (current == null || tallies == null)
            {
                return;
            }

            if (!found && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            // Unity's null check is also true for a destroyed object.
            if (!found || highway == null || dirtRoads == null)
            {
                return;
            }

            frameNow = now;
            frameTime = Time.time;
            frameKnown = true;
            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value);
            }

            heard.Clear();
            WatchGateways();
            if (host)
            {
                SwitchGroups(onHighway || AnyoneOn(true), !onHighway || AnyoneOn(false), null);
                Report(now);
            }
            else if (current.HasState)
            {
                byte groups = current.Groups;
                SwitchGroups((groups & TrafficWire.GroupHighway) != 0, (groups & TrafficWire.GroupDirtRoads) != 0, (groups & TrafficWire.GroupRally) != 0);
                Show();
                StopThrow();
            }
        }

        /// <summary>In every physics step: a client's cars, to where the host had them a moment ago.</summary>
        public void FixedUpdate()
        {
            if (!found || host || current == null || !frameKnown)
            {
                return;
            }

            double now = frameNow + (Time.fixedTime - frameTime);
            for (int i = 0; i < cars.Count; i++)
            {
                FollowedCar car = cars[i];
                BodyPose pose;

                // Unity's null check is also true for a destroyed object.
                if (!car.Following || car.Body == null || !car.Root.gameObject.activeInHierarchy || !current.Sample(i, now, out pose))
                {
                    continue;
                }

                var position = new Vector3(pose.X, pose.Y, pose.Z);
                var rotation = new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW);
                if (!car.Placed || (position - car.Body.position).sqrMagnitude > TeleportDistance * TeleportDistance)
                {
                    car.Body.interpolation = car.InterpolationBefore;
                    car.Root.position = position;
                    car.Root.rotation = rotation;
                    car.Placed = true;
                    continue;
                }

                car.Body.interpolation = RigidbodyInterpolation.Interpolate;
                car.Body.MovePosition(position);
                car.Body.MoveRotation(rotation);
            }
        }

        /// <summary>Every car and gateway here goes back to how the game had it.</summary>
        public void Release()
        {
            for (int i = 0; i < cars.Count; i++)
            {
                cars[i].Restore();
            }

            for (int i = 0; i < gateways.Count; i++)
            {
                gateways[i].Restore();
            }

            // A client's groups, as its own player's place has them.
            if (found && !host && highway != null && dirtRoads != null)
            {
                highway.SetActive(onHighway);
                dirtRoads.SetActive(!onHighway);
            }

            found = false;
        }

        private void Search()
        {
            GameObject root = GameObject.Find("/" + Root);
            Transform highwayAt = root == null ? null : root.transform.Find(HighwayPath);
            Transform dirtAt = root == null ? null : root.transform.Find(DirtRoadsPath);
            Transform rallyAt = root == null ? null : root.transform.Find(RallyPath);
            Transform gatewaysAt = root == null ? null : root.transform.Find(GatewaysPath);
            cars.Clear();
            gateways.Clear();
            if (highwayAt != null)
            {
                for (int i = 0; i < highwayAt.childCount; i++)
                {
                    Transform child = highwayAt.GetChild(i);
                    AddCar(child);

                    // A car that pulls a trailer: the trailer is a car of its own, with its own body.
                    for (int c = 0; c < child.childCount; c++)
                    {
                        if (child.GetChild(c).GetComponent<CarDynamics>() != null)
                        {
                            AddCar(child.GetChild(c));
                        }
                    }
                }
            }

            if (rallyAt != null)
            {
                for (int i = 0; i < rallyAt.childCount; i++)
                {
                    AddCar(rallyAt.GetChild(i));
                }
            }

            for (int z = 0; gatewaysAt != null && z < gatewaysAt.childCount; z++)
            {
                Transform place = gatewaysAt.GetChild(z);
                for (int t = 0; t < place.childCount; t++)
                {
                    PlayMakerFSM machine = World.DoorSync.FindMachine(place.GetChild(t), GatewayMachine);
                    FsmBool toHighway = machine == null ? null : machine.FsmVariables.FindFsmBool("TrafficHighway");
                    FsmBool toDirt = machine == null ? null : machine.FsmVariables.FindFsmBool("TrafficDirtroad");
                    if (toHighway != null && toDirt != null)
                    {
                        gateways.Add(new Gateway(place.name + "/" + place.GetChild(t).name, machine, toHighway, toDirt));
                    }
                }
            }

            if (highwayAt == null || dirtAt == null || cars.Count == 0 || cars.Count > TrafficWire.MaxCars || gateways.Count == 0)
            {
                if (!searchReported)
                {
                    searchReported = true;
                    log.Warning(LogCategory.Vehicle, "Traffic: '" + Root + "' (its groups, " + cars.Count + " car(s), " + gateways.Count + " gateway(s)) is not as this build expects: each game drives its own traffic.");
                }

                cars.Clear();
                gateways.Clear();
                return;
            }

            // The same order in every game, whatever hangs from what by now: a trailer is let go of its car while
            // the game runs, and moves to the end of its group.
            var counts = new Dictionary<string, int>();
            for (int i = 0; i < cars.Count; i++)
            {
                int seen;
                counts.TryGetValue(cars[i].Root.name, out seen);
                counts[cars[i].Root.name] = seen + 1;
                cars[i].Path = cars[i].Root.name + "#" + seen.ToString(CultureInfo.InvariantCulture);
            }

            cars.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            highway = highwayAt.gameObject;
            dirtRoads = dirtAt.gameObject;
            rally = rallyAt == null ? null : rallyAt.gameObject;
            onHighway = highway.activeSelf;
            uint shape = 2166136261;
            for (int i = 0; i < cars.Count; i++)
            {
                shape = Hash(shape, cars[i].Path);
            }

            current.Define(cars.Count, shape);
            report = new TrafficCar[cars.Count];
            found = true;
            log.Info(
                LogCategory.Vehicle,
                "Traffic: " + cars.Count + " car(s) and " + gateways.Count + " gateway(s) found (shape " + shape.ToString("x8", CultureInfo.InvariantCulture) + "); "
                + (host ? "this game drives them for everyone." : "the host drives them.") + " This player is on " + (onHighway ? "the highway." : "the dirt roads."));
        }

        private void AddCar(Transform root)
        {
            Rigidbody body = root.GetComponent<Rigidbody>();
            if (body == null || root.GetComponent<CarDynamics>() == null)
            {
                return;
            }

            cars.Add(new FollowedCar(root, body, Driving));
        }

        private static uint Hash(uint hash, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                hash = (hash ^ text[i]) * 16777619;
            }

            return (hash ^ '\n') * 16777619;
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(ZoneKey, StringComparison.Ordinal))
            {
                heard.Add(new KeyValuePair<string, int[]>(key, values));
            }
        }

        private void Hear(string key, int[] values)
        {
            byte player;
            if (values.Length >= 1 && byte.TryParse(key.Substring(ZoneKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out player)
                && player != manager.Session.LocalPlayerId)
            {
                zones[player] = values[0] != 0;
            }
        }

        // Whether a player other than this game's, still in the session, is where that group drives.
        private bool AnyoneOn(bool highwayGroup)
        {
            IList<PlayerInfo> players = manager.Session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                bool there;
                if (!players[i].IsLocal && zones.TryGetValue(players[i].Id, out there) && there == highwayGroup)
                {
                    return true;
                }
            }

            return false;
        }

        // The gateway this game's player passed last tells where they are; it is said to the others when it changes.
        private void WatchGateways()
        {
            for (int i = 0; i < gateways.Count; i++)
            {
                Gateway gateway = gateways[i];
                string state = gateway.Machine.ActiveStateName;
                if (state != gateway.LastState)
                {
                    gateway.LastState = state;
                    if (state == GatewayOn)
                    {
                        onHighway = gateway.OwnHighway;
                    }
                }
            }

            // Not before the session is connected: until then this player's number is not known.
            int zone = onHighway ? 1 : 0;
            if (zone != toldZone && manager.Session.State == SessionState.Connected)
            {
                toldZone = zone;
                tallies.Set(ZoneKey + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { zone });
                log.Info(LogCategory.Vehicle, "Traffic: this player is on " + (onHighway ? "the highway" : "the dirt roads") + " now; told.");
            }
        }

        // The groups that someone needs, on; and every gateway made to switch on, when passed, what someone needs.
        private void SwitchGroups(bool highwayOn, bool dirtOn, bool? rallyOn)
        {
            if (highway.activeSelf != highwayOn)
            {
                highway.SetActive(highwayOn);
                log.Info(LogCategory.Vehicle, "Traffic: the highway's cars are " + (highwayOn ? "on" : "off") + ".");
            }

            if (dirtRoads.activeSelf != dirtOn)
            {
                dirtRoads.SetActive(dirtOn);
                log.Info(LogCategory.Vehicle, "Traffic: the dirt roads' cars are " + (dirtOn ? "on" : "off") + ".");
            }

            if (rallyOn.HasValue && rally != null && rally.activeSelf != rallyOn.Value)
            {
                rally.SetActive(rallyOn.Value);
            }

            for (int i = 0; i < gateways.Count; i++)
            {
                Gateway gateway = gateways[i];
                bool toHighway = host ? gateway.OwnHighway || highwayOn : highwayOn;
                bool toDirt = host ? gateway.OwnDirt || dirtOn : dirtOn;
                if (gateway.ToHighway.Value != toHighway)
                {
                    gateway.ToHighway.Value = toHighway;
                }

                if (gateway.ToDirt.Value != toDirt)
                {
                    gateway.ToDirt.Value = toDirt;
                }
            }
        }

        // The host: where its cars are, about fifteen times a second.
        private void Report(double now)
        {
            for (int i = 0; i < cars.Count; i++)
            {
                FollowedCar car = cars[i];

                // Unity's null check is also true for a destroyed object.
                bool active = car.Body != null && car.Root.gameObject.activeInHierarchy;
                report[i].Active = active;
                if (!active)
                {
                    continue;
                }

                Vector3 position = car.Body.position;
                Quaternion rotation = car.Body.rotation;
                report[i].Pose = new BodyPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
                report[i].Speed = Vector3.Dot(car.Body.velocity, car.Root.forward);
                report[i].Rpm = car.Drivetrain == null ? 0f : car.Drivetrain.rpm;
                report[i].Flags = car.BrakeLights != null && car.BrakeLights.activeSelf ? TrafficWire.LightBrake : (byte)0;
            }

            byte groups = (byte)((highway.activeSelf ? TrafficWire.GroupHighway : 0) | (dirtRoads.activeSelf ? TrafficWire.GroupDirtRoads : 0)
                | (rally != null && rally.activeSelf ? TrafficWire.GroupRally : 0));
            current.Report(now, groups, report);
        }

        // A client: each car on as the host has it, driven by nothing, its wheels, engine and brake lights shown.
        private void Show()
        {
            for (int i = 0; i < cars.Count; i++)
            {
                FollowedCar car = cars[i];

                // Unity's null check is also true for a destroyed object.
                if (car.Body == null)
                {
                    continue;
                }

                bool on = current.IsActive(i);
                if (car.Root.gameObject.activeSelf != on)
                {
                    car.Root.gameObject.SetActive(on);
                }

                if (!on || !car.Root.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!car.Following)
                {
                    car.StartFollowing();
                }
                else
                {
                    car.KeepOff();
                }

                TrafficCar latest = current.Latest(i);
                car.ShowControls(latest.Speed, latest.Rpm, (latest.Flags & TrafficWire.LightBrake) != 0);
            }
        }

        // As next to a vehicle that another machine moves (VehiclePresenter.StopThrow): a car of the host's traffic
        // is kinematic here, and moving into the player it would make the game's motor launch them.
        private void StopThrow()
        {
            // Unity's null check is also true for a destroyed object.
            if (player == null || player.parent != null)
            {
                return;
            }

            if (playerMotor == null)
            {
                playerMotor = player.GetComponent<CharacterMotor>();
                if (playerMotor == null)
                {
                    return;
                }
            }

            CharacterMotorMovement movement = playerMotor.movement;
            CharacterMotorJumping jumping = playerMotor.jumping;
            if (movement == null || jumping == null)
            {
                return;
            }

            float rising = movement.velocity.y;
            float takeOff = Mathf.Sqrt(2f * Mathf.Max(0f, jumping.baseHeight + jumping.extraHeight) * Mathf.Max(0f, movement.gravity));
            if (rising <= (takeOff * ThrowFactor) + ThrowSlack)
            {
                return;
            }

            Vector3 at = player.position;
            for (int i = 0; i < cars.Count; i++)
            {
                if (cars[i].Following && cars[i].Body != null && cars[i].StandsBeside(at, ThrowReach))
                {
                    movement.velocity.y = 0f;
                    throwsStopped++;
                    if (throwsStopped == 1 || throwsStopped % 20 == 0)
                    {
                        log.Info(LogCategory.Vehicle, "Traffic: '" + cars[i].Root.name + "' pushed the local player, and the game's motor took that for a leap: taken away. " + throwsStopped + " time(s) so far.");
                    }

                    return;
                }
            }
        }

        private sealed class Gateway
        {
            public Gateway(string name, PlayMakerFSM machine, FsmBool toHighway, FsmBool toDirt)
            {
                Name = name;
                Machine = machine;
                ToHighway = toHighway;
                ToDirt = toDirt;
                OwnHighway = toHighway.Value;
                OwnDirt = toDirt.Value;
                LastState = machine.ActiveStateName;
            }

            public string Name { get; }

            public PlayMakerFSM Machine { get; }

            public FsmBool ToHighway { get; }

            public FsmBool ToDirt { get; }

            /// <summary>What the game's gateway switches on: where a player who passes it is.</summary>
            public bool OwnHighway { get; }

            public bool OwnDirt { get; }

            public string LastState { get; set; }

            public void Restore()
            {
                ToHighway.Value = OwnHighway;
                ToDirt.Value = OwnDirt;
            }
        }
    }
}
