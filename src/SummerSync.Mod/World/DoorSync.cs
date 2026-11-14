using System;
using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Keeps the doors of the house, and of the other buildings, open or closed alike for every player.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md): such a door is a
    /// PlayMaker state machine called "Use" on the door's handle, with a boolean "DoorOpen" and the states
    /// "Open door" and "Close door", which play the animation and the sound and set the boolean. The mod reads
    /// the boolean to notice the local player using a door, and to make the game show a door used by someone
    /// else it sends the state machine an event of its own that leads straight to one of those two states.
    /// The event is added through MSCLoader; the game's own way through the machine is left as it is.
    ///
    /// The mailbox's hatch keeps no boolean (dump of 2026-10-15): it is open while its machine is in one of the states it
    /// is open in, and it is opened and closed for someone else through its own "Open" and "Close", which play the
    /// animation and switch the envelopes' machines on and off.
    ///
    /// What is switched on and off in the house with a click is shared the same way (dump of 2026-10-12): the
    /// eight light switches, the kitchen tap, the bathroom's water valve and the switch that sends its water to
    /// the shower or the tap, the television and the computer's power button each
    /// keep a boolean and have a state that turns it over and does what goes with it (the lamps, the water, the
    /// picture). Both events lead to that state, and it is only sent when the boolean is not what it should be.
    ///
    /// The other buildings have doors made the same way (dump of 2026-10-12): Teimo's shop and its pub, the
    /// cabin, the cottage, the dance hall, the flats in Peräjärvi, the repair shop, the inspection and the water
    /// facility. Their roots are there, switched on, wherever the player is; many of the doors hang from a "LOD"
    /// object that the game switches off far away, and one that is told to move while switched off moves, with
    /// its animation, when it is switched on again.
    ///
    /// The doors of the van are shared the same way (dump of 2026-10-12): a machine "Use" with a boolean "Open".
    /// Three of them hang from a hinge and are pushed open by a force for as long as the mouse button is held;
    /// to open one for someone else the machine goes to that same state, and to close it, to the state that
    /// plays the sound and then shuts it. Where the door ends up swinging is each machine's own physics.
    ///
    /// The headlights of a vehicle are shared here too (dump of 2026-10-12), as up to three switches: whether
    /// the vehicle has electricity, which is an object the game switches on with the ignition and from which the
    /// lamps hang, and the two positions of the light switch, a state machine with an integer "Selection" and
    /// the states "Off", "Shorts" and "Longs". The machine that simulates a vehicle is the one whose lights
    /// count: it reports them, and the others show them. The ignition and the engine come after the lamps in
    /// the same list, and <see cref="IgnitionSync"/> looks after them; after those, the knobs on the
    /// dashboard, with <see cref="KnobSync"/>.
    ///
    /// The Satsuma's doors, hood, boot lid, glovebox and folding seats are parts that are installed on it and taken
    /// off again (dump of 2026-10-14: a machine "Use" with a boolean "Open" on "door left(Clone)", "hood(Clone)"…).
    /// They are listed by the part, whether it is on the car or not, so that the list is the same on every machine
    /// while the car is being built: the part is the object the game's record of it names ("Database/…" machine
    /// "Data", <see cref="SatsumaParts"/>). Taken off, the game switches their machine off and nothing is shown.
    /// Its light switch is on the instrument panel, another such part, found by name (<see cref="PartFinder"/>).
    /// </summary>
    internal sealed partial class DoorSync
    {
        private const string HouseRoot = "/YARD";

        // The other buildings whose doors are shared, besides the house. The same on every machine: the list of
        // doors must be.
        // What is switched with a click in the house: where it is (a path under the house, or the start of one), its
        // state machine, its boolean, and the state that turns it over.
        private static readonly string[][] HouseToggles =
        {
            new[] { "Building/Dynamics/LightSwitches/", "Use", "Switch", "Switch" },
            new[] { "Building/KITCHEN/KitchenWaterTap/Trigger", "Use", "SwitchOn", "Position" },
            new[] { "Building/BATHROOM/Shower/Valve", "Switch", "Valve", "Position" },
            new[] { "Building/BATHROOM/Shower/Switch", "Use", "ShowerSwitch", "Position" },
            new[] { "Building/LIVINGROOM/TV/Switch", "Use", "Open", "Switch" },
            new[] { "Building/BEDROOM1/COMPUTER/Computer/Meshes/386_case(xxxxx)/ButtonPower", "Use", "PowerOn", "Press" },
        };

        // What opens and closes with a click and keeps no boolean of its own (dump of 2026-10-15): where it is under the
        // house, its state machine, what the log calls it, the state that opens it, the one that closes it, and then every
        // state it is open in. The mailbox's hatch: "Open" plays its animation and goes on to "State 4", which switches on
        // the envelopes' machines, and waits in "State 3" and "State 5"; "Close" plays the other and "State 6" switches
        // them off.
        private static readonly string[][] HouseStateDoors =
        {
            new[] { "PlayerMailBox/Hatch/Pivot/Use", "Use", "mailbox hatch", "Open", "Close", "Open", "State 4", "State 3", "State 5" },
        };

        // The same, in the store: the switches behind the counter (the pumps, the door).
        private static readonly string[][] StoreToggles =
        {
            new[] { "LOD/ShopFunctions/Switches/", "Use", "SwitchOn", "Position" },
        };

        private static readonly string[] BuildingRoots = { "/STORE", "/CABIN", "/COTTAGE", "/DANCEHALL", "/PERAJARVI", "/REPAIRSHOP", "/INSPECTION", "/WATERFACILITY" };
        private const string MachineName = "Use";
        private const string OpenVariable = "DoorOpen";
        private const string OpenState = "Open door";
        private const string CloseState = "Close door";
        private const string VehicleOpenVariable = "Open";
        private const string SelectionVariable = "Selection";
        private const string PowerMachine = "Power";

        // The states of a light switch by what "Selection" is in each, and the events of the mod's that lead there.
        private static readonly string[] LightStates = { "Off", "Shorts", "Longs" };
        private static readonly string[] LightEvents = { "SUMMERSYNC LIGHTS OFF", "SUMMERSYNC LIGHTS SHORTS", "SUMMERSYNC LIGHTS LONGS" };

        // A light switch is turned for someone else a moment after the news, so that two positions that change
        // together are one turn; and again after a while if the game did not follow.
        private const double LightSettleSeconds = 0.15;
        private const double LightRetrySeconds = 2.0;

        // The state that opens a vehicle's door, and the one that starts closing it: the first that exists.
        private static readonly string[] VehicleOpenStates = { "Open door", "Open hood", "Open hood 2" };
        private static readonly string[] VehicleCloseStates = { "Sound", "Close door", "Close hood" };
        private const string OpenEvent = "SUMMERSYNC OPEN";
        private const string CloseEvent = "SUMMERSYNC CLOSE";
        private const double SearchInterval = 3.0;
        private const double FirstSearchDelay = 1.5;
        private const double FollowSeconds = 2.0;

        // How long after a hinged door was told to open or close the log says how it ended up.
        private const double SettleSeconds = 3.0;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Door> doors = new List<Door>();
        private readonly List<Lamp> lamps = new List<Lamp>();
        private readonly VehiclePresenter vehicles;
        private readonly IgnitionSync engines;
        private readonly KnobSync knobs;
        private readonly HornSync horns;
        private readonly TowSync tows;
        private readonly PartFinder parts = new PartFinder();
        private SwitchReplication current;
        private Transform player;
        private bool found;
        private bool missingReported;
        private double nextSearch;

        public DoorSync(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
            engines = new IgnitionSync(log, vehicles);
            knobs = new KnobSync(log, parts, vehicles);
            horns = new HornSync(log, vehicles);
            tows = new TowSync(log, manager, vehicles);
        }

        /// <summary>
        /// Once per frame. <paramref name="localPlayer"/> is the player's root object, or null while there is
        /// no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            checkNow = now;
            SwitchReplication switches = localPlayer == null ? null : manager.Switches;
            if (switches != current || localPlayer != player)
            {
                if (current != null)
                {
                    current.ClearWorld();
                }

                current = switches;
                player = localPlayer;
                doors.Clear();
                lamps.Clear();
                engines.Clear();
                knobs.Clear();
                horns.Clear();
                tows.Clear();
                parts.Clear();
                found = false;
                nextSearch = now + FirstSearchDelay;
            }

            if (current == null)
            {
                return;
            }

            if (!found)
            {
                if (now < nextSearch)
                {
                    return;
                }

                nextSearch = now + SearchInterval;
                if (!Find())
                {
                    return;
                }
            }

            int index;
            bool open;
            while (current.TryTakeChange(out index, out open))
            {
                if (index >= 0 && index < doors.Count)
                {
                    Show(doors[index], open, now);
                }
            }

            FrameMeter.Mark("doors: before");
            for (int i = 0; i < doors.Count; i++)
            {
                Watch(doors[i], i, now);
            }

            FrameMeter.Mark("doors: Watch");

            // The lamps come after the doors in the shared list. What they are told is not taken from the
            // queue of changes: every frame each is compared with what the session says.
            for (int i = 0; i < lamps.Count; i++)
            {
                WatchLamp(lamps[i], now);
            }

            FrameMeter.Mark("doors: WatchLamp");
            engines.Watch(current, now);
            FrameMeter.Mark("doors: engines");
            knobs.Watch(current, now);
            FrameMeter.Mark("doors: knobs");
            horns.Watch(current, now);
            FrameMeter.Mark("doors: horns");
            tows.Watch(current, now);
            FrameMeter.Mark("doors: tows");
        }

        /// <summary>
        /// The "ss doors" console command (a test aid): writes the shared list of doors and switches, one to a line with its
        /// state, for a program that has to agree with it (netprobe --switches-load).
        /// </summary>
        public string WriteList(string path)
        {
            if (current == null || !found)
            {
                return "The doors are not known yet (no game world, or still looking).";
            }

            var lines = new List<string>(current.Count);
            for (int i = 0; i < current.Count; i++)
            {
                lines.Add(current.NameOf(i) + "\t" + (current.Get(i) ? "1" : "0"));
            }

            System.IO.File.WriteAllLines(path, lines.ToArray());
            return current.Count + " door(s) and switch(es) written to " + path + ".";
        }

        /// <summary>
        /// How many doors of the vehicle with that root object are known here, and whether any of them is open:
        /// for whoever wants to know if what happens inside it can be heard outside.
        /// </summary>
        public int CountDoors(Transform vehicle, out bool anyOpen)
        {
            int count = 0;
            anyOpen = false;
            for (int i = 0; i < doors.Count; i++)
            {
                Door door = doors[i];

                // Unity's null check is also true for a destroyed object.
                if (door.Vehicle == vehicle && door.Machine != null && (door.Part == null || door.Machine.transform.IsChildOf(vehicle)))
                {
                    count++;
                    anyOpen |= door.Open.Value;
                }
            }

            return count;
        }

        private void WatchLamp(Lamp lamp, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (lamp.Root == null)
            {
                return;
            }

            // A lamp the host's world does not have is this game's alone, as if this machine simulated its vehicle.
            bool simulates = vehicles.SimulatesRoot(lamp.Root) || current.IsOnlyHere(lamp.Index);
            bool shared = current.Get(lamp.Index);
            if (lamp.Power != null)
            {
                if (simulates)
                {
                    if (lamp.Forced)
                    {
                        // This machine was showing someone else's lights and left the object as they had it.
                        // Now the vehicle is its own again: back to what its own ignition says.
                        lamp.Forced = false;
                        if (lamp.Acc != null && lamp.Motor != null)
                        {
                            bool own = lamp.Acc.Value || lamp.Motor.Value;
                            if (lamp.Power.activeSelf != own)
                            {
                                lamp.Power.SetActive(own);
                            }
                        }
                    }

                    bool real = lamp.Power.activeSelf;
                    if (real != shared)
                    {
                        current.SetLocal(lamp.Index, real);
                    }
                }
                else if (lamp.Power.activeSelf != shared)
                {
                    lamp.Power.SetActive(shared);
                    lamp.Forced = true;
                }

                return;
            }

            if (lamp.Switch.Under != null && !Attached(lamp.Switch, now))
            {
                return;
            }

            int selection = lamp.Switch.Selection.Value;
            bool value = lamp.Long ? selection >= 2 : selection >= 1;
            if (value == shared)
            {
                return;
            }

            if (simulates)
            {
                current.SetLocal(lamp.Index, value);
                return;
            }

            Turn(lamp.Switch, now);
        }

        // For a light switch on a part: whether the part is on the vehicle, found again if it was put back.
        private bool Attached(LightSwitch lights, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (lights.Machine != null && lights.Part != null && lights.Part.IsChildOf(lights.Root))
            {
                return true;
            }

            if (lights.Broken || lights.Root == null)
            {
                return false;
            }

            if (lights.Machine != null)
            {
                lights.Machine = null;
                lights.Selection = null;
                log.Info(LogCategory.Fsm, "The part with the light switch '" + lights.Label + "' came off: the lights are not turned until it is back.");
            }

            Transform part = parts.Find(lights.Root, lights.Under, now);
            if (part == null)
            {
                return false;
            }

            Transform knob = part.Find(lights.Path);
            PlayMakerFSM use = knob == null ? null : FindMachine(knob, lights.MachineName);
            FsmInt selection = use == null ? null : use.FsmVariables.FindFsmInt(SelectionVariable);
            if (selection == null || !HasState(use, LightStates[0]) || !HasState(use, LightStates[1]))
            {
                lights.Broken = true;
                log.Warning(LogCategory.Fsm, "The light switch '" + lights.Label + "' is not as expected on its part: its lights are not shared any more in this session.");
                return false;
            }

            lights.Part = part;
            lights.Machine = use;
            lights.Selection = selection;
            lights.Hooked = false;
            lights.TurnAt = 0.0;
            lights.Tries = 0;
            log.Info(LogCategory.Fsm, "The part with the light switch '" + lights.Label + "' is on the vehicle: the lights are shared there (at " + selection.Value + " here).");
            return true;
        }

        // Turns a light switch to what the machine that simulates the vehicle has it at.
        private void Turn(LightSwitch lights, double now)
        {
            PlayMakerFSM machine = lights.Machine;
            if (machine == null || !machine.enabled || !machine.gameObject.activeInHierarchy)
            {
                // The game has switched this part of the vehicle off: it is far away. It is turned when it is back.
                lights.TurnAt = 0.0;
                return;
            }

            if (lights.TurnAt == 0.0)
            {
                lights.TurnAt = now + LightSettleSeconds;
                return;
            }

            if (now < lights.TurnAt)
            {
                return;
            }

            bool dipped = current.Get(lights.ShortIndex);
            bool full = lights.LongIndex >= 0 && current.Get(lights.LongIndex);
            int target = full ? 2 : dipped ? 1 : 0;
            lights.TurnAt = now + LightRetrySeconds;
            try
            {
                if (!lights.Hooked)
                {
                    lights.Hooked = true;
                    for (int i = 0; i < LightStates.Length; i++)
                    {
                        if (HasState(machine, LightStates[i]) && !HasGlobalTransition(machine, LightEvents[i]))
                        {
                            machine.AddGlobalTransition(LightEvents[i], LightStates[i]);
                        }
                    }

                    // MSCLoader rebuilds the machine's data when a transition is added; the variable may be a new object.
                    FsmInt again = machine.FsmVariables.FindFsmInt(SelectionVariable);
                    if (again != null)
                    {
                        lights.Selection = again;
                    }
                }

                machine.SendEvent(LightEvents[target]);
            }
            catch (Exception exception)
            {
                if (!lights.Complained)
                {
                    lights.Complained = true;
                    log.Warning(LogCategory.Fsm, "Could not turn the light switch '" + lights.Label + "': " + exception.Message);
                }

                return;
            }

            lights.Tries++;
            if (lights.Tries == 4 && !lights.Complained)
            {
                lights.Complained = true;
                log.Warning(
                    LogCategory.Fsm,
                    "The light switch '" + lights.Label + "' does not go to " + LightStates[target] + " when told to (state '" + machine.ActiveStateName
                    + "', Selection " + lights.Selection.Value + ").");
            }
        }

        // The lamps of the vehicles that are in the scene, appended to the names and states of the doors.
        private void AddLamps(List<string> names, List<bool> states)
        {
            VehicleDefinition[] catalogue = VehiclePresenter.Catalog;
            var sorted = new List<VehicleDefinition>(catalogue);
            sorted.Sort(delegate(VehicleDefinition a, VehicleDefinition b) { return string.CompareOrdinal(a.RootName, b.RootName); });
            var found = new StringBuilder();
            for (int v = 0; v < sorted.Count; v++)
            {
                VehicleDefinition definition = sorted[v];
                Transform root = VehiclePresenter.FindRoot(definition.RootName);
                if (root == null)
                {
                    continue;
                }

                string what = ShortName(definition.RootName);
                Transform power = definition.PowerPath == null ? null : root.Find(definition.PowerPath);
                if (power != null)
                {
                    var lamp = new Lamp { Root = root, Power = power.gameObject, Index = names.Count };
                    PlayMakerFSM machine = power.parent == null ? null : FindMachine(power.parent, PowerMachine);
                    if (machine != null)
                    {
                        lamp.Acc = machine.FsmVariables.FindFsmBool("ACC");
                        lamp.Motor = machine.FsmVariables.FindFsmBool("Motor");
                    }

                    lamps.Add(lamp);
                    names.Add(definition.RootName + "/" + definition.PowerPath + "#power");
                    states.Add(power.gameObject.activeSelf);
                    found.Append(found.Length == 0 ? string.Empty : "; ").Append(what).Append(" electricity ").Append(power.gameObject.activeSelf ? "on" : "off")
                        .Append(lamp.Acc != null && lamp.Motor != null ? string.Empty : " (no ignition to read)");
                }

                // On a part (the Satsuma's instrument panel), the switch is always listed, installed or not.
                bool onPart = definition.LightSwitchUnder != null && definition.LightSwitchPath != null;
                Transform holder = onPart ? PartFinder.Below(root, definition.LightSwitchUnder) : root;
                Transform knob = definition.LightSwitchPath == null || holder == null ? null : holder.Find(definition.LightSwitchPath);
                PlayMakerFSM use = knob == null ? null : FindMachine(knob, definition.LightSwitchMachine);
                FsmInt selection = use == null ? null : use.FsmVariables.FindFsmInt(SelectionVariable);
                bool broken = false;
                if (selection == null || !HasState(use, LightStates[0]) || !HasState(use, LightStates[1]))
                {
                    if (definition.LightSwitchPath != null && (!onPart || holder != null))
                    {
                        log.Warning(LogCategory.Fsm, "The light switch of '" + definition.RootName + "' is not at '" + definition.LightSwitchPath + "' as expected: its lights are not shared.");
                    }

                    if (!onPart)
                    {
                        continue;
                    }

                    broken = holder != null;
                    use = null;
                    selection = null;
                }

                var lights = new LightSwitch
                {
                    Machine = use, Selection = selection, Label = what + " " + definition.LightSwitchPath, ShortIndex = names.Count, LongIndex = -1,
                    Root = root, Part = holder, Under = onPart ? definition.LightSwitchUnder : null, Path = definition.LightSwitchPath,
                    MachineName = definition.LightSwitchMachine, Broken = broken,
                };
                string listed = definition.RootName + "/" + (onPart ? definition.LightSwitchUnder + "/" : string.Empty) + definition.LightSwitchPath;
                lamps.Add(new Lamp { Root = root, Switch = lights, Index = names.Count });
                names.Add(listed + "#short");
                states.Add(selection != null && selection.Value >= 1);
                if (onPart || HasState(use, LightStates[2]))
                {
                    lights.LongIndex = names.Count;
                    lamps.Add(new Lamp { Root = root, Switch = lights, Long = true, Index = names.Count });
                    names.Add(listed + "#long");
                    states.Add(selection != null && selection.Value >= 2);
                }

                found.Append(found.Length == 0 ? string.Empty : "; ").Append(what)
                    .Append(selection == null ? " lights (their switch's part is not installed)" : " lights at " + selection.Value)
                    .Append(lights.LongIndex >= 0 ? string.Empty : " (no full beam)");
            }

            log.Info(LogCategory.Fsm, "Lamps shared: " + (found.Length == 0 ? "none" : found.ToString()) + ".");
        }

        internal static PlayMakerFSM FindMachine(Transform on, string name)
        {
            PlayMakerFSM[] machines = on.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == name)
                {
                    return machines[i];
                }
            }

            return null;
        }

        // Someone else used the door: make the game show it.
        private void Show(Door door, bool open, double now)
        {
            if (door.Machine == null)
            {
                return;
            }

            door.Wanted = open;
            door.Following = true;
            door.Sent = false;
            door.Deadline = now + FollowSeconds;
        }

        private void Watch(Door door, int index, double now)
        {
            if (door.Machine == null)
            {
                return;
            }

            if (door.Part != null && door.Vehicle != null)
            {
                // The game gives an installed door its hinge, and takes it away with the door.
                door.Hinge = door.Machine.transform.IsChildOf(door.Vehicle) ? HingeAbove(door.Machine.transform, door.Vehicle) : null;
            }

            if (door.ReportAt > 0.0 && now >= door.ReportAt)
            {
                door.ReportAt = 0.0;
                ReportSettled(door);
            }

            if (door.OpenStates != null)
            {
                // Without a boolean of its own, it is open in the states it is open in.
                door.Open.Value = door.OpenByState();
            }

            bool open = door.Open.Value;
            if (!door.Following)
            {
                // The local player used it, or the game did.
                if (open != current.Get(index))
                {
                    current.SetLocal(index, open);
                    if (door.Hinge != null)
                    {
                        door.ReportAt = now + SettleSeconds;
                        door.ReportWhat = open ? "the local player opened it" : "the local player closed it";
                    }
                }

                return;
            }

            if (open == door.Wanted)
            {
                door.Following = false;
                return;
            }

            bool awake = door.Machine.enabled && door.Machine.gameObject.activeInHierarchy;
            if (!awake)
            {
                door.Deadline = now + FollowSeconds;
                if (door.Hinge == null)
                {
                    // A door with an animation, out of sight and switched off by the game (a shop's, far away): it
                    // keeps what it shows until it is switched on again, and then it is shown opening or closing.
                    return;
                }

                // Out of sight and switched off by the game: nothing can be played. The state is set, and the door
                // may look wrong until it is used again.
                door.Open.Value = door.Wanted;
                return;
            }

            if (!door.Sent)
            {
                door.Sent = true;
                door.Deadline = now + FollowSeconds;
                Loosen(door, now);
                door.Machine.SendEvent(door.Wanted ? OpenEvent : CloseEvent);
                return;
            }

            if (now > door.Deadline)
            {
                // The game did not follow. The shared state stands; what this player sees may not match it.
                door.Following = false;
                if (door.OpenStates == null)
                {
                    door.Open.Value = door.Wanted;
                }

                if (!door.Complained)
                {
                    door.Complained = true;
                    log.Warning(
                        LogCategory.Fsm,
                        "The door '" + door.Name + "' did not " + (door.Wanted ? "open" : "close") + " when told to (state '"
                        + door.Machine.ActiveStateName + "'). " + (door.OpenStates == null ? "Its state was set without the animation." : "It stays as it is here."));
                }
            }
        }

        // Looks for the doors of the house. False while the house is not there to be searched.
        private bool Find()
        {
            GameObject house = GameObject.Find(HouseRoot);
            if (house == null)
            {
                if (!missingReported)
                {
                    missingReported = true;
                    log.Info(LogCategory.Fsm, "No active object '" + HouseRoot + "' yet: the doors of the house are shared once it is there.");
                }

                return false;
            }

            var candidates = new List<Door>();
            int machineCount = AddBuildingDoors(house, candidates);
            int toggles = AddToggles(house, HouseToggles, candidates) + AddStateDoors(house, HouseStateDoors, candidates);
            int houseDoors = candidates.Count;
            var elsewhere = new StringBuilder();
            for (int r = 0; r < BuildingRoots.Length; r++)
            {
                GameObject building = GameObject.Find(BuildingRoots[r]);
                if (building == null)
                {
                    log.Warning(LogCategory.Fsm, "No active object '" + BuildingRoots[r] + "': its doors are not shared. If the other machine has it, no door is shared at all.");
                    continue;
                }

                int before = candidates.Count;
                machineCount += AddBuildingDoors(building, candidates);
                if (BuildingRoots[r] == "/STORE")
                {
                    AddToggles(building, StoreToggles, candidates);
                }

                elsewhere.Append(elsewhere.Length == 0 ? string.Empty : ", ").Append(BuildingRoots[r].Substring(1)).Append(' ').Append(candidates.Count - before);
            }

            int buildingDoors = candidates.Count;
            // The vehicles the build has a data file for, in the order of their names: the same on every machine.
            VehicleDefinition[] vehicles = VehiclePresenter.Catalog;
            var roots = new List<string>(vehicles.Length);
            for (int v = 0; v < vehicles.Length; v++)
            {
                roots.Add(vehicles[v].RootName);
            }

            roots.Sort(string.CompareOrdinal);
            HashSet<Transform> partObjects = PartObjects();
            for (int v = 0; v < roots.Count; v++)
            {
                AddVehicleDoors(roots[v], candidates, partObjects);
            }

            int partDoors = AddPartDoors(candidates, partObjects);

            // The same order on every machine: the names say where each door sits in the scene, and the scene
            // is the same for everyone.
            candidates.Sort(delegate(Door a, Door b) { return string.CompareOrdinal(a.Name, b.Name); });

            int disturbed = 0;
            var names = new List<string>(candidates.Count);
            var states = new List<bool>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                Door door = candidates[i];
                if (!Hook(door))
                {
                    disturbed++;
                }

                names.Add(door.Name);
                states.Add(door.Open.Value);
            }

            doors.Clear();
            doors.AddRange(candidates);
            lamps.Clear();
            AddLamps(names, states);
            engines.Add(names, states);
            knobs.Add(names, states);
            horns.Add(names, states);
            tows.Add(names, states);
            found = true;
            current.SetWorld(names, states);
            log.Info(
                LogCategory.Fsm,
                "Found " + (houseDoors - toggles) + " door(s) and " + toggles + " switch(es) under '" + HouseRoot + "', " + (buildingDoors - houseDoors) + " in other buildings (" + elsewhere + ") among "
                + machineCount + " state machine(s), and " + (doors.Count - buildingDoors) + " on vehicles (" + partDoors + " on the Satsuma's parts)"
                + (disturbed > 0 ? "; " + disturbed + " changed state while being prepared" : string.Empty) + ": " + Describe() + ".");
            return true;
        }

        // What is switched with a click, as doors whose opening and closing lead to the same state.
        private int AddToggles(GameObject building, string[][] table, List<Door> candidates)
        {
            int added = 0;
            PlayMakerFSM[] machines = building.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < machines.Length; i++)
            {
                PlayMakerFSM machine = machines[i];
                string under = PlainPath(machine.transform, building.transform);
                for (int t = 0; t < table.Length; t++)
                {
                    string[] toggle = table[t];
                    bool prefix = toggle[0].EndsWith("/", StringComparison.Ordinal);
                    if (machine.FsmName != toggle[1] || (prefix ? !under.StartsWith(toggle[0], StringComparison.Ordinal) : under != toggle[0]))
                    {
                        continue;
                    }

                    FsmBool on = machine.FsmVariables.FindFsmBool(toggle[2]);
                    if (on == null || !HasState(machine, toggle[3]))
                    {
                        log.Warning(LogCategory.Fsm, "'" + under + "' is not made as this build expects (boolean '" + toggle[2] + "', state '" + toggle[3] + "'): it is not shared.");
                        continue;
                    }

                    string label = machine.gameObject.name == "Switch" || machine.gameObject.name == "Trigger" || machine.gameObject.name == "Valve"
                        ? machine.transform.parent.name + " " + machine.gameObject.name.ToLowerInvariant()
                        : machine.gameObject.name;
                    candidates.Add(new Door(machine, on, PathOf(machine.transform), toggle[2], toggle[3], toggle[3], label));
                    added++;
                }
            }

            return added;
        }

        // What keeps no boolean of its own, as doors whose state is read from where their machine is.
        private int AddStateDoors(GameObject building, string[][] table, List<Door> candidates)
        {
            int added = 0;
            for (int t = 0; t < table.Length; t++)
            {
                string[] entry = table[t];
                Transform at = building.transform.Find(entry[0]);
                PlayMakerFSM machine = at == null ? null : FindMachine(at, entry[1]);
                if (machine == null || !HasState(machine, entry[3]) || !HasState(machine, entry[4]))
                {
                    log.Warning(LogCategory.Fsm, "'" + entry[0] + "' is not made as this build expects (machine '" + entry[1] + "', states '" + entry[3] + "' and '" + entry[4] + "'): it is not shared.");
                    continue;
                }

                var openStates = new string[entry.Length - 5];
                Array.Copy(entry, 5, openStates, 0, openStates.Length);
                var door = new Door(machine, new FsmBool(), PathOf(machine.transform), null, entry[3], entry[4], entry[2]) { OpenStates = openStates };
                door.Open.Value = door.OpenByState();
                candidates.Add(door);
                added++;
            }

            return added;
        }

        // "Building/KITCHEN/KitchenWaterTap/Trigger": the names from below the root down to the object.
        private static string PlainPath(Transform transform, Transform root)
        {
            var names = new List<string>();
            for (Transform at = transform; at != null && at != root; at = at.parent)
            {
                names.Add(at.name);
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        // The doors of the house's kind under a building. Returns how many state machines were looked at.
        private int AddBuildingDoors(GameObject building, List<Door> candidates)
        {
            PlayMakerFSM[] machines = building.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < machines.Length; i++)
            {
                PlayMakerFSM machine = machines[i];
                if (machine.FsmName != MachineName)
                {
                    continue;
                }

                FsmBool open = machine.FsmVariables.FindFsmBool(OpenVariable);
                if (open == null || !HasState(machine, OpenState) || !HasState(machine, CloseState))
                {
                    continue;
                }

                string name = PathOf(machine.transform);
                candidates.Add(new Door(machine, open, name, OpenVariable, OpenState, CloseState, RoomAndDoor(name)));
            }

            return machines.Length;
        }

        // The objects of the Satsuma's parts that the game installs and takes off whole, as the game's records name
        // them. Empty if this build or this world has no such car.
        private HashSet<Transform> PartObjects()
        {
            var found = new HashSet<Transform>();
            SatsumaCatalog catalog = SatsumaParts.Load(log);
            if (catalog == null)
            {
                return found;
            }

            PartEntry[] entries = catalog.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                PartEntry entry = entries[i];
                if (entry.Family != PartFamily.Parented && entry.Family != PartFamily.Jointed)
                {
                    continue;
                }

                GameObject part = SatsumaParts.PartOf(entry, SatsumaParts.DataOf(entry));
                if (part != null)
                {
                    found.Add(part.transform);
                }
            }

            return found;
        }

        // The doors on the Satsuma's parts, listed by the part's name and the path below it: the same wherever the part
        // is. A door on a part that hangs from another part is that part's only.
        private int AddPartDoors(List<Door> candidates, HashSet<Transform> partObjects)
        {
            int added = 0;
            Transform car = null;
            SatsumaCatalog catalog = SatsumaParts.Load(log);
            if (catalog != null)
            {
                car = VehiclePresenter.FindRoot(catalog.Car);
            }

            foreach (Transform part in partObjects)
            {
                PlayMakerFSM[] machines = part.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < machines.Length; i++)
                {
                    PlayMakerFSM machine = machines[i];
                    if (machine.FsmName != MachineName || PartAbove(machine.transform, partObjects) != part)
                    {
                        continue;
                    }

                    FsmBool open = machine.FsmVariables.FindFsmBool(VehicleOpenVariable);
                    string openState = FirstState(machine, VehicleOpenStates);
                    string closeState = FirstState(machine, VehicleCloseStates);
                    if (open == null || openState == null || closeState == null)
                    {
                        continue;
                    }

                    string below = PlainPath(machine.transform, part);
                    string name = (car == null ? "SATSUMA" : car.name) + "/" + part.name + ":" + below;
                    var door = new Door(machine, open, name, VehicleOpenVariable, openState, closeState, ShortName(car == null ? "SATSUMA" : car.name) + " " + part.name + (below.Length == 0 ? string.Empty : " " + below));
                    door.Vehicle = car;
                    door.Part = part;
                    door.Hinge = car == null ? null : HingeAbove(machine.transform, car);
                    candidates.Add(door);
                    added++;
                }
            }

            return added;
        }

        // The nearest of the parts' objects at or above that object, or null.
        private static Transform PartAbove(Transform from, HashSet<Transform> partObjects)
        {
            for (Transform at = from; at != null; at = at.parent)
            {
                if (partObjects.Contains(at))
                {
                    return at;
                }
            }

            return null;
        }

        // The doors of a vehicle that is in the scene. One that is not there yet has none, and the two machines
        // would then disagree about the list; the overlay says so. Doors on parts that come off are not the
        // vehicle's: they are listed by their part.
        private void AddVehicleDoors(string rootName, List<Door> candidates, HashSet<Transform> partObjects)
        {
            Transform root = VehiclePresenter.FindRoot(rootName);
            if (root == null)
            {
                log.Info(LogCategory.Fsm, "No vehicle '" + rootName + "' in the scene: it has no doors to share.");
                return;
            }

            PlayMakerFSM[] machines = root.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < machines.Length; i++)
            {
                PlayMakerFSM machine = machines[i];
                if (machine.FsmName != MachineName || PartAbove(machine.transform, partObjects) != null)
                {
                    continue;
                }

                FsmBool open = machine.FsmVariables.FindFsmBool(VehicleOpenVariable);
                string openState = FirstState(machine, VehicleOpenStates);
                string closeState = FirstState(machine, VehicleCloseStates);
                if (open == null || openState == null || closeState == null)
                {
                    continue;
                }

                string name = PathOf(machine.transform);
                string label = name.Length > rootName.Length + 1 ? ShortName(rootName) + " " + name.Substring(rootName.Length + 1) : name;
                int at = label.IndexOf('@');
                var door = new Door(machine, open, name, VehicleOpenVariable, openState, closeState, at < 0 ? label : label.Substring(0, at));
                door.Vehicle = root;
                door.Hinge = HingeAbove(machine.transform, root);
                candidates.Add(door);
            }
        }

        // The hinge a door's state machine works: on the same object or on one above it, below the vehicle.
        private static HingeJoint HingeAbove(Transform from, Transform vehicle)
        {
            for (Transform at = from; at != null && at != vehicle; at = at.parent)
            {
                HingeJoint hinge = at.GetComponent<HingeJoint>();
                if (hinge != null)
                {
                    return hinge;
                }
            }

            return null;
        }

        // A door on a hinge is about to be sent to its open or close state for someone else. The game closes
        // such a door by narrowing the hinge's limits, which does nothing to a body that is asleep or still held
        // open by the joint the game locks it with.
        private void Loosen(Door door, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (door.Hinge == null)
            {
                return;
            }

            Rigidbody body = door.Hinge.GetComponent<Rigidbody>();
            if (!door.Wanted)
            {
                FixedJoint[] locks = door.Hinge.GetComponents<FixedJoint>();
                for (int i = 0; i < locks.Length; i++)
                {
                    UnityEngine.Object.Destroy(locks[i]);
                }
            }

            if (body != null)
            {
                body.WakeUp();
            }

            door.ReportAt = now + SettleSeconds;
            door.ReportWhat = door.Wanted ? "being told to open" : "being told to close";
        }

        private void ReportSettled(Door door)
        {
            if (door.Hinge == null || door.Machine == null)
            {
                return;
            }

            Rigidbody body = door.Hinge.GetComponent<Rigidbody>();
            JointLimits limits = door.Hinge.limits;
            log.Info(
                LogCategory.Fsm,
                "The door '" + door.Label + "', " + SettleSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s after " + door.ReportWhat + ": hinge at " + door.Hinge.angle.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                + " degrees, limits " + limits.min.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " to "
                + limits.max.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                + (body == null ? string.Empty : body.IsSleeping() ? ", asleep" : ", awake")
                + (door.Hinge.GetComponent<FixedJoint>() != null ? ", held by a FixedJoint" : string.Empty)
                + ", state '" + door.Machine.ActiveStateName + "', " + door.Variable + " " + door.Open.Value + ".");
        }

        // "HAYOSIKO(1500kg, 250)" is "HAYOSIKO" to a reader of the log.
        internal static string ShortName(string rootName)
        {
            int at = rootName.IndexOfAny(new[] { '(', ' ' });
            return at > 0 ? rootName.Substring(0, at) : rootName;
        }

        internal static string FirstState(PlayMakerFSM machine, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (HasState(machine, names[i]))
                {
                    return names[i];
                }
            }

            return null;
        }

        // The part of a house door's path that tells it apart for a reader: the door, three levels up from
        // the handle that carries the machine.
        private static string RoomAndDoor(string name)
        {
            int at = name.IndexOf('@');
            string path = at < 0 ? name : name.Substring(0, at);
            string[] parts = path.Split('/');
            return parts.Length >= 3 ? parts[parts.Length - 3] : path;
        }

        // Gives the door's state machine the two events that lead straight to its open and close states. False
        // if doing so changed the state it was in or its boolean, which it should not.
        private bool Hook(Door door)
        {
            PlayMakerFSM machine = door.Machine;
            string stateBefore = machine.ActiveStateName;
            bool openBefore = door.Open.Value;
            try
            {
                if (!HasGlobalTransition(machine, OpenEvent))
                {
                    machine.AddGlobalTransition(OpenEvent, door.OpenState);
                }

                if (!HasGlobalTransition(machine, CloseEvent))
                {
                    machine.AddGlobalTransition(CloseEvent, door.CloseState);
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Fsm, "Could not prepare the door '" + door.Name + "': " + exception.Message);
                return false;
            }

            // MSCLoader rebuilds the machine's data when a transition is added; the variable may be a new object.
            FsmBool open = door.Variable == null ? null : machine.FsmVariables.FindFsmBool(door.Variable);
            if (open != null)
            {
                door.Open = open;
            }

            return machine.ActiveStateName == stateBefore && door.Open.Value == openBefore;
        }

        internal static bool HasGlobalTransition(PlayMakerFSM machine, string eventName)
        {
            FsmTransition[] transitions = machine.FsmGlobalTransitions;
            for (int i = 0; transitions != null && i < transitions.Length; i++)
            {
                if (transitions[i].EventName == eventName)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool HasState(PlayMakerFSM machine, string name)
        {
            FsmState[] states = machine.FsmStates;
            for (int i = 0; states != null && i < states.Length; i++)
            {
                if (states[i].Name == name)
                {
                    return true;
                }
            }

            return false;
        }

        // "YARD/Building/LIVINGROOM/DoorWC/Pivot/Handle@0/2/7/16/0/1": the names down from the root, and the
        // position among the siblings at each level, which tells apart two doors with the same names.
        private static string PathOf(Transform transform)
        {
            var names = new List<string>();
            var indices = new List<int>();
            for (Transform at = transform; at != null; at = at.parent)
            {
                names.Add(at.name);
                indices.Add(at.GetSiblingIndex());
            }

            var text = new StringBuilder();
            for (int i = names.Count - 1; i >= 0; i--)
            {
                text.Append(names[i]);
                if (i > 0)
                {
                    text.Append('/');
                }
            }

            text.Append('@');
            for (int i = indices.Count - 2; i >= 0; i--)
            {
                text.Append(indices[i]);
                if (i > 0)
                {
                    text.Append('/');
                }
            }

            return text.ToString();
        }

        private string Describe()
        {
            var text = new StringBuilder();
            for (int i = 0; i < doors.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(", ");
                }

                text.Append(doors[i].Label).Append(doors[i].Open.Value ? " (open)" : string.Empty);
            }

            return text.ToString();
        }

        /// <summary>The light switch of one vehicle, which two entries of the shared list describe between them.</summary>
        private sealed class LightSwitch
        {
            public PlayMakerFSM Machine { get; set; }

            public FsmInt Selection { get; set; }

            public string Label { get; set; }

            /// <summary>Places in the shared list of "at least dipped" and of "full beam"; -1 for a switch without full beam.</summary>
            public int ShortIndex { get; set; }

            public int LongIndex { get; set; }

            /// <summary>When to turn it for someone else; 0 while there is nothing to turn.</summary>
            public double TurnAt { get; set; }

            public bool Hooked { get; set; }

            public int Tries { get; set; }

            public bool Complained { get; set; }

            /// <summary>The vehicle's root; for a switch on a part, that part's name, the path below it, the machine's name and the part's object.</summary>
            public Transform Root { get; set; }

            public string Under { get; set; }

            public string Path { get; set; }

            public string MachineName { get; set; }

            public Transform Part { get; set; }

            /// <summary>Found unlike what the data file says: left alone for the rest of the session.</summary>
            public bool Broken { get; set; }
        }

        /// <summary>One entry of the shared list that is about a vehicle's lights.</summary>
        private sealed class Lamp
        {
            public Transform Root { get; set; }

            public int Index { get; set; }

            /// <summary>For the electricity: the object the lamps hang from, and the two things the ignition says.</summary>
            public GameObject Power { get; set; }

            public FsmBool Acc { get; set; }

            public FsmBool Motor { get; set; }

            /// <summary>Whether the mod switched the object itself, to show the lights of whoever simulates the vehicle.</summary>
            public bool Forced { get; set; }

            /// <summary>For a position of the light switch: the switch, and whether this is full beam.</summary>
            public LightSwitch Switch { get; set; }

            public bool Long { get; set; }
        }

        private sealed class Door
        {
            public Door(PlayMakerFSM machine, FsmBool open, string name, string variable, string openState, string closeState, string label)
            {
                Machine = machine;
                Open = open;
                Name = name;
                Variable = variable;
                OpenState = openState;
                CloseState = closeState;
                Label = label;
            }

            /// <summary>Name of the boolean that says whether it is open; null for one that keeps none.</summary>
            public string Variable { get; private set; }

            /// <summary>For what keeps no boolean of its own: the states it is open in. Null otherwise.</summary>
            public string[] OpenStates { get; set; }

            public string OpenState { get; private set; }

            public string CloseState { get; private set; }

            /// <summary>What the log calls it.</summary>
            public string Label { get; private set; }

            /// <summary>The root object of the vehicle it is a door of; null for a door of the house.</summary>
            public Transform Vehicle { get; set; }

            /// <summary>The hinge it swings on, if it is a body that swings. Null for a door moved by an animation.</summary>
            public HingeJoint Hinge { get; set; }

            /// <summary>For a door on one of the Satsuma's parts, that part's object; null otherwise.</summary>
            public Transform Part { get; set; }

            /// <summary>When the log is due a line on how the door ended up; 0 for never.</summary>
            public double ReportAt { get; set; }

            /// <summary>What happened to the door that the line is about.</summary>
            public string ReportWhat { get; set; }

            public PlayMakerFSM Machine { get; private set; }

            public FsmBool Open { get; set; }

            public string Name { get; private set; }

            /// <summary>The state someone else gave it, which the game has not shown yet.</summary>
            public bool Following { get; set; }

            public bool Wanted { get; set; }

            public bool Sent { get; set; }

            public double Deadline { get; set; }

            public bool Complained { get; set; }

            /// <summary>Whether its machine is in one of the states it is open in.</summary>
            public bool OpenByState()
            {
                string state = Machine.ActiveStateName;
                for (int i = 0; i < OpenStates.Length; i++)
                {
                    if (OpenStates[i] == state)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
