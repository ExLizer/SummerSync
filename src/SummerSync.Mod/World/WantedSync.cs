using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The police come for the player who did the crime (docs/DECISIONS.md).
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "La policía en casa"): a player is wanted in their own game
    /// ("PlayerWanted", and the counts of "Systems/PlayerWanted :: Activate"). A crime is told by the game whose player is
    /// near and driving (the "Distance?" of a run-over person's "CarHit", or of a crashed car's "Crash"), whoever's car it
    /// was. When the wanted player wakes up by day, their game sends the police home ("At home" switches "COPS" on: the
    /// police at the front door and at Teimo's shop); walking past them is the arrest ("GLOBALEVENT" to "Activate", which
    /// passes the player out, moves the day on and wakes them up in the cell), and the cell lets them out when the days of
    /// their sentence have gone by ("JAIL/Functions :: Time").
    ///
    /// What the mod does, in a session:
    /// - A crime counts only for the driver of the car that did it: in every game, a run over that another player's car
    ///   did is an accident here; a crash of the host's people is the crime of the nearest player who drives, told to
    ///   their game ("crime/&lt;player&gt;").
    /// - The police of a wanted player are seen in every game ("police/home/&lt;player&gt;"), but in a game whose player
    ///   they do not come for they arrest nobody (their triggers and the arrest warrant are paused); a player who opens the
    ///   door to them there hears what they say, and they go on waiting.
    /// - In the wanted player's game, the door opened by someone else (shared by <see cref="DoorSync"/>) is not the wanted
    ///   player opening it: the police do not take it for that.
    /// - Jail moves nobody's clock (<see cref="SparedClock"/>: the arrest's new day and hour, the weather started over and
    ///   the sun's block begun again are held back), and each day of the sentence is
    ///   <see cref="SentenceDaySeconds"/> of play; then the cell lets the player out, by day or not.
    /// - What a guest is wanted for is the guest's own (<see cref="GuestSync"/> keeps it with the host's save): a guest
    ///   does not take up the host's crimes from the save it plays.
    /// </summary>
    internal sealed class WantedSync
    {
        private const LogCategory Category = LogCategory.Fsm;

        private const string WantedPath = "Systems/PlayerWanted";
        private const string WantedMachine = "Activate";
        private const string HomeState = "At home";
        private const string IdleState = "Idle";
        private const string CheckCrimeState = "Check crime";
        private const string AdvanceDayState = "Advance day";
        private const string WakeUpState = "Wake up";
        private const string ToJailState = "State 2";
        private const string IdleEvent = "SSYNC WANTED IDLE";
        private const string CheckEvent = "SSYNC WANTED CHECK";
        private const string WantedGlobal = "PlayerWanted";
        private const string VehicleGlobal = "PlayerCurrentVehicle";

        // The states of the police coming home, from "At home" back to it ("CHECK", the sentence worked out, the warrant).
        private static readonly string[] HomeStates = { HomeState, "Add crimes 2", "Calculate time 4", "Set data" };

        private static readonly string[] Triggers = { "TriggerAreaHome", "TriggerFrontDoor", "TriggerRearDoor", "TriggerAreaTeimo" };
        private const string TriggerMachine = "Player";
        private const string WarrantName = "ArrestWarrant";
        private const string WarrantMachine = "Use";
        private const string SpeakPath = "CopHome2/Speak";
        private const string SpeakMachine = "Speak";
        private const string DoorOpenEvent = "DOOROPEN";
        private const string KnockEvent = "KNOCK";

        private const string DoorPath = "YARD/Building/LIVINGROOM/DoorFront/Pivot/Handle";
        private const string DoorMachine = "Use";
        private const string TestCopsState = "Test cops";
        private const string DoorCopsVariable = "CopsAtHome";
        private const string DoorOpenVariable = "DoorOpen";

        // Within this of the door's handle, the one who opened it is the local player: the game's own reach is 1.5 m.
        private const float DoorReach = 2.5f;

        private const string JailFunctions = "Functions";
        private const string JailMachine = "Time";
        private const string JailWaitState = "State 1";
        private const string JailDayEvent = "DAY";
        private const string JailGetTimeState = "Get time";
        private const string JailReleaseState = "Play sound";
        private const string JailReleaseEvent = "SSYNC JAIL RELEASE";
        private const float JailReach = 10f;

        // Each day of a sentence, in seconds of play (a few real minutes).
        private const double SentenceDaySeconds = 180.0;

        private const string HomeKey = "police/home/";
        private const string CrimeKey = "crime/";

        // The game's decision of whose crime it is: "FloatCompare" of "Distance?" against "CrimeDistanceTolerance".
        private const string CrimeState = "Distance?";
        private const string CrimeTolerance = "CrimeDistanceTolerance";
        private const string HitMachine = "CarHit";
        private const string HitterVariable = "Collider";

        // Around a crashed car, how far a player's car counts as the one that crashed it.
        private const float CrashReach = 10f;

        // What a guest is wanted for, kept with the host's save: the counts of "Activate" and the fines.
        private const string RecordPrefix = "Wanted.";
        private static readonly string[] Counts = { "ManSlaughter", "PoliceEvasion", "TrafficFatality", "AttemptedManslaughter", "DaysInJail" };
        private static readonly string[] FineGlobals = { "PlayerFines", "PlayerFinesDay" };

        private static readonly HashSet<int> DoorsHooked = new HashSet<int>();
        private static WantedSync active;
        private static bool patched;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly Dictionary<byte, bool> homeOf = new Dictionary<byte, bool>();
        private readonly Dictionary<byte, int> crimesOf = new Dictionary<byte, int>();
        private readonly List<byte> left = new List<byte>();
        private readonly List<PausedMachine> paused = new List<PausedMachine>();
        private readonly List<FsmStateAction> heldBack = new List<FsmStateAction>();
        private TallyReplication current;
        private NetSession session;
        private Transform player;
        private PlayMakerFSM wanted;
        private PlayMakerFSM door;
        private PlayMakerFSM jail;
        private GameObject cops;
        private GameObject jailRoot;
        private FsmBool wantedFlag;
        private FsmString vehicleName;
        private bool searched;
        private bool told;
        private bool ownCops;
        private bool mirroring;
        private bool copsSwitchedOn;
        private bool doorWasOpen;
        private bool doorCopsHeld;
        private bool jailed;
        private double nextDay;
        private bool releaseSent;
        private bool holding;
        private int crimeHeard = -1;

        public WantedSync(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null without a world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                Release();
                current = tallies;
                player = localPlayer;
                session = current == null ? null : manager.Session;
                if (current != null && session != null)
                {
                    current.StateWanted += OnStateWanted;
                    session.PlayerLeft += OnPlayerLeft;
                    active = this;
                    Patch();
                }
            }

            if (current == null || session == null || session.State != SessionState.Connected)
            {
                return;
            }

            if (!searched)
            {
                searched = true;
                Search();
            }

            if (wanted == null)
            {
                return;
            }

            for (int i = 0; i < left.Count; i++)
            {
                Forget(left[i]);
            }

            left.Clear();
            if (doorCopsHeld)
            {
                // The door was taken for opened by someone else in the state that tells the police; it is theirs again.
                doorCopsHeld = false;
                SetDoorCops(true);
            }

            if (!GuestSync.Settled)
            {
                // A guest is still the save's player (the host), wanted for what the host is: the police here arrest
                // nobody until it has taken up what it is wanted for itself.
                Unsettled();
                return;
            }

            if (holding)
            {
                holding = false;
                ResumeArrests();
            }

            TellOwnCops();
            Mirror();
            WatchDoor();
            KeepSentence(now);
        }

        private void Unsettled()
        {
            if (!holding && cops != null && cops.activeSelf)
            {
                holding = true;
                PauseArrests();
                log.Info(Category, "Wanted: this guest is still the save's player; the police here arrest nobody until it is itself (" + paused.Count + " paused).");
            }
        }

        private void ResumeArrests()
        {
            for (int i = 0; i < paused.Count; i++)
            {
                paused[i].Resume();
            }

            paused.Clear();
        }

        /// <summary>Everything back as the game had it: the session is over, or the world.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            if (session != null)
            {
                session.PlayerLeft -= OnPlayerLeft;
            }

            StopMirroring();
            SparedClock.Release(heldBack);
            if (doorCopsHeld)
            {
                doorCopsHeld = false;
                SetDoorCops(true);
            }

            if (active == this)
            {
                active = null;
            }

            current = null;
            session = null;
            homeOf.Clear();
            crimesOf.Clear();
            left.Clear();
            searched = false;
            holding = false;
            told = false;
            ownCops = false;
            jailed = false;
            releaseSent = false;
            crimeHeard = -1;
            wanted = null;
            door = null;
            jail = null;
            cops = null;
            jailRoot = null;
        }

        /// <summary>
        /// For <see cref="GuestSync"/>: what the local player is wanted for, in the values of its record.
        /// </summary>
        public static void ReadInto(IDictionary<string, float> values)
        {
            PlayMakerFSM machine = FindWanted();
            if (machine == null)
            {
                return;
            }

            for (int i = 0; i < Counts.Length; i++)
            {
                FsmInt count = machine.FsmVariables.FindFsmInt(Counts[i]);
                if (count != null)
                {
                    values[RecordPrefix + Counts[i]] = count.Value;
                }
            }

            FsmVariables globals = FsmVariables.GlobalVariables;
            for (int i = 0; i < FineGlobals.Length; i++)
            {
                FsmFloat fine = globals.FindFsmFloat(FineGlobals[i]);
                if (fine != null)
                {
                    values[RecordPrefix + FineGlobals[i]] = fine.Value;
                }
            }

            FsmBool flag = globals.FindFsmBool(WantedGlobal);
            if (flag != null)
            {
                values[RecordPrefix + WantedGlobal] = flag.Value ? 1f : 0f;
            }
        }

        /// <summary>
        /// For <see cref="GuestSync"/>: a guest in the host's world takes up what it is wanted for from its record, or
        /// nothing for a guest the host's save does not have yet; not what the save's player (the host) is wanted for.
        /// </summary>
        public static void ApplyGuest(IDictionary<string, float> values, Logger log)
        {
            PlayMakerFSM machine = FindWanted();
            if (machine == null)
            {
                return;
            }

            int crimes = 0;
            for (int i = 0; i < Counts.Length; i++)
            {
                FsmInt count = machine.FsmVariables.FindFsmInt(Counts[i]);
                float value;
                int taken = values != null && values.TryGetValue(RecordPrefix + Counts[i], out value) ? Mathf.RoundToInt(value) : 0;

                // A guest let go in jail is not put back in it: the cell is not where the host's world has it.
                if (Counts[i] == "DaysInJail")
                {
                    taken = 0;
                }

                if (count != null)
                {
                    count.Value = taken;
                }

                crimes += taken;
            }

            FsmVariables globals = FsmVariables.GlobalVariables;
            for (int i = 0; i < FineGlobals.Length; i++)
            {
                FsmFloat fine = globals.FindFsmFloat(FineGlobals[i]);
                float value;
                if (fine != null)
                {
                    fine.Value = values != null && values.TryGetValue(RecordPrefix + FineGlobals[i], out value) ? value : 0f;
                }
            }

            float said;
            bool flagged = values != null && values.TryGetValue(RecordPrefix + WantedGlobal, out said) && said > 0f;
            FsmBool flag = globals.FindFsmBool(WantedGlobal);
            if (flag != null)
            {
                flag.Value = flagged;
            }

            bool atHome = Array.IndexOf(HomeStates, machine.ActiveStateName) >= 0;
            if (crimes > 0 || flagged)
            {
                // The game's own way at loading: a wanted player has the police at home.
                Go(machine, CheckEvent, CheckCrimeState);
                log.Info(Category, "Wanted: this guest is wanted for its own crimes (" + crimes + "); the police come for it.");
                return;
            }

            if (atHome)
            {
                // The host's save had the police at home for the host: not for this guest.
                Go(machine, IdleEvent, IdleState);
                SetDoorCopsOf(false);
                GameObject police = CopsOf(machine);
                if (police != null && police.activeSelf)
                {
                    police.SetActive(false);
                }

                log.Info(Category, "Wanted: the host's save had the police at home, for the host; this guest is wanted for nothing.");
            }
        }

        private static PlayMakerFSM FindWanted()
        {
            GameObject found = GameObject.Find(WantedPath);
            return found == null ? null : DoorSync.FindMachine(found.transform, WantedMachine);
        }

        // Takes the machine to that state by a transition of the mod's.
        private static void Go(PlayMakerFSM machine, string eventName, string state)
        {
            machine.AddGlobalTransition(eventName, state);
            machine.SendEvent(eventName);
        }

        private void Search()
        {
            wanted = FindWanted();
            if (wanted == null || !DoorSync.HasState(wanted, HomeState) || !DoorSync.HasState(wanted, AdvanceDayState) || !DoorSync.HasState(wanted, WakeUpState))
            {
                log.Warning(Category, "Wanted: no '" + WantedPath + " :: " + WantedMachine + "' as this build expects; each game's police come for its own player as the game decides.");
                wanted = null;
                return;
            }

            wantedFlag = FsmVariables.GlobalVariables.FindFsmBool(WantedGlobal);
            vehicleName = FsmVariables.GlobalVariables.FindFsmString(VehicleGlobal);
            cops = CopsOf(wanted);
            jailRoot = Activated(wanted, ToJailState);
            GameObject handle = GameObject.Find(DoorPath);
            door = handle == null ? null : DoorSync.FindMachine(handle.transform, DoorMachine);
            if (door != null && DoorSync.HasState(door, TestCopsState) && DoorsHooked.Add(door.GetInstanceID()))
            {
                door.FsmInject(TestCopsState, OnTestCops, false, 0, false);
            }

            HoldClock();
            log.Info(
                Category,
                "Wanted: the police come for the player who did the crime; the police " + (cops == null ? "NOT found" : "found") + ", the jail "
                + (jailRoot == null ? "NOT found" : "found") + ", the front door " + (door == null ? "NOT found" : "found") + "; " + heldBack.Count
                + " action(s) of the arrest that move the clock held back. A day of a sentence is " + SentenceDaySeconds.ToString("0", CultureInfo.InvariantCulture) + " s.");
        }

        // The police: what "At home" switches on.
        private static GameObject CopsOf(PlayMakerFSM machine)
        {
            return Activated(machine, HomeState);
        }

        // The object an "ActivateGameObject" of that state switches on.
        private static GameObject Activated(PlayMakerFSM machine, string stateName)
        {
            FsmState[] states = machine.FsmStates;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                if (states[s].Name != stateName)
                {
                    continue;
                }

                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    var activate = actions[a] as ActivateGameObject;
                    if (activate != null && activate.activate.Value)
                    {
                        return machine.Fsm.GetOwnerDefaultTarget(activate.gameObject);
                    }
                }
            }

            return null;
        }

        // The arrest moves the day on and sets the hour: in a session the clock is the host's, and jail moves nobody's.
        private void HoldClock()
        {
            SparedClock.Hold(wanted, heldBack);
        }

        // Says whether the police are at this player's home, for the other games, when that changes.
        private void TellOwnCops()
        {
            bool now = wantedFlag != null && wantedFlag.Value && cops != null && cops.activeSelf && Array.IndexOf(HomeStates, wanted.ActiveStateName) >= 0;
            if (now == ownCops && told)
            {
                return;
            }

            told = true;
            ownCops = now;
            current.Set(HomeKey + session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { now ? 1 : 0 });
            log.Info(Category, now ? "Wanted: the police are at this player's home; the other games see them." : "Wanted: no police at this player's home.");
        }

        // The police of another player are seen here, and arrest nobody here.
        private void Mirror()
        {
            bool others = false;
            foreach (KeyValuePair<byte, bool> home in homeOf)
            {
                others |= home.Value && home.Key != session.LocalPlayerId;
            }

            bool want = others && !ownCops && cops != null;
            if (want && !mirroring)
            {
                mirroring = true;
                copsSwitchedOn = !cops.activeSelf;
                if (copsSwitchedOn)
                {
                    cops.SetActive(true);
                }

                PauseArrests();
                log.Info(Category, "Wanted: another player's police are at home; seen here, and they arrest nobody here (" + paused.Count + " trigger(s) and warrant(s) paused).");
            }
            else if (!want && mirroring)
            {
                StopMirroring();
                log.Info(Category, ownCops ? "Wanted: the police are this player's own now." : "Wanted: the other players' police are gone.");
            }
        }

        private void PauseArrests()
        {
            Transform root = cops.transform;
            for (int i = 0; i < Triggers.Length; i++)
            {
                Transform trigger = root.Find(Triggers[i]);
                PlayMakerFSM machine = trigger == null ? null : DoorSync.FindMachine(trigger, TriggerMachine);
                if (machine != null && machine.enabled)
                {
                    paused.Add(new PausedMachine(machine));
                }
            }

            PlayMakerFSM[] machines = cops.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].gameObject.name == WarrantName && machines[i].FsmName == WarrantMachine && machines[i].enabled)
                {
                    paused.Add(new PausedMachine(machines[i]));
                }
            }
        }

        private void StopMirroring()
        {
            ResumeArrests();
            if (mirroring && copsSwitchedOn && !ownCops && cops != null)
            {
                cops.SetActive(false);
            }

            mirroring = false;
            copsSwitchedOn = false;
        }

        // Another player's police at the door here: the local player who opens it hears them, and they go on waiting.
        private void WatchDoor()
        {
            if (door == null)
            {
                return;
            }

            FsmBool open = door.FsmVariables.FindFsmBool(DoorOpenVariable);
            bool isOpen = open != null && open.Value;
            if (isOpen != doorWasOpen && mirroring && player != null && (player.position - door.transform.position).sqrMagnitude <= DoorReach * DoorReach)
            {
                PlayMakerFSM speak = Speaker();
                if (speak != null)
                {
                    speak.SendEvent(isOpen ? DoorOpenEvent : KnockEvent);
                }
            }

            doorWasOpen = isOpen;
        }

        private PlayMakerFSM Speaker()
        {
            Transform speak = cops == null ? null : cops.transform.Find(SpeakPath);
            return speak == null ? null : DoorSync.FindMachine(speak, SpeakMachine);
        }

        private static void OnTestCops()
        {
            WantedSync sync = active;
            if (sync != null)
            {
                sync.TestingCops();
            }
        }

        // The front door in the state that tells the police it opened: in the wanted player's game, only if the local player
        // opened it. Opened by another player and shared here, it is as if nobody opened it.
        private void TestingCops()
        {
            if (door == null || player == null || current == null)
            {
                return;
            }

            FsmBool copsHere = door.FsmVariables.FindFsmBool(DoorCopsVariable);
            if (copsHere == null || !copsHere.Value || (player.position - door.transform.position).sqrMagnitude <= DoorReach * DoorReach)
            {
                return;
            }

            copsHere.Value = false;
            doorCopsHeld = true;
            log.Info(Category, "Wanted: the front door was opened by another player; the police here do not take it for this player opening it.");
        }

        private void SetDoorCops(bool value)
        {
            FsmBool copsHere = door == null ? null : door.FsmVariables.FindFsmBool(DoorCopsVariable);
            if (copsHere != null)
            {
                copsHere.Value = value;
            }
        }

        private static void SetDoorCopsOf(bool value)
        {
            GameObject handle = GameObject.Find(DoorPath);
            PlayMakerFSM machine = handle == null ? null : DoorSync.FindMachine(handle.transform, DoorMachine);
            FsmBool copsHere = machine == null ? null : machine.FsmVariables.FindFsmBool(DoorCopsVariable);
            if (copsHere != null)
            {
                copsHere.Value = value;
            }
        }

        // The local player in the cell: a day of the sentence every so many seconds, and out by day or not.
        private void KeepSentence(double now)
        {
            if (jail == null)
            {
                Transform functions = jailRoot == null || !jailRoot.activeInHierarchy ? null : jailRoot.transform.Find(JailFunctions);
                jail = functions == null ? null : DoorSync.FindMachine(functions, JailMachine);
                if (jail != null)
                {
                    jail.AddGlobalTransition(JailReleaseEvent, JailReleaseState);
                }
            }

            if (jail == null || !jail.gameObject.activeInHierarchy || player == null)
            {
                jailed = false;
                return;
            }

            string state = jail.ActiveStateName;
            if (state == JailGetTimeState)
            {
                if (!releaseSent)
                {
                    // The sentence is over: out now, not waiting for the morning.
                    releaseSent = true;
                    jail.SendEvent(JailReleaseEvent);
                    log.Info(Category, "Wanted: this player's sentence is over; out of the cell now.");
                }

                return;
            }

            releaseSent = false;
            FsmInt days = wanted.FsmVariables.FindFsmInt("DaysInJail");
            bool inCell = state == JailWaitState && days != null && days.Value > 0 && (player.position - jail.transform.position).sqrMagnitude <= JailReach * JailReach;
            if (!inCell)
            {
                jailed = false;
                return;
            }

            if (!jailed)
            {
                jailed = true;
                nextDay = now + SentenceDaySeconds;
                log.Info(Category, "Wanted: this player is in jail for " + days.Value + " day(s): " + (days.Value * SentenceDaySeconds / 60.0).ToString("0", CultureInfo.InvariantCulture) + " minute(s) of play.");
                return;
            }

            if (now >= nextDay)
            {
                nextDay = now + SentenceDaySeconds;
                jail.SendEvent(JailDayEvent);
                log.Info(Category, "Wanted: a day of this player's sentence went by; " + Math.Max(0, days.Value - 1) + " left.");
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            byte id;
            if (values.Length < 1)
            {
                return;
            }

            if (key.StartsWith(HomeKey, StringComparison.Ordinal) && byte.TryParse(key.Substring(HomeKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                homeOf[id] = values[0] != 0;
                return;
            }

            if (key.StartsWith(CrimeKey, StringComparison.Ordinal) && byte.TryParse(key.Substring(CrimeKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                && session != null && id == session.LocalPlayerId)
            {
                Accused(values[0]);
            }
        }

        // The host told that this player crashed its people: as the game counts a crash it was near.
        private void Accused(int crashes)
        {
            int before = crimeHeard;
            crimeHeard = crashes;
            if (before < 0 || current.CatchingUp || crashes <= before || wanted == null)
            {
                return;
            }

            FsmInt count = wanted.FsmVariables.FindFsmInt("TrafficFatality");
            if (count != null)
            {
                count.Value += crashes - before;
            }

            if (wantedFlag != null)
            {
                wantedFlag.Value = true;
            }

            log.Info(Category, "Wanted: the host's game says this player crashed a car of its people; this player is wanted for it.");
        }

        private void OnPlayerLeft(PlayerInfo gone, DisconnectReason reason)
        {
            left.Add(gone.Id);
        }

        private void Forget(byte id)
        {
            homeOf.Remove(id);
            if (manager.Mode == NetworkMode.Host)
            {
                current.Set(HomeKey + id.ToString(CultureInfo.InvariantCulture), new[] { 0 });
            }
        }

        private void Patch()
        {
            if (patched)
            {
                return;
            }

            try
            {
                MethodInfo compare = typeof(FloatCompare).GetMethod("OnEnter", BindingFlags.Public | BindingFlags.Instance);
                if (compare == null)
                {
                    throw new InvalidOperationException("FloatCompare.OnEnter is missing");
                }

                HarmonyInstance harmony = HarmonyInstance.Create("summersync.wanted");
                harmony.Patch(compare, new HarmonyMethod(typeof(WantedSync).GetMethod("BeforeCompare", BindingFlags.NonPublic | BindingFlags.Static)), null);
                patched = true;
                log.Info(Category, "Wanted: the game's decision of whose crime a run over or a crash is, watched.");
            }
            catch (Exception exception)
            {
                log.Warning(Category, "Wanted: the game's code is not as this build expects (" + exception.Message + "); a crime counts as the game decides.");
            }
        }

        // The game's FloatCompare.OnEnter: in a crime's "Distance?", the crime is this game's player's only if their car did it.
        private static bool BeforeCompare(FloatCompare __instance)
        {
            WantedSync sync = active;
            if (sync == null)
            {
                return true;
            }

            try
            {
                return sync.LetsCompare(__instance);
            }
            catch (Exception exception)
            {
                sync.log.Warning(Category, "Wanted: whose crime it was could not be told: " + exception.Message);
                return true;
            }
        }

        private bool LetsCompare(FloatCompare compare)
        {
            FsmState state = compare.State;
            if (state == null || state.Name != CrimeState || compare.float2 == null || compare.float2.Name != CrimeTolerance)
            {
                return true;
            }

            if (current == null || session == null || session.State != SessionState.Connected || player == null)
            {
                return true;
            }

            Fsm fsm = compare.Fsm;
            bool ours;
            string what;
            if (fsm.Name == HitMachine)
            {
                FsmGameObject hitter = fsm.Variables.FindFsmGameObject(HitterVariable);
                GameObject car = hitter == null ? null : hitter.Value;
                ours = car != null && Drives(car.transform.root);
                // The person is two levels above the trigger ("…/Kale/Pivot/HumanTriggerCrime").
                Transform person = fsm.GameObject.transform.parent;
                person = person != null && person.parent != null ? person.parent : person;
                what = "the run over of '" + (person == null ? fsm.GameObject.name : person.name) + "' by '" + (car == null ? "?" : car.transform.root.name) + "'";
            }
            else
            {
                byte other;
                ours = CrashedBy(fsm.GameObject.transform.position, out other);
                what = "the crash of '" + fsm.GameObject.transform.root.name + "'";
                if (!ours && other != byte.MaxValue && manager.Mode == NetworkMode.Host)
                {
                    int count;
                    crimesOf.TryGetValue(other, out count);
                    crimesOf[other] = ++count;
                    current.Set(CrimeKey + other.ToString(CultureInfo.InvariantCulture), new[] { count });
                    what += ", player " + other + "'s crime, told to their game";
                }
            }

            if (ours)
            {
                return true;
            }

            log.Info(Category, "Wanted: " + what + " is not this player's crime here: an accident.");
            fsm.Event(compare.greaterThan);
            compare.Finish();
            return false;
        }

        // Whether the local player drives that vehicle: at the wheel the game hangs them from it.
        private bool Drives(Transform vehicle)
        {
            return vehicle != null && player != null && player.IsChildOf(vehicle) && vehicleName != null && !string.IsNullOrEmpty(vehicleName.Value);
        }

        // Whose car crashed the car that crashed here: the nearest player at a wheel. True for this player, or when no
        // player drives near it (then the game decides, as without the mod); otherwise <paramref name="other"/> says who.
        private bool CrashedBy(Vector3 at, out byte other)
        {
            other = byte.MaxValue;
            float best = CrashReach * CrashReach;
            bool local = false;
            bool anyone = false;
            if (player != null && player.parent != null && vehicleName != null && !string.IsNullOrEmpty(vehicleName.Value))
            {
                float distance = (player.root.position - at).sqrMagnitude;
                if (distance <= best)
                {
                    best = distance;
                    local = true;
                    anyone = true;
                }
            }

            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                if (!remote.Visible || !remote.Shown.Driving)
                {
                    continue;
                }

                Transform car = vehicles.RootOf(remote.Shown.Parent);
                if (car == null)
                {
                    continue;
                }

                float distance = (car.position - at).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    local = false;
                    other = remote.Id;
                    anyone = true;
                }
            }

            return local || !anyone;
        }
    }
}
