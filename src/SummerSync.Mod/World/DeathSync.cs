using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using SummerSync.Core.World;
using SummerSync.Unity.Players;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// A player's death, seen by the others, and coming back from it when the world has no permanent death.
    ///
    /// What is assumed about the game (the game with the bridge on 2026-10-16, docs/GAME_NOTES.md "Morir y volver"): every
    /// death runs "Systems/Death :: Activate Dead Body" from "Permadeath 2", which tests the global "PlayerPermaDeath" (the
    /// licence's "DeathButton" sets it and the save keeps it). Then "State 3" destroys the player's CharacterController,
    /// CharacterMotor and FPSInputController and goes to the state of the cause; that state and the next play the death's
    /// sound, hang the camera ("POV") from a dead body that falls ("Car deathbody", "Die 3", "Bloody death"), take the
    /// player ("SavePlayer") out of where they were and switch it off, and after two to five seconds reach "Take photo",
    /// the newspaper. With permanent death the save is deleted; without, "Mute audio" sets the needs a dead player comes back
    /// with (fatigue 10, hunger 12, thirst 8, drink and stress 0), "Save all" saves, and the main menu loads; "Continue" then
    /// puts the player at "YARD/GraveYardSpawn" ("Systems/Setup Game :: WaitPlayer", "Delay" and "Spawn"), which is the
    /// graveyard of Peräjärvi, by the shop. The game always loads the world again after a death.
    ///
    /// In a session the main menu is no way back: going there closes the session (Steam starts again). So in a session
    /// without permanent death the death is the game's up to the newspaper, without the actions that end the world (the
    /// player's controller destroyed, the player switched off), and the mod's action first in "Take photo" sends the machine
    /// to a state of the mod's that does nothing: the screen goes black, and the player wakes up at home with the needs the
    /// game gives a dead player (and urine, dirtiness and drink at nothing, as at the start), out of the seat they
    /// died in, with what the death moved put back where it was. Without a session the game's own way stands, the main menu
    /// takes the player back into the world by itself, and the mod moves them from the graveyard home. Home is where a new
    /// game starts: where the level has "PLAYER" before a save moves it.
    ///
    /// Whatever the way, the others are told where the player died (the tally "death/(player)": the vehicle and the seat, or
    /// the place, and which way they faced) and show them dead there (slumped in their seat in a vehicle, lying on foot)
    /// until they are back: the game has no body of a dead player to show (a vehicle's "DeadBody" is a cube with colliders
    /// that carries the dead player's view, and so is the one under "PLAYER").
    /// </summary>
    internal sealed class DeathSync
    {
        private const string DeathPath = "Systems";
        private const string DeathChild = "Death";
        private const string DeathMachine = "Activate Dead Body";
        private const string StartState = "Permadeath 2";
        private const string EndingState = "State 3";
        private const string PhotoState = "Take photo";
        private const string SaveState = "Save all";
        private const string HoldState = "SummerSync comes back";
        private const string HoldEvent = "SUMMERSYNC COME BACK";
        private const string ResumeEvent = "SUMMERSYNC RESUME";
        private const string SetupPath = "Systems/Setup Game";
        private const string SetupMachine = "WaitPlayer";
        private const string JailDays = "JailDays";
        private const string SetupDoneState = "Activate game";
        private const string CameraPath = "Pivot/AnimPivot/Camera/FPSCamera/FPSCamera";
        private const string ViewVariable = "POV";
        private const string PlayerVariable = "SavePlayer";
        private const string DeadBodyVariable = "DeadBody";
        private const string TallyPrefix = "death/";
        private const float SlumpedPitch = -60f;
        private const float SeatedEyes = 0.6f;
        private const float LyingEyes = 0.15f;
        private const double BodyLongestSeconds = 900.0;
        private const double FadeSeconds = 1.5;
        private const double DarkSeconds = 1.5;

        // The longest the game's death can take before the newspaper (the crash's is five seconds): after that the mod holds it.
        private const double LongestDying = 12.0;

        /// <summary>A vehicle's driver in the tally, in place of a passenger seat.</summary>
        public const int DriverSeat = 255;

        // The tally's seat for "back from the death": the others show the player as their game says again.
        private const int BackSeat = -1;

        // Where a new game starts: "PLAYER" in level2 (the level file read with UnityPy on 2026-10-16), the player inside the
        // house. A new game saves that place before anything moves the player ("WaitPlayer", "Save game").
        private static readonly Vector3 HomePlace = new Vector3(-11.186f, 0f, 13.088f);
        private const float HomeFacing = 151.97f;

        private static readonly string[] ComeBackNeeds = { "PlayerFatigue", "PlayerHunger", "PlayerThirst", "PlayerStress", "PlayerDrunk", "PlayerDrunkAdjusted", "PlayerUrine", "PlayerDirtiness" };
        private static readonly float[] ComeBackValues = { 10f, 12f, 8f, 0f, 0f, 0f, 0f, 0f };
        // The game's machines that kill the player and then stop for good, as the game loads the world again after a death:
        // where each goes on from (the bridge, 2026-10-16). The needs ("Simulation": thirst, hunger, urine, stress, burns)
        // go on with the next round of the needs, not from their start, which loads the needs from the save.
        private static readonly string[][] Resumes =
        {
            new[] { "Database/PlayerDatabase", "Simulation", "Run simulation" },
        };

        private static readonly HashSet<int> Hooked = new HashSet<int>();
        private static DeathSync active;

        /// <summary>
        /// Set when this player dies without permanent death and without a session, for the main menu that loads next: it
        /// takes the player back into the world by itself. Lives with the process, as the main menu makes the mod anew.
        /// </summary>
        public static bool ReturnAfterDeath { get; set; }

        // The player died without permanent death and without a session: when the world is back (a new instance, made after
        // the main menu), from the graveyard home.
        private static bool wakeAtHome;
        private static DeathSync diedIn;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly PlayerPresenter players;
        private readonly Dictionary<byte, Body> bodies = new Dictionary<byte, Body>();
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();
        private readonly List<FsmStateAction> heldBack = new List<FsmStateAction>();
        private readonly List<Placed> before = new List<Placed>();
        private TallyReplication current;
        private Transform player;
        private PlayMakerFSM machine;
        private double nextLook;
        private double clock;
        private int lastToken;
        private double dyingSince = double.NaN;
        private double heldAt = double.NaN;
        private double backAt = double.NaN;
        private string dyingWhere;
        private Texture2D black;

        private sealed class Body
        {
            public bool WasAway;
            public double Since;
        }

        // Where something the death moves was, to put it back: its parent and place in it, and whether it was on.
        private sealed class Placed
        {
            public Transform What;
            public Transform Parent;
            public Vector3 Place;
            public Quaternion Turn;
            public bool On;
            public bool WithActive;
            public bool Held;
            public Rigidbody HeldTo;
            public float HeldForce;
            public float HeldTorque;
        }

        public DeathSync(Logger log, NetworkManager manager, VehiclePresenter vehicles, PlayerPresenter players)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
            this.players = players;
        }

        /// <summary>Whether the local player is dead and waiting to come back here (a session without permanent death).</summary>
        public bool Dying
        {
            get { return !double.IsNaN(dyingSince) && double.IsNaN(backAt); }
        }

        /// <summary>Once per frame, after the players are placed. <paramref name="localPlayer"/> is null without a world.</summary>
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
                player = localPlayer;
                machine = null;
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

            // Unity's null check is also true for a destroyed object.
            if (machine == null && now >= nextLook)
            {
                nextLook = now + 2.0;
                Hook();
            }

            // The world is back after the game's way, and the game has put the player at the graveyard (or in jail).
            if (wakeAtHome && diedIn != this && SetupDone())
            {
                wakeAtHome = false;
                WakeAtHome();
            }

            if (Dying)
            {
                if (double.IsNaN(heldAt) && now - dyingSince >= LongestDying)
                {
                    Hold("the game's death did not reach '" + PhotoState + "' in " + LongestDying.ToString("0", CultureInfo.InvariantCulture) + " s");
                }

                if (!double.IsNaN(heldAt) && now - heldAt >= FadeSeconds + DarkSeconds)
                {
                    ComeBack();
                }
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value);
            }

            heard.Clear();
            Tidy();
        }

        /// <summary>The screen while a player who died in a session waits to come back: black, fading in and out. From OnGUI only.</summary>
        public void OnGUI()
        {
            if (double.IsNaN(heldAt) || Event.current.type != EventType.Repaint)
            {
                return;
            }

            double alpha = double.IsNaN(backAt) ? Math.Min(1.0, (clock - heldAt) / FadeSeconds) : 1.0 - Math.Min(1.0, (clock - backAt) / FadeSeconds);
            if (!double.IsNaN(backAt) && alpha <= 0.0)
            {
                dyingSince = double.NaN;
                heldAt = double.NaN;
                backAt = double.NaN;
                return;
            }

            if (black == null)
            {
                black = new Texture2D(1, 1);
                black.SetPixel(0, 0, Color.black);
                black.Apply();
            }

            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, (float)alpha);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), black);
            GUI.color = was;
        }

        /// <summary>
        /// Every player shown dead here is shown as their game says again; a death the local player is coming back from ends
        /// now, at home.
        /// </summary>
        public void Release()
        {
            if (Dying && player != null)
            {
                ComeBack();
            }

            foreach (KeyValuePair<byte, Body> body in bodies)
            {
                players.SetDead(body.Key, null);
            }

            bodies.Clear();
            heard.Clear();
        }

        // The mod's actions first in the death's first state, the newspaper's and the saving one, and its holding state. Once
        // per machine.
        private void Hook()
        {
            GameObject systems = GameObject.Find("/" + DeathPath);
            Transform death = systems == null ? null : systems.transform.Find(DeathChild);
            PlayMakerFSM found = death == null ? null : DoorSync.FindMachine(death, DeathMachine);
            if (found == null)
            {
                return;
            }

            int id = found.GetInstanceID();
            if (!Hooked.Contains(id))
            {
                try
                {
                    // Its object is off until a death: its machine was never started.
                    found.InitializeFSM();
                    string[] needed = { StartState, EndingState, PhotoState, SaveState };
                    for (int i = 0; i < needed.Length; i++)
                    {
                        if (!DoorSync.HasState(found, needed[i]))
                        {
                            throw new InvalidOperationException("no state '" + needed[i] + "'");
                        }
                    }

                    if (!DoorSync.HasState(found, HoldState))
                    {
                        found.AddState(HoldState);
                    }

                    if (found.GetGlobalTransition(HoldEvent) == null)
                    {
                        found.AddGlobalTransition(HoldEvent, HoldState);
                    }

                    if (!found.FsmInject(StartState, OnDying, false, 0, false) || !found.FsmInject(PhotoState, OnPhoto, false, 0, false)
                        || !found.FsmInject(SaveState, OnSaving, false, 0, false))
                    {
                        throw new InvalidOperationException("its states could not take the mod's action");
                    }
                }
                catch (Exception exception)
                {
                    log.Warning(LogCategory.Player, "Deaths: '" + DeathPath + "/" + DeathChild + " :: " + DeathMachine + "' is not as this build expects (" + exception.Message + "): a death here is the game's alone.");
                    machine = found;
                    return;
                }

                Hooked.Add(id);
            }

            machine = found;
            log.Info(LogCategory.Player, "Deaths: the game's death is watched; another player's death is shown here.");
        }

        private static void OnDying()
        {
            DeathSync sync = active;
            if (sync != null)
            {
                sync.Die();
            }
        }

        private static void OnPhoto()
        {
            DeathSync sync = active;
            if (sync != null && sync.Dying && double.IsNaN(sync.heldAt))
            {
                sync.Hold("the game's death reached '" + PhotoState + "'");
            }
        }

        private static void OnSaving()
        {
            DeathSync sync = active;
            if (sync != null)
            {
                sync.Saving();
            }
        }

        // This player dies: the others are told where, and the way back is set up if the world has no permanent death.
        private void Die()
        {
            FsmBool permanent = FsmVariables.GlobalVariables.FindFsmBool("PlayerPermaDeath");
            bool forGood = permanent == null || permanent.Value;
            bool inSession = manager.Session != null && manager.Session.State == SessionState.Connected && (manager.Mode == NetworkMode.Host || manager.Mode == NetworkMode.Client);
            ushort vehicle;
            int seat;
            Vector3 at;
            vehicles.WhereLocalPlayerIs(out vehicle, out seat, out at);
            dyingWhere = vehicle != 0 ? "in vehicle " + vehicle + (seat == DriverSeat ? ", at the wheel" : seat > 0 ? ", seat " + seat : ", standing") : "on foot";
            log.Info(
                LogCategory.Player,
                "Deaths: the local player died " + dyingWhere
                + (forGood
                    ? "; the world has permanent death: the game's way."
                    : inSession ? "; no permanent death, in a session: the game's death up to the newspaper, then awake at home without the main menu."
                    : "; no permanent death: the game's way, and back into the world from the main menu."));
            Tell(vehicle, seat, at, player == null || vehicle != 0 ? 0f : player.eulerAngles.y);
            if (forGood)
            {
                GuestSync.LocalPlayerDied(false);
                return;
            }

            if (!inSession)
            {
                ReturnAfterDeath = true;
                wakeAtHome = true;
                diedIn = this;
                return;
            }

            dyingSince = clock;
            heldAt = double.NaN;
            backAt = double.NaN;
            vehicles.NoteLocalDeath();
            NoteWhatDeathMoves();
            HoldBackTheEnd();

            // The player's capsule stays where they died, out of everything's way, until they are back.
            CharacterController controller = player == null ? null : player.GetComponent<CharacterController>();
            if (controller != null)
            {
                controller.enabled = false;
            }

            FsmBool stop = FsmVariables.GlobalVariables.FindFsmBool("PlayerStop");
            if (stop != null)
            {
                stop.Value = true;
            }
        }

        // The actions of the death that end the world: the player's controller destroyed, the player switched off. Held back
        // for this death; on again when the player is back.
        private void HoldBackTheEnd()
        {
            GameObject savedPlayer = Variable(PlayerVariable);
            if (savedPlayer == null && player != null)
            {
                savedPlayer = player.gameObject;
            }

            FsmState[] states = machine == null ? null : machine.FsmStates;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    FsmStateAction action = actions[a];
                    if (!action.Enabled)
                    {
                        continue;
                    }

                    bool ends = false;
                    if (states[s].Name == EndingState)
                    {
                        // The controller destroyed, and the options switched off (the game is about to load the main menu).
                        ends = action.GetType().Name == "DestroyComponent" || action is ActivateGameObject;
                    }
                    else
                    {
                        // The player switched off once the camera hangs from the dead body.
                        var activate = action as ActivateGameObject;
                        ends = activate != null && !activate.activate.Value && machine.Fsm.GetOwnerDefaultTarget(activate.gameObject) == savedPlayer;
                    }

                    if (ends)
                    {
                        action.Enabled = false;
                        heldBack.Add(action);
                    }
                }
            }

            log.Info(LogCategory.Player, "Deaths: " + heldBack.Count + " action(s) of the game's death that end the world held back for this death.");
        }

        // The camera, what hangs under the death's object (the blood) and the player's own dead body, as they are before the
        // death moves them.
        private void NoteWhatDeathMoves()
        {
            before.Clear();
            GameObject view = Variable(ViewVariable);
            if (view == null && player != null)
            {
                Transform camera = player.Find(CameraPath);
                view = camera == null ? null : camera.gameObject;
            }

            if (view != null)
            {
                Note(view.transform, false);
            }

            if (machine != null)
            {
                Transform death = machine.transform;
                for (int i = 0; i < death.childCount; i++)
                {
                    NoteAll(death.GetChild(i));
                }
            }

            FsmGameObject deadBody = machine == null ? null : machine.FsmVariables.FindFsmGameObject(DeadBodyVariable);
            if (deadBody != null && deadBody.Value != null)
            {
                NoteAll(deadBody.Value.transform);
            }
        }

        private void NoteAll(Transform what)
        {
            Note(what, true);
            for (int i = 0; i < what.childCount; i++)
            {
                NoteAll(what.GetChild(i));
            }
        }

        private void Note(Transform what, bool withActive)
        {
            FixedJoint hold = what.GetComponent<FixedJoint>();
            before.Add(new Placed
            {
                What = what,
                Parent = what.parent,
                Place = what.localPosition,
                Turn = what.localRotation,
                On = what.gameObject.activeSelf,
                WithActive = withActive,
                Held = hold != null,
                HeldTo = hold == null ? null : hold.connectedBody,
                HeldForce = hold == null ? 0f : hold.breakForce,
                HeldTorque = hold == null ? 0f : hold.breakTorque,
            });
        }

        // Everything noted back where it was: parents first, then what was on and off, then the joints that held them.
        private int PutBack()
        {
            int moved = 0;
            for (int i = 0; i < before.Count; i++)
            {
                Placed placed = before[i];

                // Unity's null check is also true for a destroyed object.
                if (placed.What == null)
                {
                    continue;
                }

                if (placed.What.parent != placed.Parent)
                {
                    moved++;
                    placed.What.parent = placed.Parent;
                }

                placed.What.localPosition = placed.Place;
                placed.What.localRotation = placed.Turn;
                Rigidbody body = placed.What.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }

            for (int i = 0; i < before.Count; i++)
            {
                Placed placed = before[i];
                if (placed.What == null)
                {
                    continue;
                }

                if (placed.WithActive && placed.What.gameObject.activeSelf != placed.On)
                {
                    placed.What.gameObject.SetActive(placed.On);
                }

                if (placed.Held && placed.What.GetComponent<FixedJoint>() == null)
                {
                    FixedJoint hold = placed.What.gameObject.AddComponent<FixedJoint>();
                    hold.connectedBody = placed.HeldTo;
                    hold.breakForce = placed.HeldForce;
                    hold.breakTorque = placed.HeldTorque;
                }
            }

            before.Clear();
            return moved;
        }

        // The game's death reached the newspaper, or took too long: the machine waits in the mod's state, and the screen goes
        // black.
        private void Hold(string why)
        {
            heldAt = clock;
            if (machine != null)
            {
                // The rest of the state is not run.
                machine.SendEvent(HoldEvent);
            }

            log.Info(LogCategory.Player, "Deaths: " + why + "; the screen goes black and the player wakes up at home.");
        }

        // The game's way without permanent death, right before it saves the dead player: the rest of the needs as at the start.
        private void Saving()
        {
            SetNeeds(3);
            log.Info(LogCategory.Player, "Deaths: no permanent death; the game saves the player to come back (the mod takes them home), urine, dirtiness and drink at nothing.");
        }

        // Up from a death in a session: what the death moved back where it was, out of the vehicle, at home, the needs as the
        // game sets them.
        private void ComeBack()
        {
            backAt = clock;
            if (double.IsNaN(heldAt))
            {
                heldAt = clock;
            }

            for (int i = 0; i < heldBack.Count; i++)
            {
                heldBack[i].Enabled = true;
            }

            heldBack.Clear();

            // The camera first: it may hang from a dead body that goes away.
            int moved = PutBack();
            string vehicleDone = vehicles.AfterLocalDeath();
            if (machine != null)
            {
                // Its causes back to none, and off: on again at the next death, from its start.
                FsmBool[] causes = machine.FsmVariables.BoolVariables;
                for (int i = 0; i < causes.Length; i++)
                {
                    causes[i].Value = false;
                }

                machine.gameObject.SetActive(false);
            }

            FsmGameObject deadView = FsmVariables.GlobalVariables.FindFsmGameObject("VehicleDeadBody");
            if (deadView != null)
            {
                deadView.Value = null;
            }

            // Unity's null check is also true for a destroyed object.
            if (CrashDeath.LastBody != null)
            {
                UnityEngine.Object.Destroy(CrashDeath.LastBody);
                CrashDeath.LastBody = null;
            }

            Transform camera = player == null ? null : player.Find(CameraPath);
            Behaviour overlay = camera == null ? null : camera.GetComponent("ScreenOverlay") as Behaviour;
            if (overlay != null)
            {
                overlay.enabled = false;
            }

            SetNeeds(ComeBackNeeds.Length);
            if (player != null)
            {
                if (!player.gameObject.activeSelf)
                {
                    player.gameObject.SetActive(true);
                }

                PutAtHome(player);
            }

            FsmBool stop = FsmVariables.GlobalVariables.FindFsmBool("PlayerStop");
            if (stop != null)
            {
                stop.Value = false;
            }

            PlayMakerFSM crouch = player == null ? null : DoorSync.FindMachine(player, "Crouch");
            FsmBool inCar = crouch == null ? null : crouch.FsmVariables.FindFsmBool("PlayerInCar");
            if (inCar != null)
            {
                inCar.Value = false;
            }

            string resumed = Resume();
            Tell(0, BackSeat, Vector3.zero, 0f);
            log.Info(
                LogCategory.Player,
                resumed + "Deaths: the local player, dead " + (dyingWhere ?? "somewhere") + " " + (clock - dyingSince).ToString("0.0", CultureInfo.InvariantCulture)
                + " s ago, wakes up at home with the needs the game gives a dead player; " + moved + " thing(s) the death had moved put back; " + vehicleDone + ".");
        }

        // Whether the game has put the player where it loads them ("WaitPlayer" ends in "Activate game").
        private static bool SetupDone()
        {
            PlayMakerFSM wait = SetupMachineFound();
            return wait == null || wait.ActiveStateName == SetupDoneState;
        }

        private static PlayMakerFSM SetupMachineFound()
        {
            GameObject setup = GameObject.Find("/" + SetupPath);
            return setup == null ? null : DoorSync.FindMachine(setup.transform, SetupMachine);
        }

        // Back in the world after the game's way: home, unless the game put the player in jail.
        private void WakeAtHome()
        {
            PlayMakerFSM wait = SetupMachineFound();
            FsmInt jail = wait == null ? null : wait.FsmVariables.FindFsmInt(JailDays);
            if (jail != null && jail.Value > 0)
            {
                log.Info(LogCategory.Player, "Deaths: back in the world after a death, in jail (" + jail.Value + " day(s)): the player stays there, as the game put them.");
                return;
            }

            Vector3 was = player.position;
            PutAtHome(player);
            log.Info(
                LogCategory.Player,
                "Deaths: back in the world after a death; the game put the player at (" + was.x.ToString("0", CultureInfo.InvariantCulture) + ", "
                + was.z.ToString("0", CultureInfo.InvariantCulture) + "), the graveyard: moved home, where a new game starts.");
        }

        /// <summary>The player at home, where a new game starts, on foot and facing the way a new game does.</summary>
        public static void PutAtHome(Transform player)
        {
            CharacterController controller = player.GetComponent<CharacterController>();
            if (controller != null)
            {
                controller.enabled = false;
            }

            player.parent = null;
            player.position = HomePlace;
            player.rotation = Quaternion.Euler(0f, HomeFacing, 0f);
            if (controller != null)
            {
                controller.enabled = true;
            }
        }

        // The machines that killed the player and stopped, going on again; and any other machine that switched the death on
        // and stopped, said in the log: it will not do it again until the world loads. Returns what was resumed, for the log.
        private string Resume()
        {
            var done = new System.Text.StringBuilder();
            for (int i = 0; i < Resumes.Length; i++)
            {
                GameObject owner = GameObject.Find("/" + Resumes[i][0]);
                PlayMakerFSM stopped = owner == null ? null : DoorSync.FindMachine(owner.transform, Resumes[i][1]);
                FsmState state = stopped == null ? null : stopped.Fsm.ActiveState;
                if (state == null || state.Transitions.Length != 0)
                {
                    continue;
                }

                if (!DoorSync.HasGlobalTransition(stopped, ResumeEvent))
                {
                    stopped.AddGlobalTransition(ResumeEvent, Resumes[i][2]);
                }

                stopped.SendEvent(ResumeEvent);
                done.Append("Deaths: '").Append(Resumes[i][0]).Append(" :: ").Append(Resumes[i][1]).Append("' killed the player in '").Append(state.Name)
                    .Append("' and goes on from '").Append(Resumes[i][2]).Append("'. ");
            }

            GameObject death = machine == null ? null : machine.gameObject;
            PlayMakerFSM[] machines = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
            for (int i = 0; death != null && i < machines.Length; i++)
            {
                // One with a global transition can be woken again (the camera's "Death" by DEATH).
                FsmState state = machines[i].Fsm.ActiveState;
                if (machines[i] == machine || state == null || state.Transitions.Length != 0 || machines[i].FsmGlobalTransitions.Length != 0 || !state.ActionsLoaded)
                {
                    continue;
                }

                FsmStateAction[] actions = state.Actions;
                for (int a = 0; a < actions.Length; a++)
                {
                    var activate = actions[a] as ActivateGameObject;
                    if (activate != null && activate.activate.Value && machines[i].Fsm.GetOwnerDefaultTarget(activate.gameObject) == death)
                    {
                        log.Warning(
                            LogCategory.Player,
                            "Deaths: '" + PathOf(machines[i].transform) + " :: " + machines[i].FsmName + "' switched the death on and stays in '" + state.Name
                            + "' (the game loads the world again after a death): it will not do it again until the world loads.");
                        break;
                    }
                }
            }

            return done.ToString();
        }

        private static string PathOf(Transform at)
        {
            string path = at.name;
            for (Transform up = at.parent; up != null; up = up.parent)
            {
                path = up.name + "/" + path;
            }

            return path;
        }

        // The last so many needs of the way back, set.
        private static void SetNeeds(int count)
        {
            int first = count < ComeBackNeeds.Length ? ComeBackNeeds.Length - count : 0;
            for (int i = first; i < ComeBackNeeds.Length; i++)
            {
                FsmFloat need = FsmVariables.GlobalVariables.FindFsmFloat(ComeBackNeeds[i]);
                if (need != null)
                {
                    need.Value = ComeBackValues[i];
                }
            }
        }

        private static GameObject Variable(string name)
        {
            FsmGameObject found = FsmVariables.GlobalVariables.FindFsmGameObject(name);
            return found == null ? null : found.Value;
        }

        // The others are told: where this player died, or (seat -1) that they are back.
        private void Tell(ushort vehicle, int seat, Vector3 at, float facing)
        {
            if (current == null || manager.Session == null || manager.Session.State != SessionState.Connected)
            {
                return;
            }

            int token = (int)((long)(clock * 1000.0) & 0x7FFFFFFF);
            current.Set(
                TallyPrefix + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture),
                new[] { token, vehicle, seat, Mathf.RoundToInt(at.x * 100f), Mathf.RoundToInt(at.y * 100f), Mathf.RoundToInt(at.z * 100f), Mathf.RoundToInt(facing) });
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(TallyPrefix, StringComparison.Ordinal) && values.Length >= 7 && !current.CatchingUp)
            {
                heard.Add(new KeyValuePair<string, int[]>(key, values));
            }
        }

        // Another player died, or is back.
        private void Hear(string key, int[] values)
        {
            byte who;
            if (!byte.TryParse(key.Substring(TallyPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out who)
                || manager.Session == null || who == manager.Session.LocalPlayerId || values[0] == lastToken)
            {
                return;
            }

            lastToken = values[0];
            int seat = values[2];
            if (seat == BackSeat)
            {
                if (bodies.Remove(who))
                {
                    players.SetDead(who, null);
                    log.Info(LogCategory.Player, "Deaths: player " + who + " is back from their death.");
                }

                return;
            }

            // In a vehicle slumped where they sat or stood, the eyes low and the head down; on foot lying where they fell.
            ushort vehicle = (ushort)values[1];
            var pose = new PlayerPose(values[3] / 100f, values[4] / 100f, values[5] / 100f, values[6], SlumpedPitch, vehicle != 0 ? SeatedEyes : LyingEyes);
            pose.Parent = vehicle;
            bodies[who] = new Body { Since = clock };
            players.SetDead(who, pose);
            log.Info(
                LogCategory.Player,
                "Deaths: player " + who + " died " + (vehicle != 0 ? "in vehicle " + vehicle + (seat == DriverSeat ? " at the wheel" : seat > 0 ? ", seat " + seat : ", standing") : "on foot")
                + "; shown dead there until they are back.");
        }

        // A player shown dead is shown as their game says again when they are back in the world after having left it, or left
        // the session.
        private void Tidy()
        {
            if (bodies.Count == 0)
            {
                return;
            }

            List<byte> done = null;
            foreach (KeyValuePair<byte, Body> entry in bodies)
            {
                RemotePlayer remote = manager.Players == null ? null : manager.Players.Find(entry.Key);
                Body body = entry.Value;
                if (remote != null && !remote.InWorld)
                {
                    body.WasAway = true;
                }

                if (remote == null || (body.WasAway && remote.InWorld) || clock - body.Since > BodyLongestSeconds)
                {
                    (done ?? (done = new List<byte>())).Add(entry.Key);
                }
            }

            for (int i = 0; done != null && i < done.Count; i++)
            {
                bodies.Remove(done[i]);
                players.SetDead(done[i], null);
                log.Info(LogCategory.Player, "Deaths: player " + done[i] + " is back, or gone: shown as their game says again.");
            }
        }
    }
}
