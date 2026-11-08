using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Objects;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Putting the Satsuma together between players: the game side of <see cref="AssemblyReplication"/>.
    ///
    /// What is assumed about the game (dump of 2026-10-14 and tests with the bridge on 2026-10-15, docs/GAME_NOTES.md,
    /// "El armado del Satsuma"): every part of the car has a record, the machine "Data" of "Database/&lt;entry&gt;", with a
    /// boolean "Installed"; a part goes in through its trigger's machine "Assembly", which takes the part in its
    /// variable "Part" and installs it from the state the player's click leads to; it comes off through its machine
    /// "Removal", which takes it off when its boolean "Detach" is set; a bolt is a machine "Screw" that the wrench sends
    /// TIGHTEN and UNTIGHTEN, and the part keeps the stage of each of its bolts in a list; a wire is finished when one of
    /// its ends gets to "Finish assembly". A wheel slot has no record: a wheel is on it when a wheel below the hub says
    /// that corner. The catalogue of all of it is data/satsuma/parts.json (<see cref="SatsumaCatalog"/>).
    ///
    /// The game where a player acts makes the step, and this class tells the session what it made: every frame what
    /// the game has is compared with what the session says it should show (<see cref="AssemblyReplication.Shown"/>),
    /// and what differs while nothing is being done to it is this game's doing. A step of someone else's, or one that
    /// corrects this game, is made by taking the game there the game's own way, never by writing a result: the part is
    /// handed to the trigger, the removal is told to detach, the screw is sent the wrench's event one stage at a time.
    /// Until the game gets there nothing it shows of that entry is told; if it does not get there it is left alone, and
    /// the log says why.
    ///
    /// A loose part is a shared object until it is installed (<see cref="ObjectPresenter"/>): the step says which by its
    /// shared number, and before installing the copy here it stops being moved by the replication of loose objects. A
    /// part the game creates when one comes off is expected as the remover's. A part that was just installed brings
    /// the stages of its own bolts a moment later; the game where it was installed tells them, and the others wait for
    /// that before they turn any.
    /// </summary>
    internal sealed partial class PartSync
    {
        private const double FirstLookSeconds = 4.0;
        private const double LookAgainSeconds = 5.0;
        private const double RetrySeconds = 1.5;
        private const int MostTries = 4;
        private const double BoltTurnSeconds = 0.1;
        private const int MostTurns = 30;
        private const double SettleSeconds = 1.0;
        private const double GraceSeconds = 3.0;
        private const double TriggerWaitSeconds = 60.0;
        private const double HoldBackSeconds = 1.5;

        // A part that stayed on this long since it was last put back starts counting its put-backs again.
        private const double PutBacksForgetSeconds = 30.0;

        // What the local player takes off with a right click comes off in that frame or a few after (the wheels wait).
        private const double RightClickSeconds = 0.3;
        private const double BlockWaitSeconds = 2.0;
        private const int MostPutBacks = 2;

        // One part is put in at a time, and the next a moment after the last went in (seen on 2026-10-15: a game handed
        // the whole car at once froze and unfroze the car for every part on a hinge in the same moments, and the car
        // flew off to the far end of the world). The game's own way takes a few frames after the part is in.
        private const double BetweenInstallsSeconds = 0.3;

        // And not before its bolts are as the session has them, as a player tightens a part before covering it (seen on
        // 2026-10-15: the rocker shaft's bolts, under the valve cover once it went in, could not be turned any more);
        // unless they do not get there in this long.
        private const double BoltGateSeconds = 8.0;
        private const string DisassembleGlobal = "GUIdisassemble";
        private const double WheelLookSeconds = 1.0;
        private const double BoltLookSeconds = 1.0;
        private const double BoltLogEvery = 2.0;
        private const LogCategory Category = LogCategory.Vehicle;

        private static AssemblyCatalog sharedCatalog;
        private static uint sharedShape;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly ObjectPresenter objects;
        private readonly VehiclePresenter vehicles;
        private readonly WheelSlot[] hubs = new WheelSlot[SatsumaAssembly.Corners.Length];
        private readonly CarValueSync values;
        private readonly BendSync bends;
        private readonly List<AssemblyStep> handed = new List<AssemblyStep>();
        private AssemblyReplication current;
        private Transform player;
        private SatsumaCatalog parts;
        private AssemblyCatalog catalog;
        private Slot[] slots;
        private Transform car;
        private bool loaded;
        private bool unavailable;
        private bool carMissingSaid;
        private bool mismatchSaid;
        private double lookAt;
        private double frameTime;

        // What the local player held last, and before that: the hand lets go of a part a moment before the game has it
        // installed (the click drops it, then the trigger takes it), and may take the next one before a wheel is seen on
        // its hub.
        private readonly ushort[] heldIds = new ushort[3];
        private readonly GameObject[] heldThings = new GameObject[3];
        private readonly double[] heldUntil = new double[3];

        // Whether the local player was pointing at a part to take it off, now or a frame ago (the game's global
        // "GUIdisassemble", which a part's machine "Removal" sets while the mouse is on it): what comes off then is
        // their doing, and anything else that comes off came off by itself.
        private HutongGames.PlayMaker.FsmBool disassembleGlobal;
        private bool disassembleBefore;
        private bool disassembling;
        private double rightClickAt = double.NegativeInfinity;
        private int installing = -1;
        private int nextUp = -1;
        private double nextInstall;
        private int boltsTold;
        private int boltsTurned;
        private double nextBoltLog;
        private Builder build;

        public PartSync(Logger log, NetworkManager manager, ObjectPresenter objects, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.objects = objects;
            this.vehicles = vehicles;
            values = new CarValueSync(log, vehicles);
            bends = new BendSync(log, manager);
        }

        /// <summary>
        /// The assembly replication's list of the Satsuma, the same on every machine with this build; null if the build's
        /// catalogue cannot be read.
        /// </summary>
        public static AssemblyCatalog Catalog(Logger log)
        {
            if (sharedCatalog == null)
            {
                SatsumaCatalog satsuma = SatsumaParts.Load(log);
                if (satsuma == null)
                {
                    return null;
                }

                // The car's numbers come after its entries, from the book the mod carries (data/satsuma/values.json).
                CarValue[] book = CarValueSync.Book.Values;
                var names = new string[book.Length];
                var steps = new float[book.Length];
                for (int i = 0; i < book.Length; i++)
                {
                    names[i] = book[i].Name;
                    steps[i] = book[i].Step;
                }

                sharedCatalog = SatsumaAssembly.Build(satsuma, names, steps);
                sharedShape = sharedCatalog.Shape;
                log.Info(
                    Category,
                    "Car: the assembly is shared over " + sharedCatalog.Count + " entries, " + sharedCatalog.TotalBolts + " bolts and " + sharedCatalog.ValueCount + " numbers (shape "
                    + sharedShape.ToString("x8", CultureInfo.InvariantCulture) + ").");
            }

            return sharedCatalog;
        }

        /// <summary>
        /// Once per frame, after the loose objects. <paramref name="localPlayer"/> is the player's root object, or null
        /// while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            frameTime = now;
            AssemblyReplication replication = localPlayer == null || unavailable ? null : manager.Assembly;
            if (replication != current || localPlayer != player)
            {
                Leave();
                current = replication;
                player = localPlayer;
                if (current != null)
                {
                    catalog = Catalog(log);
                    if (catalog == null)
                    {
                        unavailable = true;
                        current = null;
                        return;
                    }

                    current.Define(catalog);
                    current.StepWanted += OnStepWanted;
                    lookAt = now + FirstLookSeconds;
                }
            }

            if (current == null)
            {
                return;
            }

            if (current.Mismatch)
            {
                if (!mismatchSaid)
                {
                    mismatchSaid = true;
                    log.Warning(Category, "Car: the host's list of the Satsuma's parts is not this build's: putting it together is not shared in this session.");
                }

                return;
            }

            if (!loaded)
            {
                if (now < lookAt)
                {
                    return;
                }

                lookAt = now + LookAgainSeconds;
                if (!Bind())
                {
                    return;
                }

                Load(now);
            }

            if (disassembleGlobal == null)
            {
                disassembleGlobal = HutongGames.PlayMaker.FsmVariables.GlobalVariables.FindFsmBool(DisassembleGlobal);
            }

            // The global is set once when the mouse gets on a part and cleared by any removal that starts over or comes back
            // to rest, as one does here whenever a step of someone else's goes in or comes off: the right click itself
            // counts too (seen in a review on 2026-10-15: a part the local player took off was put back).
            if (Input.GetMouseButtonDown(1))
            {
                rightClickAt = now;
            }

            bool pointing = disassembleGlobal != null && disassembleGlobal.Value;
            disassembling = pointing || disassembleBefore || now - rightClickAt <= RightClickSeconds || build != null;
            disassembleBefore = pointing;
            Work(now);
            WorkPaint(now);
            bool runsCar = vehicles.SimulatesRoot(car);
            values.Update(now, runsCar);
            bends.Update(car, runsCar);
            if (build != null)
            {
                build.Step(now);
                if (build.Done)
                {
                    build = null;
                }
            }

            NoteHeld(now);
        }

        private const double HeldRecentlySeconds = 2.0;

        private void NoteHeld(double now)
        {
            ushort id = objects.HeldId;
            if (id == 0)
            {
                return;
            }

            if (heldIds[0] != id)
            {
                for (int i = heldIds.Length - 1; i > 0; i--)
                {
                    heldIds[i] = heldIds[i - 1];
                    heldThings[i] = heldThings[i - 1];
                    heldUntil[i] = heldUntil[i - 1];
                }

                heldIds[0] = id;
                heldThings[0] = objects.FindShared(id);
            }

            heldUntil[0] = now + HeldRecentlySeconds;
        }

        // The shared number of the part the local player just put into that slot: one held a moment ago that is now on
        // the car, by the name of one of the slot's parts. 0 if none.
        private ushort JustInstalled(Slot slot, double now)
        {
            for (int i = 0; i < heldIds.Length; i++)
            {
                GameObject thing = heldThings[i];

                // Unity's null check is also true for a destroyed object.
                if (heldIds[i] == 0 || now > heldUntil[i] || thing == null || !thing.transform.IsChildOf(car))
                {
                    continue;
                }

                PartTrigger[] triggers = slot.Entry.Triggers;
                for (int t = 0; t < triggers.Length; t++)
                {
                    if (triggers[t].Part == thing.name)
                    {
                        return heldIds[i];
                    }
                }
            }

            return 0;
        }

        /// <summary>The world was left, or the mod stops: nothing more is done to the game.</summary>
        public void Release()
        {
            Leave();
            current = null;
            player = null;
        }

        private void Leave()
        {
            if (current != null)
            {
                current.StepWanted -= OnStepWanted;
                if (loaded)
                {
                    current.ClearWorld();
                }
            }

            values.Clear();
            bends.Release();
            ReleasePaint();
            loaded = false;
            slots = null;
            car = null;
            installing = -1;
            build = null;
            handed.Clear();
            mismatchSaid = false;
            carMissingSaid = false;
            for (int i = 0; i < hubs.Length; i++)
            {
                hubs[i] = null;
            }
        }

        // Handed while the session reads its messages: kept for this class's turn in the frame. Nothing is done to the
        // game from inside the replication.
        private void OnStepWanted(AssemblyStep step)
        {
            handed.Add(step);
        }

        private bool Bind()
        {
            parts = SatsumaParts.Load(log);
            if (parts == null)
            {
                unavailable = true;
                return false;
            }

            car = VehiclePresenter.FindRoot(parts.Car);
            if (car == null)
            {
                if (!carMissingSaid)
                {
                    carMissingSaid = true;
                    log.Info(Category, "Car: no '" + parts.Car + "' in this world yet; its assembly is shared once it is there.");
                }

                return false;
            }

            string missing;
            slots = BindSlots(parts, out missing);
            BindHubs();
            BindPaint();
            if (missing != null)
            {
                log.Warning(Category, "Car: " + missing + " entr(ies) of the catalogue have no record in this world; what is done to them is not shared.");
            }

            return true;
        }

        private void Load(double now)
        {
            var scan = new AssemblyModel(catalog);
            Read(scan, null);
            current.Load(scan);
            values.Bind(car, parts, current);
            current.RequestAll();
            loaded = true;
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i].BoltsFrom = now;
            }

            int installed = 0;
            int wired = 0;
            int hubsFound = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                installed += scan.IsInstalled(i) ? 1 : 0;
                wired += scan.IsWired(i) ? 1 : 0;
            }

            for (int i = 0; i < hubs.Length; i++)
            {
                hubsFound += hubs[i] == null ? 0 : 1;
            }

            log.Info(
                Category,
                "Car: '" + car.name + "' read: " + installed + " part(s) installed, " + wired + " wire(s), " + hubsFound + " of 4 hubs, digest "
                + AssemblyReplication.DigestText(scan.Digest()) + (manager.Mode == NetworkMode.Client ? "; the host's car is asked for." : "; it is the session's car."));
        }

        private void Work(double now)
        {
            for (int i = 0; i < handed.Count; i++)
            {
                Mark(handed[i], now);
            }

            handed.Clear();
            AssemblyModel shown = current.Shown;
            if (shown == null)
            {
                return;
            }

            if (installing >= 0)
            {
                Slot holder = slots[installing];
                if (!holder.Driving && (now >= holder.GateUntil || BoltsSettled(holder)))
                {
                    Free(holder, now);
                }
            }

            nextUp = NextInstall(shown, now);
            for (int i = 0; i < slots.Length; i++)
            {
                Slot slot = slots[i];
                WorkInstalled(slot, shown, now);
                WorkWire(slot, shown, now);
                WorkBolts(slot, shown, now);
            }

            if ((boltsTold > 0 || boltsTurned > 0) && now >= nextBoltLog)
            {
                nextBoltLog = now + BoltLogEvery;
                log.Info(Category, "Car: bolts turned here and told, " + boltsTold + "; turned here as someone else did, " + boltsTurned + ".");
                boltsTold = 0;
                boltsTurned = 0;
            }
        }

        // A step handed by the session: the entry is taken there from now on.
        private void Mark(AssemblyStep step, double now)
        {
            if (step.Entry < 0 || step.Entry >= slots.Length)
            {
                return;
            }

            Slot slot = slots[step.Entry];
            string who = step.Player == AssemblyWire.NoPlayer ? "to match the session's car" : "as player " + step.Player.ToString(CultureInfo.InvariantCulture) + " did";
            switch (step.Kind)
            {
                case AssemblyStepKind.Install:
                case AssemblyStepKind.Remove:
                    if (!slot.Steps)
                    {
                        return;
                    }

                    slot.Driving = true;
                    slot.Stuck = false;
                    slot.Unreported = -1;
                    slot.PutBacks = 0;
                    slot.HeldBackUntil = 0.0;
                    slot.Tries = 0;
                    slot.NextTry = now;
                    slot.WaitUntil = 0.0;
                    slot.RemoveWaitUntil = 0.0;
                    slot.WaitSaid = false;
                    slot.Swapped = false;
                    slot.Player = step.Player;
                    if (step.Kind == AssemblyStepKind.Install)
                    {
                        slot.Variant = step.Variant;
                        slot.PartRef = (ushort)step.PartRef;
                    }

                    log.Info(Category, "Car: '" + slot.Entry.Name + "' to be " + (step.Kind == AssemblyStepKind.Install ? "installed" : "taken off") + " here, " + who + ".");
                    break;

                case AssemblyStepKind.Bolt:
                    if (step.Bolt >= 0 && step.Bolt < slot.Bolts.Length)
                    {
                        BoltState bolt = slot.Bolts[step.Bolt];
                        bolt.Driving = true;
                        bolt.Stuck = false;
                        bolt.Unreported = -1;
                        bolt.Turns = 0;
                    }

                    break;

                case AssemblyStepKind.Outcome:
                    values.Apply(step);
                    break;

                case AssemblyStepKind.Wire:
                    if (slot.Wire)
                    {
                        slot.WireDriving = true;
                        slot.Stuck = false;
                        slot.Unreported = -1;
                        slot.Tries = 0;
                        slot.NextTry = now;
                        slot.WaitSaid = false;
                        log.Info(Category, "Car: the wire '" + slot.Entry.Name + "' to be " + (step.Connected ? "made" : "cut") + " here, " + who + ".");
                    }

                    break;
            }
        }

        private void WorkInstalled(Slot slot, AssemblyModel shown, double now)
        {
            if (!slot.Steps)
            {
                return;
            }

            bool game = GameInstalled(slot);
            bool want = shown.IsInstalled(slot.Index);
            if (slot.Driving)
            {
                int took;
                if (game && want && !slot.Swapped && TryVariant(slot, out took) && took != slot.Variant)
                {
                    // Filled another way than the session's (offset wheels for regular ones, the tachometer moved to the
                    // dashboard): off first, then in the session's way; once, should the way not be told right.
                    slot.Swapped = true;
                    string off;
                    if (Remove(slot, out off))
                    {
                        log.Info(Category, "Car: '" + slot.Entry.Name + "' is in here another way than the session's (" + took + ", not " + slot.Variant + "): taken off to go in again.");
                        slot.NextTry = now + RetrySeconds;
                        return;
                    }
                }

                if (game == want)
                {
                    slot.Driving = false;
                    ClearDetach(slot);
                    if (!want && (slot.Entry.Family == PartFamily.Parented || slot.Entry.Family == PartFamily.Jointed))
                    {
                        // Off, it lies loose as it does in a player's game.
                        Unjoin(slot, SatsumaParts.PartOf(slot.Entry, slot.Data), "now that it is off");
                    }

                    if (want)
                    {
                        Unfreeze(slot);
                    }
                    else
                    {
                        LetLoose(slot);
                    }


                    log.Info(Category, "Car: '" + slot.Entry.Name + "' is " + (want ? "installed" : "off") + " here now.");
                    Changed(slot, want, false, now);
                    if (want && installing == slot.Index && slot.Bolts.Length > 0)
                    {
                        // Its bolts go first: the next part may cover them.
                        slot.GateUntil = now + BoltGateSeconds;
                    }
                    else
                    {
                        Free(slot, now);
                    }

                    return;
                }

                if (now < slot.NextTry)
                {
                    return;
                }

                if (want && installing != slot.Index && (installing >= 0 || now < nextInstall || nextUp != slot.Index))
                {
                    // Another part is going in, or goes first.
                    return;
                }

                if (!want)
                {
                    // A part comes off once its bolts here are as the session has them, as a player loosens them first: the
                    // covering parts tell what they cover that it is free only when their last bolt is out, and coming off
                    // switches that off for good (dump of 2026-10-14, "cylinder head(Clone)" :: BoltCheck and Removal). And
                    // a part that another one covers here waits for that word. Neither for longer than a part's bolts are.
                    if (slot.RemoveWaitUntil == 0.0)
                    {
                        slot.RemoveWaitUntil = now + BoltGateSeconds;
                    }

                    bool bolts = !BoltsSettled(slot);
                    if (now < slot.RemoveWaitUntil && (bolts || Covered(slot)))
                    {
                        if (!slot.WaitSaid)
                        {
                            slot.WaitSaid = true;
                            log.Info(Category, "Car: '" + slot.Entry.Name + "' waits to be taken off here: " + (bolts ? "its bolts are still being turned as the session has them" : "another part still covers it here") + ".");
                        }

                        return;
                    }
                }

                if (slot.Tries >= MostTries)
                {
                    slot.Driving = false;
                    slot.Stuck = true;
                    ClearDetach(slot);
                    Free(slot, now);
                    GiveBack(slot, want);
                    log.Warning(
                        Category,
                        "Car: '" + slot.Entry.Name + "' did not get " + (want ? "installed" : "taken off") + " here after " + MostTries
                        + " tries: this game differs from the session's car there until it is done again.");
                    return;
                }

                // A trigger that is not there is waited for this long from the first try, not from every try.
                if (slot.WaitUntil == 0.0)
                {
                    slot.WaitUntil = now + TriggerWaitSeconds;
                }

                slot.Tries++;
                slot.NextTry = now + RetrySeconds;
                string why;
                slot.TriggerOff = false;
                bool sent = want ? Install(slot, slot.Variant, slot.PartRef, out why) : Remove(slot, out why);
                if (want)
                {
                    // While it goes in, nothing else does; if it could not even start, the next one may.
                    installing = sent ? slot.Index : -1;
                }

                if (!sent)
                {
                    bool waits = slot.TriggerOff && now < slot.WaitUntil;
                    if (!waits || !slot.WaitSaid)
                    {
                        slot.WaitSaid = waits;
                        log.Info(Category, "Car: '" + slot.Entry.Name + "' cannot be " + (want ? "installed" : "taken off") + " here yet: " + why + (waits ? "; waited for" : string.Empty) + ".");
                    }

                    if (waits)
                    {
                        // Not a try: the part that switches its trigger on may still be on its way in.
                        slot.Tries--;
                    }
                }

                return;
            }

            if (game == want)
            {
                slot.Stuck = false;
                slot.Unreported = -1;
                slot.HeldBackUntil = 0.0;
                return;
            }

            if (slot.Stuck || slot.Unreported == (game ? 1 : 0))
            {
                return;
            }

            if (!game && !disassembling && !vehicles.SimulatesRoot(car))
            {
                // It came off by itself (its joint broke) in a game that does not run the car: whether it fell is the
                // word of the game that does. That word is waited for, and if it does not come the part is put back.
                if (slot.HeldBackUntil == 0.0)
                {
                    if (now - slot.PutBackAt > PutBacksForgetSeconds)
                    {
                        slot.PutBacks = 0;
                    }

                    slot.HeldBackUntil = now + HoldBackSeconds;
                    log.Info(Category, "Car: '" + slot.Entry.Name + "' came off by itself here, where the car is not run: the game that runs it says whether it fell.");
                    return;
                }

                if (now < slot.HeldBackUntil)
                {
                    return;
                }

                slot.HeldBackUntil = 0.0;
                slot.PutBackAt = now;
                if (++slot.PutBacks > MostPutBacks)
                {
                    // It falls again as soon as it is put back: left as it is, rather than put back for ever.
                    slot.Stuck = true;
                    log.Warning(Category, "Car: '" + slot.Entry.Name + "' keeps coming off here by itself: it is left off until the session's car changes there.");
                    return;
                }

                slot.Driving = true;
                slot.Tries = 0;
                slot.NextTry = now;
                slot.WaitUntil = 0.0;
                slot.WaitSaid = false;
                slot.Variant = shown.Variant(slot.Index);
                slot.PartRef = (ushort)shown.PartRef(slot.Index);
                slot.Player = AssemblyWire.NoPlayer;
                log.Info(Category, "Car: '" + slot.Entry.Name + "' did not fall in the game that runs the car: put back here.");
                return;
            }

            slot.HeldBackUntil = 0.0;

            // This game did it: its player, or the game that runs the car by itself (a part that falls off).
            ushort partRef = game ? JustInstalled(slot, now) : (ushort)0;
            AssemblyStep step = game ? AssemblyStep.Install(slot.Index, VariantOf(slot), partRef) : AssemblyStep.Remove(slot.Index);
            if (!current.Report(step))
            {
                slot.Unreported = game ? 1 : 0;
                return;
            }

            log.Info(
                Category,
                "Car: '" + slot.Entry.Name + "' " + (game ? "installed" : "taken off") + " here" + (partRef != 0 ? " (part #" + partRef.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty)
                + ": the others are told.");
            Changed(slot, game, true, now);
        }

        // Whether none of a part's bolts is being turned any more (once they are read, after the part settled in).
        private bool BoltsSettled(Slot slot)
        {
            if (frameTime < slot.BoltsFrom)
            {
                return false;
            }

            for (int b = 0; b < slot.Bolts.Length; b++)
            {
                if (slot.Bolts[b].Driving)
                {
                    return false;
                }
            }

            return true;
        }

        // What this game set to take a part off is set back once that is done or given up (Undetach says why).
        private static void ClearDetach(Slot slot)
        {
            if (slot.Detached != null)
            {
                slot.Detached.Value = false;
                slot.Detached = null;
            }
        }

        // The part that was going in is in, or will not go in: the next one may, a moment later.
        private void Free(Slot slot, double now)
        {
            if (installing == slot.Index)
            {
                installing = -1;
                nextInstall = now + BetweenInstallsSeconds;
            }
        }

        // The part to go in next: of those to be installed here and ready for a try, the lowest of the car (the catalogue's
        // rank: the order the session hands a whole car in, what goes under before what covers it), not the first of the
        // list (seen in a review on 2026-10-15: the cylinder head went in before the gasket and the pistons).
        private int NextInstall(AssemblyModel shown, double now)
        {
            if (installing >= 0)
            {
                return installing;
            }

            int best = -1;
            int lowest = int.MaxValue;
            for (int i = 0; i < slots.Length; i++)
            {
                Slot slot = slots[i];
                if (!slot.Driving || !slot.Steps || now < slot.NextTry || !shown.IsInstalled(i) || GameInstalled(slot))
                {
                    continue;
                }

                int rank = catalog.Rank(i);
                if (rank < lowest)
                {
                    lowest = rank;
                    best = i;
                }
            }

            return best;
        }

        // Whether a part is being put in or taken off here for the session.
        private bool PartsGoing()
        {
            if (installing >= 0)
            {
                return true;
            }

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Driving)
                {
                    return true;
                }
            }

            return false;
        }

        // A part went in: a wire given up because neither end was there may have one now, and a part that waits for its
        // trigger (which another part brings) waits again from now (seen in a review on 2026-10-15: joining with a whole
        // car to put in, the hubcaps waited for the wheels, the last of the list, and gave up).
        private void AfterInstall(double now)
        {
            AssemblyModel shown = current.Shown;
            for (int i = 0; i < slots.Length && shown != null; i++)
            {
                Slot slot = slots[i];
                if (slot.Driving && slot.WaitUntil != 0.0)
                {
                    slot.WaitUntil = now + TriggerWaitSeconds;
                }

                if (slot.Wire && slot.Stuck && !slot.WireDriving && shown.IsWired(i) && !GameWired(slot))
                {
                    slot.Stuck = false;
                    slot.WireDriving = true;
                    slot.Tries = 0;
                    slot.NextTry = now;
                    slot.WaitSaid = false;
                }
            }
        }

        // After a part went in or came off in this game.
        private void Changed(Slot slot, bool installed, bool local, double now)
        {
            slot.Released = null;
            vehicles.PartsChanged(car);
            if (installed)
            {
                AfterInstall(now);
                // Its bolts come with the stages the part brings: this game tells them once they show; another game
                // that installed it waits for that word before turning any, unless the session's car already says how
                // they are (a correction, the whole car).
                slot.BoltsFrom = now + SettleSeconds + (local || slot.Player == AssemblyWire.NoPlayer ? 0.0 : GraceSeconds);
                for (int b = 0; b < slot.Bolts.Length; b++)
                {
                    slot.Bolts[b].Driving = !local;
                    slot.Bolts[b].Turns = 0;
                    slot.Bolts[b].Stuck = false;
                    slot.Bolts[b].NextLook = double.NegativeInfinity;
                }

                return;
            }

            // What comes off lies loose: shared as the remover's, once the next look for loose objects finds it.
            if (!local && slot.Player != AssemblyWire.NoPlayer)
            {
                string loose = slot.Entry.Family == PartFamily.Clone ? slot.Entry.Spawn : slot.Entry.Part;
                if (loose != null)
                {
                    objects.Expect(loose, slot.Player, now);
                }
            }

            objects.ScanSoon(now);
        }

        // A part this game put in for the session, held by a joint: whatever was following someone else's copy was made
        // kinematic, and the game leaves that as it is when the joint holds the part (the battery: "trigger_battery",
        // "Assemble 2" adds a FixedJoint and nothing else; seen in a review on 2026-10-15). A part held by a joint moves.
        private void Unfreeze(Slot slot)
        {
            GameObject part = slot.Released;

            // Unity's null check is also true for a destroyed object.
            Joint joint = part == null ? null : part.GetComponent<Joint>();
            if (joint == null || !part.transform.IsChildOf(car) || joint.connectedBody == null || joint.connectedBody.transform != car)
            {
                return;
            }

            Rigidbody body = part.GetComponent<Rigidbody>();
            if (body != null && body.isKinematic)
            {
                body.isKinematic = false;
                log.Info(Category, "Car: '" + slot.Entry.Name + "' went in held by a joint, still kinematic from following the others: it moves now.");
            }
        }

        // A part this game took off for the session that its removal leaves where it was, without its joint, for the
        // player's hand (the battery: "Remove part" only destroys the joint). In the game where it was taken off it is in
        // the player's hand already: here it lies loose, to be shared as theirs.
        private void LetLoose(Slot slot)
        {
            GameObject part = slot.TakenOff;
            slot.TakenOff = null;

            // Unity's null check is also true for a destroyed object.
            if (part == null || !part.activeInHierarchy || !part.transform.IsChildOf(car) || part.GetComponent<Rigidbody>() == null || part.GetComponent<Joint>() != null)
            {
                return;
            }

            part.transform.parent = null;
            log.Info(Category, "Car: '" + slot.Entry.Name + "' was left on the car by its removal, as for a player's hand: it lies loose now.");
        }

        // A part handed to the game that it did not install: given back to the loose objects. One never handed to it (its
        // trigger stayed off, the part was not found) may wait where it was last shown, frozen, as the part the host's game
        // built in: given back to this game's physics (seen in a review on 2026-10-15).
        private void GiveBack(Slot slot, bool install)
        {
            GameObject part = slot.Released;
            slot.Released = null;
            if (part == null && install)
            {
                PartTrigger[] triggers = slot.Entry.Triggers;
                int which = slot.Variant % SatsumaAssembly.CornerFactor;
                string name = triggers.Length == 0 ? slot.Entry.Part : triggers[which >= 0 && which < triggers.Length ? which : 0].Part;
                GameObject waiting = objects.Abandon(slot.PartRef, name);
                if (waiting != null)
                {
                    log.Info(Category, "Car: '" + waiting.name + "', the part the others put into '" + slot.Entry.Name + "', is left loose here.");
                }

                return;
            }

            // Unity's null check is also true for a destroyed object.
            if (part == null || part.transform.IsChildOf(car))
            {
                return;
            }

            Rigidbody body = part.GetComponent<Rigidbody>();
            if (body != null && body.isKinematic)
            {
                body.isKinematic = false;
            }

            objects.ScanSoon(frameTime);
        }

        private void WorkWire(Slot slot, AssemblyModel shown, double now)
        {
            if (!slot.Wire)
            {
                return;
            }

            bool game = GameWired(slot);
            bool want = shown.IsWired(slot.Index);
            if (slot.WireDriving)
            {
                if (game == want)
                {
                    slot.WireDriving = false;
                    log.Info(Category, "Car: the wire '" + slot.Entry.Name + "' is " + (want ? "made" : "cut") + " here now.");
                    return;
                }

                if (now < slot.NextTry)
                {
                    return;
                }

                string why = "the game has no way to cut a wire";
                if (!want || slot.Tries >= MostTries)
                {
                    slot.WireDriving = false;
                    slot.Stuck = true;
                    log.Warning(Category, "Car: the wire '" + slot.Entry.Name + "' is not " + (want ? "made" : "cut") + " here: " + why + ".");
                    return;
                }

                slot.Tries++;
                slot.NextTry = now + RetrySeconds;
                if (!Connect(slot, out why))
                {
                    // An end that is not switched on comes with the part that carries it, which may still be on its way in
                    // (joining a session brings the whole car, the battery near the end): not a try while parts still go
                    // in or come off here.
                    bool waits = PartsGoing();
                    if (waits)
                    {
                        slot.Tries--;
                    }

                    if (!waits || !slot.WaitSaid)
                    {
                        slot.WaitSaid = waits;
                        log.Info(Category, "Car: the wire '" + slot.Entry.Name + "' cannot be made here yet: " + why + (waits ? "; waited for while parts go in" : string.Empty) + ".");
                    }
                }

                return;
            }

            if (game == want)
            {
                slot.Unreported = -1;
                return;
            }

            if (slot.Stuck || slot.Unreported == (game ? 1 : 0))
            {
                return;
            }

            if (!current.Report(AssemblyStep.Wire(slot.Index, game)))
            {
                slot.Unreported = game ? 1 : 0;
                return;
            }

            log.Info(Category, "Car: the wire '" + slot.Entry.Name + "' " + (game ? "made" : "cut") + " here: the others are told.");
        }

        private void WorkBolts(Slot slot, AssemblyModel shown, double now)
        {
            if (slot.Bolts.Length == 0 || now < slot.BoltsFrom)
            {
                return;
            }

            // The bolts of a part that is off do not turn, whatever its list says: they are the session's as they are.
            if (slot.Steps && !shown.IsInstalled(slot.Index) && !GameInstalled(slot))
            {
                return;
            }

            for (int b = 0; b < slot.Bolts.Length; b++)
            {
                BoltState bolt = slot.Bolts[b];
                int game = GameStage(slot, b, false);
                bool hidden = bolt.Object == null || !bolt.Object.activeInHierarchy;
                if (bolt.Driving && hidden && (catalog.Flags(slot.Index) & AssemblyEntryFlags.BoltsWhileLoose) != 0 && slot.Entry.Triggers.Length > 0)
                {
                    // The block's bolts show at its mounts only once it hangs there.
                    if (slot.HiddenSince == 0.0)
                    {
                        slot.HiddenSince = now;
                    }
                    else if (now >= slot.HiddenSince + BlockWaitSeconds)
                    {
                        BringBlock(slot);
                    }

                    continue;
                }

                if (!hidden)
                {
                    slot.HiddenSince = 0.0;
                }

                if (game < 0)
                {
                    continue;
                }

                int want = shown.Stage(slot.Index, b);
                if (game == want)
                {
                    bolt.Driving = false;
                    bolt.Stuck = false;
                    bolt.Unreported = -1;
                    continue;
                }

                if (bolt.Driving)
                {
                    if (now < bolt.NextTurn)
                    {
                        continue;
                    }

                    if (bolt.Turns >= MostTurns)
                    {
                        bolt.Driving = false;
                        bolt.Stuck = true;
                        log.Warning(Category, "Car: bolt " + b + " of '" + slot.Entry.Name + "' does not get to stage " + want + " here (it is at " + game + "): left as it is.");
                        continue;
                    }

                    if (Turn(slot, b, want > game ? 1 : -1))
                    {
                        bolt.Turns++;
                        boltsTurned++;
                    }

                    bolt.NextTurn = now + BoltTurnSeconds;
                    continue;
                }

                if (bolt.Stuck || bolt.Unreported == game)
                {
                    continue;
                }

                // This game turned it: one step per stage, as the wrench does.
                int delta = game > want ? 1 : -1;
                bool told = true;
                for (int n = Math.Abs(game - want); n > 0 && told; n--)
                {
                    told = current.Report(AssemblyStep.Turn(slot.Index, b, delta));
                    boltsTold += told ? 1 : 0;
                }

                if (!told)
                {
                    bolt.Unreported = game;
                }
            }
        }

        /// <summary>What "ss satsuma" says: the car here and in the session, and what is being done to it.</summary>
        public string Report(string what)
        {
            if (current == null || !loaded)
            {
                return "No Satsuma assembly shared here (no session, no world, or it is still being read).";
            }

            if (string.Equals(what, "build", StringComparison.OrdinalIgnoreCase))
            {
                build = new Builder(this);
                return "Putting the Satsuma together here, the game's way, one part at a time; the others are told each step. 'ss satsuma' says how far it got.";
            }

            PartEntry asked = what == null ? null : parts.Find(what);
            if (asked != null)
            {
                return DescribeEntry(slots[asked.Index]);
            }

            var text = new StringBuilder();
            text.Append(current.Describe() ?? "Car: nothing shared yet.");
            var game = new AssemblyModel(catalog);
            Read(game, current.Shown);
            string differences = game.DescribeDifferences(current.Shown, 8);
            text.Append("\nThis game: digest ").Append(AssemblyReplication.DigestText(game.Digest())).Append(", should show ")
                .Append(AssemblyReplication.DigestText(current.Shown.Digest()))
                .Append(string.IsNullOrEmpty(differences) ? " (the same)" : "; differs in " + differences);
            int driving = 0;
            int stuck = 0;
            var stuckNames = new List<string>();
            for (int i = 0; i < slots.Length; i++)
            {
                Slot slot = slots[i];
                bool boltsDriving = false;
                bool boltsStuck = false;
                for (int b = 0; b < slot.Bolts.Length; b++)
                {
                    boltsDriving |= slot.Bolts[b].Driving;
                    boltsStuck |= slot.Bolts[b].Stuck;
                }

                driving += slot.Driving || slot.WireDriving || boltsDriving ? 1 : 0;
                if (slot.Stuck || boltsStuck)
                {
                    stuck++;
                    if (stuckNames.Count < 6)
                    {
                        stuckNames.Add(slot.Entry.Name);
                    }
                }
            }

            text.Append("\nBeing done here: ").Append(driving).Append("; left as they are: ").Append(stuck)
                .Append(stuckNames.Count == 0 ? string.Empty : " (" + string.Join(", ", stuckNames.ToArray()) + ")");

            // Installed with bolts not at their last stage: what a player still has to tighten (or the game cannot).
            var loose = new List<string>();
            int looseCount = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                PartEntry entry = slots[i].Entry;
                if (entry.Bolts.Length == 0 || (slots[i].Steps && !current.Shown.IsInstalled(i)))
                {
                    continue;
                }

                int most = 0;
                for (int b = 0; b < entry.Bolts.Length; b++)
                {
                    most += entry.Bolts[b].Stages;
                }

                int tight = current.Shown.Tightness(i);
                if (tight < most && slots[i].Steps)
                {
                    looseCount++;
                    if (loose.Count < 10)
                    {
                        loose.Add(entry.Name + " " + tight + "/" + most);
                    }
                }
            }

            text.Append("\nInstalled with bolts to tighten: ").Append(looseCount).Append(loose.Count == 0 ? string.Empty : " (" + string.Join(", ", loose.ToArray()) + ")");
            string numbers = values.Describe();
            if (numbers != null)
            {
                text.Append("\n").Append(numbers);
            }

            if (build != null)
            {
                text.Append("\n").Append(build.Describe());
            }

            log.Info(Category, text.ToString().Replace("\n", " | "));
            return text.ToString();
        }
    }
}
