using System.Globalization;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SummerSync.Core.Logging;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// A test aid: firewood in front of the local player, as if just chopped.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md): the state machine "Use" of
    /// "YARD/MachineHall/Logging/Logwall" makes a log on the chopping block with "CreateObject" from the prefab
    /// "log": two halves held by a "FixedJoint". The state machine "Check joint" of the inner half waits for the
    /// joint to be gone (the axe breaks it) and then names both halves "firewood(Clone)", tags them "PART" and lets
    /// them go. Here the log is made from that prefab and its joint is taken away: the game does the rest. Like an
    /// axe, it lowers the local player's stress a little and counts as chopped.
    ///
    /// Also, for a client joining a world where the host chopped wood since the save: pieces made from the same prefab and
    /// split the way <see cref="FireSync"/> splits a log chopped in another game, for nobody's stress.
    /// </summary>
    internal static class Firewood
    {
        /// <summary>What the game calls a piece of firewood.</summary>
        public const string PieceName = "firewood(Clone)";

        private const string LogwallPath = "YARD/MachineHall/Logging/Logwall";
        private const string LogwallMachine = "Use";
        private const int Default = 4;
        private const int Most = 20;

        /// <summary>Makes <paramref name="countText"/> pieces of firewood (two per log). Returns what happened, for the console.</summary>
        public static string Spawn(string countText, Transform player, Logger log)
        {
            if (player == null)
            {
                return "There is no game world yet.";
            }

            int count = Default;
            if (!string.IsNullOrEmpty(countText) && (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1 || count > Most))
            {
                return "A number of pieces from 1 to " + Most + ", please (two come from each log).";
            }

            GameObject prefab = LogPrefab();
            if (prefab == null)
            {
                return "The woodpile was not found as this build expects it ('" + LogwallPath + "', state machine '" + LogwallMachine + "' making a log).";
            }

            int logs = (count + 1) / 2;
            Vector3 forward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            for (int i = 0; i < logs; i++)
            {
                Vector3 at = player.position + (forward * 1.2f) + (right * ((i - ((logs - 1) * 0.5f)) * 0.45f)) + (Vector3.up * 0.8f);
                var made = (GameObject)Object.Instantiate(prefab, at, Quaternion.LookRotation(right, Vector3.up));

                // What the axe does: the halves part, and the game makes firewood of them.
                FixedJoint[] joints = made.GetComponentsInChildren<FixedJoint>(true);
                for (int j = 0; j < joints.Length; j++)
                {
                    Object.Destroy(joints[j]);
                }
            }

            log.Info(LogCategory.Player, "Test aid: " + logs + " log(s) made from '" + prefab.name + "' in front of the local player and split, " + (logs * 2) + " piece(s) of firewood.");
            return logs * 2 + " pieces of firewood in front of you.";
        }

        /// <summary>
        /// A client joining: one or two pieces of firewood (one log split) at that place. Returns how many were made: none
        /// if the woodpile is not as this build expects.
        /// </summary>
        public static int MakeForJoin(Vector3 at, Quaternion rotation, int pieces)
        {
            GameObject prefab = LogPrefab();
            if (prefab == null || pieces < 1)
            {
                return 0;
            }

            var made = (GameObject)Object.Instantiate(prefab, at + (Vector3.up * 0.3f), rotation);
            Transform second = null;
            for (int i = 0; i < made.transform.childCount && second == null; i++)
            {
                if (made.transform.GetChild(i).GetComponent<Rigidbody>() != null)
                {
                    second = made.transform.GetChild(i);
                }
            }

            FireSync.Split(made);
            if (pieces == 1 && second != null)
            {
                Object.Destroy(second.gameObject);
                return 1;
            }

            return second == null ? 1 : 2;
        }

        private static GameObject LogPrefab()
        {
            GameObject wall = GameObject.Find(LogwallPath);
            PlayMakerFSM fsm = wall == null ? null : DoorSync.FindMachine(wall.transform, LogwallMachine);
            FsmState[] states = fsm == null || fsm.Fsm == null ? null : fsm.Fsm.States;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    var create = actions[a] as CreateObject;
                    if (create != null && create.gameObject != null && create.gameObject.Value != null)
                    {
                        return create.gameObject.Value;
                    }
                }
            }

            return null;
        }
    }
}
