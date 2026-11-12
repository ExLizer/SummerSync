using System;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The kitchen stove's four hot plates: a knob turned in one game is turned in every game.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "La cocina de la casa"): each of
    /// "YARD/Building/KITCHEN/OvenStove/KnobPower1" to "KnobPower4" has a machine "Screw" that the scroll wheel turns:
    /// its float "Rot" goes up or down by "ScrewAmount" (51.4), back to 0 past 330, and is written as the y angle of the
    /// knob's "mesh"; "State 1" then reads that angle into "Data". "OvenStove/Simulation" (machine "Data") reads each
    /// knob's "Data" once a turn of its loop: above 10 the plate heats, else it cools. The oven's two knobs have their
    /// machines switched off.
    ///
    /// Where the knobs are is told by whoever turned one last. Each game's stove heats by itself from them.
    ///
    /// Offline and in the loopback test nothing here touches the game.
    /// </summary>
    internal sealed class StoveSync
    {
        private const string Key = "stove/knobs";
        private const string StovePath = "/YARD/Building/KITCHEN/OvenStove";
        private const int Knobs = 4;
        private const double SearchInterval = 3.0;

        // A player who just turned a knob has it as they left it, whatever arrives meanwhile (what they said, coming back).
        private const double OwnSeconds = 0.6;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly Knob[] knobs = new Knob[Knobs];
        private readonly int[] known = new int[Knobs];
        private TallyReplication current;
        private double nextSearch;
        private double ownUntil;
        private bool found;
        private bool heard;
        private int[] heardValues;
        private bool heardCatchUp;

        public StoveSync(Logger log, NetworkManager manager)
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

                current = tallies;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }

                found = false;
                heard = false;
                nextSearch = now + 1.0;
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
                Search();
                if (!found)
                {
                    return;
                }
            }

            // Unity's null check is also true for a destroyed object.
            if (knobs[0].Machine == null)
            {
                found = false;
                return;
            }

            bool turned = false;
            for (int i = 0; i < Knobs; i++)
            {
                int now10 = Tenths(knobs[i].Rot.Value);
                if (now10 != known[i])
                {
                    known[i] = now10;
                    turned = true;
                }
            }

            if (turned)
            {
                ownUntil = now + OwnSeconds;
                current.Set(Key, (int[])known.Clone());
                log.Info(LogCategory.Fsm, "Stove: a hot plate's knob was turned here (" + Describe(known) + "); told.");
            }

            if (!heard)
            {
                return;
            }

            heard = false;
            if (now < ownUntil)
            {
                return;
            }

            bool any = false;
            for (int i = 0; i < Knobs; i++)
            {
                if (heardValues[i] == known[i])
                {
                    continue;
                }

                any = true;
                Knob knob = knobs[i];
                float angle = heardValues[i] / 10f;
                knob.Rot.Value = angle;
                known[i] = heardValues[i];
                if (knob.Mesh != null)
                {
                    knob.Mesh.localRotation = Quaternion.Euler(0f, angle, 0f);
                    if (knob.Data != null)
                    {
                        // What the game reads back after a turn: the simulation heats the plate from this.
                        knob.Data.Value = knob.Mesh.localEulerAngles.y;
                    }
                }

                if (!heardCatchUp)
                {
                    try
                    {
                        MasterAudio.PlaySound3DFollowTransformAndForget("HouseFoley", knob.Machine.transform, 1f, 1f, 0f, "oven_knob");
                    }
                    catch (Exception exception)
                    {
                        log.Warning(LogCategory.Fsm, "Stove: the knob's sound could not be played: " + exception.Message);
                    }
                }
            }

            if (any)
            {
                log.Info(LogCategory.Fsm, "Stove: the hot plates' knobs " + (heardCatchUp ? "are, in the host's game" : "were turned in another game") + ": " + Describe(known) + ".");
            }
        }

        private void Search()
        {
            GameObject stove = GameObject.Find(StovePath);
            if (stove == null)
            {
                return;
            }

            for (int i = 0; i < Knobs; i++)
            {
                Transform at = stove.transform.Find("KnobPower" + (i + 1));
                PlayMakerFSM machine = at == null ? null : DoorSync.FindMachine(at, "Screw");
                FsmFloat rot = machine == null ? null : machine.FsmVariables.FindFsmFloat("Rot");
                if (rot == null)
                {
                    return;
                }

                knobs[i] = new Knob { Machine = machine, Rot = rot, Data = machine.FsmVariables.FindFsmFloat("Data"), Mesh = at.Find("mesh") };
                known[i] = Tenths(rot.Value);
            }

            found = true;
            log.Info(LogCategory.Fsm, "Stove: the four hot plates' knobs are shared (" + Describe(known) + ").");
            if (manager.Mode == NetworkMode.Host)
            {
                // How the host's stove is now is the session's from the start.
                current.Set(Key, (int[])known.Clone());
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key == Key && values.Length >= Knobs)
            {
                heard = true;
                heardValues = values;
                heardCatchUp = current.CatchingUp;
            }
        }

        private static int Tenths(float angle)
        {
            return Mathf.RoundToInt(angle * 10f);
        }

        private static string Describe(int[] values)
        {
            var parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                parts[i] = (values[i] / 10f).ToString("0.#", CultureInfo.InvariantCulture);
            }

            return string.Join(", ", parts);
        }

        private sealed class Knob
        {
            public PlayMakerFSM Machine;
            public FsmFloat Rot;
            public FsmFloat Data;
            public Transform Mesh;
        }
    }
}
