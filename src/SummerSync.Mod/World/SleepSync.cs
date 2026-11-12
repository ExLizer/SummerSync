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
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Sleeping with others: the hours go by only when every player sleeps at once. One
    /// who sleeps alone rests and wakes up with the time where it was.
    ///
    /// What is assumed about the game, from the dump of 2026-10-14 (docs/GAME_NOTES.md, "Dormir"): every bed (the
    /// house's, the cottage's, the cabin's, the jail's, the sofa, the van's and the cars' seats) is a "SleepTrigger"
    /// with a state machine "Activate". Its state "Sleep" sets "PlayerSleeps" and closes the eyes with an animation
    /// ("sleep_on" on the camera's "SleepEyes"); when that ends, in one go, "Calc sleep" picks two to ten hours by the
    /// fatigue, "Calc rates" rests the player (hunger, thirst and urine up by fatigue / 5, dirtiness up by 10, the
    /// fatigue to nothing), "Check time of day" adds the hours to the sun's "Time" (and a day past midnight), and the
    /// wake-up writes it and sends "WAKEUP" to everything; then "AnimateWake" and "State 5" get the player up.
    ///
    /// In a session, a player who lies down stays asleep (that animation held) while saying so ("sleep/&lt;player&gt;").
    /// Once every player sleeps, each game lets its own go on: the host's moves the time, and a client's is taken to the
    /// host's time (<see cref="WorldSync"/>). Nobody else coming within <see cref="WaitSeconds"/>, the player is rested
    /// as the game rests them and taken straight to getting up, past the hours and the wake-up.
    /// </summary>
    internal sealed class SleepSync
    {
        private const string Key = "sleep/";
        private const string SleepState = "Sleep";
        private const string GetUpState = "AnimateWake";
        private const string GetUpEvent = "SSYNC GET UP";
        private const float WaitSeconds = 20f;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly Dictionary<byte, bool> asleep = new Dictionary<byte, bool>();
        private readonly List<byte> left = new List<byte>();
        private TallyReplication current;
        private NetSession session;
        private FsmBool sleeps;
        private FsmString subtitle;
        private bool wasAsleep;
        private bool told;
        private Bed bed;
        private double holdSince;
        private string waiting;
        private int shownAwake;
        private int shownTotal;
        private bool shownSpanish;

        public SleepSync(Logger log, NetworkManager manager)
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
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                if (session != null)
                {
                    session.PlayerLeft -= OnPlayerLeft;
                }

                Release();
                current = tallies;
                session = current == null ? null : manager.Session;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }

                if (session != null)
                {
                    session.PlayerLeft += OnPlayerLeft;
                }

                asleep.Clear();
                left.Clear();
                wasAsleep = false;
                told = false;
                sleeps = FsmVariables.GlobalVariables.FindFsmBool("PlayerSleeps");
                subtitle = FsmVariables.GlobalVariables.FindFsmString("GUIsubtitle");
            }

            if (current == null || sleeps == null)
            {
                return;
            }

            for (int i = 0; i < left.Count; i++)
            {
                Forget(left[i]);
            }

            left.Clear();

            // Said as soon as this game is in the session, too: a player who took the number of one who left would
            // otherwise sleep as that one did, for the others, until lying down. Not before it is connected: until
            // then this player's number is not known.
            bool nowAsleep = sleeps.Value;
            if ((nowAsleep != wasAsleep || !told) && manager.Session.State == SessionState.Connected)
            {
                bool layingDown = nowAsleep && !wasAsleep;
                wasAsleep = nowAsleep;
                told = true;
                current.Set(Key + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { nowAsleep ? 1 : 0 });
                if (layingDown)
                {
                    LayDown(now);
                }
            }

            if (bed != null)
            {
                Hold(now);
            }
        }

        /// <summary>A bed held here is let go of, the game's way.</summary>
        public void Release()
        {
            if (bed != null)
            {
                bed.Let();
                bed = null;
            }

            ClearWaiting();
        }

        private void OnStateWanted(string key, int[] values)
        {
            byte player;
            if (key.StartsWith(Key, StringComparison.Ordinal) && values.Length >= 1
                && byte.TryParse(key.Substring(Key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out player))
            {
                bool sleeping = values[0] != 0;
                bool was;
                asleep.TryGetValue(player, out was);
                if (sleeping != was && player != manager.Session.LocalPlayerId)
                {
                    log.Info(LogCategory.Fsm, "Sleep: player " + player + (sleeping ? " lay down." : " is up."));
                }

                asleep[player] = sleeping;
            }
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            left.Add(player.Id);
        }

        // A player who left sleeps no more; the host says so, for whoever gets their number next.
        private void Forget(byte player)
        {
            asleep.Remove(player);
            if (manager.Mode == NetworkMode.Host)
            {
                current.Set(Key + player.ToString(CultureInfo.InvariantCulture), new[] { 0 });
            }
        }

        // Whether every other player in the game world sleeps: one in the main menu or loading cannot, and does not count.
        private bool EveryoneAsleep()
        {
            int awake;
            Count(out awake);
            return awake == 0;
        }

        // How many players are in the game world, this one too, and how many of the others are awake.
        private int Count(out int awake)
        {
            awake = 0;
            int inWorld = 1;
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                if (!remotes[i].InWorld)
                {
                    continue;
                }

                inWorld++;
                bool sleeping;
                if (!asleep.TryGetValue(remotes[i].Id, out sleeping) || !sleeping)
                {
                    awake++;
                }
            }

            return inWorld;
        }

        // The local player has lain down: the bed they are in is found and its eyes closing held.
        private void LayDown(double now)
        {
            bed = Bed.Find();
            if (bed == null)
            {
                // Passing out or being knocked out: not a bed, and the game goes on as it does.
                return;
            }

            bed.Unstick();
            if (EveryoneAsleep())
            {
                log.Info(LogCategory.Fsm, "Sleep: this player lay down last; everyone sleeps, the hours go by.");
                bed = null;
                return;
            }

            bed.Hold();
            holdSince = now;
            log.Info(LogCategory.Fsm, "Sleep: this player lay down in '" + bed.Name + "'; waiting for the others.");
        }

        private void Hold(double now)
        {
            int awake;
            int total = Count(out awake);
            if (awake == 0)
            {
                bed.Let();
                bed = null;
                ClearWaiting();
                log.Info(LogCategory.Fsm, "Sleep: everyone sleeps now; the hours go by.");
                return;
            }

            if (now - holdSince >= WaitSeconds || !sleeps.Value)
            {
                bed.GetUpRested();
                bed = null;
                waiting = null;
                log.Info(LogCategory.Fsm, "Sleep: the others did not lie down; this player got up rested, the time where it was.");
                return;
            }

            // The text is made again only when the count changes: this runs every frame.
            if (waiting == null || awake != shownAwake || total != shownTotal || shownSpanish != Texts.Spanish)
            {
                shownAwake = awake;
                shownTotal = total;
                shownSpanish = Texts.Spanish;
                waiting = Texts.Spanish
                    ? "Esperando a que duerman los demás (" + (total - awake) + " de " + total + ")"
                    : "Waiting for the others to fall asleep (" + (total - awake) + " of " + total + ")";
            }

            if (subtitle != null && subtitle.Value != waiting)
            {
                subtitle.Value = waiting;
            }
        }

        // The waiting text goes, if it is still the one shown.
        private void ClearWaiting()
        {
            if (subtitle != null && waiting != null && subtitle.Value == waiting)
            {
                subtitle.Value = string.Empty;
            }

            waiting = null;
        }

        private sealed class Bed
        {
            private readonly PlayMakerFSM machine;
            private readonly Animation eyes;
            private readonly string clip;
            private bool held;

            private Bed(PlayMakerFSM machine, Animation eyes, string clip)
            {
                this.machine = machine;
                this.eyes = eyes;
                this.clip = clip;
            }

            public string Name
            {
                get { return machine.transform.parent == null ? machine.name : machine.transform.parent.parent == null ? machine.transform.parent.name : machine.transform.parent.parent.name; }
            }

            // The bed whose machine is closing the local player's eyes now.
            public static Bed Find()
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM));
                for (int i = 0; i < all.Length; i++)
                {
                    var machine = all[i] as PlayMakerFSM;
                    if (machine == null || machine.FsmName != "Activate" || machine.gameObject.name != "SleepTrigger"
                        || !machine.gameObject.activeInHierarchy || machine.ActiveStateName != SleepState)
                    {
                        continue;
                    }

                    PlayAnimation close = Closing(machine);
                    GameObject target = close == null ? null : machine.Fsm.GetOwnerDefaultTarget(close.gameObject);
                    Animation eyes = target == null ? null : target.GetComponent<Animation>();
                    if (eyes != null && close.animName != null && !string.IsNullOrEmpty(close.animName.Value) && DoorSync.HasState(machine, GetUpState))
                    {
                        return new Bed(machine, eyes, close.animName.Value);
                    }
                }

                return null;
            }

            private static PlayAnimation Closing(PlayMakerFSM machine)
            {
                FsmState[] states = machine.Fsm == null ? null : machine.Fsm.States;
                for (int s = 0; states != null && s < states.Length; s++)
                {
                    if (states[s].Name != SleepState)
                    {
                        continue;
                    }

                    FsmStateAction[] actions = states[s].Actions;
                    for (int a = 0; actions != null && a < actions.Length; a++)
                    {
                        var play = actions[a] as PlayAnimation;
                        if (play != null)
                        {
                            return play;
                        }
                    }
                }

                return null;
            }

            /// <summary>The eyes stop closing where they are: the machine waits for them.</summary>
            public void Hold()
            {
                AnimationState state = eyes[clip];
                if (state != null)
                {
                    state.speed = 0f;
                    held = true;
                }
            }

            /// <summary>The eyes close on: the game sleeps as it does.</summary>
            public void Let()
            {
                // Unity's null check is also true for a destroyed object.
                AnimationState state = eyes == null ? null : eyes[clip];
                if (held && state != null)
                {
                    state.speed = 1f;
                }

                held = false;
            }

            /// <summary>The clip runs at the game's speed again, without going on from where it was held.</summary>
            public void Free()
            {
                // Unity's null check is also true for a destroyed object.
                AnimationState state = eyes == null ? null : eyes[clip];
                if (held && state != null)
                {
                    eyes.Stop(clip);
                }

                held = false;
                if (state != null && state.speed == 0f)
                {
                    state.speed = 1f;
                }
            }

            /// <summary>A clip left held by an earlier night closes the eyes as the game does.</summary>
            public void Unstick()
            {
                AnimationState state = eyes == null ? null : eyes[clip];
                if (!held && state != null && state.speed == 0f)
                {
                    state.speed = 1f;
                }
            }

            /// <summary>Rested as the game rests a sleeper, and up, past the hours and the wake-up.</summary>
            public void GetUpRested()
            {
                FsmVariables globals = FsmVariables.GlobalVariables;
                FsmFloat fatigue = globals.FindFsmFloat("PlayerFatigue");
                float rate = fatigue == null ? 0f : fatigue.Value / 5f;
                Add(globals, "PlayerHunger", rate);
                Add(globals, "PlayerThirst", rate);
                Add(globals, "PlayerUrine", rate);
                Add(globals, "PlayerDrunkAdjusted", -rate);
                Add(globals, "PlayerDirtiness", 10f);
                if (fatigue != null)
                {
                    fatigue.Value = 0f;
                }

                FsmBool sleeping = globals.FindFsmBool("PlayerSleeps");
                if (sleeping != null)
                {
                    sleeping.Value = false;
                }

                FsmFloat volume = globals.FindFsmFloat("GameVolume");
                if (volume != null)
                {
                    volume.Value = 1f;
                }

                FsmString text = globals.FindFsmString("GUIsubtitle");
                if (text != null)
                {
                    text.Value = string.Empty;
                }

                // The clip keeps its speed from one night to the next: left at 0, the next time the game plays it by
                // itself (this player lying down last, or with no session) the eyes never close and the bed never
                // lets go. Stopped first, so that the eyes do not close on while the player gets up.
                Free();
                if (!DoorSync.HasGlobalTransition(machine, GetUpEvent))
                {
                    machine.AddGlobalTransition(GetUpEvent, GetUpState);
                }

                machine.SendEvent(GetUpEvent);
            }

            private static void Add(FsmVariables globals, string name, float amount)
            {
                FsmFloat value = globals.FindFsmFloat(name);
                if (value != null)
                {
                    value.Value += amount;
                }
            }
        }
    }
}
