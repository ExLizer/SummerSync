using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// What a machine of the game does to the time when it wakes its player up somewhere else (knocked out, let out of
    /// jail): it writes the new hour to the sun's block ("Color"'s "Time"), may move the day on ("GlobalDay"), starts the
    /// weather over (switches the clouds' "Weather" off and on again) and broadcasts "WAKEUP", on which the sun begins its
    /// block again and the clocks are set from it. In a session the clock is the host's and such a wake-up moves nobody's:
    /// those actions are held back, and "WAKEUP" reaches every machine but the clocks' (docs/GAME_NOTES.md, "El knockout").
    /// </summary>
    internal static class SparedClock
    {
        private const string WakeEvent = "WAKEUP";
        private const string BlockMachine = "Color";
        private const string BlockVariable = "Time";
        private const string DayGlobal = "GlobalDay";
        private const string WeatherMachine = "Weather";

        // The machines that keep the time, on these objects: the sun's blocks and hours, and the kitchen clock.
        private static readonly string[][] Clocks =
        {
            new[] { "MAP", "SUN", "Pivot", "SUN" },
            new[] { "YARD", "Building", "Dynamics", "SuomiClock", "Clock" },
        };

        private static readonly HashSet<FsmStateAction> Replaced = new HashSet<FsmStateAction>();

        /// <summary>Holds back what the machine does to the time; the actions held are added to <paramref name="held"/>.</summary>
        public static void Hold(PlayMakerFSM machine, List<FsmStateAction> held)
        {
            FsmState[] states = machine.FsmStates;
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

                    var wake = action as SendEvent;
                    if (wake != null && !Wakes(wake))
                    {
                        continue;
                    }

                    if (wake == null && !MovesTime(action))
                    {
                        continue;
                    }

                    if (wake != null && !Replaced.Contains(wake))
                    {
                        // In its place, the same broadcast but for the clocks; it does nothing while the game's own is on.
                        int at = Array.IndexOf(states[s].Actions, wake);
                        if (!machine.FsmInject(states[s].Name, () => { if (!wake.Enabled) WakeAllButClocks(); }, false, at, false))
                        {
                            continue;
                        }

                        Replaced.Add(wake);
                    }

                    action.Enabled = false;
                    held.Add(action);
                }
            }
        }

        /// <summary>The actions back as the game had them.</summary>
        public static void Release(List<FsmStateAction> held)
        {
            for (int i = 0; i < held.Count; i++)
            {
                if (held[i] != null)
                {
                    held[i].Enabled = true;
                }
            }

            held.Clear();
        }

        private static bool Wakes(SendEvent send)
        {
            return send.eventTarget != null && send.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll && send.sendEvent != null
                && send.sendEvent.Name == WakeEvent && (send.delay == null || send.delay.Value <= 0f) && !send.everyFrame;
        }

        private static bool MovesTime(FsmStateAction action)
        {
            var write = action as SetFsmInt;
            if (write != null)
            {
                return write.fsmName != null && write.fsmName.Value == BlockMachine && write.variableName != null && write.variableName.Value == BlockVariable;
            }

            var add = action as IntAdd;
            if (add != null)
            {
                return add.intVariable != null && add.intVariable.Name == DayGlobal;
            }

            var set = action as SetIntValue;
            if (set != null)
            {
                return set.intVariable != null && set.intVariable.Name == DayGlobal;
            }

            var enable = action as EnableFSM;
            return enable != null && enable.fsmName != null && enable.fsmName.Value == WeatherMachine;
        }

        // What PlayMaker's broadcast does (Fsm.BroadcastEvent), but for the machines that keep the time.
        private static void WakeAllButClocks()
        {
            FsmEvent wake = FsmEvent.GetFsmEvent(WakeEvent);
            var machines = new List<PlayMakerFSM>(PlayMakerFSM.FsmList);
            for (int i = 0; i < machines.Count; i++)
            {
                PlayMakerFSM machine = machines[i];
                if (machine == null || machine.Fsm == null || IsClock(machine.transform))
                {
                    continue;
                }

                machine.Fsm.ProcessEvent(wake);
            }
        }

        private static bool IsClock(Transform at)
        {
            for (int c = 0; c < Clocks.Length; c++)
            {
                string[] path = Clocks[c];
                Transform step = at;
                int k = path.Length - 1;
                while (step != null && k >= 0 && step.name == path[k])
                {
                    step = step.parent;
                    k--;
                }

                if (k < 0 && step == null)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
