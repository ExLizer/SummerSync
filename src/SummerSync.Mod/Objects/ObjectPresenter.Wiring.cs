using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using UnityEngine;

namespace SummerSync.Unity.Objects
{
    /// <summary>
    /// The wiring mess, the Satsuma's wiring tool, as a loose object.
    ///
    /// What is assumed about the game (decoded level, and two copies of the game on 2026-11-11; docs/GAME_NOTES.md, "Los
    /// cables del Satsuma y la herramienta"): the item's machine "Use" puts the item itself into the global "WiringTool"
    /// each time it starts, and nothing else writes that global. Every end of a wire ("SATSUMA(557kg, 248)/Wiring/Triggers/
    /// &lt;wire&gt;/&lt;end&gt; :: Assemble") measures, every frame, how far the global's object is: within 0.1 m it shows its
    /// name (the globals "GUIassemble" and "GUIinteraction"), and the button "Use" takes that end, wherever the player is; the
    /// second end taken makes the wire.
    ///
    /// Shared, the item is at the same place in every game: an end showed its name in every game and any player's "Use" made
    /// the wire from where they stood (seen with two copies on 2026-11-11: a guest in the kitchen of the house made the
    /// radio's wire). So the item is the wiring tool only in the game that runs its physics: the one whose player holds it,
    /// or, while it lies, the one that simulates it. Where another machine moves it, the global points to an object of the
    /// mod far below the world, out of every end's reach, and back to the item when this machine takes it over or the
    /// session ends. The game writes the global again whenever the item is switched on: it is looked at on every frame.
    /// </summary>
    internal sealed partial class ObjectPresenter
    {
        private const string WiringMessName = "wiring mess(itemx)";
        private const string WiringToolGlobal = "WiringTool";
        private const string WiringStandInName = "SummerSync wiring tool elsewhere";

        // Far below the world: farther from every end of a wire than the 0.1 m within which an end answers.
        private static readonly Vector3 WiringStandInPlace = new Vector3(0f, -5000f, 0f);

        private Tracked wiringMess;
        private FsmGameObject wiringTool;
        private GameObject wiringStandIn;
        private bool wiringToolMissingSaid;

        /// <summary>
        /// Where the copy here of the wiring mess is, while another machine moves it and the global points to the stand-in;
        /// false otherwise. What the game puts at the wiring tool for a wire that another player made (the battery's spark,
        /// <see cref="World.PartSync"/>) goes there, where that player's tool is, instead of far below the world.
        /// </summary>
        public bool WiringToolElsewhere(out Vector3 at)
        {
            at = Vector3.zero;
            if (!WiringToolAway())
            {
                return false;
            }

            at = wiringMess.Transform.position;
            return true;
        }

        // When a thing is found: whether it is the wiring mess.
        private void NoteWiringMess(Tracked item)
        {
            if (wiringMess == null && item.Shared.Name == WiringMessName)
            {
                wiringMess = item;
            }
        }

        // Once per frame, after every thing has been moved: the global as this machine's part in the wiring mess wants it.
        private void KeepWiringTool()
        {
            Tracked item = wiringMess;

            // Unity's null check is also true for a destroyed object.
            if (item == null || item.Transform == null)
            {
                return;
            }

            if (wiringTool == null)
            {
                wiringTool = FsmVariables.GlobalVariables.FindFsmGameObject(WiringToolGlobal);
                if (wiringTool == null)
                {
                    if (!wiringToolMissingSaid)
                    {
                        wiringToolMissingSaid = true;
                        log.Warning(
                            LogCategory.Authority,
                            "The game has no global '" + WiringToolGlobal + "': the ends of the Satsuma's wires answer to the wiring mess in every game of the session.");
                    }

                    wiringMess = null;
                    return;
                }
            }

            GameObject thing = item.Transform.gameObject;
            GameObject value = wiringTool.Value;
            if (item.Shared.Role == BodyRole.Follow)
            {
                // The game's own value: the item, put there when its "Use" started (the world loaded, or the item was
                // switched on again). Anything else is left alone.
                if (value == thing)
                {
                    wiringTool.Value = StandIn();
                    log.Info(
                        LogCategory.Authority,
                        "'" + item.Shared.Name + "' is moved by player " + item.Shared.Owner + "'s machine" + (item.Shared.Held ? ", in hand" : string.Empty)
                        + ": it is not the wiring tool in this game (the ends of the Satsuma's wires answer to it in that game only).");
                }
            }
            else if (value != null && value == wiringStandIn)
            {
                wiringTool.Value = thing;
                log.Info(
                    LogCategory.Authority,
                    "'" + item.Shared.Name + "' is " + (item.Shared.Role == BodyRole.Simulate ? "simulated here" : "this game's own again")
                    + ": it is the wiring tool in this game again.");
            }
        }

        // Whether the global points to the stand-in now, with the item still here.
        private bool WiringToolAway()
        {
            // Unity's null check is also true for a destroyed object.
            return wiringMess != null && wiringMess.Transform != null && wiringTool != null && wiringStandIn != null && wiringTool.Value == wiringStandIn;
        }

        private GameObject StandIn()
        {
            // Unity's null check is also true for a destroyed object: the world was loaded again.
            if (wiringStandIn == null)
            {
                wiringStandIn = new GameObject(WiringStandInName);
                wiringStandIn.transform.position = WiringStandInPlace;
            }

            return wiringStandIn;
        }

        // The wiring mess is no longer shared (the session ended, another world, or the thing is gone): the global goes back
        // to the item, as the game had it.
        private void ReleaseWiringTool()
        {
            if (WiringToolAway())
            {
                wiringTool.Value = wiringMess.Transform.gameObject;
                log.Info(LogCategory.Authority, "'" + wiringMess.Shared.Name + "' is the wiring tool in this game again, as without a session.");
            }

            wiringMess = null;
            wiringTool = null;
        }
    }
}
