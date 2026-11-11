using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The shock of a wire made with the battery on, in a game where the mod makes the wire.
    ///
    /// What is assumed about the game (decoded level; docs/GAME_NOTES.md, "Los cables del Satsuma y la herramienta"): every
    /// wire made ends in a broadcast of "RESETWIRING", which takes "SATSUMA(557kg, 248)/Wiring :: Shock" to "Battery charge?".
    /// With the battery in, its plus terminal bolted, more than 90 of charge and the battery harness wired, it goes on to
    /// "Spark" (the sparks put at the wiring tool for half a second) and then "Random", which kills the local player one time
    /// in 200 ("DIE" 0.005, "OFF" 0.995; "Death": "Systems/Death", electrocuted).
    ///
    /// A wire the mod makes here was made by another game's player, who took that chance in their game. Here the spark
    /// happens as it does there, where the copy of their wiring tool is, and "DIE" weighs nothing until the machine has thrown
    /// its dice.
    /// </summary>
    internal sealed partial class PartSync
    {
        private const string WiringChild = "Wiring";
        private const string ShockMachine = "Shock";
        private const string ShockDiceState = "Random";
        private const string ShockDeathEvent = "DIE";
        private const string ShockSparkState = "Spark";
        private const string ShockSparksVariable = "Spark";

        // The spark waits half a second before the dice: spared this long after the wire, and while the machine is still on
        // its way to them.
        private const double ShockSpareSeconds = 2.0;
        private static readonly string[] ShockOnItsWay = { "Spark", "Spark 2", "Random" };

        private PlayMakerFSM shock;
        private bool shockSearched;
        private SendRandomEvent shockDice;
        private int shockDeathSlot = -1;
        private FsmFloat shockDeathWeight;
        private double shockSpareUntil;
        private bool shockMissingSaid;

        // Before the mod makes a wire here: the shock that follows, if any, does not kill this game's player.
        private void SpareShock()
        {
            if (!FindShockDice())
            {
                return;
            }

            shockSpareUntil = frameTime + ShockSpareSeconds;
            if (shockDeathWeight == null)
            {
                shockDeathWeight = shockDice.weights[shockDeathSlot];
                shockDice.weights[shockDeathSlot] = new FsmFloat { Value = 0f };
            }
        }

        // Right after the mod made a wire here: whether the game sparked. The machine has just put its sparks at the wiring
        // tool ("Spark": its events are handled in the call that sends them). Where another player's machine moves the wiring
        // mess, the tool here is the mod's stand-in far below the world (ObjectPresenter.Wiring): the sparks go to the copy
        // here of the wiring mess instead, where that player made the wire.
        private bool SparkAtTool()
        {
            // Unity's null check is also true for a destroyed component.
            if (shock == null || shock.ActiveStateName != ShockSparkState)
            {
                return false;
            }

            Vector3 at;
            FsmGameObject sparks = shock.FsmVariables.FindFsmGameObject(ShockSparksVariable);
            if (objects.WiringToolElsewhere(out at) && sparks != null && sparks.Value != null)
            {
                sparks.Value.transform.position = at;
            }

            return true;
        }

        // Once per frame: the dice as the game has them again, once nothing the mod made here can still throw them.
        private void WorkShock()
        {
            if (shockDeathWeight == null || frameTime < shockSpareUntil)
            {
                return;
            }

            // Unity's null check is also true for a destroyed component.
            if (shock != null && System.Array.IndexOf(ShockOnItsWay, shock.ActiveStateName) >= 0)
            {
                return;
            }

            ReleaseShock();
        }

        private void ReleaseShock()
        {
            if (shockDeathWeight != null && shockDice != null)
            {
                shockDice.weights[shockDeathSlot] = shockDeathWeight;
            }

            shockDeathWeight = null;
            shock = null;
            shockDice = null;
            shockDeathSlot = -1;
            shockSearched = false;
        }

        private bool FindShockDice()
        {
            // Unity's null check is also true for a destroyed component.
            if (shockDice != null && shock != null)
            {
                return true;
            }

            if (shockSearched || car == null)
            {
                return false;
            }

            shockSearched = true;
            Transform wiring = car.Find(WiringChild);
            shock = wiring == null ? null : DoorSync.FindMachine(wiring, ShockMachine);
            FsmState[] states = shock == null ? null : shock.FsmStates;
            for (int s = 0; states != null && s < states.Length && shockDice == null; s++)
            {
                if (states[s].Name != ShockDiceState)
                {
                    continue;
                }

                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length && shockDice == null; a++)
                {
                    var dice = actions[a] as SendRandomEvent;
                    for (int e = 0; dice != null && dice.events != null && dice.weights != null && e < dice.events.Length && e < dice.weights.Length; e++)
                    {
                        if (dice.events[e] != null && dice.events[e].Name == ShockDeathEvent)
                        {
                            shockDice = dice;
                            shockDeathSlot = e;
                            break;
                        }
                    }
                }
            }

            if (shockDice == null && !shockMissingSaid)
            {
                shockMissingSaid = true;
                log.Warning(
                    Category,
                    "Car: no '" + ShockDeathEvent + "' in the dice of '" + WiringChild + " :: " + ShockMachine + "' (state '" + ShockDiceState
                    + "') in this game: a wire made here for another player can still kill this game's player with the battery on.");
            }

            return shockDice != null;
        }
    }
}
