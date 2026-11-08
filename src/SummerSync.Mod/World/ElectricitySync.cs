using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The house's electricity is the host's: its game counts what the house uses, sends the
    /// bills, cuts the power off when they are not paid, and has the storms' blackouts and blown fuses. The other games have
    /// those machines paused and show what the host's game has. What a player does is theirs, in any game: the main switch,
    /// paying a bill, screwing a fuse holder or putting a new fuse in it; the holders go into and out of the fuse table as
    /// mounts (<see cref="MountSync"/>).
    ///
    /// What is assumed about the game (dumps of 2026-10-14 and 2026-10-15; docs/GAME_NOTES.md, "La electricidad de la
    /// casa"):
    /// - "Systems/ElectricityBills :: Data" counts seconds to the next bill and to the cut-off, keeps the globals
    ///   "HouseElectricity" (from its "MainSwitch") and "HouseElectricityKWH", the unpaid sum ("UnpaidBills"), and the bill
    ///   in the mailbox ("YARD/PlayerMailBox/EnvelopeElectricityBill"); "Cut off" switches the power off and broadcasts
    ///   ELEC_CUTOFF; "GLOBALEVENT" leads to "Pay bills". The bill's sheet pays it: "Sheets/ElectricityBill/Pay ::
    ///   Button", state "Date", takes the money and sends that event.
    /// - "YARD/Building/Dynamics/HouseElectricity :: Status" has the storm's way with the house (a blackout: "Lights off 2",
    ///   then "Reset mat"; "Cut off"; and "Blow fuse", a random holder's "BLOWFUSE"); the computer's
    ///   "…/386_case(xxxxx)/Lights :: Fuseblow" blows one when too much is on.
    /// - "…/FuseTable/Fusetable/MainSwitch :: Use": "Switch" flips its "Switch" and goes to "ON" or "OFF".
    /// - A fuse holder ("fuse holder(Clone)", its "Use" variable "ID" fuseholder01 to 07) has in its "Use" "FuseState" (0
    ///   none, 1 good, 2 blown; "State 6" blows it) and "Tightness" (its "Screw": "Screw" and "Unscrew", one turn each);
    ///   its "FuseTrigger :: Assembly", state "Assemble", puts a new fuse in. Each turn of the screw sends "SHOCK" to
    ///   "…/FuseTable/Database :: Shock", which, with the power on, kills the local player once in two thousand; mounted, the
    ///   holder's screw gives itself its first turn (from 0 to 1).
    /// </summary>
    internal sealed class ElectricitySync
    {
        private const string BillsPath = "/Systems/ElectricityBills";
        private const string BillsMachine = "Data";
        private const string StatusPath = "/YARD/Building/Dynamics/HouseElectricity";
        private const string StatusMachine = "Status";
        private const string ComputerFusePath = "/YARD/Building/BEDROOM1/COMPUTER/Computer/Meshes/386_case(xxxxx)/Lights";
        private const string ComputerFuseMachine = "Fuseblow";
        private const string MainSwitchPath = "/YARD/Building/Dynamics/FuseTable/Fusetable/MainSwitch";
        private const string ShockPath = "/YARD/Building/Dynamics/FuseTable/Database";
        private const string ShockMachine = "Shock";
        private const string EnvelopePath = "/YARD/PlayerMailBox/EnvelopeElectricityBill";
        private const string PayPath = "/Sheets/ElectricityBill/Pay";
        private const string PayMachine = "Button";
        private const string PayState = "Date";
        private const string HolderName = "fuse holder(Clone)";
        private const string HolderIdPrefix = "fuseholder";
        private const string UseMachine = "Use";
        private const string ScrewMachine = "Screw";
        private const string AssemblyMachine = "Assembly";
        private const string FusePartVariable = "Part";
        private const string EventPrefix = "SUMMERSYNC ELEC ";

        private const string PowerKey = "elec/power";
        private const string KwhKey = "elec/kwh";
        private const string UnpaidKey = "elec/unpaid";
        private const string BillKey = "elec/bill";
        private const string MainKey = "elec/main";
        private const string PayPrefix = "elec/pay/";
        private const string StatePrefix = "elec/state/";
        private const string HolderPrefix = "elec/holder/";

        private const double FirstLookSeconds = 3.0;
        private const double BindSeconds = 5.0;
        private const double LookSeconds = 0.1;
        private const double KwhSeconds = 1.0;
        private const double HookSeconds = 2.0;
        // A client takes the house as it is once the host's answer to its request has come; if none comes, after this long.
        private const double CatchUpSeconds = 10.0;
        private const double TurnSeconds = 0.15;
        private const double ShockSeconds = 0.5;
        private const double StepSeconds = 0.4;
        private const int MostSteps = 40;
        private const LogCategory Category = LogCategory.Fsm;

        // The storm machine's blackout: the lamp's look is kept on going dark and put back after, and the wait in between
        // is the storm's own number, which a client's game never draws.
        private const string LightsOff = "Lights off 2";
        private const string WaitForPower = "State 10";
        private const string ResetLamp = "Reset mat";
        private const string BlackoutVariable = "Blackout";
        private const float HeldBlackout = 10000000f;

        // The host's states that the other games go to as well, by number: the bills' and the storm's.
        private static readonly string[] RelayStates = { "Cut off", "Pay bills", "Cut off", "Lights off 2", "Reset mat" };
        private static readonly bool[] RelayOnBills = { true, true, false, false, false };

        // The hooks put in the game's states stay there for the run of the game; each calls whichever sync is current.
        private static readonly HashSet<string> Hooked = new HashSet<string>();
        private static ElectricitySync active;
        private static int relaySerial;

        // A client's payments, numbered from where this run of the game happened to start: a player who takes the number of
        // one who left, or comes back after restarting the game, does not say a number the host already heard from it.
        private static int paySerial = Environment.TickCount & 0x3FFFFFFF;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<PausedMachine> paused = new List<PausedMachine>();
        private readonly List<Holder> holders = new List<Holder>();
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();

        // Whether each came in the host's answer to a request (how the house is, not something that happens now).
        private readonly List<bool> heardCatchUp = new List<bool>();
        private bool answerHeard;
        private readonly int[] followed = new int[RelayStates.Length];
        private readonly Dictionary<int, int> paidHeard = new Dictionary<int, int>();
        private TallyReplication current;
        private bool host;
        private bool bound;
        private bool unavailable;
        private double bindAt;
        private double nextHookTry;
        private double nextLook;
        private double nextKwh;
        private PlayMakerFSM bills;
        private PlayMakerFSM status;
        private PlayMakerFSM mainSwitch;
        private PlayMakerFSM shock;
        private PausedMachine shockPaused;
        private double shockUntil;
        private GameObject envelope;
        private FsmBool power;
        private FsmFloat kwh;
        private FsmFloat unpaid;
        private FsmBool mainOn;
        private bool computerFusePaused;
        private bool payHooked;

        // A client: the host's states are followed as they come once the first word (what the host's game is in when
        // this one joins) has been taken as it is now, not as the way there.
        private bool caughtUp;
        private double catchUpUntil = -1.0;
        private FsmFloat blackout;
        private bool blackoutHeld;

        // What the host's game was last told to have, and, in a client, what the host said.
        private int toldPower = -1;
        private int toldKwh = int.MinValue;
        private int toldUnpaid = int.MinValue;
        private int toldBill = -1;
        private int heardPower = -1;
        private int heardBill = -1;
        private int heardUnpaid = int.MinValue;

        // The main switch as last seen here, and another game's word to follow.
        private int knownMain = -1;
        private bool mainWanted;
        private bool mainWant;

        public ElectricitySync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's root object,
        /// or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                    current.CaughtUp -= OnCaughtUp;
                }

                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                bound = false;
                unavailable = false;
                bindAt = now + FirstLookSeconds;
                active = current == null ? null : this;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                    current.CaughtUp += OnCaughtUp;
                }
            }

            if (current == null || unavailable)
            {
                return;
            }

            if (!bound)
            {
                if (now < bindAt)
                {
                    return;
                }

                bindAt = now + BindSeconds;
                Bind(now);
                return;
            }

            if (shockPaused != null && now >= shockUntil)
            {
                shockPaused.Resume();
                shockPaused = null;
            }

            if (manager.Session.State != SessionState.Connected || now < nextLook)
            {
                return;
            }

            nextLook = now + LookSeconds;
            Hear();
            if (host)
            {
                TellHost(now);
            }
            else
            {
                if (catchUpUntil < 0.0)
                {
                    catchUpUntil = now + CatchUpSeconds;
                }
                else if (!caughtUp && (answerHeard || now >= catchUpUntil))
                {
                    CatchUp();
                }

                // After the states it may have gone through, the power and the bill as the host has them.
                ShowPower();
                if (now >= nextHookTry)
                {
                    nextHookTry = now + HookSeconds;
                    PauseComputerFuse();
                    HookPay();
                }
            }

            WorkMain();
            for (int i = 0; i < holders.Count; i++)
            {
                WorkHolder(holders[i], now);
            }
        }

        /// <summary>The paused machines go on from where they are; nothing more is done to the game.</summary>
        public void Release()
        {
            if (blackoutHeld && blackout != null)
            {
                // The blackout held for the host's word ends with the session; this game's own machine goes on from there.
                blackout.Value = 0f;
            }

            blackoutHeld = false;
            blackout = null;
            caughtUp = false;
            catchUpUntil = -1.0;
            answerHeard = false;
            heardCatchUp.Clear();
            for (int i = 0; i < paused.Count; i++)
            {
                paused[i].Resume();
            }

            paused.Clear();
            if (shockPaused != null)
            {
                shockPaused.Resume();
                shockPaused = null;
            }

            shock = null;
            holders.Clear();
            heard.Clear();
            paidHeard.Clear();
            computerFusePaused = false;
            payHooked = false;
            bills = null;
            status = null;
            mainSwitch = null;
            envelope = null;
            toldPower = -1;
            toldKwh = int.MinValue;
            toldUnpaid = int.MinValue;
            toldBill = -1;
            heardPower = -1;
            heardBill = -1;
            heardUnpaid = int.MinValue;
            knownMain = -1;
            mainWanted = false;
            for (int i = 0; i < followed.Length; i++)
            {
                followed[i] = 0;
            }
        }

        private void Bind(double now)
        {
            bills = Machine(BillsPath, BillsMachine);
            status = Machine(StatusPath, StatusMachine);
            mainSwitch = Machine(MainSwitchPath, UseMachine);
            shock = Machine(ShockPath, ShockMachine);
            Transform envelopeAt = Scene(EnvelopePath);
            envelope = envelopeAt == null ? null : envelopeAt.gameObject;
            if (bills == null || status == null)
            {
                log.Warning(Category, "Electricity: no '" + (bills == null ? BillsPath : StatusPath) + "' in this world; each game has its own electricity.");
                bound = true;
                unavailable = true;
                return;
            }

            power = FsmVariables.GlobalVariables.FindFsmBool("HouseElectricity");
            kwh = FsmVariables.GlobalVariables.FindFsmFloat("HouseElectricityKWH");
            unpaid = bills.FsmVariables.FindFsmFloat("UnpaidBills");
            mainOn = mainSwitch == null ? null : mainSwitch.FsmVariables.FindFsmBool("Switch");
            knownMain = mainOn == null ? -1 : mainOn.Value ? 1 : 0;
            FindHolders();
            blackout = status.FsmVariables.FindFsmFloat(BlackoutVariable);
            if (host)
            {
                HookStates(bills, true);
                HookStates(status, false);
            }
            else
            {
                // The host's game runs them; here they show what it does.
                paused.Add(new PausedMachine(bills));
                paused.Add(new PausedMachine(status));
            }

            bound = true;
            if (host)
            {
                // Everything as this game has it is the session's from the start, a blackout too.
                TellHost(now);
                if (IsDark(status.ActiveStateName))
                {
                    relaySerial++;
                    current.Set(StatePrefix + Array.IndexOf(RelayStates, LightsOff).ToString(CultureInfo.InvariantCulture), new[] { relaySerial });
                }

                if (knownMain >= 0)
                {
                    current.Set(MainKey, new[] { knownMain, manager.Session.LocalPlayerId });
                }

                for (int i = 0; i < holders.Count; i++)
                {
                    TellHolder(holders[i]);
                }
            }
            else
            {
                // The bill's sheet is ready before its player can pay.
                PauseComputerFuse();
                HookPay();
                current.RequestAll();
            }

            log.Info(
                Category,
                "Electricity: " + (host ? "this game runs the house's electricity (bills, cut-offs, storms, blown fuses)" : "the host's game runs the house's electricity; here it is shown")
                + "; " + holders.Count + " fuse holder(s), main switch " + (mainSwitch == null ? "NOT FOUND" : knownMain == 1 ? "on" : "off") + ", power "
                + (power == null ? "unknown" : power.Value ? "on" : "off") + ".");
        }

        // The seven holders, by the name the game gave them first: in their slots or loose.
        private void FindHolders()
        {
            holders.Clear();
            PlayMakerFSM[] machines = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                PlayMakerFSM use = machines[i];
                if (use.FsmName != UseMachine || use.gameObject.name != HolderName)
                {
                    continue;
                }

                FsmString id = use.FsmVariables.FindFsmString("ID");
                if (id == null || id.Value == null || !id.Value.StartsWith(HolderIdPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var holder = new Holder
                {
                    Id = id.Value,
                    Use = use,
                    Screw = DoorSync.FindMachine(use.transform, ScrewMachine),
                    Fuse = use.FsmVariables.FindFsmInt("FuseState"),
                    Tight = use.FsmVariables.FindFsmFloat("Tightness"),
                };

                Transform trigger = use.transform.Find("FuseTrigger");
                holder.FuseAssembly = trigger == null ? null : DoorSync.FindMachine(trigger, AssemblyMachine);
                if (holder.Fuse == null || holder.Tight == null)
                {
                    continue;
                }

                holder.KnownFuse = holder.Fuse.Value;
                holder.KnownTight = Mathf.RoundToInt(holder.Tight.Value);
                holders.Add(holder);
            }
        }

        // The host: hooks that say when its game enters the states the others follow.
        private void HookStates(PlayMakerFSM machine, bool onBills)
        {
            for (int i = 0; i < RelayStates.Length; i++)
            {
                if (RelayOnBills[i] != onBills)
                {
                    continue;
                }

                string state = RelayStates[i];
                string hookKey = machine.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "/" + state;
                if (Hooked.Contains(hookKey) || !DoorSync.HasState(machine, state))
                {
                    continue;
                }

                int index = i;
                Action hook = () => Entered(index);
                if (machine.FsmInject(state, hook, false, 0, false) || machine.FsmInject(state, hook, false, -1, false))
                {
                    Hooked.Add(hookKey);
                }
            }
        }

        private static void Entered(int index)
        {
            ElectricitySync sync = active;
            if (sync != null)
            {
                sync.OnEntered(index);
            }
        }

        private void OnEntered(int index)
        {
            if (!host || current == null || manager.Session == null || manager.Session.State != SessionState.Connected)
            {
                return;
            }

            relaySerial++;
            current.Set(StatePrefix + index.ToString(CultureInfo.InvariantCulture), new[] { relaySerial });
            log.Info(Category, "Electricity: the house's " + (RelayOnBills[index] ? "bills" : "storm machine") + " went to '" + RelayStates[index] + "' here; told.");
        }

        // A client: the bill's sheet, where its player pays; it is hooked once the game has it ready.
        private void HookPay()
        {
            if (payHooked)
            {
                return;
            }

            PlayMakerFSM pay = Machine(PayPath, PayMachine);
            if (pay == null)
            {
                return;
            }

            if (pay.Fsm == null || !pay.Fsm.Initialized)
            {
                try
                {
                    pay.InitializeFSM();
                }
                catch (Exception)
                {
                    return;
                }
            }

            string hookKey = pay.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "/" + PayState;
            if (!Hooked.Contains(hookKey))
            {
                if (!DoorSync.HasState(pay, PayState) || !pay.FsmInject(PayState, Paid, false, 0, false))
                {
                    return;
                }

                Hooked.Add(hookKey);
            }

            payHooked = true;
        }

        private static void Paid()
        {
            ElectricitySync sync = active;
            if (sync != null)
            {
                sync.OnPaid();
            }
        }

        private void OnPaid()
        {
            if (host || current == null || manager.Session == null || manager.Session.State != SessionState.Connected)
            {
                return;
            }

            // The money went with the game's own payment; the bill is paid in the host's game.
            paySerial = (paySerial + 1) & 0x3FFFFFFF;
            current.Set(PayPrefix + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { paySerial });
            log.Info(Category, "Electricity: the player paid the electricity bill here; the host's game is told.");
        }

        // A client: the computer's own way of blowing a fuse is the host's game's too, once the computer is there.
        private void PauseComputerFuse()
        {
            if (computerFusePaused)
            {
                return;
            }

            PlayMakerFSM fuse = Machine(ComputerFusePath, ComputerFuseMachine);
            if (fuse != null && fuse.enabled)
            {
                paused.Add(new PausedMachine(fuse));
                computerFusePaused = true;
            }
        }

        // Heard while the session reads its messages: done in this class's turn in the frame.
        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith("elec/", StringComparison.Ordinal))
            {
                heard.Add(new KeyValuePair<string, int[]>(key, values));
                heardCatchUp.Add(current != null && current.CatchingUp);
            }
        }

        private void Hear()
        {
            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value, heardCatchUp[i]);
            }

            heard.Clear();
            heardCatchUp.Clear();
        }

        private void OnCaughtUp()
        {
            answerHeard = true;
        }

        private void Hear(string key, int[] values, bool catchUp)
        {
            if (values.Length < 1)
            {
                return;
            }

            if (key.StartsWith(HolderPrefix, StringComparison.Ordinal))
            {
                if (values.Length < 3 || values[2] == manager.Session.LocalPlayerId)
                {
                    return;
                }

                string id = key.Substring(HolderPrefix.Length);
                for (int h = 0; h < holders.Count; h++)
                {
                    Holder holder = holders[h];
                    if (holder.Id == id)
                    {
                        holder.Wanted = true;
                        holder.WantFuse = values[0];
                        holder.WantTight = values[1];
                        holder.WantBy = (byte)Mathf.Clamp(values[2], 0, byte.MaxValue);
                        holder.Steps = 0;
                        holder.NextStep = 0.0;
                    }
                }

                return;
            }

            if (key == MainKey)
            {
                if (values.Length >= 2 && values[1] != manager.Session.LocalPlayerId)
                {
                    mainWanted = true;
                    mainWant = values[0] != 0;
                }

                return;
            }

            if (host)
            {
                if (key.StartsWith(PayPrefix, StringComparison.Ordinal))
                {
                    int player;
                    int last;
                    if (int.TryParse(key.Substring(PayPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out player)
                        && (!paidHeard.TryGetValue(player, out last) || values[0] != last))
                    {
                        paidHeard[player] = values[0];
                        bills.SendEvent("GLOBALEVENT");
                        log.Info(Category, "Electricity: player " + player + " paid the electricity bill in their game; paid here.");
                    }
                }

                return;
            }

            if (key == PowerKey)
            {
                heardPower = values[0] != 0 ? 1 : 0;
            }
            else if (key == KwhKey)
            {
                if (kwh != null)
                {
                    kwh.Value = values[0] / 1000f;
                }
            }
            else if (key == UnpaidKey)
            {
                // What the bill's sheet shows here.
                heardUnpaid = values[0];
                if (unpaid != null)
                {
                    unpaid.Value = values[0] / 100f;
                }
            }
            else if (key == BillKey)
            {
                heardBill = values[0] != 0 ? 1 : 0;
            }
            else if (key.StartsWith(StatePrefix, StringComparison.Ordinal))
            {
                int index;
                if (int.TryParse(key.Substring(StatePrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                    && index >= 0 && index < RelayStates.Length && values[0] > followed[index])
                {
                    // The host says its states in the order its game went through them, and its words come in that order.
                    followed[index] = values[0];
                    if (caughtUp && !catchUp)
                    {
                        Follow(index);
                    }
                }
            }
        }

        // A client: a state the host's game went to, entered in its paused machine here (switched on for the moment it takes).
        private void Follow(int index)
        {
            PlayMakerFSM machine = RelayOnBills[index] ? bills : status;
            string state = RelayStates[index];

            // Unity's null check is also true for a destroyed object.
            if (machine == null)
            {
                return;
            }

            if (RelayOnBills[index])
            {
                if (machine.ActiveStateName != state)
                {
                    Enter(machine, state);
                    log.Info(Category, "Electricity: the house's bills went to '" + state + "' in the host's game; here too.");
                }

                // What the state does to the power in the host's game, before its word on the power comes: cut off, the
                // power stays off; paid, it is on until the main switch says otherwise.
                heardPower = state == RelayStates[0] ? 0 : 1;
                return;
            }

            // Dark already, going dark again would keep the dark lamp as the lit one; lit, there is no lamp's look to put
            // back.
            bool dark = IsDark(machine.ActiveStateName);
            if ((state == LightsOff && dark) || (state == ResetLamp && !dark))
            {
                return;
            }

            EnterStorm(state);
            log.Info(Category, "Electricity: the house's storm machine went to '" + state + "' in the host's game; here too.");
        }

        // A client, once the first word from the host has come: its storm machine is dark or lit as the host's is now. The
        // bills are what the words on the power, the sum and the bill say: their states are not gone through again (a
        // cut-off would switch off what has been switched on since).
        private void CatchUp()
        {
            caughtUp = true;
            int newest = -1;
            for (int i = 0; i < RelayStates.Length; i++)
            {
                if (!RelayOnBills[i] && followed[i] > 0 && (newest < 0 || followed[i] > followed[newest]))
                {
                    newest = i;
                }
            }

            // Unity's null check is also true for a destroyed object.
            if (status == null)
            {
                return;
            }

            bool darkThere = newest >= 0 && RelayStates[newest] != ResetLamp;
            bool darkHere = IsDark(status.ActiveStateName);
            if (darkThere != darkHere)
            {
                EnterStorm(darkThere ? LightsOff : ResetLamp);
            }

            log.Info(Category, "Electricity: the house is " + (darkThere ? "dark" : "lit") + " in the host's game; here too" + (darkThere != darkHere ? " now." : "."));
        }

        // The storm machine to one of the host's states. Its blackout lasts until the host's word ends it, and the lamp's
        // look goes back with the power on, as in the host's game at that moment.
        private void EnterStorm(string state)
        {
            if (blackout != null)
            {
                blackout.Value = HeldBlackout;
                blackoutHeld = true;
            }

            if (state == ResetLamp)
            {
                heardPower = 1;
                if (power != null)
                {
                    power.Value = true;
                }
            }

            Enter(status, state);
        }

        private static bool IsDark(string state)
        {
            return state == LightsOff || state == WaitForPower;
        }

        // A paused machine to a state, switched on for the moment it takes.
        private static void Enter(PlayMakerFSM machine, string state)
        {
            bool wasOn = machine.enabled;
            machine.enabled = true;
            try
            {
                ToState(machine, state);
            }
            finally
            {
                machine.enabled = wasOn;
            }
        }

        // A client: the power and the bill in the mailbox, as the host's game has them.
        private void ShowPower()
        {
            if (heardPower >= 0 && power != null && power.Value != (heardPower != 0))
            {
                power.Value = heardPower != 0;
            }

            // Unity's null check is also true for a destroyed object.
            if (heardBill >= 0 && envelope != null && envelope.activeSelf != (heardBill != 0))
            {
                envelope.SetActive(heardBill != 0);
            }
        }

        // The host: the power, what the house used, the unpaid sum and the bill in the mailbox, as its game has them.
        private void TellHost(double now)
        {
            if (power != null)
            {
                int on = power.Value ? 1 : 0;
                if (on != toldPower)
                {
                    toldPower = on;
                    current.Set(PowerKey, new[] { on });
                }
            }

            if (kwh != null && now >= nextKwh)
            {
                nextKwh = now + KwhSeconds;
                int used = Mathf.RoundToInt(kwh.Value * 1000f);
                if (used != toldKwh)
                {
                    toldKwh = used;
                    current.Set(KwhKey, new[] { used });
                }
            }

            if (unpaid != null)
            {
                int sum = Mathf.RoundToInt(unpaid.Value * 100f);
                if (sum != toldUnpaid)
                {
                    toldUnpaid = sum;
                    current.Set(UnpaidKey, new[] { sum });
                }
            }

            // Unity's null check is also true for a destroyed object.
            if (envelope != null)
            {
                int bill = envelope.activeSelf ? 1 : 0;
                if (bill != toldBill)
                {
                    toldBill = bill;
                    current.Set(BillKey, new[] { bill });
                }
            }
        }

        // The main switch is whoever moved it last.
        private void WorkMain()
        {
            // Unity's null check is also true for a destroyed object.
            if (mainSwitch == null || mainOn == null)
            {
                return;
            }

            int now = mainOn.Value ? 1 : 0;
            if (mainWanted)
            {
                mainWanted = false;
                if (now != (mainWant ? 1 : 0))
                {
                    // The game's own click: it flips the switch, plays its sound and puts the lever where it goes.
                    ToState(mainSwitch, "Switch");
                    log.Info(Category, "Electricity: the main switch was turned " + (mainWant ? "on" : "off") + " in another game; here too.");
                }

                knownMain = mainOn.Value ? 1 : 0;
                return;
            }

            if (now != knownMain)
            {
                knownMain = now;
                current.Set(MainKey, new[] { now, manager.Session.LocalPlayerId });
                log.Info(Category, "Electricity: the main switch was turned " + (now == 1 ? "on" : "off") + " here; told.");
            }
        }

        private void WorkHolder(Holder holder, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (holder.Use == null)
            {
                return;
            }

            int fuse = holder.Fuse.Value;
            int tight = Mathf.RoundToInt(holder.Tight.Value);
            if (holder.Wanted)
            {
                bool screwable = holder.Screw != null && holder.Screw.enabled && holder.Screw.gameObject.activeInHierarchy;
                // Out of its slot its screw does nothing: what the other game screwed waits for the holder to be mounted here.
                bool tightDone = holder.WantTight < 1 || tight == holder.WantTight;
                if (fuse == holder.WantFuse && tightDone)
                {
                    holder.Wanted = false;
                    holder.KnownFuse = fuse;
                    holder.KnownTight = tight;
                    if (holder.Steps > 0)
                    {
                        log.Info(Category, "Electricity: the fuse holder '" + holder.Id + "' is as in player " + holder.WantBy + "'s game here now (fuse " + fuse + ", tightness " + tight + ").");
                    }

                    return;
                }

                if (now < holder.NextStep)
                {
                    return;
                }

                if (++holder.Steps > MostSteps)
                {
                    holder.Wanted = false;
                    holder.KnownFuse = fuse;
                    holder.KnownTight = tight;
                    log.Warning(
                        Category,
                        "Electricity: the fuse holder '" + holder.Id + "' did not get to fuse " + holder.WantFuse + ", tightness " + holder.WantTight + " here (it has " + fuse + ", "
                        + tight + "); this game differs there until it changes again.");
                    return;
                }

                if (fuse != holder.WantFuse)
                {
                    holder.NextStep = now + StepSeconds;
                    if (fuse == 1 && holder.WantFuse == 2)
                    {
                        // Blown, the way the storm blows it.
                        ToState(holder.Use, "State 6");
                    }
                    else if (fuse == 0 && holder.WantFuse == 1 && holder.FuseAssembly != null && holder.FuseAssembly.gameObject.activeInHierarchy)
                    {
                        // A new fuse in, the way the player's click puts it in. The click's game throws away the fuse its
                        // player held (and the others with it); here, whatever this trigger last touched is left alone.
                        if (!holder.FuseAssembly.enabled)
                        {
                            holder.FuseAssembly.enabled = true;
                        }

                        FsmGameObject touched = holder.FuseAssembly.FsmVariables.FindFsmGameObject(FusePartVariable);
                        if (touched != null)
                        {
                            touched.Value = null;
                        }

                        ToState(holder.FuseAssembly, "Assemble");
                    }

                    // Otherwise it waits: a fuse comes out only with the holder taken out of its slot.
                }
                else if (!screwable)
                {
                    holder.NextStep = now + StepSeconds;
                }
                else
                {
                    // Each turn of the game's screw may kill whoever turns it, with the power on. Here nobody did: the other
                    // game's player did, and took that chance there. The fuse table's shock is paused while the turns last.
                    if (shockPaused == null && shock != null && shock.enabled)
                    {
                        shockPaused = new PausedMachine(shock);
                    }

                    shockUntil = now + ShockSeconds;
                    holder.NextStep = now + TurnSeconds;
                    ToState(holder.Screw, tight < holder.WantTight ? "Screw" : "Unscrew");
                }

                holder.KnownFuse = holder.Fuse.Value;
                holder.KnownTight = Mathf.RoundToInt(holder.Tight.Value);
                return;
            }

            if (fuse == holder.KnownFuse && tight == holder.KnownTight)
            {
                return;
            }

            bool blew = holder.KnownFuse == 1 && fuse == 2;
            bool settled = fuse == holder.KnownFuse && holder.KnownTight == 0 && tight == 1;
            holder.KnownFuse = fuse;
            holder.KnownTight = tight;
            if (settled)
            {
                // Mounted, the game gives the screw its first turn by itself, in every game: no player did it, and saying it
                // could undo what the player who mounted it has screwed since.
                return;
            }

            if (blew && !host)
            {
                // Fuses blow in the host's game; here its word blows them.
                log.Warning(Category, "Electricity: the fuse in '" + holder.Id + "' blew by itself here, where the host's game decides that; not told.");
                return;
            }

            TellHolder(holder);
        }

        private void TellHolder(Holder holder)
        {
            current.Set(HolderPrefix + holder.Id, new[] { holder.KnownFuse, holder.KnownTight, manager.Session.LocalPlayerId });
        }

        // Takes a machine to one of its states through an event of the mod's.
        private static void ToState(PlayMakerFSM machine, string state)
        {
            // Unity's null check is also true for a destroyed object.
            if (machine == null || !DoorSync.HasState(machine, state))
            {
                return;
            }

            string eventName = EventPrefix + state;
            if (!DoorSync.HasGlobalTransition(machine, eventName))
            {
                machine.AddGlobalTransition(eventName, state);
            }

            machine.SendEvent(eventName);
        }

        private static PlayMakerFSM Machine(string path, string name)
        {
            Transform at = Scene(path);
            return at == null ? null : DoorSync.FindMachine(at, name);
        }

        // An object from the scene's top, switched on or not.
        private static Transform Scene(string path)
        {
            string plain = path.Substring(1);
            int slash = plain.IndexOf('/');
            GameObject root = GameObject.Find("/" + (slash < 0 ? plain : plain.Substring(0, slash)));
            return root == null ? null : slash < 0 ? root.transform : root.transform.Find(plain.Substring(slash + 1));
        }

        private ElectricitySection check;

        /// <summary>The section of the house's electricity (Fase 8 checksums).</summary>
        public ICheckSection Check
        {
            get { return check ?? (check = new ElectricitySection(this)); }
        }

        private bool CheckReady
        {
            get { return current != null && bound && !unavailable && (host || caughtUp); }
        }

        private void ReadCheck(List<CheckEntry> entries)
        {
            entries.Add(new CheckEntry(ChecksumReplication.KeyOf("power"), power == null ? 2UL : power.Value ? 1UL : 0UL));
            entries.Add(new CheckEntry(ChecksumReplication.KeyOf("bill"), envelope == null ? 2UL : envelope.activeSelf ? 1UL : 0UL));
            entries.Add(new CheckEntry(ChecksumReplication.KeyOf("unpaid"), unpaid == null ? 0UL : (ulong)(uint)Mathf.RoundToInt(unpaid.Value * 100f)));
            entries.Add(new CheckEntry(ChecksumReplication.KeyOf("main"), mainOn == null ? 2UL : mainOn.Value ? 1UL : 0UL));
            entries.Add(new CheckEntry(ChecksumReplication.KeyOf("dark"), status != null && IsDark(status.ActiveStateName) ? 1UL : 0UL));
            for (int i = 0; i < holders.Count; i++)
            {
                Holder holder = holders[i];
                if (holder.Use != null)
                {
                    entries.Add(new CheckEntry(ChecksumReplication.KeyOf("holder/" + holder.Id), (ulong)(uint)((holder.Fuse.Value * 100) + Mathf.RoundToInt(holder.Tight.Value))));
                }
            }
        }

        // A client: what the host said last is shown again; what this game lost of it comes again from the host.
        private void FixCheckHere(IList<ulong> keys)
        {
            ShowPower();
            if (heardUnpaid != int.MinValue && unpaid != null)
            {
                unpaid.Value = heardUnpaid / 100f;
            }

            if (keys.Contains(ChecksumReplication.KeyOf("main")) && knownMain >= 0 && mainOn != null && (mainOn.Value ? 1 : 0) != knownMain)
            {
                mainWanted = true;
                mainWant = knownMain == 1;
            }

            for (int i = 0; i < holders.Count; i++)
            {
                Holder holder = holders[i];
                if (keys.Contains(ChecksumReplication.KeyOf("holder/" + holder.Id)) && holder.WantBy != byte.MaxValue)
                {
                    holder.Wanted = true;
                    holder.Steps = 0;
                    holder.NextStep = 0.0;
                }
            }
        }

        // The host: everything said again, a blackout too.
        private void FixCheckFor(IList<ulong> keys)
        {
            toldPower = -1;
            toldKwh = int.MinValue;
            toldUnpaid = int.MinValue;
            toldBill = -1;
            TellHost(double.PositiveInfinity);
            if (knownMain >= 0)
            {
                current.Set(MainKey, new[] { knownMain, manager.Session.LocalPlayerId });
            }

            relaySerial++;
            int state = Array.IndexOf(RelayStates, IsDark(status.ActiveStateName) ? LightsOff : ResetLamp);
            current.Set(StatePrefix + state.ToString(CultureInfo.InvariantCulture), new[] { relaySerial });
            for (int i = 0; i < holders.Count; i++)
            {
                TellHolder(holders[i]);
            }
        }

        private sealed class ElectricitySection : ICheckSection
        {
            private readonly ElectricitySync owner;

            public ElectricitySection(ElectricitySync owner)
            {
                this.owner = owner;
            }

            public byte Id
            {
                get { return CheckSections.Electricity; }
            }

            public string Name
            {
                get { return "electricity"; }
            }

            public bool Ready
            {
                get { return owner.CheckReady; }
            }

            public bool CountsMissing
            {
                get { return false; }
            }

            public bool SendsEntries
            {
                get { return false; }
            }

            public void Read(List<CheckEntry> entries)
            {
                owner.ReadCheck(entries);
            }

            public ulong Coarse(ulong value)
            {
                return value;
            }

            public bool Same(ulong host, ulong here)
            {
                return host == here;
            }

            public void FixHere(IList<ulong> different, IList<ulong> onlyHost, IList<ulong> onlyHere)
            {
                owner.FixCheckHere(different);
            }

            public void FixFor(byte player, IList<ulong> keys)
            {
                owner.FixCheckFor(keys);
            }
        }

        private sealed class Holder
        {
            public string Id;
            public PlayMakerFSM Use;
            public PlayMakerFSM Screw;
            public PlayMakerFSM FuseAssembly;
            public FsmInt Fuse;
            public FsmFloat Tight;
            public int KnownFuse;
            public int KnownTight;
            public bool Wanted;
            public int WantFuse;
            public int WantTight;
            public byte WantBy = byte.MaxValue;
            public int Steps;
            public double NextStep;
        }
    }
}
