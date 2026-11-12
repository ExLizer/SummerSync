using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// Shares the vehicles of the game world: the machine of whoever drives one runs its physics, and the others
    /// show it where that machine says it is.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 and the game's classes (docs/GAME_NOTES.md):
    /// a vehicle is a root object with a Rigidbody, an AxisCarController, a Drivetrain and its Wheels below it.
    /// Getting into driving mode makes the player object a child of the vehicle, and that is how the mod tells
    /// that the local player drives. The state machine "PlayerTrigger" on the vehicle's drive trigger is what
    /// offers driving mode; while someone else drives, it is switched off here.
    ///
    /// A vehicle someone else simulates is made kinematic and moved in FixedUpdate with MovePosition, so that
    /// the doors and whatever else hangs from it by a joint are carried along instead of being yanked. Its
    /// controller and drivetrain are switched off, and what they would have produced for the eye and the ear
    /// (steering, wheel spin, engine speed) is written from what the driver's machine sends.
    ///
    /// A vehicle that runs its own physics never comes to rest, and neither do the doors that hang from it. A
    /// kinematic one standing still lets them fall asleep, and a sleeping door ignores a change of its hinge
    /// limits, which is how the game closes it (seen between two PCs on 2026-10-13). So the parts of a followed
    /// vehicle are kept awake.
    ///
    /// Passengers: a player who stands inside a vehicle that another machine moves is carried by it. After
    /// every physics step, which is when the vehicle moves, the player is put back at the place inside it where
    /// the game's own movement last left them, and turned as much as the vehicle turned. They go on walking
    /// with the game's movement against the vehicle's real walls: when that movement runs, walls and player
    /// have moved by the same amount. For that the vehicle is drawn without Unity's interpolation while the
    /// local player rides it. Where the inside of a vehicle is comes from its file in data/vehicles.
    ///
    /// Making the player a child of the vehicle, as the game does with the driver, does not carry them (tried
    /// on 2026-10-13): the physics moves the vehicle's transform without telling the player's
    /// CharacterController, which on its next move puts the player back where it thinks they are. Setting the
    /// player's position is what it does listen to.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const double SearchInterval = 3.0;
        private const double FirstSearchDelay = 1.5;
        private const string EjectEvent = "SUMMERSYNC EJECT";
        private const string EjectState = "Create player";
        private const string OfferState = "Press return";

        // How often a vehicle whose driver has nothing to hold is looked over again.
        private const float CockpitLookEvery = 3f;

        private const float SummonAhead = 6f;
        private const float SummonAbove = 1f;

        // Brought vehicles are parked side by side, this far apart, further and further from the player.
        private const float SummonApart = 4.5f;
        private const int SummonTries = 8;

        // While the local player drives: what counts as driving along, how little steering counts as holding
        // the wheel straight, and how often the log gets a line about how the vehicle behaves.
        private const float WatchSpeed = 5f;
        private const float CentredInput = 0.02f;
        private const float WatchEvery = 10f;

        // The local player is thrown, not jumping, when they rise this many times faster than the game's own
        // jump takes off, and a little more. It is looked at within this many metres of a vehicle another
        // machine moves, and written to the log no more often than every so many seconds.
        private const float ThrowFactor = 1.5f;
        private const float ThrowSlack = 1f;
        private const float ThrowReach = 1.5f;
        private const double ThrowLogEvery = 5.0;

        // Slower than this in every sense, a vehicle nobody drives counts as standing still.
        private const float RestSpeed = 0.05f;
        private const float RestSpin = 0.05f;

        // Or that is this far from where it was last told to be, whatever its speed says.
        private const float CreepDistance = 0.002f;
        private const float CreepTurn = 0.1f;

        // Further than this from where it should be, it is put there instead of being moved there.
        private const float TeleportDistance = 4f;

        // A vehicle that stands where another machine left it is looked at this often, in physics steps, and put
        // back if it or one of its doors is further than this from where it belongs.
        private const int PlaceCheckSteps = 25;
        private const float PlaceTolerance = 0.002f;
        private const float PlaceTurnTolerance = 0.1f;
        private const float HingeTolerance = 0.005f;

        // Further than this from where it is drawn, the body of a vehicle that stands is put back: a fiftieth of
        // a millimetre, a tenth of a second of what it creeps.
        private const float CreepTolerance = 0.00002f;

        // A followed vehicle whose step differs from its last one by more than this is put in place, not moved
        // there: 8 cm in a step is 4 m/s gained or lost in a fiftieth of a second, twenty times what brakes do.
        private const float MaxStepChange = 0.08f;
        private const double PlaceLogEvery = 5.0;

        private const string DataPrefix = "SummerSync.vehicles.";

        // How far to the sides of the measured interior a thing still counts as on board. The interior is where
        // a person can stand; a thing can lie against a front door, further out than that (2026-10-13: thrown
        // there, a remote control was taken for something lying beside the van).
        private const float CargoMargin = 0.25f;

        // Around a vehicle whose physics runs here, the space in which a thing that another machine simulates
        // could touch it: the box of its colliders, this much bigger, plus what the vehicle covers in a moment.
        private const float ReachMargin = 1f;
        private const float ReachSeconds = 0.25f;

        // Faster than this between two physics steps, a vehicle was put somewhere, not driven there.
        private const float MaxSteppedSpeed = 150f;

        // For how long after the local player last turned a vehicle's key the vehicle stays theirs.
        private const double TouchSeconds = 3.0;

        // The game's variable for how fast the player is being carried: the driver's seat writes the vehicle's
        // velocity to it on every frame, and the hand gives it to whatever it lets go of (dump of 2026-10-12).
        private const string CarriedVelocityVariable = "PlayerVelocity";

        // The vehicles this build knows how to share, read once from the files it carries.
        private static VehicleDefinition[] catalog;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Tracked> tracked = new List<Tracked>();
        private ObjectReplication current;
        private Transform player;
        private double nextSearch;
        private bool searched;
        private Tracked driven;
        private Tracked ridden;

        // The vehicle the local player still stands in although it no longer carries them: its physics came to
        // run on this machine.
        private Tracked stayedIn;
        private CharacterMotor riderMotor;
        private bool platformWasOn;
        private Vector3 riderPlace;
        private float riderHeading;
        private double rideStarted;
        private float rideDistance;
        private FsmVector3 carriedVelocity;
        private bool simulatesAny;
        private Rigidbody handBody;
        private Transform touchedRoot;
        private double touchedUntil;
        private Tracked watched;
        private double describeAt = double.PositiveInfinity;
        private CharacterMotor playerMotor;
        private int throwsStopped;
        private double nextThrowLog;
        private double frameNow;
        private float frameTime;
        private double gameClockOffset;
        private bool gameClockKnown;
        private const double GameClockJump = 0.05;
        private const double GameClockBlend = 0.02;
        private bool frameKnown;

        public VehiclePresenter(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
            if (catalog == null)
            {
                catalog = LoadCatalog(log);
            }
        }

        private static VehicleDefinition[] LoadCatalog(Logger log)
        {
            var found = new List<VehicleDefinition>();
            var names = new List<string>();
            Assembly assembly = typeof(VehiclePresenter).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            for (int i = 0; i < resources.Length; i++)
            {
                string resource = resources[i];
                if (!resource.StartsWith(DataPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resource))
                    using (var reader = new StreamReader(stream))
                    {
                        VehicleDefinition definition = VehicleDefinition.Parse(reader.ReadToEnd());
                        found.Add(definition);
                        names.Add("'" + definition.RootName + "'");
                    }
                }
                catch (FormatException exception)
                {
                    log.Error(LogCategory.Vehicle, "The vehicle file '" + resource + "' cannot be read; that vehicle is not shared. " + exception.Message);
                }
            }

            log.Info(LogCategory.Vehicle, "Vehicles this build can share: " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())) + ".");
            return found.ToArray();
        }

        /// <summary>The vehicles this build knows how to share, from its data files. Empty before the first presenter exists.</summary>
        public static VehicleDefinition[] Catalog
        {
            get { return catalog ?? new VehicleDefinition[0]; }
        }

        /// <summary>Vehicles of the game world that the mod knows how to share and has found.</summary>
        public int Count
        {
            get { return tracked.Count; }
        }

        /// <summary>
        /// Once per frame, after the game has moved everything and before the players are read and placed.
        /// <paramref name="localPlayer"/> is the player's root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            ObjectReplication vehicles = localPlayer == null ? null : manager.Vehicles;
            if (vehicles != current || localPlayer != player)
            {
                ReleaseAll();
                current = vehicles;
                player = localPlayer;
                playerMotor = null;
                nextSearch = now + FirstSearchDelay;
                searched = false;
            }

            frameNow = now;
            frameTime = Time.time;
            frameKnown = current != null;
            TrackGameClock(now);
            if (current == null)
            {
                return;
            }

            if (now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            driven = null;
            Tracked carried = null;
            Tracked touched = null;
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                Tracked vehicle = tracked[i];

                // Unity's null check is also true for a destroyed object.
                if (vehicle.Body == null)
                {
                    current.RemoveLocal(vehicle.Shared);
                    tracked.RemoveAt(i);
                    continue;
                }

                // At the wheel the game hangs the player from the driver's head, inside the vehicle. A passenger hangs from
                // the mod's seat.
                if (player.parent != null && player.parent != seatObject && player.IsChildOf(vehicle.Root))
                {
                    driven = vehicle;
                }
                else if (handBody != null && vehicle.Body == handBody)
                {
                    // The moped can be picked up and carried. In the hand it is this machine's, like at the wheel.
                    carried = vehicle;
                }
                else if (vehicle.Root == touchedRoot && now < touchedUntil && !(vehicle.Shared.Held && vehicle.Shared.Role == BodyRole.Follow))
                {
                    // Its key is being turned, and nobody else drives it: whoever turns the key runs the engine.
                    touched = vehicle;
                }
            }

            UpdateSeat();
            UpdateBelts();
            UpdateRiding();
            NoteRide();
            StopThrow();

            current.SetHeld(driven != null ? driven.Shared : carried != null ? carried.Shared : touched != null ? touched.Shared : null);
            Watch();
            simulatesAny = false;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                Drive(vehicle);
                bool mine = vehicle.Shared.HasId && vehicle.Shared.Role == BodyRole.Simulate && !vehicle.Body.isKinematic;
                vehicle.SetReach(mine, mine ? ReachMargin + (vehicle.Body.velocity.magnitude * ReachSeconds) : 0f);
                simulatesAny |= mine;
            }

            UpdateNearest();
            UpdateContacts(now);
            UpdateHeads(now);
            for (int i = 0; i < tracked.Count; i++)
            {
                tracked[i].NoteDeathParts();
            }

            DriveAid();
        }

        // The session's clock against the game's: both go at the same pace, so the difference is all but fixed. Read once a
        // frame it jitters by the few milliseconds between reading one and the other (2 ms either way, measured on
        // 2026-10-16), which at 20 ms a physics step is a tenth of a vehicle's speed: so it is smoothed, and a jump of
        // more than a few frames (a hitch the game's clock did not keep up with) is taken at once.
        private void TrackGameClock(double now)
        {
            double offset = now - Time.time;
            if (!gameClockKnown || Math.Abs(offset - gameClockOffset) > GameClockJump)
            {
                gameClockOffset = offset;
                gameClockKnown = true;
                return;
            }

            gameClockOffset += (offset - gameClockOffset) * GameClockBlend;
        }

        // The session's clock at the game's last physics step: when the vehicles this machine runs were where they are, and
        // the moment to place the ones it follows for.
        private double StepTime()
        {
            return gameClockOffset + Time.fixedTime;
        }

        /// <summary>
        /// Once per physics step: moves the vehicles someone else simulates. Their place is worked out for the
        /// time of the step, which runs a little behind or ahead of the frame's.
        /// </summary>
        public void FixedUpdate()
        {
            if (!frameKnown || current == null)
            {
                return;
            }

            current.Sample(StepTime());
            WatchStep();
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                if (vehicle.Body != null && vehicle.Following)
                {
                    vehicle.KeepPartsAwake();
                    if (vehicle.InContact)
                    {
                        Track(vehicle);
                    }
                    else
                    {
                        Move(vehicle);
                    }
                }
            }
        }

        /// <summary>Where the vehicle that the local player drives is, and how fast it goes. False on foot.</summary>
        public bool TryGetDriven(out Vector3 position, out float speed)
        {
            // Unity's null check is also true for a destroyed object.
            if (driven == null || driven.Body == null)
            {
                position = Vector3.zero;
                speed = 0f;
                return false;
            }

            position = driven.Root.position;
            speed = driven.Body.isKinematic ? 0f : driven.Body.velocity.magnitude;
            return true;
        }

        /// <summary>Whether the local player is at the wheel of the vehicle with that number.</summary>
        public bool Drives(ushort vehicleId)
        {
            return driven != null && driven.Shared.HasId && driven.Shared.Id == vehicleId;
        }

        /// <summary>Whether this machine runs the physics of the vehicle with that number.</summary>
        public bool Simulates(ushort vehicleId)
        {
            Tracked vehicle = Find(vehicleId);
            return vehicle != null && vehicle.Body != null && vehicle.Shared.Role == BodyRole.Simulate;
        }

        /// <summary>
        /// Whether this machine runs the physics of the vehicle with that root object. A vehicle the session
        /// does not know yet is the host's.
        /// </summary>
        public bool SimulatesRoot(Transform root)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Root == root && tracked[i].Shared.HasId)
                {
                    return tracked[i].Shared.Role == BodyRole.Simulate;
                }
            }

            return manager.Mode == NetworkMode.Host;
        }

        /// <summary>
        /// The player whose machine simulates the vehicle with that root object, as this machine knows it (on the host,
        /// the session's word). False for a vehicle the session does not know yet: that one is the host's.
        /// </summary>
        public bool TryGetSimulator(Transform root, out byte player)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Root == root && tracked[i].Shared.HasId)
                {
                    player = tracked[i].Shared.Owner;
                    return true;
                }
            }

            player = 0;
            return false;
        }

        /// <summary>
        /// Once per frame, from whoever shares the engines: whether the engine of the vehicle with that root
        /// object runs on the machine that simulates it. Shown here, such a vehicle idles with nobody at the
        /// wheel, and its engine goes on running if its physics come to this machine.
        /// </summary>
        public void SetEngine(Transform root, bool running)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Root == root)
                {
                    tracked[i].EngineRunning = running;
                    return;
                }
            }
        }

        /// <summary>
        /// Something was installed on the vehicle with that root object, or taken off it: what hangs from it with a body
        /// of its own is looked for again. Returns how many such parts it has now, or -1 if it is not a vehicle here.
        /// </summary>
        public int PartsChanged(Transform root)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Root == root)
                {
                    tracked[i].FindParts();
                    return tracked[i].PartCount;
                }
            }

            return -1;
        }

        /// <summary>
        /// The local player is turning the key of the vehicle with that root object. Unless someone else
        /// drives it, the vehicle is this machine's for the next moments, as if it were driven.
        /// </summary>
        public void Touch(Transform root, double now)
        {
            touchedRoot = root;
            touchedUntil = now + TouchSeconds;
        }

        /// <summary>Whether someone on another machine drives the vehicle with that root object, or holds it some other way.</summary>
        public bool HeldElsewhere(Transform root)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Root == root)
                {
                    return tracked[i].Shared.Held && tracked[i].Shared.Role == BodyRole.Follow;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether that object is a vehicle this class knows or a part of one; if it is, <paramref name="followed"/> says
        /// whether another machine simulates it (here it is a copy).
        /// </summary>
        public bool FollowedVehicleOf(Transform part, out bool followed)
        {
            followed = false;
            for (int i = 0; part != null && i < tracked.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (tracked[i].Root != null && part.IsChildOf(tracked[i].Root))
                {
                    followed = tracked[i].Following;
                    return true;
                }
            }

            return false;
        }

        /// <summary>The shared number of the vehicle with that root object; false if it has none (yet).</summary>
        public bool IdOf(Transform root, out ushort id)
        {
            id = 0;
            for (int i = 0; root != null && i < tracked.Count; i++)
            {
                if (tracked[i].Root == root && tracked[i].Shared.HasId)
                {
                    id = tracked[i].Shared.Id;
                    return true;
                }
            }

            return false;
        }

        /// <summary>The root object of the vehicle that object is a part of, or null if it is part of none this class knows.</summary>
        public Transform RootOf(Transform part)
        {
            for (int i = 0; part != null && i < tracked.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (tracked[i].Root != null && part.IsChildOf(tracked[i].Root))
                {
                    return tracked[i].Root;
                }
            }

            return null;
        }

        /// <summary>
        /// Whether a player at that place of the world is inside the vehicle with that root object: at its wheel,
        /// or in the space its data file calls its inside.
        /// </summary>
        public bool IsInside(Transform root, Vector3 position)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                if (vehicle.Root != root)
                {
                    continue;
                }

                if (vehicle == driven)
                {
                    return true;
                }

                VehicleDefinition definition = vehicle.Definition;
                if (!definition.HasInterior)
                {
                    return false;
                }

                Vector3 local = root.InverseTransformPoint(position);
                return definition.Interior.Contains(local.x, local.y, local.z, definition.ExitMargin);
            }

            return false;
        }

        /// <summary>
        /// Whether that object is a vehicle: something the game drives with its car physics. A vehicle is shared
        /// by this class, whatever else it looks like: the moped is also something the hand can pick up.
        /// </summary>
        public static bool IsVehicle(Transform candidate)
        {
            return candidate.GetComponent<CarDynamics>() != null || (candidate.parent == null && IsCatalogRoot(candidate.name));
        }

        // Whether a root of that name is a vehicle of the catalog: the boat has no car physics.
        private static bool IsCatalogRoot(string name)
        {
            VehicleDefinition[] known = Catalog;
            for (int i = 0; i < known.Length; i++)
            {
                if (known[i].RootName == name)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Once per frame: the body the local player's hand holds, or null.</summary>
        public void SetInHand(Rigidbody body)
        {
            handBody = body;
        }

        /// <summary>
        /// The "ss car" console command: how this machine has the vehicle the local player drives, rides or
        /// stands next to. It goes to the log as well.
        /// </summary>
        public string Report(Transform localPlayer)
        {
            if (localPlayer == null || current == null)
            {
                return "There is no session with a game world: nothing to report.";
            }

            Tracked vehicle = driven ?? ridden;
            float nearest = 15f * 15f;
            for (int i = 0; vehicle == null && i < tracked.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (tracked[i].Body == null)
                {
                    continue;
                }

                float distance = (tracked[i].Root.position - localPlayer.position).sqrMagnitude;
                if (distance < nearest)
                {
                    nearest = distance;
                    vehicle = tracked[i];
                }
            }

            if (vehicle == null || vehicle.Body == null)
            {
                return "No shared vehicle within 15 m.";
            }

            string report = vehicle.DescribeState(localPlayer);
            log.Info(LogCategory.Vehicle, report);
            return report;
        }

        // While the local player drives a vehicle whose physics runs here: keeps count of how it behaves.
        private void Watch()
        {
            Tracked now = driven != null && driven.Shared.HasId && driven.Shared.Role == BodyRole.Simulate ? driven : null;
            if (now != watched)
            {
                watched = now;
                if (now != null)
                {
                    // Described a moment later, once the game has finished seating the driver.
                    now.StartWatch();
                    describeAt = frameNow + 1.0;
                }
            }

            if (watched == null)
            {
                return;
            }

            if (frameNow >= describeAt)
            {
                describeAt = double.PositiveInfinity;
                log.Info(LogCategory.Vehicle, "The local player is at the wheel. " + watched.DescribeState(player));
            }

            watched.Watch(Time.deltaTime, WatchSpeed, CentredInput, WatchEvery, Aboard(watched.Shared.Id));
        }

        // How many other players this machine shows inside the vehicle with that number.
        private int Aboard(ushort vehicleId)
        {
            PlayerReplication players = manager.Players;
            if (players == null)
            {
                return 0;
            }

            int aboard = 0;
            IList<RemotePlayer> remotes = players.Remotes;
            for (int i = 0; i < remotes.Count; i++)
            {
                if (remotes[i].Visible && remotes[i].Shown.Parent == vehicleId)
                {
                    aboard++;
                }
            }

            return aboard;
        }

        /// <summary>Whether this machine runs the physics of any shared vehicle right now.</summary>
        public bool SimulatesAny
        {
            get { return simulatesAny; }
        }

        /// <summary>
        /// Whether that point of the world is within reach of a vehicle whose physics runs on this machine: in
        /// the box of its colliders or close enough to be in it a moment from now.
        /// </summary>
        public bool TouchesSimulated(Vector3 position)
        {
            if (!simulatesAny)
            {
                return false;
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (tracked[i].Body != null && tracked[i].Reaches(position))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// How fast that point of the vehicle with that number moved in the last physics step, in the world's
        /// axes. It is the same question for a vehicle this machine simulates and for one it moves to where
        /// someone else has it. Zero for a vehicle this machine does not have.
        /// </summary>
        public Vector3 VelocityAt(ushort vehicleId, Vector3 point)
        {
            Tracked vehicle = Find(vehicleId);
            return vehicle == null || vehicle.Body == null ? Vector3.zero : vehicle.VelocityAt(point);
        }

        /// <summary>
        /// The shared vehicle that a thing at that point of the world is on board of, if any: in its interior,
        /// or a little to the side of it.
        /// </summary>
        public bool TryGetCarrier(Vector3 position, out ushort vehicleId, out Transform root)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                if (vehicle.Body == null || !vehicle.Shared.HasId)
                {
                    continue;
                }

                if (!vehicle.Definition.HasInterior)
                {
                    continue;
                }

                Vector3 local = vehicle.Root.InverseTransformPoint(position);
                if (vehicle.Definition.Interior.Contains(local.x, local.y, local.z, CargoMargin, 0f))
                {
                    vehicleId = vehicle.Shared.Id;
                    root = vehicle.Root;
                    return true;
                }
            }

            vehicleId = 0;
            root = null;
            return false;
        }

        /// <summary>
        /// The vehicles of this world with a space inside (<see cref="VehicleDefinition.Interior"/>), as this machine has
        /// them: its own, or its copy of one another machine simulates. <paramref name="into"/> is emptied first.
        /// </summary>
        public void ListCabins(List<KeyValuePair<Transform, VehicleDefinition>> into)
        {
            into.Clear();
            for (int i = 0; i < tracked.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                Tracked vehicle = tracked[i];
                if (vehicle.Body != null && vehicle.Root != null && vehicle.Definition.HasInterior)
                {
                    into.Add(new KeyValuePair<Transform, VehicleDefinition>(vehicle.Root, vehicle.Definition));
                }
            }
        }

        /// <summary>This machine's copy of the vehicle with that number, or null if it has none.</summary>
        public Transform RootOf(ushort vehicleId)
        {
            Tracked vehicle = Find(vehicleId);
            return vehicle == null || vehicle.Body == null ? null : vehicle.Root;
        }

        /// <summary>
        /// Right after every physics step: the vehicle the local player rides has just moved, and takes them
        /// along.
        /// </summary>
        public void AfterPhysics()
        {
            EndKnock();
            Cruise();
            for (int i = 0; i < tracked.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (tracked[i].Body != null)
                {
                    tracked[i].RecordStep();
                }
            }

            if (ridden == null || ridden.Body == null || player == null)
            {
                return;
            }

            Transform root = ridden.Root;
            Vector3 before = player.position;
            Vector3 place = root.TransformPoint(riderPlace);
            if (place != before)
            {
                player.position = place;
                rideDistance += (place - before).magnitude;
            }

            if (carriedVelocity != null)
            {
                // What the game does for a driver: whatever the hand lets go of starts at the vehicle's speed.
                // Without it, a thing dropped by a passenger stands still in the world while the vehicle drives
                // into it (2026-10-13: a remote control thrown inside the van at speed).
                carriedVelocity.Value = ridden.VelocityAt(place);
            }

            float heading = root.eulerAngles.y;
            float turned = Mathf.DeltaAngle(riderHeading, heading);
            if (turned != 0f)
            {
                player.Rotate(0f, turned, 0f, Space.World);
                riderHeading = heading;
            }
        }

        /// <summary>
        /// Turns the local player's pose into one relative to the vehicle they drive or ride, and adds what a
        /// driver is doing with it. On foot the pose is left as it is.
        /// </summary>
        public void Localise(ref PlayerPose pose)
        {
            Tracked vehicle = null;
            bool driving = false;
            if (driven != null && driven.Body != null && driven.Shared.HasId && driven.Shared.Role == BodyRole.Simulate)
            {
                vehicle = driven;
                driving = true;
            }
            else if (seatedIn != null && seatedIn.Body != null && seatedIn.Shared.HasId)
            {
                vehicle = seatedIn;
            }
            else if (ridden != null && ridden.Body != null && ridden.Shared.HasId)
            {
                vehicle = ridden;
            }

            if (vehicle == null)
            {
                return;
            }

            // The belt and the helmet: a passenger's for the head the mod gives them, a driver's to be shown.
            pose.Seat = !driving && seatedIn == vehicle ? (byte)(seatIndex + 1) : (byte)0;
            pose.Belted = driving ? driverBelted != null && driverBelted.Value : seatedIn == vehicle && seatBelted;
            pose.Helmet = LookUpGlobals() != null && helmetWorn != null && helmetWorn.Value;

            Transform root = vehicle.Root;
            Vector3 local = root.InverseTransformPoint(new Vector3(pose.X, pose.Y, pose.Z));
            pose.X = local.x;
            pose.Y = local.y;
            pose.Z = local.z;
            pose.Yaw = Mathf.Repeat(pose.Yaw - root.eulerAngles.y, 360f);
            pose.Parent = vehicle.Shared.Id;
            pose.Driving = driving;
            if (driving)
            {
                pose.Controls = vehicle.ReadControls();
            }
        }

        /// <summary>
        /// Turns the pose of a player who rides a vehicle into one in the world, on this machine's copy of the
        /// vehicle. False if this machine does not have that vehicle: the player cannot be shown then.
        /// </summary>
        public bool ToWorld(ref PlayerPose pose)
        {
            if (pose.Parent == 0)
            {
                return true;
            }

            Tracked vehicle = Find(pose.Parent);
            if (vehicle == null || vehicle.Body == null)
            {
                return false;
            }

            Transform root = vehicle.Root;
            Vector3 world = root.TransformPoint(new Vector3(pose.X, pose.Y, pose.Z));
            pose.X = world.x;
            pose.Y = world.y;
            pose.Z = world.z;
            pose.Yaw = Mathf.Repeat(pose.Yaw + root.eulerAngles.y, 360f);
            return true;
        }

        /// <summary>
        /// A test aid, for a save in which a vehicle is someone else's and far away: gives the local player its
        /// key for this run of the game (the game's own variable, which the ignition checks) and, if this
        /// machine is the one that simulates the vehicle, parks it a few metres in front of them. Returns what
        /// it did, for the console.
        /// </summary>
        public string Summon(Transform localPlayer, string nick)
        {
            if (localPlayer == null)
            {
                return "There is no game world yet: load a save first.";
            }

            VehicleDefinition definition = null;
            var nicks = new System.Text.StringBuilder();
            for (int i = 0; i < catalog.Length; i++)
            {
                nicks.Append(i == 0 ? string.Empty : ", ").Append(catalog[i].Nick);
                if (string.Equals(catalog[i].Nick, nick, StringComparison.OrdinalIgnoreCase))
                {
                    definition = catalog[i];
                }
            }

            if (definition == null)
            {
                return "No vehicle is called '" + nick + "'. This build knows: " + nicks + ".";
            }

            string keyText;
            if (definition.KeyVariable == null)
            {
                keyText = "The " + definition.Nick + " needs no key.";
            }
            else
            {
                FsmInt key = FsmVariables.GlobalVariables.FindFsmInt(definition.KeyVariable);
                if (key == null)
                {
                    keyText = "The game has no variable '" + definition.KeyVariable + "': the key could not be handed over.";
                }
                else if (key.Value == 1)
                {
                    keyText = "You already had the key of the " + definition.Nick + ".";
                }
                else
                {
                    key.Value = 1;
                    keyText = "You have the key of the " + definition.Nick + " now.";
                }
            }

            Tracked vehicle = null;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Definition == definition && tracked[i].Body != null)
                {
                    vehicle = tracked[i];
                }
            }

            Transform root;
            Rigidbody body;
            if (current != null)
            {
                // In a session only the machine that simulates the vehicle may move it; the others are shown
                // where that one has it.
                if (vehicle == null)
                {
                    return keyText + " The " + definition.Nick + " has not been found yet; try again in a few seconds.";
                }

                if (vehicle.Shared.Held)
                {
                    return keyText + " Someone is driving the " + definition.Nick + ": it stays where it is.";
                }

                if (vehicle.Shared.Role != BodyRole.Simulate)
                {
                    return keyText + " The " + definition.Nick + " is simulated by another machine: have the host run 'ss bring " + definition.Nick + "'.";
                }

                root = vehicle.Root;
                body = vehicle.Body;
            }
            else
            {
                root = FindRoot(definition.RootName);
                body = root == null ? null : root.GetComponent<Rigidbody>();
                if (body == null)
                {
                    return keyText + " There is no '" + definition.RootName + "' in the scene to bring.";
                }
            }

            if (localPlayer.IsChildOf(root) || ridden == vehicle && vehicle != null)
            {
                return keyText + " You are in it.";
            }

            Camera view = Camera.main;
            Vector3 ahead = view != null ? view.transform.forward : localPlayer.forward;
            ahead.y = 0f;
            ahead = ahead.sqrMagnitude > 0.0001f ? ahead.normalized : Vector3.forward;
            Vector3 from = root.position;

            // Not on top of another vehicle (on 2026-10-13 a Ferndale was brought onto the moped brought eleven
            // seconds earlier): each one further from the player than the last, parked side by side.
            float distance = SummonAhead;
            CarDynamics[] cars = UnityEngine.Object.FindObjectsOfType<CarDynamics>();
            for (int attempt = 0; attempt < SummonTries && Crowded(localPlayer.position + (ahead * distance), root, cars); attempt++)
            {
                distance += SummonApart;
            }

            // Side on to the player, a little above the ground: it settles on its wheels.
            root.position = localPlayer.position + (ahead * distance) + (Vector3.up * SummonAbove);
            root.rotation = Quaternion.LookRotation(Vector3.Cross(Vector3.up, ahead), Vector3.up);
            if (!body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.WakeUp();
            }

            log.Info(
                LogCategory.Vehicle,
                "Test aid: brought '" + root.name + "' from (" + from.x.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + from.y.ToString("0.0", CultureInfo.InvariantCulture) + ", " + from.z.ToString("0.0", CultureInfo.InvariantCulture) + ") to ("
                + root.position.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " + root.position.y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + root.position.z.ToString("0.0", CultureInfo.InvariantCulture) + "). " + keyText);
            return keyText + " It is parked " + distance.ToString("0", CultureInfo.InvariantCulture)
                + " m in front of you. If you save the game now, the save keeps the key and the vehicle's new place.";
        }

        // Whether another vehicle stands at that spot or right next to it.
        private bool Crowded(Vector3 spot, Transform bringing, CarDynamics[] cars)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (tracked[i].Body != null && tracked[i].Root != bringing && tracked[i].StandsAt(spot, SummonApart * 0.5f))
                {
                    return true;
                }
            }

            for (int i = 0; i < cars.Length; i++)
            {
                Transform car = cars[i].transform;
                if (car == bringing || IsTracked(car.name))
                {
                    continue;
                }

                Vector3 apart = car.position - spot;
                apart.y = 0f;
                if (apart.sqrMagnitude < SummonApart * SummonApart)
                {
                    return true;
                }
            }

            return false;
        }

        // The game's motor takes how far the player's capsule moved in a frame for the player's speed, upwards
        // too (CharacterMotor.UpdateFunction, ILSpy). A vehicle that another machine moves is kinematic here: it
        // does not give way to the capsule. When it moves into the player, the capsule is put outside it within
        // one frame, the motor reads that as taking off, and the player flies: a hand's width at 170 frames per
        // second is 17 m/s. Seen on 2026-10-13: the guest, pushing the van while the host moved it, "flew off
        // into the sky". No jump takes off that fast, so next to such a vehicle the rise is taken away.
        private void StopThrow()
        {
            // Unity's null check is also true for a destroyed object.
            if (player.parent != null)
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

            Tracked beside = null;
            Vector3 at = player.position;
            for (int i = 0; i < tracked.Count && beside == null; i++)
            {
                if (tracked[i].Body != null && tracked[i].Following && tracked[i].StandsBeside(at, ThrowReach))
                {
                    beside = tracked[i];
                }
            }

            if (beside == null)
            {
                return;
            }

            movement.velocity.y = 0f;
            throwsStopped++;
            if (frameNow >= nextThrowLog)
            {
                nextThrowLog = frameNow + ThrowLogEvery;
                log.Info(
                    LogCategory.Passenger,
                    "'" + beside.Root.name + "', which another machine moves, pushed the local player, and the game's motor took that for a leap of "
                    + rising.ToString("0.0", CultureInfo.InvariantCulture) + " m/s upwards (its jump takes off at "
                    + takeOff.ToString("0.0", CultureInfo.InvariantCulture) + " m/s): taken away. " + throwsStopped + " time(s) so far.");
            }
        }

        /// <summary>Gives every vehicle back to the game as the game had it.</summary>
        public void ReleaseAll()
        {
            watched = null;
            handBody = null;
            stayedIn = null;
            StopOffering();
            if (seatedIn != null)
            {
                LeaveSeat(true, "the vehicles are given back to the game");
            }

            DropHeads();
            DropBelts();
            if (crashTallies != null)
            {
                crashTallies.StateWanted -= OnCrashTally;
                crashTallies = null;
            }
            if (ridden != null)
            {
                StopRiding(true, "the vehicles are given back to the game");
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Body != null)
                {
                    tracked[i].Restore();
                }

                tracked[i].DropProbe();
            }

            tracked.Clear();
            driven = null;
            simulatesAny = false;
            frameKnown = false;

            // What this machine drove goes back to the host, or it stands still in the other games.
            if (current != null)
            {
                current.ClearWorld();
            }
        }

        // Gets the local player on a vehicle they have walked into, and off one they have walked out of.
        private void UpdateRiding()
        {
            // Dead, the player is nobody aboard: what is left of them stays where they died, out of everything's way.
            if (localDead)
            {
                if (ridden != null)
                {
                    StopRiding(false, "the player died");
                }

                stayedIn = null;
                return;
            }

            if (ridden != null)
            {
                if (ridden.Body == null || !tracked.Contains(ridden))
                {
                    StopRiding(false, "the vehicle is gone");
                }
                else if (player.parent != null)
                {
                    // The game hung the player from something: the driver's seat, most likely.
                    StopRiding(false, driven == ridden ? "the player took the wheel" : "the game hung the player from '" + player.parent.name + "'");
                }
                else if (!ridden.Following)
                {
                    // Put back in their place after every step, the player's own capsule would shove a vehicle
                    // that gives way. Here they stand in it as they would in a parked one in the game alone.
                    Tracked vehicle = ridden;
                    StopRiding(false, "this machine runs its physics now");
                    stayedIn = vehicle;
                }
                else if (!Inside(ridden, ridden.Definition.ExitMargin))
                {
                    StopRiding(true, "the player left it");
                }
                else
                {
                    // Where the game's movement left them this frame: that is the place they are carried in.
                    riderPlace = ridden.Root.InverseTransformPoint(player.position);
                }
            }

            // Unity's null check is also true for a destroyed object.
            if (stayedIn != null && (stayedIn.Body == null || !tracked.Contains(stayedIn) || !Inside(stayedIn, stayedIn.Definition.ExitMargin)))
            {
                stayedIn = null;
            }

            // Only a vehicle that another machine moves needs it: one that runs its physics here is left to
            // the game. Getting on takes being well inside; someone who never got out is still on, wherever in
            // it they stand, the moment another machine moves it again.
            if (ridden == null && driven == null && player.parent == null && player.gameObject.activeInHierarchy)
            {
                for (int i = 0; i < tracked.Count; i++)
                {
                    Tracked vehicle = tracked[i];
                    float margin = vehicle == stayedIn ? vehicle.Definition.ExitMargin : 0f;
                    if (vehicle.Following && vehicle.Shared.HasId && Inside(vehicle, margin))
                    {
                        StartRiding(vehicle);
                        break;
                    }
                }
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                tracked[i].Ridden = tracked[i] == ridden;
            }
        }

        private bool Inside(Tracked vehicle, float margin)
        {
            if (!vehicle.Definition.HasInterior)
            {
                return false;
            }

            Vector3 local = vehicle.Root.InverseTransformPoint(player.position);
            return vehicle.Definition.Interior.Contains(local.x, local.y, local.z, margin);
        }

        private void StartRiding(Tracked vehicle)
        {
            ridden = vehicle;
            stayedIn = null;
            riderPlace = vehicle.Root.InverseTransformPoint(player.position);
            riderHeading = vehicle.Root.eulerAngles.y;
            rideStarted = frameNow;
            rideDistance = 0f;
            riderMotor = player.GetComponent<CharacterMotor>();
            platformWasOn = false;
            if (riderMotor != null && riderMotor.movingPlatform != null)
            {
                platformWasOn = riderMotor.movingPlatform.enabled;
                // If the game's motor carried the player along with what they stand on, they would be carried twice.
                riderMotor.movingPlatform.enabled = false;
                riderMotor.movingPlatform.activePlatform = null;
            }

            carriedVelocity = FsmVariables.GlobalVariables.FindFsmVector3(CarriedVelocityVariable);
            Vector3 local = riderPlace;
            log.Info(
                LogCategory.Passenger,
                "The local player rides '" + vehicle.Root.name + "' (vehicle " + vehicle.Shared.Id + "), at ("
                + local.x.ToString("0.00", CultureInfo.InvariantCulture) + ", " + local.y.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                + local.z.ToString("0.00", CultureInfo.InvariantCulture) + ") inside it. The game's motor: "
                + (riderMotor == null ? "NOT FOUND" : "moving platforms were " + (platformWasOn ? "on" : "off") + ", it runs in "
                + (riderMotor.useFixedUpdate ? "FixedUpdate" : "Update")) + ". "
                + (carriedVelocity == null
                    ? "The game has no variable '" + CarriedVelocityVariable + "': what the hand lets go of will not keep the vehicle's speed."
                    : "What the hand lets go of keeps the vehicle's speed."));
        }

        // steppedOff: whether the player walked or jumped out, as opposed to the ride ending some other way.
        private void StopRiding(bool steppedOff, string why)
        {
            Tracked vehicle = ridden;
            ridden = null;
            vehicle.Ridden = false;

            // Unity's null check is also true for a destroyed object.
            if (steppedOff && player != null && vehicle.Body != null && riderMotor != null && riderMotor.movement != null)
            {
                // Whoever steps off a moving vehicle goes on at its speed.
                riderMotor.movement.velocity += vehicle.Root.forward * vehicle.Speed;
            }

            if (riderMotor != null && riderMotor.movingPlatform != null)
            {
                riderMotor.movingPlatform.activePlatform = null;
                riderMotor.movingPlatform.enabled = platformWasOn;
            }

            riderMotor = null;
            if (carriedVelocity != null)
            {
                // At the wheel the game writes it itself, on every frame; on foot it is zero.
                carriedVelocity.Value = Vector3.zero;
                carriedVelocity = null;
            }

            log.Info(
                LogCategory.Passenger,
                "The local player no longer rides '" + (vehicle.Body == null ? "a vehicle" : vehicle.Root.name) + "': " + why + ". The ride lasted "
                + (frameNow - rideStarted).ToString("0.0", CultureInfo.InvariantCulture) + " s and the vehicle took them "
                + rideDistance.ToString("0.0", CultureInfo.InvariantCulture) + " m.");
        }

        /// <summary>
        /// What a driver works with in the vehicle with that number, for showing another player at its wheel; null if this
        /// machine does not have it.
        /// </summary>
        public Cockpit CockpitOf(ushort id)
        {
            Tracked vehicle = Find(id);

            // Unity's null check is also true for a destroyed object.
            return vehicle == null || vehicle.Body == null ? null : vehicle.Cockpit;
        }

        private Tracked Find(ushort id)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Shared.HasId && tracked[i].Shared.Id == id)
                {
                    return tracked[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The root object of the vehicle with that name, or null. Looked up among the scene's vehicles:
        /// GameObject.Find reads a slash as a path, and "GIFU(750/450psi)" has one.
        /// </summary>
        public static Transform FindRoot(string rootName)
        {
            Transform found = FindRoot(rootName, UnityEngine.Object.FindObjectsOfType<CarDynamics>());
            if (found != null)
            {
                return found;
            }

            // A vehicle the game keeps switched off until its story needs it (the farmer's combine, switched on by his call):
            // what each vehicle shares (doors, knobs, engine, tank) is listed once, as the session starts, the same in every
            // game, so it is listed then too. Only a root switched off by itself: an asset in memory is never that.
            CarDynamics[] all = Resources.FindObjectsOfTypeAll<CarDynamics>();
            for (int i = 0; i < all.Length; i++)
            {
                Transform car = all[i].transform;
                if (car.parent == null && car.name == rootName && !car.gameObject.activeSelf && car.gameObject.hideFlags == HideFlags.None)
                {
                    return car;
                }
            }

            return null;
        }

        private static Transform FindRoot(string rootName, CarDynamics[] cars)
        {
            for (int i = 0; i < cars.Length; i++)
            {
                Transform car = cars[i].transform;
                if (car.parent == null && car.name == rootName)
                {
                    return car;
                }
            }

            // A vehicle without the game's car physics (the boat: a body pushed by its own state machines), by its name. Not
            // a name with a slash in it, which Find reads as a path.
            if (rootName.IndexOf('/') < 0)
            {
                GameObject named = GameObject.Find("/" + rootName);
                if (named != null && named.GetComponent<CarDynamics>() == null && named.GetComponent<Rigidbody>() != null)
                {
                    return named.transform;
                }
            }

            return null;
        }

        private void Search()
        {
            if (tracked.Count >= catalog.Length)
            {
                return;
            }

            CarDynamics[] cars = UnityEngine.Object.FindObjectsOfType<CarDynamics>();
            for (int i = 0; i < catalog.Length; i++)
            {
                VehicleDefinition definition = catalog[i];
                if (IsTracked(definition.RootName))
                {
                    continue;
                }

                Transform root = FindRoot(definition.RootName, cars);
                Rigidbody body = root == null ? null : root.GetComponent<Rigidbody>();
                if (body == null)
                {
                    if (!searched && root == null)
                    {
                        // Not a fault: the game keeps some switched off until their story needs them (the farmer's combine,
                        // until his call). It is looked for again every few seconds. Not in the players' console.
                        log.Info(LogCategory.Vehicle, "No vehicle '" + definition.RootName + "' switched on in the game scene yet: it is shared when it is.");
                    }
                    else if (!searched)
                    {
                        log.Warning(LogCategory.Vehicle, "'" + definition.RootName + "' has no Rigidbody: that vehicle is not shared.");
                    }

                    continue;
                }

                var vehicle = new Tracked(definition, root, body, current.AddLocal(root.name, PoseOf(root)), log);
                tracked.Add(vehicle);
                log.Info(LogCategory.Vehicle, vehicle.Describe());
            }

            searched = true;
        }

        private bool IsTracked(string rootName)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Definition.RootName == rootName)
                {
                    return true;
                }
            }

            return false;
        }

        private void Drive(Tracked vehicle)
        {
            SharedBody shared = vehicle.Shared;
            switch (shared.Role)
            {
                case BodyRole.Simulate:
                    Simulate(vehicle, shared);
                    break;

                case BodyRole.Follow:
                    Follow(vehicle, shared);
                    break;

                default:
                    vehicle.Restore();
                    break;
            }
        }

        private void Simulate(Tracked vehicle, SharedBody shared)
        {
            bool taken = vehicle.Following;
            vehicle.Restore();
            if (taken)
            {
                Watch(vehicle, "another machine simulated it until now");
            }

            Rigidbody body = vehicle.Body;
            BodyPose pose;
            BodyMotion motion;
            if (shared.TakeHandOff(out pose, out motion))
            {
                // Someone else drove it until now: it goes on from where and how they left it.
                vehicle.Root.position = new Vector3(pose.X, pose.Y, pose.Z);
                vehicle.Root.rotation = new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW);
                if (!body.isKinematic)
                {
                    body.velocity = new Vector3(motion.VX, motion.VY, motion.VZ);
                    body.angularVelocity = new Vector3(motion.AX, motion.AY, motion.AZ);
                    body.WakeUp();
                }
            }

            motion = default(BodyMotion);
            bool awake = false;

            // Lifted on the floor jack, a vehicle rises a tenth of a millimetre a frame with next to no speed of its own
            // (the lift pushes it by moving into it): it is moving all the same, and is told every couple of millimetres
            // (seen on 2026-10-15: the guest saw the Satsuma rise only every third pump, all at once).
            Vector3 at = vehicle.Root.position;
            Quaternion turned = vehicle.Root.rotation;
            bool creeps = (at - vehicle.ToldAt).sqrMagnitude > CreepDistance * CreepDistance || Quaternion.Angle(turned, vehicle.ToldTurn) > CreepTurn;
            awake = creeps;
            if (!body.isKinematic)
            {
                Vector3 velocity = body.velocity;
                Vector3 spin = body.angularVelocity;
                awake = shared.Held || creeps || velocity.sqrMagnitude > RestSpeed * RestSpeed || spin.sqrMagnitude > RestSpin * RestSpin;
                if (awake)
                {
                    motion.VX = velocity.x;
                    motion.VY = velocity.y;
                    motion.VZ = velocity.z;
                    motion.AX = spin.x;
                    motion.AY = spin.y;
                    motion.AZ = spin.z;
                }
            }

            if (awake)
            {
                vehicle.ToldAt = at;
                vehicle.ToldTurn = turned;
            }

            if (body.isKinematic)
            {
                shared.Report(PoseOf(vehicle.Root), motion, awake);
            }
            else
            {
                // Where the physics left it, and when: the other machines place it for the time of each of their steps.
                Vector3 place = body.position;
                Quaternion turn = body.rotation;
                shared.Report(
                    new BodyPose(place.x, place.y, place.z, turn.x, turn.y, turn.z, turn.w),
                    motion,
                    awake,
                    StepTime());
            }
        }

        private void Follow(Tracked vehicle, SharedBody shared)
        {
            if (vehicle == driven)
            {
                // The local player got behind the wheel at the same moment as someone else, and the host gave
                // the vehicle to the other one.
                vehicle.Eject(true);
            }

            vehicle.StartFollowing();
            vehicle.HoldParts();
            vehicle.LockSeat(shared.Held && vehicle != driven);
            VehicleControls controls;
            bool hasDriver = DriverControls(shared.Id, out controls);
            vehicle.ShowControls(controls, hasDriver);
        }

        // In FixedUpdate: takes the vehicle to where its owner's machine has it.
        private static void Move(Tracked vehicle)
        {
            SharedBody shared = vehicle.Shared;
            if (shared.Role != BodyRole.Follow || !shared.HasTarget)
            {
                return;
            }

            // Standing where its owner left it, it is not touched again until it moves. Not by Unity either:
            // interpolation rewrites the vehicle's transform on every frame, and with it the transforms of the
            // doors below it, which are bodies with a physics of their own.
            bool atRest = shared.AtRest;
            if (atRest && vehicle.AtRest)
            {
                BodyPose rest = shared.Target;
                vehicle.KeepInPlace(new Vector3(rest.X, rest.Y, rest.Z), new Quaternion(rest.QX, rest.QY, rest.QZ, rest.QW));
                return;
            }

            BodyPose target = shared.Target;
            var position = new Vector3(target.X, target.Y, target.Z);
            var rotation = new Quaternion(target.QX, target.QY, target.QZ, target.QW);
            Rigidbody body = vehicle.Body;
            Vector3 before = body.position;
            if (atRest || !vehicle.Placed || (position - before).sqrMagnitude > TeleportDistance * TeleportDistance)
            {
                // Put there, not moved there: the first time, after a jump, and for the last step of all. With
                // interpolation still on, that last step would leave the transform at a place between two
                // physics steps for good, and the vehicle would be drawn a little off the doors that hang from
                // its body (seen on 2026-10-13: every door a touch to one side after the rear door was used).
                vehicle.Smooth(false);
                vehicle.Root.position = position;
                vehicle.Root.rotation = rotation;
                vehicle.Speed = 0f;
                vehicle.AtRest = atRest && vehicle.Placed;
                vehicle.Placed = true;
                vehicle.NoteStep(Vector3.zero);
                return;
            }

            float step = Time.fixedDeltaTime;
            Vector3 moved = position - before;
            vehicle.AtRest = false;
            if (vehicle.Jolts(moved))
            {
                // Put there by its transform, which carries the doors and the glass with it.
                vehicle.Smooth(false);
                vehicle.Root.position = position;
                vehicle.Root.rotation = rotation;
                vehicle.CarryParts(moved, step);
            }
            else
            {
                // Not while the local player rides it: they move with its body, and have to see it where its body is.
                vehicle.Smooth(!vehicle.Ridden);
                body.MovePosition(position);
                body.MoveRotation(rotation);
            }

            vehicle.NoteStep(moved);

            // How fast it goes along its own nose, for the wheels.
            if (step > 0f)
            {
                vehicle.Speed = Vector3.Dot(moved, rotation * Vector3.forward) / step;
            }
        }

        // What the player who drives that vehicle is doing with it, as this machine shows that player right now.
        // False if nobody this machine can see drives it.
        private bool DriverControls(ushort vehicleId, out VehicleControls controls)
        {
            PlayerReplication players = manager.Players;
            if (players != null)
            {
                IList<RemotePlayer> remotes = players.Remotes;
                for (int i = 0; i < remotes.Count; i++)
                {
                    RemotePlayer remote = remotes[i];
                    if (remote.Visible && remote.Shown.Driving && remote.Shown.Parent == vehicleId)
                    {
                        controls = remote.Shown.Controls;
                        return true;
                    }
                }
            }

            controls = default(VehicleControls);
            return false;
        }

        private static BodyPose PoseOf(Transform transform)
        {
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            return new BodyPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
        }

        /// <summary>One vehicle of the game world, and what the mod changed about it and has to put back.</summary>
        private sealed partial class Tracked
        {
            private readonly Logger log;
            private readonly AxisCarController controller;
            private readonly Drivetrain drivetrain;
            private readonly Wheel[] wheels;
            private Rigidbody[] parts;
            private readonly PlayMakerFSM driveMachine;
            private bool headLooked;
            private ConfigurableJoint headPattern;
            private bool forcesLooked;
            private FsmFloat headForceBase;
            private bool deathNoted;
            private Transform headPivot;
            private ConfigurableJoint headJoint;
            private Vector3 headPlace;
            private Quaternion headTurn;
            private List<KeyValuePair<PropertyInfo, object>> headSettings;
            private Transform deadBody;
            private Transform deadParent;
            private Vector3 deadPlace;
            private Quaternion deadTurn;
            private bool deadHeld;
            private bool driverBeltShown;
            private bool driverBeltLooked;
            private GameObject driverBeltOn;
            private GameObject driverBeltOff;
            private bool madeKinematic;
            private RigidbodyInterpolation interpolationBefore;
            private bool controllerOff;
            private bool drivetrainOff;
            private bool seatLocked;
            private bool ejectHooked;
            private bool ejectReported;
            private readonly Bounds shell;
            private readonly int shellColliders;
            private bool reaches;
            private float reach;
            private bool stepKnown;
            private Vector3 stepPosition;
            private Quaternion stepRotation;
            private Vector3 previousPosition;
            private Quaternion previousRotation;
            private Vector3[] partHome;
            private Quaternion[] partHomeTurn;
            private float[] loadSum;
            private float[] brakeSum;
            private float[] handbrakeSum;
            private float[] slipSum;
            private float watchSeconds;
            private float watchSpeed;
            private float watchInput;
            private float watchSteering;
            private float watchYaw;
            private float watchSideways;
            private float centredSeconds;
            private float centredYaw;
            private float centredSideways;
            private int watchAboard;

            // The hinges that hold a part of the vehicle to its body, and where on the vehicle each one is. Only
            // to measure: a hinge is never written to. Writing its anchor makes Unity build the joint again
            // around wherever the door is at that moment, open or shut, and from then on "shut" is that angle
            // (seen on 2026-10-13, in the one build that did it: a door shut at -154 degrees).
            private HingeJoint[] partHinge;
            private Vector3[] partAnchor;

            // The joints that hold its parts to it (a door's hinge and lock, a bumper's, the windscreen's), and how hard
            // each held before this machine started following the vehicle. While it follows, none breaks: the vehicle
            // is moved here by the mod, and what breaks off in a crash is the other game's to have (seen on 2026-10-15:
            // the Satsuma driven by another machine shed its doors, fenders and bumpers here). Only how hard they hold
            // is written, as the game itself does (a part's "BoltCheck" sets it from its bolts), never their anchors.
            private Joint[] partJoints = new Joint[0];
            private readonly List<World.PausedMachine> followPaused = new List<World.PausedMachine>();
            private readonly List<Rigidbody> followHeld = new List<Rigidbody>();
            private readonly List<Joint> held = new List<Joint>();
            private readonly List<float> heldForce = new List<float>();
            private readonly List<float> heldTorque = new List<float>();
            private Cockpit cockpit;
            private float nextCockpitLook;
            private bool pedalsShown;
            private int restSteps;
            private float nextPlaceLog;
            private Vector3 lastStep;
            private float nextJoltLog;
            private int jolts;
            private Vector3 creep;
            private bool creepSaid;

            public Tracked(VehicleDefinition definition, Transform root, Rigidbody body, SharedBody shared, Logger log)
            {
                Definition = definition;
                Root = root;
                Body = body;
                Shared = shared;
                this.log = log;
                shell = MeasureShell(root, out shellColliders);
                controller = root.GetComponent<AxisCarController>();
                drivetrain = root.GetComponent<Drivetrain>();
                wheels = root.GetComponentsInChildren<Wheel>(true);

                FindParts();

                loadSum = new float[wheels.Length];
                brakeSum = new float[wheels.Length];
                handbrakeSum = new float[wheels.Length];
                slipSum = new float[wheels.Length];
                // Something only towed has no drive trigger.
                Transform trigger = definition.DriveTriggerPath == null ? null : root.Find(definition.DriveTriggerPath);
                if (trigger != null)
                {
                    PlayMakerFSM[] machines = trigger.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < machines.Length; i++)
                    {
                        if (machines[i].FsmName == definition.DriveMachineName)
                        {
                            driveMachine = machines[i];
                        }
                    }
                }
            }

            /// <summary>
            /// Looks again for everything below the vehicle with a body of its own: doors, the fuel tank. The Satsuma's
            /// change while it is built (a door installed is a body below it; taken off, it is not).
            /// </summary>
            public void FindParts()
            {
                Transform root = Root;
                Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
                var others = new List<Rigidbody>(bodies.Length);
                for (int i = 0; i < bodies.Length; i++)
                {
                    if (bodies[i] != Body)
                    {
                        others.Add(bodies[i]);
                    }
                }

                parts = others.ToArray();
                partHome = new Vector3[parts.Length];
                partHomeTurn = new Quaternion[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    partHome[i] = root.InverseTransformPoint(parts[i].transform.position);
                    partHomeTurn[i] = Quaternion.Inverse(root.rotation) * parts[i].transform.rotation;
                }

                var joints = new List<Joint>();
                for (int i = 0; i < parts.Length; i++)
                {
                    joints.AddRange(parts[i].GetComponents<Joint>());
                }

                partJoints = joints.ToArray();
                if (cockpit != null)
                {
                    cockpit.Measure();
                }

                partHinge = new HingeJoint[parts.Length];
                partAnchor = new Vector3[parts.Length];
                Vector3 scale = root.lossyScale;
                bool plain = Mathf.Abs(scale.x - 1f) < 0.001f && Mathf.Abs(scale.y - 1f) < 0.001f && Mathf.Abs(scale.z - 1f) < 0.001f;
                for (int i = 0; plain && i < parts.Length; i++)
                {
                    HingeJoint hinge = parts[i].GetComponent<HingeJoint>();
                    if (hinge != null && hinge.connectedBody == Body)
                    {
                        partHinge[i] = hinge;
                        partAnchor[i] = root.InverseTransformPoint(parts[i].transform.TransformPoint(hinge.anchor));
                    }
                }
            }

            /// <summary>
            /// What its driver works with: looked for the first time it is asked for, again when its parts change, and every
            /// few seconds while it has nothing to hold (the Satsuma before its steering wheel is installed).
            /// </summary>
            public Cockpit Cockpit
            {
                get
                {
                    if (cockpit == null)
                    {
                        cockpit = Cockpit.Find(Root, Definition);
                        nextCockpitLook = Time.time + CockpitLookEvery;
                        log.Info(LogCategory.Vehicle, cockpit.Describe());
                    }
                    else if (!cockpit.Found && Time.time >= nextCockpitLook)
                    {
                        nextCockpitLook = Time.time + CockpitLookEvery;
                        cockpit.Measure();
                        if (cockpit.Found)
                        {
                            log.Info(LogCategory.Vehicle, cockpit.Describe());
                        }
                    }

                    return cockpit;
                }
            }

            /// <summary>How many parts below it have a body of their own, as last looked for.</summary>
            public int PartCount
            {
                get { return parts.Length; }
            }

            public VehicleDefinition Definition { get; private set; }

            public Transform Root { get; private set; }

            public Rigidbody Body { get; private set; }

            public SharedBody Shared { get; private set; }

            /// <summary>Whether this machine shows it where another machine says it is.</summary>
            public bool Following { get; private set; }

            /// <summary>Whether it has been put where its owner has it at least once since it started following.</summary>
            public bool Placed { get; set; }

            /// <summary>Metres per second along its own nose while following.</summary>
            public float Speed { get; set; }

            /// <summary>Whether the local player rides it as a passenger.</summary>
            public bool Ridden { get; set; }

            /// <summary>Whether it was last moved to a place where its owner says it stands still.</summary>
            public bool AtRest { get; set; }

            /// <summary>Whether its engine runs on the machine that simulates it, as the session says.</summary>
            public bool EngineRunning { get; set; }

            /// <summary>While this machine simulates it: where it was when it was last told as moving, to see it creep.</summary>
            public Vector3 ToldAt { get; set; }

            public Quaternion ToldTurn { get; set; }

            public string Describe()
            {
                Vector3 position = Root.position;
                Vector3 low = shell.min;
                Vector3 high = shell.max;
                return "Found the vehicle '" + Root.name + "' at (" + Format(position.x) + ", " + Format(position.y) + ", " + Format(position.z)
                    + "): mass " + Format(Body.mass) + ", " + (Body.isKinematic ? "kinematic" : "not kinematic") + ", interpolation " + Body.interpolation
                    + ", controller " + (controller == null ? "MISSING" : controller.enabled ? "on" : "off")
                    + ", drivetrain " + (drivetrain == null ? "MISSING" : drivetrain.enabled ? "on" : "off")
                    + ", " + wheels.Length + " wheel(s), " + parts.Length + " part(s) with a body of their own, drive trigger "
                    + (driveMachine == null ? (Definition.CanBeDriven ? "MISSING: the driver's seat cannot be locked" : "none (it is only towed)") : "in state '" + driveMachine.ActiveStateName + "'")
                    + ", " + (shellColliders == 0 ? "NO solid colliders found, a box of a guessed size is used: " : shellColliders + " solid collider(s) within ")
                    + "(" + Format(low.x) + ", " + Format(low.y) + ", " + Format(low.z) + ") to (" + Format(high.x) + ", " + Format(high.y) + ", "
                    + Format(high.z) + ") of its own coordinates.";
            }

            /// <summary>Whether that point of the world is in the box of the vehicle's colliders, or within <paramref name="margin"/> of it.</summary>
            public bool StandsBeside(Vector3 point, float margin)
            {
                Vector3 local = Root.InverseTransformPoint(point) - shell.center;
                Vector3 half = shell.extents;
                return Mathf.Abs(local.x) <= half.x + margin && Mathf.Abs(local.y) <= half.y + margin && Mathf.Abs(local.z) <= half.z + margin;
            }

            /// <summary>Whether that point of the world is over or under the vehicle, or within <paramref name="margin"/> of it to the sides.</summary>
            public bool StandsAt(Vector3 point, float margin)
            {
                Vector3 local = Root.InverseTransformPoint(point) - shell.center;
                Vector3 half = shell.extents;
                return Mathf.Abs(local.x) <= half.x + margin && Mathf.Abs(local.z) <= half.z + margin;
            }

            /// <summary>
            /// Where the game leaves a vehicle whose driver gets out of driving mode without leaving it, it adds
            /// a joint that holds the vehicle to the world, and takes it away when the player walks out or
            /// drives again (state machine "PlayerTrigger", dump of 2026-10-12). That joint holds it to the
            /// place where it was made. If someone else has driven the vehicle away meanwhile, it must not be
            /// there when the physics comes back to this machine: it would pull the vehicle back to that place.
            /// </summary>
            /// <summary>
            /// While following: whether the step the vehicle is about to take is nothing like the one before it.
            /// A hiccup of the network or of either game makes it stand for a moment and then be somewhere
            /// else. Moved there as a body, it would drag its doors and its glass along by their joints in one
            /// step of physics: the windscreen's joint breaks at 7500 N, which is less than half a metre in one
            /// step, and a door held open is torn off its lock.
            /// </summary>
            public bool Jolts(Vector3 step)
            {
                bool jolt = (step - lastStep).sqrMagnitude > MaxStepChange * MaxStepChange;
                if (jolt)
                {
                    jolts++;
                    if (Time.time >= nextJoltLog)
                    {
                        nextJoltLog = Time.time + (float)PlaceLogEvery;
                        log.Info(
                            LogCategory.Vehicle,
                            "'" + Root.name + "' was " + Format(step.magnitude) + " m from where it was a step ago, after a step of " + Format(lastStep.magnitude)
                            + " m: put there with everything that hangs from it, not dragged. " + jolts + " such step(s) since it is shown here.");
                    }
                }

                return jolt;
            }

            /// <summary>
            /// After the vehicle was put somewhere by its transform, which carries its parts: what they move at
            /// is what the vehicle moves at, as far as the slower of its last two steps tells.
            /// </summary>
            public void CarryParts(Vector3 step, float seconds)
            {
                Vector3 slower = lastStep.sqrMagnitude < step.sqrMagnitude ? lastStep : step;
                Vector3 velocity = seconds > 0f ? slower / seconds : Vector3.zero;
                for (int i = 0; i < parts.Length; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object.
                    if (part != null && !part.isKinematic && part.gameObject.activeInHierarchy)
                    {
                        part.velocity = velocity;
                    }
                }
            }

            /// <summary>Notes the step the vehicle took; a zero step after it was put somewhere new.</summary>
            public void NoteStep(Vector3 step)
            {
                lastStep = step;
            }

            /// <summary>
            /// While following, for a vehicle that stands where its owner left it: every now and then, whether
            /// it is still there, drawn and solid, and its doors on their hinges. The vehicle is put back if it
            /// is not; a door is only reported. The log says by how much each was off.
            /// </summary>
            public void KeepInPlace(Vector3 position, Quaternion rotation)
            {
                // The body creeps away from where the vehicle is drawn, a fifth of a millimetre per second on
                // the van (measured on 2026-10-13 on the machine that follows it: 2 mm every nine seconds, always
                // the same way). The game's car physics writes the body's centre of mass on every step, and on a
                // body that nothing simulates there is nothing to put it back. With the body go the doors that
                // hang from it and the places a knob is clicked at, while what is drawn stays: so it is put back
                // on every step, before it shows.
                Vector3 crept = Body.position - position;
                if (crept.sqrMagnitude > CreepTolerance * CreepTolerance)
                {
                    creep += Quaternion.Inverse(rotation) * crept;
                    Body.position = position;
                }

                restSteps++;
                if (restSteps < PlaceCheckSteps)
                {
                    return;
                }

                if (!creepSaid && creep.sqrMagnitude > 0f)
                {
                    creepSaid = true;
                    log.Info(
                        LogCategory.Vehicle,
                        "The body of '" + Root.name + "' crept " + Format(creep.magnitude * 1000f) + " mm in half a second from where the vehicle is drawn ("
                        + Format(creep.x * 1000f) + " mm to its right, " + Format(creep.y * 1000f) + " up, " + Format(creep.z * 1000f)
                        + " ahead). It is put back on every step while another machine has the vehicle.");
                }

                creep = Vector3.zero;
                restSteps = 0;
                Vector3 drawn = Root.position;
                Vector3 solid = Body.position;
                float drawnOff = (drawn - position).magnitude;
                float solidOff = (solid - position).magnitude;
                float turned = Quaternion.Angle(Root.rotation, rotation);
                bool say = Time.time >= nextPlaceLog;
                if (drawnOff > PlaceTolerance || solidOff > PlaceTolerance || turned > PlaceTurnTolerance)
                {
                    Vector3 off = Quaternion.Inverse(rotation) * ((drawnOff >= solidOff ? drawn : solid) - position);
                    Body.position = position;
                    Body.rotation = rotation;
                    Root.position = position;
                    Root.rotation = rotation;
                    if (say)
                    {
                        nextPlaceLog = Time.time + (float)PlaceLogEvery;
                        say = false;
                        log.Info(
                            LogCategory.Vehicle,
                            "'" + Root.name + "' stood off the place its owner has it at: drawn " + Format(drawnOff * 1000f) + " mm off, solid " + Format(solidOff * 1000f)
                            + " mm off, turned " + Format(turned) + " degrees (" + Format(off.x * 1000f) + " mm to its right, " + Format(off.y * 1000f) + " up, "
                            + Format(off.z * 1000f) + " ahead). Put back.");
                    }
                }

                for (int i = 0; i < partHinge.Length; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object.
                    if (partHinge[i] == null || part == null || !part.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    // The point of the door that is on its hinge stays where it is however far the door swings.
                    Vector3 hingeAt = part.transform.TransformPoint(partHinge[i].anchor);
                    Vector3 away = Root.InverseTransformPoint(hingeAt) - partAnchor[i];
                    if (away.sqrMagnitude <= HingeTolerance * HingeTolerance)
                    {
                        continue;
                    }

                    // Measured, not corrected: moving a door cannot undo what holds it wrong, and its hinge
                    // pulls it straight back.
                    if (say)
                    {
                        nextPlaceLog = Time.time + (float)PlaceLogEvery;
                        say = false;
                        log.Info(
                            LogCategory.Vehicle,
                            "The part '" + part.name + "' of '" + Root.name + "' is off its hinge: " + Format(away.x * 1000f) + " mm to the vehicle's right, "
                            + Format(away.y * 1000f) + " up, " + Format(away.z * 1000f) + " ahead, and turned "
                            + Format(Quaternion.Angle(partHomeTurn[i], Quaternion.Inverse(Root.rotation) * part.transform.rotation))
                            + " degrees from where the game put it (an open door is turned). The vehicle itself is drawn " + Format(drawnOff * 1000f)
                            + " mm and solid " + Format(solidOff * 1000f) + " mm from where its owner has it.");
                    }
                }
            }

            private void DropWorldAnchors()
            {
                FixedJoint[] joints = Root.GetComponents<FixedJoint>();
                int dropped = 0;
                for (int i = 0; i < joints.Length; i++)
                {
                    if (joints[i].connectedBody == null)
                    {
                        UnityEngine.Object.Destroy(joints[i]);
                        dropped++;
                    }
                }

                if (dropped > 0)
                {
                    log.Info(
                        LogCategory.Vehicle,
                        "Took " + dropped + " joint(s) off '" + Root.name + "' that held it to the place where the local player last got out of driving mode: "
                        + "another machine has moved it since.");
                }
            }

            /// <summary>The local player has just taken the wheel: the counts for the log start again.</summary>
            public void StartWatch()
            {
                watchSeconds = 0f;
                watchSpeed = 0f;
                watchInput = 0f;
                watchSteering = 0f;
                watchYaw = 0f;
                watchSideways = 0f;
                centredSeconds = 0f;
                centredYaw = 0f;
                centredSideways = 0f;
                watchAboard = 0;
                for (int i = 0; i < wheels.Length; i++)
                {
                    loadSum[i] = 0f;
                    brakeSum[i] = 0f;
                    handbrakeSum[i] = 0f;
                    slipSum[i] = 0f;
                }
            }

            /// <summary>
            /// Once per frame while the local player drives the vehicle and its physics runs here. Above
            /// <paramref name="minSpeed"/> it adds up what the vehicle does, and every <paramref name="every"/>
            /// seconds of that it writes the averages to the log: whether it turns or slides sideways with the
            /// wheel held straight, and what each wheel carries and brakes. A vehicle that pulls to one side
            /// shows there, with the reason or without it.
            /// </summary>
            public void Watch(float seconds, float minSpeed, float centredInput, float every, int aboard)
            {
                if (Body.isKinematic || seconds <= 0f)
                {
                    return;
                }

                Vector3 velocity = Body.velocity;
                float forward = Vector3.Dot(velocity, Root.forward);
                if (forward < minSpeed)
                {
                    return;
                }

                float input = controller != null ? controller.steerInput : 0f;
                float yaw = Vector3.Dot(Body.angularVelocity, Root.up) * Mathf.Rad2Deg;
                float sideways = Vector3.Dot(velocity, Root.right);
                watchSeconds += seconds;
                watchAboard = Mathf.Max(watchAboard, aboard);
                watchSpeed += forward * seconds;
                watchInput += input * seconds;
                watchSteering += (controller != null ? controller.steering : 0f) * seconds;
                watchYaw += yaw * seconds;
                watchSideways += sideways * seconds;
                if (Mathf.Abs(input) < centredInput)
                {
                    centredSeconds += seconds;
                    centredYaw += yaw * seconds;
                    centredSideways += sideways * seconds;
                }

                for (int i = 0; i < wheels.Length; i++)
                {
                    Wheel wheel = wheels[i];
                    if (wheel != null)
                    {
                        loadSum[i] += wheel.normalForce * seconds;
                        brakeSum[i] += wheel.brake * seconds;
                        handbrakeSum[i] += wheel.handbrake * seconds;
                        slipSum[i] += wheel.lateralSlip * seconds;
                    }
                }

                if (watchSeconds < every)
                {
                    return;
                }

                var text = new System.Text.StringBuilder(480);
                text.Append("Driving '").Append(Root.name).Append("': ").Append(Format(watchSeconds)).Append(" s at ")
                    .Append(Format(watchSpeed / watchSeconds)).Append(" m/s. To the right is positive. Steering input ")
                    .Append(Signed(watchInput / watchSeconds)).Append(", steering ").Append(Signed(watchSteering / watchSeconds))
                    .Append(", turning ").Append(Signed(watchYaw / watchSeconds)).Append(" deg/s, sliding ")
                    .Append(Signed(watchSideways / watchSeconds)).Append(" m/s sideways. ");
                if (centredSeconds > 0.5f)
                {
                    text.Append("With the wheel held straight (").Append(Format(centredSeconds)).Append(" s): turning ")
                        .Append(Signed(centredYaw / centredSeconds)).Append(" deg/s, sliding ").Append(Signed(centredSideways / centredSeconds))
                        .Append(" m/s. ");
                }
                else
                {
                    text.Append("The wheel was never held straight. ");
                }

                text.Append("Wheels");
                for (int i = 0; i < wheels.Length; i++)
                {
                    if (wheels[i] != null)
                    {
                        text.Append(i == 0 ? " " : "; ").Append(wheels[i].name).Append(" load ").Append(Format(loadSum[i] / watchSeconds))
                            .Append(" N, brake ").Append(Signed(brakeSum[i] / watchSeconds)).Append(", handbrake ")
                            .Append(Signed(handbrakeSum[i] / watchSeconds)).Append(", side slip ").Append(Signed(slipSum[i] / watchSeconds));
                    }
                }

                text.Append(". Other players aboard: ").Append(watchAboard).Append(". Out of place:");
                int displaced = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object.
                    if (part == null || !part.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    float moved = (Root.InverseTransformPoint(part.transform.position) - partHome[i]).magnitude;
                    float turned = Quaternion.Angle(partHomeTurn[i], Quaternion.Inverse(Root.rotation) * part.transform.rotation);
                    if ((moved > 0.15f || turned > 10f) && displaced < 5)
                    {
                        text.Append(displaced == 0 ? " " : ", ").Append(part.name).Append(' ').Append(Format(moved)).Append(" m and ")
                            .Append(Format(turned)).Append(" deg");
                        displaced++;
                    }
                }

                text.Append(displaced == 0 ? " nothing." : " (an open door is out of place).");
                log.Info(LogCategory.Vehicle, text.ToString());
                StartWatch();
            }

            /// <summary>
            /// How this machine has the vehicle right now, for the log: its body, what holds it, the bodies that
            /// hang from it and whether they are where they were, its wheels, and whatever else is inside the
            /// box of its colliders. Built on demand: it allocates.
            /// </summary>
            public string DescribeState(Transform localPlayer)
            {
                var text = new System.Text.StringBuilder(1200);
                Vector3 centre = Body.centerOfMass;
                text.Append('\'').Append(Root.name).Append("' as this machine has it: ")
                    .Append(Shared.Role == BodyRole.Simulate ? "its physics runs here" : Shared.Role == BodyRole.Follow ? "shown where another machine has it" : "not shared")
                    .Append(Body.isKinematic ? ", kinematic" : ", not kinematic").Append(", mass ").Append(Format(Body.mass)).Append(" kg, centre of mass (")
                    .Append(Signed(centre.x)).Append(", ").Append(Signed(centre.y)).Append(", ").Append(Signed(centre.z)).Append("), speed ")
                    .Append(Format(Body.isKinematic ? Speed : Body.velocity.magnitude)).Append(" m/s, interpolation ").Append(Body.interpolation)
                    .Append(", controller ").Append(controller == null ? "MISSING" : controller.enabled ? "on" : "off")
                    .Append(", drivetrain ").Append(drivetrain == null ? "MISSING" : drivetrain.enabled ? "on" : "off")
                    .Append(", engine at ").Append(drivetrain == null ? "?" : Format(drivetrain.rpm)).Append(" rpm (idles at ")
                    .Append(drivetrain == null ? "?" : Format(drivetrain.minRPM)).Append("), ")
                    .Append(EngineRunning ? "running" : "stopped").Append(" as the session has it. ");

                Joint[] joints = Root.GetComponents<Joint>();
                text.Append("Joints on its root: ");
                if (joints.Length == 0)
                {
                    text.Append("none");
                }

                for (int i = 0; i < joints.Length; i++)
                {
                    text.Append(i == 0 ? string.Empty : ", ").Append(joints[i].GetType().Name).Append(" to ")
                        .Append(joints[i].connectedBody == null ? "THE WORLD (it is held where it stands)" : "'" + joints[i].connectedBody.name + "'");
                }

                text.Append(". Bodies that hang from it: ");
                for (int i = 0; i < parts.Length; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object.
                    if (part == null)
                    {
                        text.Append(i == 0 ? string.Empty : "; ").Append("one destroyed");
                        continue;
                    }

                    bool on = part.gameObject.activeInHierarchy;
                    text.Append(i == 0 ? string.Empty : "; ").Append(part.name).Append(' ').Append(Format(part.mass)).Append(" kg ")
                        .Append(on ? "on" : "off");
                    if (on)
                    {
                        float moved = (Root.InverseTransformPoint(part.transform.position) - partHome[i]).magnitude;
                        float turned = Quaternion.Angle(partHomeTurn[i], Quaternion.Inverse(Root.rotation) * part.transform.rotation);
                        text.Append(", ").Append(Format(moved)).Append(" m and ").Append(Format(turned)).Append(" deg from its place, ")
                            .Append(part.GetComponent<Joint>() == null ? "NO joint" : "jointed")
                            .Append(part.isKinematic ? ", kinematic" : string.Empty);
                    }
                }

                text.Append(". Wheels: ");
                for (int i = 0; i < wheels.Length; i++)
                {
                    Wheel wheel = wheels[i];
                    if (wheel == null)
                    {
                        continue;
                    }

                    Vector3 place = Root.InverseTransformPoint(wheel.transform.position);
                    Vector3 turned = (Quaternion.Inverse(Root.rotation) * wheel.transform.rotation).eulerAngles;
                    text.Append(i == 0 ? string.Empty : "; ").Append(wheel.name).Append(" at (").Append(Signed(place.x)).Append(", ").Append(Signed(place.y))
                        .Append(", ").Append(Signed(place.z)).Append(") turned (").Append(Signed(Mathf.DeltaAngle(0f, turned.x))).Append(", ")
                        .Append(Signed(Mathf.DeltaAngle(0f, turned.y))).Append(", ").Append(Signed(Mathf.DeltaAngle(0f, turned.z))).Append("), load ")
                        .Append(Format(wheel.normalForce)).Append(" N, steering ").Append(Signed(wheel.steering)).Append(" of ").Append(Format(wheel.maxSteeringAngle))
                        .Append(" deg, brake ").Append(Signed(wheel.brake)).Append(", handbrake ").Append(Signed(wheel.handbrake)).Append(", grip ")
                        .Append(Format(wheel.forwardGripFactor)).Append('/').Append(Format(wheel.sidewaysGripFactor)).Append(", camber ").Append(Signed(wheel.camber));
                }

                text.Append(". Inside its box, not part of it: ");
                DescribeInside(text, localPlayer);
                text.Append(' ').Append(Cockpit.Describe());
                if (localPlayer.IsChildOf(Root))
                {
                    // Where the game seats a driver, as a player's pose says it: the bottom of their capsule, and the camera.
                    CharacterController capsule = localPlayer.GetComponent<CharacterController>();
                    Camera eyes = Camera.main;
                    if (capsule != null && eyes != null)
                    {
                        Vector3 feet = localPlayer.TransformPoint(capsule.center) - (Vector3.up * capsule.height * 0.5f * localPlayer.lossyScale.y);
                        Vector3 seat = Root.InverseTransformPoint(feet);
                        text.Append(" At the wheel here: feet at (").Append(Signed(seat.x)).Append(", ").Append(Signed(seat.y)).Append(", ").Append(Signed(seat.z))
                            .Append(") of its own coordinates, eyes ").Append(Format(eyes.transform.position.y - feet.y)).Append(" m above them.");
                    }
                }

                return text.ToString();
            }

            /// <summary>Appends whatever else has a body, or is a player's capsule, inside the box of the vehicle's colliders.</summary>
            public void DescribeInside(System.Text.StringBuilder text, Transform localPlayer)
            {
                // Whatever else has a body, or is a player's capsule, inside the box of the vehicle's colliders.
                Collider[] around = Physics.OverlapSphere(Root.TransformPoint(shell.center), shell.extents.magnitude);
                var seen = new List<int>();
                for (int i = 0; i < around.Length && seen.Count < 12; i++)
                {
                    Collider other = around[i];
                    if (other.isTrigger || other.transform.IsChildOf(Root))
                    {
                        continue;
                    }

                    Rigidbody body = other.attachedRigidbody;
                    bool capsule = other is CharacterController;
                    if (body == null && !capsule)
                    {
                        // The ground, a building.
                        continue;
                    }

                    Vector3 local = Root.InverseTransformPoint(other.bounds.center) - shell.center;
                    Vector3 half = shell.extents;
                    if (Mathf.Abs(local.x) > half.x + 0.3f || Mathf.Abs(local.y) > half.y + 0.3f || Mathf.Abs(local.z) > half.z + 0.3f)
                    {
                        continue;
                    }

                    int id = body != null ? body.GetInstanceID() : other.GetInstanceID();
                    if (seen.Contains(id))
                    {
                        continue;
                    }

                    text.Append(seen.Count == 0 ? string.Empty : "; ");
                    seen.Add(id);
                    Vector3 at = local + shell.center;
                    if (body != null)
                    {
                        text.Append('\'').Append(body.name).Append("' ").Append(Format(body.mass)).Append(" kg, layer ").Append(other.gameObject.layer)
                            .Append(body.isKinematic ? ", KINEMATIC" : ", gives way").Append(body.detectCollisions ? ", touches" : ", touches nothing");
                    }
                    else
                    {
                        text.Append("the capsule of '").Append(other.name).Append('\'').Append(other.transform == localPlayer ? " (the local player)" : string.Empty);
                    }

                    text.Append(" at (").Append(Signed(at.x)).Append(", ").Append(Signed(at.y)).Append(", ").Append(Signed(at.z)).Append(')');
                }

                text.Append(seen.Count == 0 ? "nothing." : ".");
            }

            private static string Signed(float value)
            {
                return value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            }

            /// <summary>
            /// Once per frame: whether this machine runs its physics, and how far beyond the box of its colliders
            /// something could still get in its way.
            /// </summary>
            public void SetReach(bool simulatedHere, float margin)
            {
                reaches = simulatedHere;
                reach = margin;
            }

            /// <summary>Whether that point of the world is in the space that <see cref="SetReach"/> describes.</summary>
            public bool Reaches(Vector3 position)
            {
                if (!reaches)
                {
                    return false;
                }

                Vector3 local = Root.InverseTransformPoint(position) - shell.center;
                Vector3 half = shell.extents;
                return Mathf.Abs(local.x) <= half.x + reach && Mathf.Abs(local.y) <= half.y + reach && Mathf.Abs(local.z) <= half.z + reach;
            }

            /// <summary>Right after every physics step: remembers where the step left the vehicle.</summary>
            public void RecordStep()
            {
                Vector3 position = Body.position;
                Quaternion rotation = Body.rotation;
                previousPosition = stepKnown ? stepPosition : position;
                previousRotation = stepKnown ? stepRotation : rotation;
                stepPosition = position;
                stepRotation = rotation;
                stepKnown = true;
            }

            /// <summary>
            /// How fast that point of the vehicle moved in the last physics step, in the world's axes: where the
            /// same point of it was one step earlier, taken from where it is now.
            /// </summary>
            public Vector3 VelocityAt(Vector3 point)
            {
                float step = Time.fixedDeltaTime;
                if (!stepKnown || step <= 0f)
                {
                    return Vector3.zero;
                }

                Vector3 onVehicle = Quaternion.Inverse(stepRotation) * (point - stepPosition);
                Vector3 before = previousPosition + (previousRotation * onVehicle);
                Vector3 now = stepPosition + (stepRotation * onVehicle);
                Vector3 velocity = (now - before) / step;
                return velocity.sqrMagnitude > MaxSteppedSpeed * MaxSteppedSpeed ? Vector3.zero : velocity;
            }

            // The box, in the vehicle's own coordinates, that holds every solid collider below it: its body, its
            // doors, whatever is bolted to it.
            private static Bounds MeasureShell(Transform root, out int counted)
            {
                Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
                Matrix4x4 toRoot = root.worldToLocalMatrix;
                var measured = default(Bounds);
                counted = 0;
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider collider = colliders[i];
                    Bounds box;
                    if (collider.isTrigger || !OwnBox(collider, out box))
                    {
                        continue;
                    }

                    Matrix4x4 toShell = toRoot * collider.transform.localToWorldMatrix;
                    Vector3 half = box.extents;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        var offset = new Vector3(
                            (corner & 1) == 0 ? -half.x : half.x, (corner & 2) == 0 ? -half.y : half.y, (corner & 4) == 0 ? -half.z : half.z);
                        Vector3 point = toShell.MultiplyPoint3x4(box.center + offset);
                        if (counted == 0 && corner == 0)
                        {
                            measured = new Bounds(point, Vector3.zero);
                        }
                        else
                        {
                            measured.Encapsulate(point);
                        }
                    }

                    counted++;
                }

                return counted == 0 ? new Bounds(new Vector3(0f, 1f, 0f), new Vector3(3f, 3f, 7f)) : measured;
            }

            // The box around a collider in the coordinates of its own object. False for a kind that has none
            // there (a wheel collider).
            private static bool OwnBox(Collider collider, out Bounds box)
            {
                var flat = collider as BoxCollider;
                if (flat != null)
                {
                    box = new Bounds(flat.center, flat.size);
                    return true;
                }

                var sphere = collider as SphereCollider;
                if (sphere != null)
                {
                    float across = sphere.radius * 2f;
                    box = new Bounds(sphere.center, new Vector3(across, across, across));
                    return true;
                }

                var capsule = collider as CapsuleCollider;
                if (capsule != null)
                {
                    float across = capsule.radius * 2f;
                    float along = Mathf.Max(capsule.height, across);
                    box = new Bounds(
                        capsule.center,
                        new Vector3(capsule.direction == 0 ? along : across, capsule.direction == 1 ? along : across, capsule.direction == 2 ? along : across));
                    return true;
                }

                var mesh = collider as MeshCollider;
                if (mesh != null && mesh.sharedMesh != null)
                {
                    box = mesh.sharedMesh.bounds;
                    return true;
                }

                box = default(Bounds);
                return false;
            }

            /// <summary>What the local player, at the wheel, is doing with it.</summary>
            public VehicleControls ReadControls()
            {
                var controls = default(VehicleControls);
                if (controller != null)
                {
                    controls.Steering = controller.steering;
                    controls.Throttle = controller.throttle;
                    controls.Brake = controller.brake;
                    controls.Clutch = Mathf.Clamp01(controller.clutchInput);
                    controls.Flags = controller.handbrakeInput > 0.1f ? VehicleControls.FlagHandbrake : (byte)0;
                }

                if (drivetrain != null)
                {
                    controls.Rpm = drivetrain.rpm;
                    controls.Gear = drivetrain.gear;
                }

                return controls;
            }

            /// <summary>
            /// While following: Unity's interpolation on while the vehicle moves, so that it is drawn between
            /// physics steps, and as the game had it while the vehicle stands.
            /// </summary>
            public void Smooth(bool moving)
            {
                RigidbodyInterpolation wanted = moving ? RigidbodyInterpolation.Interpolate : interpolationBefore;
                if (Body.interpolation != wanted)
                {
                    Body.interpolation = wanted;
                }
            }

            /// <summary>While following, in every physics step: no part of the vehicle is left asleep.</summary>
            public void KeepPartsAwake()
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object.
                    if (part != null && !part.isKinematic && part.gameObject.activeInHierarchy && part.IsSleeping())
                    {
                        part.WakeUp();
                    }
                }
            }

            /// <summary>While following, in every frame after the game's own: its parts' joints do not break.</summary>
            public void HoldParts()
            {
                for (int i = 0; i < partJoints.Length; i++)
                {
                    Joint joint = partJoints[i];

                    // Unity's null check is also true for a destroyed object.
                    if (joint == null || (joint.breakForce >= float.MaxValue && joint.breakTorque >= float.MaxValue))
                    {
                        continue;
                    }

                    if (!held.Contains(joint))
                    {
                        held.Add(joint);
                        heldForce.Add(joint.breakForce);
                        heldTorque.Add(joint.breakTorque);
                    }

                    joint.breakForce = float.MaxValue;
                    joint.breakTorque = float.MaxValue;
                }
            }

            // Back to how hard they held: this machine runs the vehicle's physics again.
            private void ReleaseParts()
            {
                for (int i = 0; i < held.Count; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    if (held[i] != null)
                    {
                        held[i].breakForce = heldForce[i];
                        held[i].breakTorque = heldTorque[i];
                    }
                }

                held.Clear();
                heldForce.Clear();
                heldTorque.Clear();
            }

            public void StartFollowing()
            {
                if (Following)
                {
                    // The game's own state machines switch a vehicle's physics back on now and then. Not while a vehicle
                    // simulated here is within reach: then it has its physics on purpose (VehiclePresenter.Contact).
                    if (!Body.isKinematic && !InContact)
                    {
                        madeKinematic = true;
                        Body.isKinematic = true;
                    }

                    // The boat's hull, which its "LOD" makes kinematic or not by the distance to this game's player.
                    if (HasHeldBodies)
                    {
                        HoldFollowKinematic();
                    }

                    // And its engine: the starter's state machine switches it on when it is taken to "running"
                    // to show what the machine that simulates the vehicle has.
                    if (drivetrain != null && drivetrain.enabled)
                    {
                        drivetrainOff = true;
                        drivetrain.enabled = false;
                    }

                    return;
                }

                Following = true;
                PauseFollowOff();
                HoldFollowKinematic();
                Placed = false;
                AtRest = false;
                Speed = 0f;
                lastStep = Vector3.zero;
                jolts = 0;
                creep = Vector3.zero;
                creepSaid = false;
                if (!Body.isKinematic)
                {
                    madeKinematic = true;
                    Body.isKinematic = true;
                }

                interpolationBefore = Body.interpolation;
                if (controller != null && controller.enabled)
                {
                    controllerOff = true;
                    controller.enabled = false;
                }

                if (drivetrain != null && drivetrain.enabled)
                {
                    drivetrainOff = true;
                    drivetrain.enabled = false;
                }
            }

            // The game's machines that move the vehicle by themselves (the boat's floating and bow), paused while another
            // machine simulates it: they would push a copy that is put where its owner has it.
            private void PauseFollowOff()
            {
                string[] off = Definition.FollowOff;
                for (int i = 0; off != null && i < off.Length; i++)
                {
                    int split = off[i].IndexOf("::", StringComparison.Ordinal);
                    string path = off[i].Substring(0, split);
                    string name = off[i].Substring(split + 2);
                    Transform at = path.Length == 0 ? Root : Root.Find(path);
                    PlayMakerFSM[] machines = at == null ? new PlayMakerFSM[0] : at.GetComponents<PlayMakerFSM>();
                    for (int m = 0; m < machines.Length; m++)
                    {
                        if (machines[m].FsmName == name && machines[m].enabled)
                        {
                            followPaused.Add(new World.PausedMachine(machines[m]));
                        }
                    }
                }
            }

            private void ResumeFollowOff()
            {
                for (int i = 0; i < followPaused.Count; i++)
                {
                    followPaused[i].Resume();
                }

                followPaused.Clear();
                for (int i = 0; i < followHeld.Count; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    if (followHeld[i] != null)
                    {
                        followHeld[i].isKinematic = false;
                        followHeld[i].WakeUp();
                    }
                }

                followHeld.Clear();
            }

            // The bodies that carry the vehicle's colliders below its root (the boat's hull), held to it while it is followed.
            private void HoldFollowKinematic()
            {
                string[] held = Definition.FollowKinematic;
                for (int i = 0; held != null && i < held.Length; i++)
                {
                    Transform at = Root.Find(held[i]);
                    Rigidbody part = at == null ? null : at.GetComponent<Rigidbody>();
                    if (part != null && !part.isKinematic)
                    {
                        part.isKinematic = true;
                        if (!followHeld.Contains(part))
                        {
                            followHeld.Add(part);
                        }
                    }
                }
            }

            /// <summary>Whether the vehicle's colliders are on bodies of its own held kinematic while followed.</summary>
            public bool HasHeldBodies
            {
                get { return Definition.FollowKinematic != null && Definition.FollowKinematic.Length > 0; }
            }

            /// <summary>
            /// For the test aid "ss drive": the controls take this throttle and steering as if from the keys, until the
            /// keys are read again in the next frame, and the gearbox goes from neutral to first. False without controls.
            /// </summary>
            public bool DriveByAid(float throttle, float steer)
            {
                if (controller == null || drivetrain == null)
                {
                    return false;
                }

                controller.throttleInput = throttle;
                controller.steerInput = steer;
                controller.brakeInput = 0f;
                if (drivetrain.gear == drivetrain.neutral && !drivetrain.changingGear)
                {
                    drivetrain.Shift(drivetrain.first);
                }

                return true;
            }

            /// <summary>
            /// Writes what the driver's machine reports into the parts of the vehicle that the game draws and
            /// sounds from: the steering, the brake lights, the engine speed and the spin of the wheels. With
            /// nobody at the wheel the engine is shown idling if it runs and stopped if it does not, and steering
            /// and gear stay as they were left.
            /// </summary>
            public void ShowControls(VehicleControls controls, bool hasDriver)
            {
                if (!hasDriver)
                {
                    controls.Steering = controller != null ? controller.steering : 0f;
                }

                if (controller != null)
                {
                    controller.steering = controls.Steering;
                    controller.throttle = controls.Throttle;
                    controller.brake = controls.Brake;
                    controller.brakeKey = controls.Brake > 0.02f;
                }

                if (drivetrain != null)
                {
                    // An engine that runs does not turn slower than it idles: with nobody at the wheel, and
                    // in the moment before the driver's first report of it running arrives.
                    drivetrain.rpm = EngineRunning && controls.Rpm < drivetrain.minRPM ? drivetrain.minRPM : controls.Rpm;
                    drivetrain.throttle = controls.Throttle;
                    if (hasDriver)
                    {
                        drivetrain.gear = controls.Gear;
                    }
                }

                for (int i = 0; i < wheels.Length; i++)
                {
                    Wheel wheel = wheels[i];
                    if (wheel == null)
                    {
                        continue;
                    }

                    wheel.steering = controls.Steering;
                    if (wheel.radius > 0.01f)
                    {
                        wheel.angularVelocity = Speed / wheel.radius;
                    }
                }

                // A driver who gets out lets go of the pedals: the Satsuma's brake and clutch keep the last angle written to them
                // until the local player drives it.
                if (hasDriver || pedalsShown)
                {
                    Cockpit.ShowPedals(hasDriver ? controls : default(VehicleControls));
                    pedalsShown = hasDriver;
                }
            }

            /// <summary>
            /// Notes the driver's head (its place and every setting of its joint) and the dead body (its place and whether a
            /// joint holds it) while they are whole, to put them back after a death the player comes back from in place.
            /// </summary>
            public void NoteDeathParts()
            {
                if (deathNoted || HeadPattern == null)
                {
                    return;
                }

                deathNoted = true;
                ConfigurableJoint joint = HeadPattern;
                headJoint = joint;
                headPivot = joint.transform;
                headPlace = headPivot.localPosition;
                headTurn = headPivot.localRotation;
                headSettings = new List<KeyValuePair<PropertyInfo, object>>();
                PropertyInfo[] settings = typeof(ConfigurableJoint).GetProperties(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < settings.Length; i++)
                {
                    PropertyInfo setting = settings[i];
                    if (!setting.CanRead || !setting.CanWrite || setting.GetIndexParameters().Length != 0 || setting.DeclaringType == typeof(Component)
                        || setting.DeclaringType == typeof(UnityEngine.Object))
                    {
                        continue;
                    }

                    switch (setting.Name)
                    {
                        case "connectedBody":
                        case "connectedAnchor":
                        case "autoConfigureConnectedAnchor":
                        case "breakForce":
                        case "breakTorque":
                        case "name":
                        case "tag":
                        case "hideFlags":
                            continue;
                    }

                    headSettings.Add(new KeyValuePair<PropertyInfo, object>(setting, setting.GetValue(joint, null)));
                }

                deadBody = Root.Find("DeadBody");
                if (deadBody != null)
                {
                    deadParent = deadBody.parent;
                    deadPlace = deadBody.localPosition;
                    deadTurn = deadBody.localRotation;
                    deadHeld = deadBody.GetComponent<FixedJoint>() != null;
                }
            }

            // Every variable of the vehicle's machines that holds the old joint ("HeadForce" keeps it in "Joint"), and every
            // property action aimed at it directly, now the new one. PlayMaker reads a property action's target again when it
            // changes (FsmProperty.CheckForReinitialize). Only the actions of machines that have loaded them: one never started
            // has nothing aimed yet.
            private void Retarget(ConfigurableJoint old, ConfigurableJoint now)
            {
                if (ReferenceEquals(old, null))
                {
                    return;
                }

                int count = 0;
                PlayMakerFSM[] machines = Root.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int m = 0; m < machines.Length; m++)
                {
                    FsmObject[] held = machines[m].FsmVariables.ObjectVariables;
                    for (int v = 0; held != null && v < held.Length; v++)
                    {
                        if (ReferenceEquals(held[v].Value, old))
                        {
                            held[v].Value = now;
                            count++;
                        }
                    }

                    FsmState[] states = machines[m].FsmStates;
                    for (int s = 0; states != null && s < states.Length; s++)
                    {
                        if (!states[s].ActionsLoaded)
                        {
                            continue;
                        }

                        FsmStateAction[] actions = states[s].Actions;
                        for (int a = 0; actions != null && a < actions.Length; a++)
                        {
                            FieldInfo[] fields = actions[a].GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                            for (int f = 0; f < fields.Length; f++)
                            {
                                FsmObject target = null;
                                if (fields[f].FieldType == typeof(FsmProperty))
                                {
                                    var property = fields[f].GetValue(actions[a]) as FsmProperty;
                                    target = property == null ? null : property.TargetObject;
                                }
                                else if (fields[f].FieldType == typeof(FsmObject))
                                {
                                    target = fields[f].GetValue(actions[a]) as FsmObject;
                                }

                                if (target != null && ReferenceEquals(target.Value, old))
                                {
                                    target.Value = now;
                                    count++;
                                }
                            }
                        }
                    }
                }

                log.Info(LogCategory.Passenger, "'" + Root.name + "': the driver's head has a new joint; " + count + " variable(s) and action(s) of the game's machines that held the old one now hold it.");
            }

            /// <summary>Puts back the driver's head and the dead body if a death broke them; true if it did.</summary>
            public bool RepairDeath()
            {
                if (!deathNoted || headPivot == null)
                {
                    return false;
                }

                bool repaired = false;
                if (headPivot.GetComponent<ConfigurableJoint>() == null)
                {
                    headPivot.localPosition = headPlace;
                    headPivot.localRotation = headTurn;
                    Rigidbody head = headPivot.GetComponent<Rigidbody>();
                    if (head != null)
                    {
                        head.velocity = Body.velocity;
                        head.angularVelocity = Vector3.zero;
                    }

                    ConfigurableJoint joint = headPivot.gameObject.AddComponent<ConfigurableJoint>();
                    for (int i = 0; i < headSettings.Count; i++)
                    {
                        headSettings[i].Key.SetValue(joint, headSettings[i].Value, null);
                    }

                    joint.autoConfigureConnectedAnchor = true;
                    joint.connectedBody = Body;
                    joint.breakForce = float.PositiveInfinity;
                    joint.breakTorque = float.PositiveInfinity;
                    headPattern = joint;

                    // What the game's machines write on the head's joint ("HeadForce" sets how much it takes, every frame at the
                    // wheel) names the joint itself: the new one in its place, or it would never break again.
                    Retarget(headJoint, joint);
                    headJoint = joint;

                    // Its "Death" watches the joint from its start, after its first ten seconds.
                    PlayMakerFSM[] machines = headPivot.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < machines.Length; i++)
                    {
                        if (machines[i].FsmName == "Death")
                        {
                            machines[i].enabled = false;
                            machines[i].enabled = true;
                        }
                    }

                    repaired = true;
                }

                // Unity's null check is also true for a destroyed object.
                if (deadBody != null && (deadBody.parent != deadParent || deadBody.gameObject.activeSelf))
                {
                    deadBody.gameObject.SetActive(false);
                    deadBody.parent = deadParent;
                    deadBody.localPosition = deadPlace;
                    deadBody.localRotation = deadTurn;
                    if (deadHeld && deadBody.GetComponent<FixedJoint>() == null)
                    {
                        deadBody.gameObject.AddComponent<FixedJoint>().connectedBody = Body;
                    }

                    repaired = true;
                }

                return repaired;
            }

            /// <summary>Whether the driver's belt was not found as a passenger's needs it: nobody in it gets one.</summary>
            public bool BeltMissing { get; set; }

            /// <summary>
            /// Shows the vehicle's own driver's belt fastened (true) or as the game left it (false), for a driver of another
            /// machine. Only what is drawn: the game's machines of the belt are not touched.
            /// </summary>
            public void ShowDriverBelt(bool on)
            {
                if (on == driverBeltShown)
                {
                    return;
                }

                if (!driverBeltLooked)
                {
                    driverBeltLooked = true;
                    VehicleSeat[] seats = Definition.Seats;
                    for (int s = 0; s < seats.Length && driverBeltOn == null; s++)
                    {
                        Transform belt = seats[s].BeltPath == null ? null : Root.Find(seats[s].BeltPath);
                        if (belt == null)
                        {
                            continue;
                        }

                        Transform fastened = belt.Find(seats[s].Belt == SeatBelt.Handle ? "Fastened_mesh" : "Close");
                        Transform loose = belt.Find(seats[s].Belt == SeatBelt.Handle ? "HandleUpPivot" : "Open");
                        driverBeltOn = fastened == null ? null : fastened.gameObject;
                        driverBeltOff = loose == null ? null : loose.gameObject;
                    }
                }

                driverBeltShown = on;

                // Unity's null check is also true for a destroyed object.
                if (driverBeltOn != null && driverBeltOff != null)
                {
                    driverBeltOn.SetActive(on);
                    driverBeltOff.SetActive(!on);
                }
            }

            /// <summary>Whether the game offers the local player driving mode in it: they stand at its wheel.</summary>
            public bool OffersDriving
            {
                get { return driveMachine != null && driveMachine.enabled && driveMachine.ActiveStateName == OfferState; }
            }

            /// <summary>The joint of the vehicle's own driver's head ("DriverHeadPivot"), switched on or not; null if it has none.</summary>
            public ConfigurableJoint HeadPattern
            {
                get
                {
                    if (!headLooked)
                    {
                        headLooked = true;
                        ConfigurableJoint[] joints = Root.GetComponentsInChildren<ConfigurableJoint>(true);
                        for (int i = 0; i < joints.Length && headPattern == null; i++)
                        {
                            if (joints[i].name == HeadPivotName)
                            {
                                headPattern = joints[i];
                            }
                        }
                    }

                    return headPattern;
                }
            }

            /// <summary>
            /// "ForceBase" of the drive trigger's "HeadForce", for the log to set beside what the vehicle's file says; null for
            /// a vehicle whose head is set by "HelmetSim".
            /// </summary>
            public FsmFloat HeadForceBase
            {
                get
                {
                    if (!forcesLooked)
                    {
                        forcesLooked = true;
                        PlayMakerFSM[] machines = driveMachine == null ? new PlayMakerFSM[0] : driveMachine.GetComponents<PlayMakerFSM>();
                        for (int i = 0; i < machines.Length; i++)
                        {
                            if (machines[i].FsmName == HeadMachine)
                            {
                                headForceBase = machines[i].FsmVariables.FindFsmFloat("ForceBase");
                            }
                        }
                    }

                    return headForceBase;
                }
            }

            /// <summary>Takes the offer of driving mode away while someone else is at the wheel, and gives it back.</summary>
            public void LockSeat(bool locked)
            {
                if (driveMachine == null || locked == seatLocked)
                {
                    return;
                }

                seatLocked = locked;
                if (locked && driveMachine.ActiveStateName == OfferState)
                {
                    // The local player stands at the door with "ENTER DRIVING MODE" on screen.
                    FsmString interaction = FsmVariables.GlobalVariables.FindFsmString("GUIinteraction");
                    FsmBool drive = FsmVariables.GlobalVariables.FindFsmBool("GUIdrive");
                    if (interaction != null)
                    {
                        interaction.Value = string.Empty;
                    }

                    if (drive != null)
                    {
                        drive.Value = false;
                    }
                }

                driveMachine.enabled = !locked;
                log.Info(
                    LogCategory.Vehicle,
                    locked
                        ? "Someone else drives '" + Root.name + "': its driver's seat is not offered here."
                        : "Nobody else drives '" + Root.name + "' now: its driver's seat is offered again.");
            }

            /// <summary>
            /// Makes the game take the local player out of driving mode. <paramref name="contested"/>: because another player
            /// got the wheel (said once in the log as a warning).
            /// </summary>
            public void Eject(bool contested)
            {
                if (driveMachine == null)
                {
                    return;
                }

                try
                {
                    if (!ejectHooked)
                    {
                        ejectHooked = true;
                        driveMachine.AddGlobalTransition(EjectEvent, EjectState);
                    }

                    driveMachine.SendEvent(EjectEvent);
                }
                catch (Exception exception)
                {
                    if (!ejectReported)
                    {
                        log.Warning(LogCategory.Vehicle, "Could not take the local player out of '" + Root.name + "': " + exception.Message);
                    }
                }

                if (contested && !ejectReported)
                {
                    ejectReported = true;
                    log.Warning(LogCategory.Vehicle, "Two players got behind the wheel of '" + Root.name + "' at once; the host gave it to the other one.");
                }
            }

            /// <summary>Back to how the game had it: its own physics, its own controls, its seat on offer.</summary>
            public void Restore()
            {
                if (!Following && !seatLocked)
                {
                    return;
                }

                if (seatLocked)
                {
                    seatLocked = false;
                    if (driveMachine != null)
                    {
                        driveMachine.enabled = true;
                    }
                }

                if (!Following)
                {
                    return;
                }

                Following = false;
                ResumeFollowOff();
                ForgetContact();
                ejectReported = false;
                ReleaseParts();
                DropWorldAnchors();
                Body.interpolation = interpolationBefore;
                if (madeKinematic)
                {
                    madeKinematic = false;
                    Body.isKinematic = false;
                    Body.WakeUp();
                }

                if (controllerOff)
                {
                    controllerOff = false;
                    if (controller != null)
                    {
                        controller.enabled = true;
                    }
                }

                if (drivetrainOff)
                {
                    drivetrainOff = false;
                    if (drivetrain != null)
                    {
                        drivetrain.enabled = true;
                    }
                }

                if (EngineRunning && drivetrain != null && drivetrain.enabled)
                {
                    // Its engine ran on the machine that simulated it until now. This machine's copy of the
                    // engine stood still all that time, and with the game's physics an engine that stands
                    // still has stalled: it is given the turn the game gives one that has just been started.
                    drivetrain.StartEngine();
                    log.Info(LogCategory.Vehicle, "The engine of '" + Root.name + "' was running where the vehicle was simulated until now: it goes on running here.");
                }
            }

            private static string Format(float value)
            {
                return value.ToString("0.#", CultureInfo.InvariantCulture);
            }
        }
    }
}
