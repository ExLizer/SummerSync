using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The game's side of <see cref="PartSync"/>: where each thing of the catalogue is in this world, what the game
    /// has, and the game's own way to install, remove, bolt and wire without the player.
    /// </summary>
    internal sealed partial class PartSync
    {
        private const string InstalledVariable = "Installed";
        private const string DamagedVariable = "Damaged";
        private const string AssemblyMachine = "Assembly";
        private const string RemovalMachine = "Removal";
        private const string ScrewMachine = "Screw";
        private const string UseMachine = "Use";
        private const string DetachVariable = "Detach";
        private const string CornerVariable = "Corner";
        private const string RemovePartState = "Remove part";

        // The covers (seats, dashboard, steering wheel) have no "Remove part": the click leads to the state that sees which
        // cover is on (dump of 2026-10-14, "seat cover(xxxx1)", "seat cover(xxxx2)" :: Removal).
        private static readonly string[] CoverRemoveStates = { "What part", "What part 2" };

        // A part that another one covers (the head gasket under the head, the crankshaft under the oil pan, the timing chain
        // under its cover, the engine plate under the gearbox, the clutch disc and pressure plate under its cover plate):
        // the covering part's "BoltCheck" sends its removal BLOCKED when its bolts are in, which leads to a state that
        // switches the part's collider off, and UNBLOCKED when the last bolt is out, which leads back to "Mouse off".
        private const string BlockedState = "State 2";
        private const string UnblockedEvent = "UNBLOCKED";
        private const string TightenEvent = "TIGHTEN";
        private const string UntightenEvent = "UNTIGHTEN";
        private const string EventPrefix = "SUMMERSYNC PART ";

        // Looks through the scene for this world's copy of every entry: its record, its bolts. Null if there is no car.
        private Slot[] BindSlots(SatsumaCatalog parts, out string missing)
        {
            var slots = new Slot[parts.Entries.Length];
            var absent = new List<string>();
            for (int i = 0; i < slots.Length; i++)
            {
                PartEntry entry = parts.Entries[i];
                var slot = new Slot { Entry = entry, Index = i, Steps = SatsumaAssembly.InstalledBySteps(entry), Corner = SatsumaAssembly.CornerOf(entry) };
                slot.Wire = entry.Wiring != null;
                if (entry.Family != PartFamily.Wheel && !SatsumaAssembly.IsTool(entry))
                {
                    slot.Data = SatsumaParts.DataOf(entry);
                    if (slot.Data == null)
                    {
                        absent.Add(entry.Name);
                    }
                    else
                    {
                        slot.Installed = slot.Data.FsmVariables.FindFsmBool(InstalledVariable);
                        slot.Damaged = slot.Data.FsmVariables.FindFsmBool(DamagedVariable);
                    }
                }

                slot.Bolts = new BoltState[entry.Bolts.Length];
                for (int b = 0; b < slot.Bolts.Length; b++)
                {
                    slot.Bolts[b] = new BoltState { Bolt = entry.Bolts[b] };
                }

                slots[i] = slot;
            }

            missing = absent.Count == 0 ? null : absent.Count + " (" + string.Join(", ", absent.GetRange(0, Math.Min(5, absent.Count)).ToArray()) + (absent.Count > 5 ? ", …" : string.Empty) + ")";
            return slots;
        }

        // Whether the game has the entry installed, as far as a player's steps go.
        private bool GameInstalled(Slot slot)
        {
            if (slot.Corner >= 0)
            {
                return WheelAt(slot.Corner) != null;
            }

            return slot.Installed != null && slot.Installed.Value;
        }

        private bool GameWired(Slot slot)
        {
            return slot.Installed != null && slot.Installed.Value;
        }

        // What this game shows of the car, as a model: what the session is told at first, and what a client's game is
        // compared with. A bolt whose part is off is not known here: it is taken from <paramref name="unknown"/>.
        private void Read(AssemblyModel into, AssemblyModel unknown)
        {
            into.Clear();
            for (int i = 0; i < slots.Length; i++)
            {
                Slot slot = slots[i];
                if (slot.Steps && GameInstalled(slot))
                {
                    into.SetInstalled(i, true, VariantOf(slot), 0);
                }

                if (slot.Wire && GameWired(slot))
                {
                    into.SetWired(i, true);
                }

                if (slot.Damaged != null && slot.Damaged.Value)
                {
                    into.SetDamaged(i, true);
                }

                for (int b = 0; b < slot.Bolts.Length; b++)
                {
                    int stage = GameStage(slot, b, false);
                    into.SetStage(i, b, stage >= 0 ? stage : unknown == null ? 0 : unknown.Stage(i, b));
                }
            }
        }

        // Which way into its slot the installed part took: the trigger whose part it is and, on a trigger that sits on a
        // wheel, the corner of that wheel. 0 when there is only one way in, or when it cannot be told.
        private int VariantOf(Slot slot)
        {
            int variant;
            TryVariant(slot, out variant);
            return variant;
        }

        // The same, false when it cannot be told (the variant is then the best guess).
        private bool TryVariant(Slot slot, out int variant)
        {
            variant = 0;
            PartEntry entry = slot.Entry;
            if (entry.Triggers.Length <= 1 && (entry.Triggers.Length == 0 || entry.Triggers[0].Place.Kind != PartPlaceKind.Wheel))
            {
                return true;
            }

            GameObject part = slot.Corner >= 0 ? GameObjectOf(WheelAt(slot.Corner)) : PartObject(entry);
            if (part == null)
            {
                return false;
            }

            string name = part.name;
            int ways = 0;
            for (int t = 0; t < entry.Triggers.Length; t++)
            {
                ways += entry.Triggers[t].Part == name ? 1 : 0;
            }

            if (ways > 1)
            {
                // Several ways in for a part of one name (the tachometer on the column or on the dashboard, the clutch
                // cover plate on either flywheel): the one it took, or nothing known (seen in a review on 2026-10-15:
                // the first was always told, and the other games put it there).
                variant = TakenWay(entry, part);
                if (variant < 0)
                {
                    variant = 0;
                    while (variant < entry.Triggers.Length - 1 && entry.Triggers[variant].Part != name)
                    {
                        variant++;
                    }

                    return false;
                }

                return true;
            }

            for (int t = 0; t < entry.Triggers.Length; t++)
            {
                PartTrigger trigger = entry.Triggers[t];
                if (trigger.Part != name)
                {
                    continue;
                }

                variant = t;
                if (trigger.Place.Kind != PartPlaceKind.Wheel)
                {
                    return true;
                }

                for (int corner = 0; corner < SatsumaAssembly.Corners.Length; corner++)
                {
                    Transform wheel = WheelAt(corner);
                    if (wheel != null && part.transform.IsChildOf(wheel))
                    {
                        variant = t + (corner * SatsumaAssembly.CornerFactor);
                        return true;
                    }
                }

                // The wheel it is on is not seen yet.
                return false;
            }

            return false;
        }

        // Of the triggers that take a part of that name, the one it took: the copy on the car the game switched on for
        // it (family A: dump of 2026-10-14, "tachometer(xxxx1)" on the column, "tachometer(xxxx2)" on the dashboard), or
        // the part it hangs from ("flywheel(Clone)/pivot_clutch cover plate"). -1 if neither says.
        private int TakenWay(PartEntry entry, GameObject part)
        {
            PartPlace[] copies = entry.Copies;
            bool byCopy = copies != null && copies.Length == entry.Triggers.Length;
            for (int t = 0; t < entry.Triggers.Length; t++)
            {
                PartTrigger trigger = entry.Triggers[t];
                if (trigger.Part != part.name)
                {
                    continue;
                }

                if (byCopy)
                {
                    Transform copy = Resolve(copies[t], -1);
                    if (copy != null && copy.gameObject.activeInHierarchy)
                    {
                        return t;
                    }

                    continue;
                }

                if (trigger.Place.Kind == PartPlaceKind.Part)
                {
                    PartEntry holder = parts.Find(trigger.Place.Entry);
                    GameObject on = holder == null ? null : PartObject(holder);
                    if (on != null && part.transform.IsChildOf(on.transform))
                    {
                        return t;
                    }
                }
            }

            return -1;
        }

        private static GameObject GameObjectOf(Transform transform)
        {
            return transform == null ? null : transform.gameObject;
        }

        // The object the game's record names as the entry's part; for a wheel slot, the wheel on it.
        private GameObject PartObject(PartEntry entry)
        {
            Slot slot = SlotOf(entry.Name);
            if (slot == null)
            {
                return null;
            }

            if (slot.Corner >= 0)
            {
                return GameObjectOf(WheelAt(slot.Corner));
            }

            return SatsumaParts.PartOf(entry, slot.Data);
        }

        private Slot SlotOf(string name)
        {
            PartEntry entry = parts.Find(name);
            return entry == null ? null : slots[entry.Index];
        }

        // An object of the catalogue in this world; for a place on a wheel, on the wheel at that corner.
        private Transform Resolve(PartPlace place, int corner)
        {
            switch (place.Kind)
            {
                case PartPlaceKind.Car:
                    return car.Find(place.Path);
                case PartPlaceKind.Scene:
                    // Below a root of the scene, switched off or not (the hoist's hook shows its bolts only while the block
                    // is at it): GameObject.Find sees only what is switched on.
                    int slash = place.Path.IndexOf('/');
                    GameObject root = GameObject.Find("/" + (slash < 0 ? place.Path : place.Path.Substring(0, slash)));
                    return root == null ? null : slash < 0 ? root.transform : root.transform.Find(place.Path.Substring(slash + 1));
                case PartPlaceKind.Part:
                    PartEntry holder = parts.Find(place.Entry);
                    GameObject part = holder == null ? null : PartObject(holder);
                    return part == null ? null : place.Path.Length == 0 ? part.transform : part.transform.Find(place.Path);
                case PartPlaceKind.Wheel:
                    Transform wheel = corner >= 0 && corner < SatsumaAssembly.Corners.Length ? WheelAt(corner) : null;
                    return wheel == null ? null : place.Path.Length == 0 ? wheel : wheel.Find(place.Path);
                default:
                    return null;
            }
        }

        // The wheel installed at a corner: below the hub's object, a wheel whose machine "Use" says that corner
        // (dump of 2026-10-14, "wheel_regula :: Use", string "Corner"). Kept while it stays there.
        private Transform WheelAt(int corner)
        {
            WheelSlot hub = hubs[corner];
            if (hub == null)
            {
                return null;
            }

            // Unity's null check is also true for a destroyed object.
            if (hub.Wheel != null && hub.Wheel.IsChildOf(hub.Hub) && hub.Corner != null && hub.Corner.Value == SatsumaAssembly.Corners[corner])
            {
                return hub.Wheel;
            }

            hub.Wheel = null;
            hub.Corner = null;
            if (frameTime < hub.NextLook)
            {
                return null;
            }

            hub.NextLook = frameTime + WheelLookSeconds;
            var queue = new Queue<Transform>();
            queue.Enqueue(hub.Hub);
            while (queue.Count > 0)
            {
                Transform at = queue.Dequeue();
                for (int i = 0; i < at.childCount; i++)
                {
                    Transform child = at.GetChild(i);
                    if (Array.IndexOf(hub.Names, child.name) >= 0)
                    {
                        PlayMakerFSM use = DoorSync.FindMachine(child, UseMachine);
                        FsmString said = use == null ? null : use.FsmVariables.FindFsmString(CornerVariable);
                        if (said != null && said.Value == SatsumaAssembly.Corners[corner])
                        {
                            hub.Wheel = child;
                            hub.Corner = said;
                            hub.NextLook = double.NegativeInfinity;
                            return child;
                        }
                    }

                    queue.Enqueue(child);
                }
            }

            return null;
        }

        // The four hubs: where each wheel slot's trigger sits, and the names its wheels have.
        private void BindHubs()
        {
            for (int corner = 0; corner < hubs.Length; corner++)
            {
                hubs[corner] = null;
                Slot slot = SlotOf("Wheels/" + SatsumaAssembly.Corners[corner]);
                if (slot == null || slot.Entry.Triggers.Length == 0 || slot.Entry.Triggers[0].Place.Kind != PartPlaceKind.Car)
                {
                    continue;
                }

                Transform trigger = car.Find(slot.Entry.Triggers[0].Place.Path);
                if (trigger == null || trigger.parent == null)
                {
                    continue;
                }

                var names = new List<string>();
                for (int t = 0; t < slot.Entry.Triggers.Length; t++)
                {
                    if (!names.Contains(slot.Entry.Triggers[t].Part))
                    {
                        names.Add(slot.Entry.Triggers[t].Part);
                    }
                }

                hubs[corner] = new WheelSlot { Hub = trigger.parent, Names = names.ToArray(), NextLook = double.NegativeInfinity };
            }
        }

        // The stage a bolt is at in this game, or -1 if that cannot be read now. The stage is the game's own list of
        // the part's bolts, which is what it saves, what changes at the end of a turn, and what a bolt the game loosens
        // by itself changes; it is there while the bolt is hidden by another part (the rocker shaft's under the valve
        // cover). Without a list, the screw's own number, while it rests.
        private int GameStage(Slot slot, int index, bool needAwake)
        {
            BoltState bolt = slot.Bolts[index];
            if (!BindBolt(slot, bolt))
            {
                return -1;
            }

            bool awake = bolt.Object.activeInHierarchy && bolt.Screw.enabled;
            if (needAwake && !awake)
            {
                return -1;
            }

            if (bolt.List != null)
            {
                ArrayList list = bolt.List.arrayList;
                int at = bolt.Bolt.Index;
                if (list != null && at >= 0 && at < list.Count)
                {
                    int stage;
                    if (TryStage(list[at], out stage))
                    {
                        return stage;
                    }
                }
            }

            return awake && bolt.Stage != null && Resting(bolt.Screw) ? bolt.Stage.Value : -1;
        }

        // Whether a screw is between turns: "Idle", "Idle 2"… and not in a state of a turn (dump of 2026-10-14: "Screw 2",
        // "Wait 3", "Stages 2", "8 2", "Get scroll 2", "Setup 2"…).
        private static bool Resting(PlayMakerFSM screw)
        {
            string state = screw.ActiveStateName;
            if (string.IsNullOrEmpty(state))
            {
                return false;
            }

            for (int i = 0; i < TurnStates.Length; i++)
            {
                if (state.StartsWith(TurnStates[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return !(state[0] >= '0' && state[0] <= '9');
        }

        private static readonly string[] TurnStates = { "Screw", "Unscrew", "Wait", "Stages", "Get scroll", "Setup", "Save data", "Load", "Check" };

        private static bool TryStage(object value, out int stage)
        {
            stage = 0;
            if (value == null)
            {
                return false;
            }

            if (value is int)
            {
                stage = (int)value;
                return true;
            }

            if (value is float)
            {
                stage = (int)Math.Round((float)value);
                return true;
            }

            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out stage);
        }

        // Finds the bolt's screw and its part's list in this world, again if its part changed (a wheel swapped).
        private bool BindBolt(Slot slot, BoltState bolt)
        {
            // Unity's null check is also true for a destroyed object.
            if (bolt.Object != null && bolt.Screw != null)
            {
                if (slot.Corner < 0)
                {
                    return true;
                }

                // A wheel slot's bolts are the wheel's: another wheel there has its own.
                Transform wheel = WheelAt(slot.Corner);
                if (wheel != null && bolt.Object.transform.IsChildOf(wheel))
                {
                    return true;
                }
            }

            bolt.Object = null;
            bolt.Screw = null;
            bolt.Stage = null;
            bolt.List = null;
            if (frameTime < bolt.NextLook)
            {
                return false;
            }

            bolt.NextLook = frameTime + BoltLookSeconds;
            PartBolt described = bolt.Bolt;
            Transform place = Resolve(described.Place, slot.Corner);
            Transform holder = place == null ? null : place.parent;
            if (holder == null)
            {
                return false;
            }

            for (int i = 0; i < holder.childCount; i++)
            {
                Transform child = holder.GetChild(i);
                if (child.name != place.name || IndexOfBolt(child) != described.Index)
                {
                    continue;
                }

                PlayMakerFSM screw = DoorSync.FindMachine(child, ScrewMachine);
                if (screw == null)
                {
                    continue;
                }

                bolt.Object = child.gameObject;
                bolt.Screw = screw;
                bolt.Stage = screw.FsmVariables.FindFsmInt("Stage");
                Transform checkAt = described.Check == null ? null : Resolve(described.Check.Place, slot.Corner);
                bolt.List = checkAt == null ? null : checkAt.GetComponent<PlayMakerArrayListProxy>();
                bolt.NextLook = double.NegativeInfinity;
                return true;
            }

            return false;
        }

        // The game's index of a bolt: the last digit of its child "bolt<n>" (dump of 2026-10-14; "bolt10" is 0).
        private static int IndexOfBolt(Transform boltObject)
        {
            for (int i = 0; i < boltObject.childCount; i++)
            {
                string name = boltObject.GetChild(i).name;
                if (name.StartsWith("bolt", StringComparison.Ordinal) && name.Length > 4)
                {
                    char last = name[name.Length - 1];
                    if (last >= '0' && last <= '9')
                    {
                        return last - '0';
                    }
                }
            }

            return -1;
        }

        // Puts a part into its slot the way the game does once the player clicks: the trigger is given the part and
        // taken to the state the click leads to. False if it cannot be done now (trigger switched off, part not here).
        private bool Install(Slot slot, int variant, ushort partRef, out string why)
        {
            PartEntry entry = slot.Entry;
            int which = variant % SatsumaAssembly.CornerFactor;
            int corner = slot.Corner >= 0 ? slot.Corner : variant / SatsumaAssembly.CornerFactor;
            if (which < 0 || which >= entry.Triggers.Length)
            {
                which = 0;
            }

            PartTrigger trigger = entry.Triggers[which];
            Transform at = Resolve(trigger.Place, corner);
            PlayMakerFSM assembly = at == null ? null : DoorSync.FindMachine(at, AssemblyMachine);
            slot.TriggerOff = assembly == null || !assembly.enabled || !assembly.gameObject.activeInHierarchy;
            if (slot.TriggerOff)
            {
                // Another part switches it on (the steering column's trigger the steering wheel's), or it sits on a
                // part that is not on yet.
                why = "its trigger '" + trigger.Place + "' is " + (assembly == null ? "not there" : "switched off");
                return false;
            }

            GameObject part = PartFor(slot, trigger, partRef, at.position);
            if (part == null && entry.Family != PartFamily.Clone)
            {
                why = "the part '" + trigger.Part + "' is not here";
                return false;
            }

            if (part != null)
            {
                objects.Release(part);
                if (!part.activeSelf)
                {
                    part.SetActive(true);
                }

                Undetach(slot, part.transform);
                Unjoin(slot, part, "before it goes in");
            }

            // The copy on the car that the game switches on instead (family A) comes off by its own removal.
            Undetach(slot, RemovalObject(slot));
            if (!ToState(assembly, trigger.State, TriggerPart, part))
            {
                why = "its trigger has no state '" + trigger.State + "'";
                return false;
            }

            slot.Released = part;
            why = null;
            return true;
        }

        private const string TriggerPart = "Part";

        // The part to put in: the one named by number, else the one the game's record names (a whole part is always
        // the same object), else the loose one of that name nearest the trigger.
        private GameObject PartFor(Slot slot, PartTrigger trigger, ushort partRef, Vector3 near)
        {
            GameObject part = objects.FindShared(partRef);
            if (part != null && part.name == trigger.Part)
            {
                return part;
            }

            PartEntry entry = slot.Entry;
            if (entry.Family == PartFamily.Parented || entry.Family == PartFamily.Jointed)
            {
                part = SatsumaParts.PartOf(entry, slot.Data);
                if (part != null && part.name == trigger.Part && !part.transform.IsChildOf(car))
                {
                    return part;
                }
            }

            part = objects.FindLoose(trigger.Part, near);
            if (part != null && !part.transform.IsChildOf(car))
            {
                return part;
            }

            // A part the loose objects do not share (a wheel lies kinematic until it is picked up): any body of that
            // name that is not on the car, nearest the trigger.
            Rigidbody[] bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            float best = float.MaxValue;
            part = null;
            for (int i = 0; i < bodies.Length; i++)
            {
                Transform body = bodies[i].transform;
                if (body.name != trigger.Part || body.IsChildOf(car) || (player != null && body.IsChildOf(player)))
                {
                    continue;
                }

                float distance = (body.position - near).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    part = body.gameObject;
                }
            }

            return part;
        }

        // Takes a part off the way a player's right click does: its machine "Removal" straight to the state the click leads
        // to, which also skips what the part needs to come off (the player of the other game had it). The removal looks at
        // its "Detach" only while nobody here points at the part ("Mouse off"; dump of 2026-10-14, the head gasket), and a
        // player working on the engine mostly does: it came off on the third try, three seconds late (seen on
        // 2026-10-15). "Detach" is left for a removal that has no such state.
        private bool Remove(Slot slot, out string why)
        {
            Transform at = RemovalObject(slot);
            PlayMakerFSM removal = at == null ? null : DoorSync.FindMachine(at, RemovalMachine);
            if (removal == null)
            {
                why = "its removal is not there";
                return false;
            }

            if (!at.gameObject.activeInHierarchy)
            {
                why = "its part is switched off";
                return false;
            }

            if (!removal.enabled)
            {
                removal.enabled = true;
            }

            slot.TakenOff = at.gameObject;

            if (Blocked(removal))
            {
                // Still covered in this game after the wait (the covering part's bolts never got out here): the game's
                // way back first, so that the part comes off with its collider on.
                removal.SendEvent(UnblockedEvent);
                log.Info(Category, "Car: '" + slot.Entry.Name + "' was still covered here (its removal blocked): unblocked to take it off.");
            }

            if (ToState(removal, RemovePartState, null, null))
            {
                why = null;
                return true;
            }

            for (int i = 0; i < CoverRemoveStates.Length; i++)
            {
                if (ToState(removal, CoverRemoveStates[i], null, null))
                {
                    why = null;
                    return true;
                }
            }

            FsmBool detach = removal.FsmVariables.FindFsmBool(DetachVariable);
            if (detach == null)
            {
                why = "its removal has neither a state '" + RemovePartState + "' nor '" + DetachVariable + "'";
                return false;
            }

            detach.Value = true;
            slot.Detached = detach;
            why = null;
            return true;
        }

        // A loose part has no joints of its own: the game joins it to the car as it builds it in, and its trigger connects
        // the part's first joint to the car ("SetJointConnectedBody"). A door taken off while shut keeps its lock (a
        // FixedJoint to the car, which a player opens before taking the door off): put back on, the lock got the car and
        // the new hinge stayed joined to the world where the door had been, broke at a billion newtons at the next step,
        // and the removal took the door straight off again (seen on 2026-10-15, in the guest's game, a door the host put
        // back on). So whatever joints a loose part has left are taken off it, once it is off and before it goes in.
        private void Unjoin(Slot slot, GameObject part, string when)
        {
            // Unity's null check is also true for a destroyed object.
            if (part == null || part.transform.IsChildOf(car))
            {
                return;
            }

            Joint[] joints = part.GetComponents<Joint>();
            if (joints.Length == 0)
            {
                return;
            }

            for (int i = 0; i < joints.Length; i++)
            {
                UnityEngine.Object.Destroy(joints[i]);
            }

            log.Info(Category, "Car: '" + slot.Entry.Name + "' still had " + joints.Length + " joint(s) of when it was on the car (" + joints[0].GetType().Name + "…): taken off it " + when + ".");
        }

        // A removal told to detach keeps "Detach" true once the part is off, and the game does not put it back: the
        // trigger that installs the part again switches the removal on at its end, which takes the part straight off
        // again (seen on 2026-10-15: a door the host put back on stayed loose in the guest's game, its trigger off). So
        // once the part is off it is made false, and before a part goes in it is made false if it was left true.
        private void Undetach(Slot slot, Transform part)
        {
            PlayMakerFSM removal = part == null ? null : DoorSync.FindMachine(part, RemovalMachine);
            FsmBool detach = removal == null ? null : removal.FsmVariables.FindFsmBool(DetachVariable);
            if (detach != null && detach.Value)
            {
                detach.Value = false;
                log.Info(Category, "Car: '" + slot.Entry.Name + "' was still told to come off ('" + DetachVariable + "' of its removal); not any more, before it goes in.");
            }
        }

        // Whether another part covers this one in this game (see BlockedState).
        private bool Covered(Slot slot)
        {
            Transform at = RemovalObject(slot);
            return Blocked(at == null ? null : DoorSync.FindMachine(at, RemovalMachine));
        }

        private static bool Blocked(PlayMakerFSM removal)
        {
            // Unity's null check is also true for a destroyed object.
            return removal != null && removal.enabled && removal.ActiveStateName == BlockedState && DoorSync.HasGlobalTransition(removal, UnblockedEvent);
        }

        // The object whose machine "Removal" takes the entry's part off: the wheel on a wheel slot.
        private Transform RemovalObject(Slot slot)
        {
            if (slot.Corner >= 0)
            {
                return WheelAt(slot.Corner);
            }

            // A part with two places has a removal at each; the one where it is now is switched on (seen in a review on
            // 2026-10-15: the tachometer on the dashboard could not be taken off from the column's).
            PartPlace[] removals = slot.Entry.Removals;
            Transform first = null;
            for (int i = 0; i < removals.Length; i++)
            {
                Transform at = Resolve(removals[i], -1);
                if (at != null && at.gameObject.activeInHierarchy)
                {
                    return at;
                }

                first = first ?? at;
            }

            return first;
        }

        // Turns a bolt one stage the way the wrench does. False if it cannot turn now.
        private bool Turn(Slot slot, int index, int delta)
        {
            BoltState bolt = slot.Bolts[index];
            if (!BindBolt(slot, bolt))
            {
                return false;
            }

            if (!bolt.Object.activeInHierarchy)
            {
                return false;
            }

            if (!bolt.Screw.enabled || !Resting(bolt.Screw))
            {
                return false;
            }

            bolt.Screw.SendEvent(delta > 0 ? TightenEvent : UntightenEvent);
            return true;
        }

        // The engine mounts show their bolts only while the block hangs within ten centimetres of them (dump of
        // 2026-10-15, "_Motor/Trigger_motor1 :: Assembly": "DistanceTolerance" 0.1, the distance from the block's
        // "_Triggers/_Trigger1" to the mount's bolt measured every frame). The game whose player bolted the block had
        // it there; here it may still be on its way, as the loose objects move it. Once it has not shown its bolts for a
        // moment, the block is put where the game puts it once bolted ("CarMotorPivot", which its "Bolts ON" makes its
        // parent), unless it hangs from something (the hoist), which would be dragged along: then it is waited for.
        private void BringBlock(Slot slot)
        {
            if (frameTime < slot.BringAt)
            {
                return;
            }

            slot.BringAt = frameTime + BringSeconds;
            GameObject block = SatsumaParts.PartOf(slot.Entry, slot.Data);
            Transform pivot = PartFinder.Below(car, MotorPivot);

            // Unity's null check is also true for a destroyed object.
            if (block == null || pivot == null || block.transform.IsChildOf(car) || block.GetComponent<Joint>() != null)
            {
                ShowBolts(slot);
                return;
            }

            objects.Release(block);
            Rigidbody body = block.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = true;
            }

            block.transform.position = pivot.position;
            block.transform.rotation = pivot.rotation;
            log.Info(Category, "Car: the block put at its mounts here, for its bolts to show as they did for whoever bolted it.");
        }

        private const string MotorPivot = "CarMotorPivot";
        private const double BringSeconds = 3.0;

        // For an entry whose bolts turn before it is installed (the block): the state of its trigger that shows them.
        private void ShowBolts(Slot slot)
        {
            if ((catalog.Flags(slot.Index) & AssemblyEntryFlags.BoltsWhileLoose) == 0 || slot.Entry.Triggers.Length == 0 || frameTime < slot.ShowAt)
            {
                return;
            }

            slot.ShowAt = frameTime + RetrySeconds;
            for (int t = 0; t < slot.Entry.Triggers.Length; t++)
            {
                PartTrigger trigger = slot.Entry.Triggers[t];
                Transform at = Resolve(trigger.Place, -1);
                PlayMakerFSM machine = at == null ? null : DoorSync.FindMachine(at, AssemblyMachine);
                if (machine != null && machine.enabled && machine.gameObject.activeInHierarchy && ToState(machine, trigger.State, null, null))
                {
                    log.Info(Category, "Car: the bolts of '" + slot.Entry.Name + "' shown at '" + trigger.Place + "' (" + trigger.State + "), to turn them as someone else did.");
                    return;
                }
            }
        }

        // Makes a wire the way the game does once both ends meet: one end taken to the state that finishes it.
        private bool Connect(Slot slot, out string why)
        {
            PartWiring wiring = slot.Entry.Wiring;
            for (int e = 0; e < wiring.Ends.Length; e++)
            {
                Transform at = Resolve(wiring.Ends[e], -1);
                PlayMakerFSM machine = at == null ? null : DoorSync.FindMachine(at, PartWiring.Machine);
                if (machine != null && machine.enabled && machine.gameObject.activeInHierarchy && ToState(machine, wiring.State, null, null))
                {
                    why = null;
                    return true;
                }
            }

            why = "neither end of the wire is there and switched on";
            return false;
        }

        // Takes a machine to one of its states through an event of the mod's, after giving it a game object variable
        // if one is named. MSCLoader rebuilds a machine's data when a transition is added, so the variable is found
        // after that.
        private bool ToState(PlayMakerFSM machine, string state, string variable, GameObject value)
        {
            if (!DoorSync.HasState(machine, state))
            {
                return false;
            }

            string eventName = EventPrefix + state;
            if (!DoorSync.HasGlobalTransition(machine, eventName))
            {
                machine.AddGlobalTransition(eventName, state);
            }

            if (variable != null)
            {
                FsmGameObject slotVariable = machine.FsmVariables.FindFsmGameObject(variable);
                if (slotVariable != null)
                {
                    slotVariable.Value = value;
                }
            }

            machine.SendEvent(eventName);
            return true;
        }

        // "ss satsuma <entry>": the entry here and in the session, bolt by bolt.
        private string DescribeEntry(Slot slot)
        {
            AssemblyModel shown = current.Shown;
            var text = new System.Text.StringBuilder();
            text.Append(slot.Entry.Name).Append(" (").Append(SatsumaCatalog.NameOf(slot.Entry.Family)).Append("): here ")
                .Append(slot.Steps ? (GameInstalled(slot) ? "installed" : "off") : slot.Wire ? (GameWired(slot) ? "wired" : "not wired") : "-")
                .Append(", session ").Append(shown.IsInstalled(slot.Index) ? "installed" : "off").Append(shown.IsWired(slot.Index) ? ", wired" : string.Empty)
                .Append(slot.Driving || slot.WireDriving ? ", being done" : string.Empty).Append(slot.Stuck ? ", left as it is" : string.Empty)
                .Append(", variant ").Append(VariantOf(slot));
            for (int b = 0; b < slot.Bolts.Length; b++)
            {
                BoltState bolt = slot.Bolts[b];
                bool bound = BindBolt(slot, bolt);
                text.Append("\n bolt ").Append(b).Append(" (index ").Append(bolt.Bolt.Index).Append(", ").Append(bolt.Bolt.Place).Append("): session ")
                    .Append(shown.Stage(slot.Index, b));
                if (!bound)
                {
                    text.Append(", not found here");
                    continue;
                }

                text.Append(", here ").Append(GameStage(slot, b, false)).Append(" (screw ").Append(bolt.Stage == null ? "?" : bolt.Stage.Value.ToString(CultureInfo.InvariantCulture))
                    .Append(" in '").Append(bolt.Screw.ActiveStateName).Append("'").Append(Resting(bolt.Screw) ? ", resting" : string.Empty).Append(bolt.Object.activeInHierarchy ? string.Empty : ", switched off")
                    .Append(bolt.Screw.enabled ? string.Empty : ", machine off").Append("; list ");
                ArrayList list = bolt.List == null ? null : bolt.List.arrayList;
                if (list == null)
                {
                    text.Append(bolt.List == null ? "none" : "empty");
                }
                else
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        text.Append(i == 0 ? "[" : ",").Append(list[i]);
                    }

                    text.Append(list.Count == 0 ? "[]" : "]");
                }

                text.Append(")").Append(bolt.Driving ? ", being turned" : string.Empty).Append(bolt.Stuck ? ", left as it is" : string.Empty);
            }

            log.Info(Category, text.ToString().Replace("\n", " |"));
            return text.ToString();
        }

        /// <summary>One entry of the catalogue in this world, and what is being done to it.</summary>
        private sealed class Slot
        {
            public PartEntry Entry;
            public int Index;

            /// <summary>Installed and removed by a player's step; whether it is a wire; the corner of a wheel slot, or -1.</summary>
            public bool Steps;
            public bool Wire;
            public int Corner;

            public PlayMakerFSM Data;
            public FsmBool Installed;
            public FsmBool Damaged;
            public BoltState[] Bolts;

            /// <summary>The game is being taken to what the session says (installed or not, wired), and how.</summary>
            public bool Driving;
            public bool WireDriving;
            public int Variant;
            public ushort PartRef;
            public byte Player;
            public int Tries;
            public double NextTry;

            /// <summary>The game would not do what the session says: left alone until it matches or another step comes.</summary>
            public bool Stuck;

            /// <summary>What the game had when the session could not be told, so it is not told again every frame.</summary>
            public int Unreported = -1;

            /// <summary>The part handed to the game to install, while it is being installed.</summary>
            public GameObject Released;

            /// <summary>From when the bolts are read: a part that was just installed brings its own stages a moment later.</summary>
            public double BoltsFrom;

            /// <summary>When its bolts were last shown at its trigger (the block's mounts).</summary>
            public double ShowAt;

            /// <summary>Its part's trigger was switched off at the last try (it waits for another part), and until when that is waited for.</summary>
            public bool TriggerOff;
            public double WaitUntil;

            /// <summary>Whether the log already says it waits for its trigger, this time.</summary>
            public bool WaitSaid;

            /// <summary>The removal's "Detach" this game set to take the part off, to be made false once it is off.</summary>
            public FsmBool Detached;

            /// <summary>Why "ss satsuma build" could not put it in.</summary>
            public string BuildWhy;

            /// <summary>It came off by itself where the car is not run: until when the game that runs it is waited for, and how often it was put back.</summary>
            public double HeldBackUntil;
            public int PutBacks;
            public double PutBackAt;

            /// <summary>To be taken off: until when its bolts and what covers it are waited for.</summary>
            public double RemoveWaitUntil;

            /// <summary>Taken off once already because it was in another way than the session's.</summary>
            public bool Swapped;

            /// <summary>What this game was told to take off: the object of its removal.</summary>
            public GameObject TakenOff;

            /// <summary>Just installed for the session: until when its bolts are waited for before the next part goes in.</summary>
            public double GateUntil;

            /// <summary>The block: when it was last brought to its mounts, and since when its bolts have been waited for.</summary>
            public double BringAt;
            public double HiddenSince;
        }

        /// <summary>One bolt in this world.</summary>
        private sealed class BoltState
        {
            public PartBolt Bolt;
            public GameObject Object;
            public PlayMakerFSM Screw;
            public FsmInt Stage;
            public PlayMakerArrayListProxy List;
            public double NextLook;

            /// <summary>The game is being taken to the stage the session says, one turn at a time.</summary>
            public bool Driving;
            public double NextTurn;
            public int Turns;
            public bool Stuck;
            public int Unreported = -1;
        }

        /// <summary>A hub of the car and the wheel on it.</summary>
        private sealed class WheelSlot
        {
            public Transform Hub;
            public string[] Names;
            public Transform Wheel;
            public FsmString Corner;
            public double NextLook;
        }
    }
}
