using System.Collections.Generic;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// A player knocked out in a fight (or by the friends' car) wakes up four hours later somewhere else: the game's
    /// "Systems/KnockOut :: Activate" moves the sun's block on and maybe the day, and starts the weather over. In a session
    /// the clock is the host's, and a knockout moves nobody's: the player still passes out,
    /// wakes up at one of the game's places and has the needs of the hours asleep, but the time goes on as it was
    /// (<see cref="SparedClock"/>).
    /// </summary>
    internal sealed class KnockoutSync
    {
        private const string KnockOutPath = "Systems/KnockOut";
        private const string KnockOutMachine = "Activate";

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<FsmStateAction> held = new List<FsmStateAction>();
        private TallyReplication current;
        private bool searched;

        public KnockoutSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null without a world.</summary>
        public void LateUpdate(Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                Release();
                current = tallies;
            }

            if (current == null || searched || manager.Session == null || manager.Session.State != SessionState.Connected)
            {
                return;
            }

            searched = true;
            GameObject found = GameObject.Find(KnockOutPath);
            PlayMakerFSM machine = found == null ? null : DoorSync.FindMachine(found.transform, KnockOutMachine);
            if (machine == null)
            {
                log.Warning(LogCategory.Fsm, "Knockout: no '" + KnockOutPath + " :: " + KnockOutMachine + "' in this game; a knockout moves the time as the game does.");
                return;
            }

            SparedClock.Hold(machine, held);
            log.Info(LogCategory.Fsm, "Knockout: a knockout moves nobody's clock in the session; " + held.Count + " action(s) that move the time held back.");
        }

        /// <summary>Everything back as the game had it: the session is over, or the world.</summary>
        public void Release()
        {
            SparedClock.Release(held);
            searched = false;
        }
    }
}
