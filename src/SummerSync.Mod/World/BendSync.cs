using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// How bent each corner of the Satsuma is, the same in every game.
    ///
    /// What is assumed about the game (the game with the bridge on 2026-10-16, docs/GAME_NOTES.md "Las abolladuras"):
    /// <c>CarSimulation/Symptoms/SuspDamage/Calculations</c> has a machine per corner ("FL", "FR", "RL", "RR"). Hit hard
    /// enough, its "State 4" draws two angles between <c>BendMin</c> and <c>BendMax</c> into <c>Y</c> and <c>Z</c> and turns
    /// the corner (<c>SATSUMA/FL</c>…) to them in its own coordinates; "State 5" does the same between -2 and 2. Those
    /// machines decide only in the game that simulates the car (<see cref="CarValueSync"/> pauses them in the others), so
    /// that game tells each corner's angles (the tally "bend/(corner)", in thousandths of a degree) and the others turn their
    /// corners the same and write the two angles into the machine.
    /// </summary>
    internal sealed class BendSync
    {
        private const string TallyPrefix = "bend/";
        private const string MachinesPath = "CarSimulation/Symptoms/SuspDamage/Calculations";
        private const int Least = 10;

        private static readonly string[] Corners = { "FL", "FR", "RL", "RR" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly Corner[] corners = new Corner[4];
        private TallyReplication current;
        private Transform car;

        private sealed class Corner
        {
            public Transform Pivot;
            public PlayMakerFSM Machine;
            public bool Told;
            public int ToldY;
            public int ToldZ;
            public bool Heard;
            public int HeardY;
            public int HeardZ;
            public bool Applied;
        }

        public BendSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
            for (int i = 0; i < corners.Length; i++)
            {
                corners[i] = new Corner();
            }
        }

        /// <summary>Once per frame with the car's root (null without one) and whether this game simulates it.</summary>
        public void Update(Transform carRoot, bool runsCar)
        {
            TallyReplication tallies = carRoot == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                current = tallies;
                for (int i = 0; i < corners.Length; i++)
                {
                    corners[i].Told = false;
                    corners[i].Heard = false;
                }

                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }
            }

            if (carRoot != car)
            {
                car = carRoot;
                Find();
            }

            NetSession session = manager.Session;
            if (current == null || car == null || session == null || session.State != SessionState.Connected)
            {
                return;
            }

            for (int i = 0; i < corners.Length; i++)
            {
                Corner corner = corners[i];

                // Unity's null check is also true for a destroyed object.
                if (corner.Pivot == null)
                {
                    continue;
                }

                if (runsCar)
                {
                    Tell(i, corner);
                }
                else if (corner.Heard && !corner.Applied)
                {
                    Turn(i, corner);
                }
            }
        }

        /// <summary>Lets go of the session; the corners stay as they are.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            current = null;
        }

        private void Find()
        {
            Transform machines = car == null ? null : car.Find(MachinesPath);
            for (int i = 0; i < corners.Length; i++)
            {
                corners[i].Pivot = car == null ? null : car.Find(Corners[i]);
                corners[i].Machine = machines == null ? null : DoorSync.FindMachine(machines, Corners[i]);
                corners[i].Told = false;
                corners[i].Applied = false;
            }

            if (car != null && (corners[0].Pivot == null || corners[0].Machine == null))
            {
                log.Warning(LogCategory.Vehicle, "Car corners: '" + car.name + "' has no corner '" + Corners[0] + "' or no machine '" + MachinesPath + " :: " + Corners[0] + "'; a bent corner is each game's.");
            }
        }

        // This game simulates the car: a corner that turned is told.
        private void Tell(int index, Corner corner)
        {
            Vector3 angles = corner.Pivot.localEulerAngles;
            int y = Mathf.RoundToInt(Signed(angles.y) * 1000f);
            int z = Mathf.RoundToInt(Signed(angles.z) * 1000f);
            if (corner.Told && Math.Abs(y - corner.ToldY) < Least && Math.Abs(z - corner.ToldZ) < Least)
            {
                return;
            }

            bool first = !corner.Told;
            corner.Told = true;
            corner.ToldY = y;
            corner.ToldZ = z;
            corner.Applied = true;
            current.Set(TallyPrefix + Corners[index], new[] { y, z });
            if (!first || y != 0 || z != 0)
            {
                log.Info(LogCategory.Vehicle, "Car corners: '" + Corners[index] + "' is at " + Degrees(y) + ", " + Degrees(z) + " degrees here; the others are told.");
            }
        }

        // Another game simulates the car: the corner turned as it has it, as the game's own machine turns it.
        private void Turn(int index, Corner corner)
        {
            corner.Applied = true;
            float y = corner.HeardY / 1000f;
            float z = corner.HeardZ / 1000f;
            corner.Pivot.localRotation = Quaternion.Euler(0f, y, z);
            if (corner.Machine != null)
            {
                FsmFloat fy = corner.Machine.FsmVariables.FindFsmFloat("Y");
                FsmFloat fz = corner.Machine.FsmVariables.FindFsmFloat("Z");
                if (fy != null)
                {
                    fy.Value = y;
                }

                if (fz != null)
                {
                    fz.Value = z;
                }
            }

            // Should this game take the car over, it has nothing new to tell.
            corner.Told = true;
            corner.ToldY = corner.HeardY;
            corner.ToldZ = corner.HeardZ;
            if (corner.HeardY != 0 || corner.HeardZ != 0)
            {
                log.Info(LogCategory.Vehicle, "Car corners: '" + Corners[index] + "' turned to " + Degrees(corner.HeardY) + ", " + Degrees(corner.HeardZ) + " degrees, as the game that simulates the car has it.");
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (!key.StartsWith(TallyPrefix, StringComparison.Ordinal) || values.Length < 2)
            {
                return;
            }

            int index = Array.IndexOf(Corners, key.Substring(TallyPrefix.Length));
            if (index < 0)
            {
                return;
            }

            Corner corner = corners[index];
            corner.Heard = true;
            corner.HeardY = values[0];
            corner.HeardZ = values[1];
            corner.Applied = false;
        }

        private static float Signed(float degrees)
        {
            return degrees > 180f ? degrees - 360f : degrees;
        }

        private static string Degrees(int thousandths)
        {
            return (thousandths / 1000f).ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
