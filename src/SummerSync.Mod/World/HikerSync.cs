using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The hitchhikers (Jokke from the pub, the grandmother to church), one ride for everyone: the game of the player who
    /// picks them up runs it (the people one deals with are run by the game of whoever deals
    /// with them), and the others show them sitting in that vehicle.
    ///
    /// What is assumed about the game (the game with the bridge on 2026-10-16, docs/GAME_NOTES.md "Los que hacen dedo"):
    /// each hitchhiker's story is a machine ("Logic") that waits for the local player within 4 m at the wheel of a vehicle
    /// it takes ("Which car" reads "PlayerCurrentVehicle"), walks to the vehicle's "GetInPivot", hangs itself from it
    /// ("Open door"), rides (talks, idles; Jokke pays at the end), and gets out ("Start walking" puts the walker where the
    /// pivot is and walks away). Breaking the joint of the vehicle's "MassPassenger" kills them. Jokke appears after his call
    /// ("Activate"), which only the games that took the call make: the host's always does (the shared phone).
    ///
    /// So: when the host's Jokke appears, the others' does too (if theirs was waiting). The game whose hitchhiker gets in
    /// says so (the tally "hiker/(name)": the vehicle); the others' machines are paused and their hitchhiker sits in their
    /// copy of the vehicle, with the clip of getting in held at its end. When the ride ends, the others take theirs out where
    /// it got out, with the number of the story the ride changed (Jokke's "DrunkStage"), and it walks away the game's way.
    /// If it dies in the ride, it dies in every game.
    /// </summary>
    internal sealed class HikerSync
    {
        private const string DataPrefix = "SummerSync.hikers.";
        private const string TallyPrefix = "hiker/";
        private const string EventPrefix = "SUMMERSYNC HIKER ";
        private const int PhaseAppears = 1;
        private const int PhaseAboard = 2;
        private const int PhaseOff = 3;
        private const int PhaseDies = 4;
        private const double LookEvery = 2.0;
        private const double HitLongestSeconds = 15.0;
        private const float HitClearMetres = 4f;
        private const float OutsideMetres = 1.6f;

        private static readonly HashSet<string> Hooked = new HashSet<string>();
        private static HikerDefinition[] catalog;
        private static HikerSync active;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly List<Hiker> hikers = new List<Hiker>();
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();
        private readonly List<bool> heardCatchingUp = new List<bool>();
        private TallyReplication current;
        private Transform player;
        private double clock;
        private double nextLook;

        private sealed class Hiker
        {
            public HikerDefinition Spec;
            public PlayMakerFSM Logic;
            public PlayMakerFSM KeepMachine;
            public PlayMakerFSM HitMachine;
            public Transform Owner;
            public Transform Walker;
            public Transform Body;

            // The object whose clips get them in: the owner, or the data's "animated".
            public Transform Animated;
            public List<Held> Held;
            public byte Rider;
            public ushort Vehicle;
            public Transform Pivot;
            public string Clip;
            public Vector3 BodyPlace;
            public Quaternion BodyTurn;
            public bool RiddenHere;
            public bool Applying;
            public PlayMakerFSM Hit;
            public double HitAt;
            public Vector3 HitFrom;
        }

        // A machine paused while another game runs the ride, with what it was.
        private sealed class Held
        {
            public PlayMakerFSM Machine;
            public bool Enabled;
            public bool Restart;
        }

        public HikerSync(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
            if (catalog == null)
            {
                catalog = LoadCatalog(log);
            }

            for (int i = 0; i < catalog.Length; i++)
            {
                hikers.Add(new Hiker { Spec = catalog[i] });
            }
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is null without a world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            clock = now;
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current || localPlayer != player)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                Release();
                current = tallies;
                if (localPlayer != player)
                {
                    for (int i = 0; i < hikers.Count; i++)
                    {
                        hikers[i].Logic = null;
                    }
                }

                player = localPlayer;
                nextLook = now;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }
            }

            active = player == null ? null : this;
            if (player == null)
            {
                return;
            }

            if (now >= nextLook)
            {
                nextLook = now + LookEvery;
                for (int i = 0; i < hikers.Count; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    if (hikers[i].Logic == null)
                    {
                        Hook(hikers[i]);
                    }
                }
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value, heardCatchingUp[i]);
            }

            heard.Clear();
            heardCatchingUp.Clear();
            for (int i = 0; i < hikers.Count; i++)
            {
                // The car's hit back on, the hitchhiker clear of the vehicle it got out of.
                Hiker hiker = hikers[i];
                if (hiker.Hit != null && (now >= hiker.HitAt || hiker.Walker == null || (hiker.Walker.position - hiker.HitFrom).sqrMagnitude > HitClearMetres * HitClearMetres))
                {
                    hiker.Hit.enabled = true;
                    hiker.Hit = null;
                }
            }

            // A rider who left the session: the ride ends here where the vehicle is.
            for (int i = 0; i < hikers.Count; i++)
            {
                Hiker hiker = hikers[i];
                if (hiker.Held != null && (manager.Players == null || manager.Players.Find(hiker.Rider) == null))
                {
                    log.Info(LogCategory.Player, "Hitchhikers: player " + hiker.Rider + " left with " + hiker.Spec.Name + " aboard; " + hiker.Spec.Name + " gets out here.");
                    GetOut(hiker, Vector3.zero, null);
                }
            }
        }

        /// <summary>Every hitchhiker shown sitting in another game's vehicle gets out where the vehicle is, and goes on here.</summary>
        public void Release()
        {
            for (int i = 0; i < hikers.Count; i++)
            {
                if (hikers[i].Held != null)
                {
                    GetOut(hikers[i], Vector3.zero, null);
                }

                hikers[i].RiddenHere = false;
            }

            heard.Clear();
            heardCatchingUp.Clear();
        }

        private static HikerDefinition[] LoadCatalog(Logger log)
        {
            var found = new List<HikerDefinition>();
            Assembly assembly = typeof(HikerSync).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            for (int i = 0; i < resources.Length; i++)
            {
                if (!resources[i].StartsWith(DataPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resources[i]))
                    using (var reader = new StreamReader(stream))
                    {
                        found.Add(HikerDefinition.Parse(reader.ReadToEnd()));
                    }
                }
                catch (FormatException exception)
                {
                    log.Error(LogCategory.Player, "The hitchhiker file '" + resources[i] + "' cannot be read; that hitchhiker is each game's. " + exception.Message);
                }
            }

            return found.ToArray();
        }

        // The machines found, the mod's actions first in the states that matter, and the ways in to the ones the others are
        // taken to. Once per machine.
        private void Hook(Hiker hiker)
        {
            HikerDefinition spec = hiker.Spec;
            Transform owner = FindPath(spec.Logic.Path);
            PlayMakerFSM logic = owner == null ? null : DoorSync.FindMachine(owner, spec.Logic.Machine);
            if (logic == null)
            {
                return;
            }

            try
            {
                logic.InitializeFSM();
                string[] states = { spec.Aboard, spec.Off, spec.Dies, spec.Appears };
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i] != null && !DoorSync.HasState(logic, states[i]))
                    {
                        throw new InvalidOperationException("no state '" + states[i] + "'");
                    }
                }

                string[] entered = { spec.Off, spec.Dies, spec.Appears };
                for (int i = 0; i < entered.Length; i++)
                {
                    if (entered[i] != null && !DoorSync.HasGlobalTransition(logic, EventPrefix + entered[i]))
                    {
                        logic.AddGlobalTransition(EventPrefix + entered[i], entered[i]);
                    }
                }

                int id = logic.GetInstanceID();
                string name = spec.Name;
                Inject(logic, id, spec.Aboard, delegate { OnState(name, PhaseAboard); });
                Inject(logic, id, spec.Off, delegate { OnState(name, PhaseOff); });
                Inject(logic, id, spec.Dies, delegate { OnState(name, PhaseDies); });
                if (spec.Appears != null)
                {
                    Inject(logic, id, spec.Appears, delegate { OnState(name, PhaseAppears); });
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Player, "Hitchhikers: '" + spec.Logic.Path + " :: " + spec.Logic.Machine + "' is not as this build expects (" + exception.Message + "); " + spec.Name + " is each game's.");
                hiker.Logic = logic;
                hiker.Owner = null;
                return;
            }

            hiker.Logic = logic;
            hiker.Owner = owner;

            // Found now, while it is where the file says: in a ride it hangs from the vehicle.
            HikerMachine keep = spec.Keep;
            Transform keeper = keep == null || keep.Path == spec.Logic.Path ? null : FindPath(keep.Path);
            hiker.KeepMachine = keep == null ? null : keep.Path == spec.Logic.Path && keep.Machine == spec.Logic.Machine ? logic
                : keeper == null ? null : DoorSync.FindMachine(keeper, keep.Machine);
            Transform hitAt = spec.Hit == null ? null : FindPath(spec.Hit.Path);
            hiker.HitMachine = hitAt == null ? null : DoorSync.FindMachine(hitAt, spec.Hit.Machine);
            hiker.Walker = FindPath(spec.Walker);
            hiker.Body = FindPath(spec.Body);
            hiker.Animated = spec.Animated == null ? owner : FindPath(spec.Animated) ?? owner;
            log.Info(LogCategory.Player, "Hitchhikers: " + spec.Name + "'s ride is one for everyone: whoever picks them up runs it, the others see them aboard.");
        }

        private static void Inject(PlayMakerFSM machine, int id, string state, Action action)
        {
            if (Hooked.Add(id.ToString(CultureInfo.InvariantCulture) + "/" + state))
            {
                machine.FsmInject(state, action, false, 0, false);
            }
        }

        private static void OnState(string name, int phase)
        {
            HikerSync sync = active;
            if (sync != null)
            {
                sync.Entered(name, phase);
            }
        }

        // This game's hitchhiker reached a state that matters.
        private void Entered(string name, int phase)
        {
            Hiker hiker = Find(name);
            NetSession session = manager.Session;
            if (hiker == null || hiker.Applying || hiker.Owner == null || current == null || session == null || session.State != SessionState.Connected
                || !(manager.Mode == NetworkMode.Host || manager.Mode == NetworkMode.Client))
            {
                return;
            }

            switch (phase)
            {
                case PhaseAppears:
                    // The host's Jokke is the one who called; the others' come out with him.
                    if (session.Role == SessionRole.Host)
                    {
                        Tell(hiker, PhaseAppears, 0, Vector3.zero);
                        log.Info(LogCategory.Player, "Hitchhikers: " + name + " is out here; the others are told.");
                    }

                    break;
                case PhaseAboard:
                    Transform pivot = PivotOf(hiker);
                    Transform root = pivot == null ? null : vehicles.RootOf(pivot);
                    ushort vehicle;
                    if (root == null || !vehicles.IdOf(root, out vehicle))
                    {
                        log.Warning(LogCategory.Player, "Hitchhikers: " + name + " got in a vehicle here that is not shared; the others do not see it.");
                        return;
                    }

                    hiker.RiddenHere = true;
                    hiker.Vehicle = vehicle;
                    Tell(hiker, PhaseAboard, vehicle, Vector3.zero);
                    log.Info(LogCategory.Player, "Hitchhikers: " + name + " got in '" + root.name + "' (vehicle " + vehicle + ") here; this game runs the ride, the others see them aboard.");
                    break;
                case PhaseOff:
                    if (!hiker.RiddenHere)
                    {
                        return;
                    }

                    // Out where the seat is, walking the way the walker faced when it got in: the others the same.
                    hiker.RiddenHere = false;
                    Transform at = PivotOf(hiker);
                    ushort facing = (ushort)Mathf.RoundToInt(Mathf.Repeat(hiker.Walker.eulerAngles.y, 360f));
                    Tell(hiker, PhaseOff, facing, at == null ? hiker.Walker.position : at.position);
                    log.Info(LogCategory.Player, "Hitchhikers: " + name + " got out here (" + KeepText(hiker) + "); the others take theirs out there.");
                    break;
                case PhaseDies:
                    if (!hiker.RiddenHere)
                    {
                        return;
                    }

                    hiker.RiddenHere = false;
                    Tell(hiker, PhaseDies, hiker.Vehicle, Vector3.zero);
                    log.Info(LogCategory.Player, "Hitchhikers: " + name + " died here in the ride; they die in the others' games too.");
                    break;
            }
        }

        private void Tell(Hiker hiker, int phase, ushort vehicle, Vector3 at)
        {
            int token = (int)((long)(clock * 1000.0) & 0x7FFFFFFF);
            current.Set(
                TallyPrefix + hiker.Spec.Name,
                new[] { token, phase, manager.Session.LocalPlayerId, vehicle, Mathf.RoundToInt(at.x * 100f), Mathf.RoundToInt(at.y * 100f), Mathf.RoundToInt(at.z * 100f), ReadKeep(hiker) });
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(TallyPrefix, StringComparison.Ordinal) && values.Length >= 8)
            {
                heard.Add(new KeyValuePair<string, int[]>(key, values));
                heardCatchingUp.Add(current.CatchingUp);
            }
        }

        // Another game's hitchhiker: out, aboard, off or dead.
        private void Hear(string key, int[] values, bool catchingUp)
        {
            Hiker hiker = Find(key.Substring(TallyPrefix.Length));
            NetSession session = manager.Session;
            if (hiker == null || hiker.Logic == null || hiker.Owner == null || session == null || values[2] == session.LocalPlayerId)
            {
                return;
            }

            int phase = values[1];
            byte rider = (byte)values[2];
            ushort vehicle = (ushort)values[3];
            var at = new Vector3(values[4] / 100f, values[5] / 100f, values[6] / 100f);
            switch (phase)
            {
                case PhaseAppears:
                    if (!catchingUp && hiker.Spec.Appears != null && Array.IndexOf(hiker.Spec.AppearsFrom, hiker.Logic.ActiveStateName) >= 0)
                    {
                        Send(hiker, hiker.Spec.Appears);
                        log.Info(LogCategory.Player, "Hitchhikers: " + hiker.Spec.Name + " is out in the host's game; out here too.");
                    }

                    break;
                case PhaseAboard:
                    if (hiker.RiddenHere)
                    {
                        // Two games picked them up at once: the smaller number keeps the ride.
                        if (rider > session.LocalPlayerId)
                        {
                            return;
                        }

                        log.Warning(LogCategory.Player, "Hitchhikers: " + hiker.Spec.Name + " got in here and in player " + rider + "'s game at once; that game keeps the ride.");
                        GameObject mass = Variable(hiker, hiker.Spec.PassengerMass);
                        if (mass != null)
                        {
                            mass.SetActive(false);
                        }

                        hiker.RiddenHere = false;
                    }

                    Aboard(hiker, rider, vehicle);
                    break;
                case PhaseOff:
                    if (!catchingUp || hiker.Held != null)
                    {
                        // Getting out, the vehicle's place in the message is the way the walker faces.
                        GetOut(hiker, at, values[7], vehicle);
                    }

                    break;
                case PhaseDies:
                    if (!catchingUp)
                    {
                        Die(hiker);
                    }

                    break;
            }
        }

        // Sitting in this game's copy of the vehicle, as it sits in the game that runs the ride.
        private void Aboard(Hiker hiker, byte rider, ushort vehicle)
        {
            Transform root = vehicles.RootOf(vehicle);
            Transform pivot = root == null ? null : root.Find(hiker.Spec.PivotFor(root.name));
            if (pivot == null)
            {
                log.Warning(LogCategory.Player, "Hitchhikers: " + hiker.Spec.Name + " got in vehicle " + vehicle + " in player " + rider + "'s game, which has no '" + hiker.Spec.Pivot + "' here; not shown aboard.");
                return;
            }

            if (hiker.Held == null)
            {
                hiker.Held = new List<Held>();
                Hold(hiker, hiker.Logic);
                for (int i = 0; i < hiker.Spec.Paused.Length; i++)
                {
                    Transform at = FindPath(hiker.Spec.Paused[i].Path);
                    PlayMakerFSM machine = at == null ? null : DoorSync.FindMachine(at, hiker.Spec.Paused[i].Machine);
                    if (machine != null)
                    {
                        Hold(hiker, machine);
                    }
                }
            }

            hiker.Rider = rider;
            hiker.Vehicle = vehicle;
            hiker.Pivot = pivot;
            FsmGameObject pivotVariable = hiker.Logic.FsmVariables.FindFsmGameObject(hiker.Spec.Pivot);
            if (pivotVariable != null)
            {
                // Where the game's "Start walking" puts the walker when the ride ends.
                pivotVariable.Value = pivot.gameObject;
            }

            if (hiker.Body != null)
            {
                // Where it is on the walker: the clip of getting in moves it to the seat, and stopped it stays there.
                hiker.BodyPlace = hiker.Body.localPosition;
                hiker.BodyTurn = hiker.Body.localRotation;
                hiker.Body.gameObject.SetActive(true);
                Animation[] walking = hiker.Body.GetComponentsInChildren<Animation>();
                for (int i = 0; i < walking.Length; i++)
                {
                    walking[i].Stop();
                }
            }

            hiker.Owner.parent = pivot;
            hiker.Owner.localPosition = Vector3.zero;
            hiker.Owner.localRotation = Quaternion.identity;
            hiker.Clip = hiker.Spec.ClipFor(root.name);
            Animation animation = hiker.Animated == null ? null : hiker.Animated.GetComponent<Animation>();
            AnimationState clip = animation == null ? null : animation[hiker.Clip];
            if (clip != null)
            {
                clip.wrapMode = WrapMode.ClampForever;
                animation.CrossFade(hiker.Clip, 0.3f);
            }

            log.Info(
                LogCategory.Player,
                "Hitchhikers: " + hiker.Spec.Name + " rides in '" + root.name + "' (vehicle " + vehicle + ") in player " + rider + "'s game; shown sitting there here"
                + (clip == null ? " (no clip '" + hiker.Clip + "' here)" : string.Empty) + "; paused: " + Machines(hiker) + ".");
        }

        // The ride is over: out where it got out, with the story where the ride left it, walking away the game's way.
        private void GetOut(Hiker hiker, Vector3 at, int? keep)
        {
            GetOut(hiker, at, keep, -1);
        }

        private void GetOut(Hiker hiker, Vector3 at, int? keep, int facing)
        {
            bool wasAboard = hiker.Held != null;
            if (wasAboard && at == Vector3.zero && hiker.Pivot != null)
            {
                at = hiker.Pivot.position;
            }

            // Out of the vehicle, on the passenger's side (the right in every vehicle that takes them): in the game that ran
            // the ride the walker starts in the seat and walks out, but this game's copy of the vehicle is kinematic and
            // walls them in (seen with the bridge: the walker's "Obstacle" turning for ever, "Move" off).
            Transform vehicle = wasAboard ? vehicles.RootOf(hiker.Vehicle) : null;
            if (vehicle != null && at != Vector3.zero)
            {
                at += vehicle.right * OutsideMetres;
            }

            Resume(hiker);
            if (keep.HasValue)
            {
                WriteKeep(hiker, keep.Value);
            }

            // Getting out switches on the trigger that is hit by cars, inside the vehicle: in this game's copy of it, which is
            // kinematic, that is a car hitting them (seen with the bridge: Jokke run over by the van he got out of, there and
            // as he walked out of it). Off until they are clear of it.
            // Unity's null check is also true for a destroyed object.
            if (wasAboard && hiker.HitMachine != null)
            {
                hiker.Hit = hiker.HitMachine;
                if (hiker.Hit != null)
                {
                    hiker.Hit.enabled = false;
                    hiker.HitAt = clock + HitLongestSeconds;
                    hiker.HitFrom = at;
                }
            }

            Send(hiker, hiker.Spec.Off);
            if (at != Vector3.zero && hiker.Walker != null)
            {
                hiker.Walker.position = at;
            }

            if (facing >= 0 && hiker.Walker != null)
            {
                hiker.Walker.rotation = Quaternion.Euler(0f, facing, 0f);
            }

            log.Info(
                LogCategory.Player,
                "Hitchhikers: " + hiker.Spec.Name + " got out " + (wasAboard ? "of the ride shown here" : "in another game") + " and walks away here (" + KeepText(hiker) + "); its story in '"
                + hiker.Logic.ActiveStateName + "', " + (hiker.Logic.enabled ? "on" : "OFF") + ".");
        }

        private void Die(Hiker hiker)
        {
            Resume(hiker);
            Send(hiker, hiker.Spec.Dies);
            log.Info(LogCategory.Player, "Hitchhikers: " + hiker.Spec.Name + " died in the ride in another game; here too.");
        }

        private static string Machines(Hiker hiker)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; hiker.Held != null && i < hiker.Held.Count; i++)
            {
                Held held = hiker.Held[i];
                text.Append(i == 0 ? string.Empty : ", ").Append(held.Machine.FsmName).Append(held.Enabled ? " (was on, in '" : " (was off, in '").Append(held.Machine.ActiveStateName).Append("')");
            }

            return text.ToString();
        }

        private static void Hold(Hiker hiker, PlayMakerFSM machine)
        {
            var held = new Held { Machine = machine, Enabled = machine.enabled, Restart = machine.Fsm != null && machine.Fsm.RestartOnEnable };
            if (machine.Fsm != null)
            {
                machine.Fsm.RestartOnEnable = false;
            }

            machine.enabled = false;
            hiker.Held.Add(held);
        }

        // Every paused machine as it was, and the clip held at its end let go.
        private static void Resume(Hiker hiker)
        {
            if (hiker.Held == null)
            {
                return;
            }

            for (int i = 0; i < hiker.Held.Count; i++)
            {
                Held held = hiker.Held[i];

                // Unity's null check is also true for a destroyed object.
                if (held.Machine == null)
                {
                    continue;
                }

                held.Machine.enabled = held.Enabled;
                if (held.Machine.Fsm != null)
                {
                    held.Machine.Fsm.RestartOnEnable = held.Restart;
                }
            }

            hiker.Held = null;
            Animation animation = hiker.Animated == null ? null : hiker.Animated.GetComponent<Animation>();
            AnimationState clip = animation == null || hiker.Clip == null ? null : animation[hiker.Clip];
            if (clip != null)
            {
                animation.Stop(hiker.Clip);
                clip.wrapMode = WrapMode.Default;
            }

            // Off the seat: where it was on the walker. Left where the clip put it, the hitchhiker's own trigger sits in front
            // of the ray it looks for obstacles with (seen with the bridge: Jokke turning on the spot a long while).
            if (hiker.Body != null)
            {
                hiker.Body.localPosition = hiker.BodyPlace;
                hiker.Body.localRotation = hiker.BodyTurn;
            }
        }

        private static void Send(Hiker hiker, string state)
        {
            hiker.Applying = true;
            try
            {
                hiker.Logic.SendEvent(EventPrefix + state);
            }
            finally
            {
                hiker.Applying = false;
            }
        }

        private Transform PivotOf(Hiker hiker)
        {
            GameObject pivot = Variable(hiker, hiker.Spec.Pivot);
            return pivot == null ? null : pivot.transform;
        }

        private static GameObject Variable(Hiker hiker, string name)
        {
            FsmGameObject variable = hiker.Logic == null ? null : hiker.Logic.FsmVariables.FindFsmGameObject(name);
            return variable == null ? null : variable.Value;
        }

        private static int ReadKeep(Hiker hiker)
        {
            NamedVariable keep = KeepVariable(hiker);
            var number = keep as FsmInt;
            var flag = keep as FsmBool;
            var real = keep as FsmFloat;
            return number != null ? number.Value : flag != null ? (flag.Value ? 1 : 0) : real != null ? Mathf.RoundToInt(real.Value * 100f) : 0;
        }

        private static void WriteKeep(Hiker hiker, int value)
        {
            NamedVariable keep = KeepVariable(hiker);
            var number = keep as FsmInt;
            var flag = keep as FsmBool;
            var real = keep as FsmFloat;
            if (number != null)
            {
                number.Value = value;
            }
            else if (flag != null)
            {
                flag.Value = value != 0;
            }
            else if (real != null)
            {
                real.Value = value / 100f;
            }
        }

        private static NamedVariable KeepVariable(Hiker hiker)
        {
            HikerMachine keep = hiker.Spec.Keep;

            // Unity's null check is also true for a destroyed object.
            PlayMakerFSM machine = hiker.KeepMachine;
            if (keep == null || machine == null)
            {
                return null;
            }

            FsmVariables variables = machine.FsmVariables;
            return (NamedVariable)variables.FindFsmInt(keep.Variable) ?? (NamedVariable)variables.FindFsmBool(keep.Variable) ?? variables.FindFsmFloat(keep.Variable);
        }

        private static string KeepText(Hiker hiker)
        {
            return hiker.Spec.Keep == null ? "nothing of the story to tell" : hiker.Spec.Keep.Variable + " " + ReadKeep(hiker).ToString(CultureInfo.InvariantCulture);
        }

        private Hiker Find(string name)
        {
            for (int i = 0; i < hikers.Count; i++)
            {
                if (hikers[i].Spec.Name == name)
                {
                    return hikers[i];
                }
            }

            return null;
        }

        // A path from the scene's top, whatever below it is switched off. One the game took to the scene's top (the moving
        // Jokke, unhooked from his house when the job starts) is looked for there too, from where its name is in the path.
        private static Transform FindPath(string path)
        {
            int slash = path.IndexOf('/');
            GameObject top = GameObject.Find("/" + (slash < 0 ? path : path.Substring(0, slash)));
            if (top != null && slash < 0)
            {
                return top.transform;
            }

            Transform found = top == null ? null : top.transform.Find(path.Substring(slash + 1));
            if (found != null || slash < 0)
            {
                return found;
            }

            string[] names = path.Split('/');
            for (int k = 1; k < names.Length; k++)
            {
                GameObject moved = GameObject.Find("/" + names[k]);
                if (moved == null)
                {
                    continue;
                }

                string rest = k + 1 < names.Length ? string.Join("/", names, k + 1, names.Length - k - 1) : string.Empty;
                Transform below = rest.Length == 0 ? moved.transform : moved.transform.Find(rest);
                if (below != null)
                {
                    return below;
                }
            }

            return null;
        }
    }
}
