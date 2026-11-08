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
    /// The garage's tools that a player works where they stand, shared like a knob (whoever worked it last): how high the
    /// motor hoist's arm is, and how high the floor jack has lifted. Where each tool stands goes with the loose objects;
    /// what it lifts moves by the physics of the machine that runs that thing (the car on the jack, the block on the
    /// hoist).
    ///
    /// What is assumed about the game (dump of 2026-10-14;
    /// the game on 2026-10-15; docs/GAME_NOTES.md, "Las herramientas del garaje"):
    /// - the hoist's handle, "ITEMS/motor hoist(itemx)/Pump/Trigger", has a machine "Usage" whose states "Up" and "Down"
    ///   add or take 0.07 from its float "Angle" on every frame the mouse is held, kept within 330 and 380, and write it as
    ///   the x angle of "motorhoist_arm" (it is 0 until first used: the arm where the level has it). The hook hangs from
    ///   the arm by a joint. "Down" plays "HouseFoley/carjack1"; "Up" plays the handle's "motor_hoist_pump_down", and
    ///   "State 1", after it, "motor_hoist_pump_up".
    /// - the jack's handle, "ITEMS/floor jack(itemx)/Trigger", has a machine "Use": a click goes to "Up", which adds 0.015
    ///   to its float "Y" (up to 0.38), moves "Lifter/target_position2" to that height and plays "HouseFoley/carjack2" and
    ///   the lever's "lever_movement"; the other button goes to "Down", which makes "Y" 0 and moves the target back to
    ///   "Lifter/target_position". The lift ("Lifter/lift", machine "Movement") goes to the target at 0.32 m/s by itself.
    ///
    /// In the other games the number is written and the arm or the target put where the game puts them, and the sounds
    /// and the handles' moves are played as the game plays them. Those states are not entered: they write a hint on the
    /// screen of whoever is playing.
    /// </summary>
    internal sealed class ToolSync
    {
        private const string HoistKey = "tool/hoist";
        private const string JackKey = "tool/jack";
        private const double BindSeconds = 5.0;
        private const double TellSeconds = 0.1;

        // A player who just worked a tool has it as they left it, whatever arrives meanwhile (what they said, coming back).
        private const double OwnSeconds = 0.6;

        // A remote arm that has not moved for this long has stopped: the handle goes back up.
        private const double StillSeconds = 0.3;
        private const string SoundGroup = "HouseFoley";

        private readonly Logger log;
        private readonly NetworkManager manager;
        private TallyReplication current;
        private Hoist hoist;
        private Jack jack;
        private double bindAt;
        private bool bound;
        private bool hoistWanted;
        private int hoistValue;
        private bool hoistCatchUp;
        private bool jackWanted;
        private int jackValue;
        private bool jackCatchUp;

        public ToolSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's root
        /// object, or null while there is no game world.
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

                hoist = null;
                jack = null;
                bound = false;
                bindAt = now + 1.0;
                hoistWanted = false;
                jackWanted = false;
            }

            if (current == null)
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
                Bind();
                if (!bound)
                {
                    return;
                }
            }

            if (hoist != null)
            {
                WorkHoist(now);
            }

            if (jack != null)
            {
                WorkJack(now);
            }
        }

        // Heard while the session reads its messages: done in this class's turn in the frame.
        private void OnStateWanted(string key, int[] values)
        {
            if (values.Length < 1)
            {
                return;
            }

            // The last word wins, and so does whether it was part of the host's answer to this game's request.
            if (key == HoistKey)
            {
                hoistWanted = true;
                hoistValue = values[0];
                hoistCatchUp = current.CatchingUp;
            }
            else if (key == JackKey)
            {
                jackWanted = true;
                jackValue = values[0];
                jackCatchUp = current.CatchingUp;
            }
        }

        private void Bind()
        {
            hoist = Hoist.Find();
            jack = Jack.Find();
            bound = hoist != null || jack != null;
            log.Info(
                LogCategory.Fsm,
                "Tools: " + (hoist != null ? "the motor hoist's arm" : "no motor hoist") + " and " + (jack != null ? "the floor jack's lift" : "no floor jack")
                + " are shared; whoever works one last moves it in every game.");

            // What this game has is the session's from the start: the host's. Its nothing too (the arm where the level has
            // it, the jack down): what the session remembers may be of an earlier world, and a guest whose tool is not at
            // nothing would never be corrected otherwise.
            if (manager.Mode != NetworkMode.Host)
            {
                return;
            }

            if (hoist != null)
            {
                hoist.Told = hoist.Angle.Value;
                current.Set(HoistKey, new[] { Mathf.RoundToInt(hoist.Told * 100f) });
            }

            if (jack != null)
            {
                current.Set(JackKey, new[] { Mathf.RoundToInt(jack.Y.Value * 1000f) });
            }

            log.Info(
                LogCategory.Fsm,
                "Tools: told as they are now, for whoever joins: "
                + (hoist != null ? "the hoist's arm at " + (hoist.Told == 0f ? "the level's place (never used)" : hoist.Told.ToString("0.00", CultureInfo.InvariantCulture)) : "no hoist")
                + ", " + (jack != null ? "the jack at " + Mathf.RoundToInt(jack.Y.Value * 1000f).ToString(CultureInfo.InvariantCulture) + " mm" : "no jack") + ".");
        }

        private void WorkHoist(double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (hoist.Usage == null || hoist.Arm == null)
            {
                hoist = null;
                return;
            }

            float angle = hoist.Angle.Value;
            if (Math.Abs(angle - hoist.Known) >= 0.005f)
            {
                // Worked here: what the others say is left alone for a moment.
                hoist.Known = angle;
                hoist.OwnUntil = now + OwnSeconds;
            }

            if (Math.Abs(hoist.Known - hoist.Told) >= 0.01f && now >= hoist.NextTell)
            {
                // Said ten times a second while it moves, and once more where it stops.
                hoist.Told = hoist.Known;
                hoist.NextTell = now + TellSeconds;
                current.Set(HoistKey, new[] { Mathf.RoundToInt(hoist.Told * 100f) });
            }

            if (now < hoist.OwnUntil)
            {
                hoistWanted = false;
                return;
            }

            if (hoistWanted && hoistValue == 0)
            {
                // Never used in the other game: its arm is where the level has it.
                hoistWanted = false;
                Unused(angle);
            }

            if (hoistWanted)
            {
                hoistWanted = false;
                float wanted = hoistValue / 100f;
                if (Math.Abs(wanted - angle) >= 0.01f && hoistCatchUp)
                {
                    // Where the host's arm is now, not a move to hear: put there in silence.
                    hoist.Angle.Value = wanted;
                    Vector3 placed = hoist.Arm.localEulerAngles;
                    placed.x = wanted;
                    hoist.Arm.localEulerAngles = placed;
                    hoist.Moving = 0;
                    log.Info(LogCategory.Fsm, "Tools: the hoist's arm put at " + wanted.ToString("0.00", CultureInfo.InvariantCulture) + ", where the host has it (a catch-up on joining).");
                }
                else if (Math.Abs(wanted - angle) >= 0.01f)
                {
                    int direction = wanted > angle ? 1 : -1;
                    hoist.Angle.Value = wanted;
                    Vector3 turned = hoist.Arm.localEulerAngles;
                    turned.x = wanted;
                    hoist.Arm.localEulerAngles = turned;
                    if (direction != hoist.Moving)
                    {
                        // Starting to move, or the other way: what the game plays when a player starts to.
                        if (direction > 0)
                        {
                            hoist.Play("motor_hoist_pump_down");
                        }
                        else
                        {
                            PlaySound(hoist.Handle, "carjack1");
                        }
                    }

                    hoist.Moving = direction;
                    hoist.StillAt = now + StillSeconds;
                }

                hoist.Known = wanted;
                hoist.Told = wanted;
            }

            if (hoist.Moving != 0 && now >= hoist.StillAt)
            {
                if (hoist.Moving > 0)
                {
                    hoist.Play("motor_hoist_pump_up");
                }

                hoist.Moving = 0;
            }
        }

        // The other game's hoist was never used (its "Angle" is 0): its arm is where the level has it. So it is put here, if
        // this one was moved and where the level has it was read when it was found, still unused; else it is left, and said.
        private void Unused(float angle)
        {
            if (angle == 0f)
            {
                hoist.Known = 0f;
                hoist.Told = 0f;
                return;
            }

            if (!hoist.LevelKnown)
            {
                log.Info(LogCategory.Fsm, "Tools: the host's hoist was never used and this one was (its arm at " + angle.ToString("0.00", CultureInfo.InvariantCulture)
                    + "); where the level has the arm is not known here, so it is left as it is.");
                return;
            }

            hoist.Angle.Value = 0f;
            hoist.Arm.localRotation = hoist.LevelArm;
            hoist.Known = 0f;
            hoist.Told = 0f;
            hoist.Moving = 0;
            log.Info(LogCategory.Fsm, "Tools: the hoist's arm put back where the level has it, as it is in the host's game" + (hoistCatchUp ? " (a catch-up on joining)." : "."));
        }

        private void WorkJack(double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (jack.Use == null || jack.Target == null)
            {
                jack = null;
                return;
            }

            float y = jack.Y.Value;
            if (Math.Abs(y - jack.Known) >= 0.0005f)
            {
                // Pumped or let down here.
                jack.Known = y;
                jack.OwnUntil = now + OwnSeconds;
                current.Set(JackKey, new[] { Mathf.RoundToInt(y * 1000f) });
            }

            if (!jackWanted)
            {
                return;
            }

            jackWanted = false;
            float wanted = jackValue / 1000f;
            if (now < jack.OwnUntil || Math.Abs(wanted - y) < 0.0005f)
            {
                return;
            }

            jack.Y.Value = wanted;
            jack.Known = wanted;
            jack.Target.localPosition = wanted > 0f || jack.Rest == null ? new Vector3(0f, wanted, 0f) : jack.Rest.localPosition;
            if (jackCatchUp)
            {
                // Where the host's jack is now, not a pump to hear: the lift goes there by itself, in silence.
                log.Info(LogCategory.Fsm, "Tools: the floor jack set to " + Mathf.RoundToInt(wanted * 1000f).ToString(CultureInfo.InvariantCulture) + " mm, where the host has it (a catch-up on joining).");
            }
            else if (wanted > y)
            {
                if (jack.Lever != null)
                {
                    jack.Lever.Play("lever_movement");
                }

                PlaySound(jack.LeverAt, "carjack2");
            }
        }

        private void PlaySound(Transform at, string variation)
        {
            if (at == null)
            {
                return;
            }

            try
            {
                MasterAudio.PlaySound3DFollowTransformAndForget(SoundGroup, at, 1f, 1f, 0f, variation);
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Fsm, "Tools: the sound '" + SoundGroup + "/" + variation + "' could not be played: " + exception.Message);
            }
        }

        private static Transform Below(Transform root, string path)
        {
            return root == null ? null : root.Find(path);
        }

        private sealed class Hoist
        {
            public PlayMakerFSM Usage;
            public FsmFloat Angle;
            public Transform Arm;
            public Transform Handle;
            public Animation HandleMoves;
            public float Known;
            public float Told;
            public double NextTell;
            public double OwnUntil;
            public int Moving;
            public double StillAt;

            /// <summary>The arm's turn where the level has it, read while the hoist was still unused ("Angle" 0).</summary>
            public Quaternion LevelArm;
            public bool LevelKnown;

            public static Hoist Find()
            {
                GameObject root = GameObject.Find("/ITEMS/motor hoist(itemx)");
                Transform trigger = root == null ? null : Below(root.transform, "Pump/Trigger");
                PlayMakerFSM usage = trigger == null ? null : DoorSync.FindMachine(trigger, "Usage");
                FsmFloat angle = usage == null ? null : usage.FsmVariables.FindFsmFloat("Angle");
                Transform arm = Below(root == null ? null : root.transform, "motorhoist_arm");
                if (angle == null || arm == null)
                {
                    return null;
                }

                Transform handle = Below(root.transform, "Pump/HandlePivot");
                return new Hoist
                {
                    Usage = usage,
                    Angle = angle,
                    Arm = arm,
                    Handle = handle ?? trigger,
                    HandleMoves = handle == null ? null : handle.GetComponent<Animation>(),
                    Known = angle.Value,
                    Told = angle.Value,
                    LevelArm = arm.localRotation,
                    LevelKnown = angle.Value == 0f,
                };
            }

            public void Play(string clip)
            {
                // Unity's null check is also true for a destroyed object.
                if (HandleMoves != null && HandleMoves[clip] != null)
                {
                    HandleMoves.Play(clip);
                }
            }
        }

        private sealed class Jack
        {
            public PlayMakerFSM Use;
            public FsmFloat Y;
            public Transform Target;
            public Transform Rest;
            public Transform LeverAt;
            public Animation Lever;
            public float Known;
            public double OwnUntil;

            public static Jack Find()
            {
                GameObject root = GameObject.Find("/ITEMS/floor jack(itemx)");
                Transform trigger = root == null ? null : Below(root.transform, "Trigger");
                PlayMakerFSM use = trigger == null ? null : DoorSync.FindMachine(trigger, "Use");
                FsmFloat y = use == null ? null : use.FsmVariables.FindFsmFloat("Y");
                Transform target = Below(root == null ? null : root.transform, "Lifter/target_position2");
                if (y == null || target == null)
                {
                    return null;
                }

                Transform lever = Below(root.transform, "Pivot/Move/Pivot");
                return new Jack
                {
                    Use = use,
                    Y = y,
                    Target = target,
                    Rest = Below(root.transform, "Lifter/target_position"),
                    LeverAt = lever ?? trigger,
                    Lever = lever == null ? null : lever.GetComponent<Animation>(),
                    Known = y.Value,
                };
            }
        }
    }
}
