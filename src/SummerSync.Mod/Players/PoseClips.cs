using System;
using System.Collections.Generic;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// The bones of a person that the mod's own animations move, and how long they are, measured on the copy.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "La gente del juego"): the thighs hang from "skeleton",
    /// beside "pelvis", which carries the back, the arms and the head; a bone points along its own -X, towards its
    /// child. The body looks along +Z of the copy's root and stands on its origin.
    /// </summary>
    internal sealed class PoseRig
    {
        private const string Skeleton = "skeleton/";
        private const string Spine = "skeleton/pelvis/spine_middle/spine_upper/";
        private static readonly string[] Sides = { "left", "right" };

        private PoseRig()
        {
        }

        public Transform Body { get; private set; }

        public Transform Pelvis { get; private set; }

        public Transform SpineMiddle { get; private set; }

        public Transform SpineUpper { get; private set; }

        public Transform[] Thigh { get; private set; }

        public Transform[] Knee { get; private set; }

        public Transform[] Ankle { get; private set; }

        public Transform[] Shoulder { get; private set; }

        public Transform[] Arm { get; private set; }

        public Transform[] Hand { get; private set; }

        /// <summary>Every transform a clip of the mod's sets, the body first.</summary>
        public Transform[] Keyed { get; private set; }

        /// <summary>Where the hips are, standing: x is each side's.</summary>
        public Vector3[] Hip { get; private set; }

        public float HipHeight { get; private set; }

        /// <summary>How high the ankles are above the soles.</summary>
        public float AnkleHeight { get; private set; }

        /// <summary>The ankles' turn, standing, in the root's frame: the feet flat and ahead.</summary>
        public Quaternion[] AnkleRest { get; private set; }

        /// <summary>
        /// The bones of the copy under <paramref name="root"/>, measured with the root at the origin facing +Z and
        /// every bone where it was made. Null for a person whose bones are not named as the game's men's are.
        /// </summary>
        public static PoseRig Measure(Transform root, Transform body, out string missing)
        {
            var rig = new PoseRig
            {
                Body = body,
                Pelvis = body.Find("skeleton/pelvis"),
                SpineMiddle = body.Find("skeleton/pelvis/spine_middle"),
                SpineUpper = body.Find("skeleton/pelvis/spine_middle/spine_upper"),
                Thigh = new Transform[2],
                Knee = new Transform[2],
                Ankle = new Transform[2],
                Shoulder = new Transform[2],
                Arm = new Transform[2],
                Hand = new Transform[2],
                Hip = new Vector3[2],
                AnkleRest = new Quaternion[2],
            };

            var collars = new Transform[2];
            missing = rig.Pelvis == null ? "pelvis" : rig.SpineMiddle == null ? "spine_middle" : rig.SpineUpper == null ? "spine_upper" : null;
            for (int s = 0; s < 2 && missing == null; s++)
            {
                string side = Sides[s];
                rig.Thigh[s] = body.Find(Skeleton + "thig_" + side);
                rig.Knee[s] = rig.Thigh[s] == null ? null : rig.Thigh[s].Find("knee_" + side);
                rig.Ankle[s] = rig.Knee[s] == null ? null : rig.Knee[s].Find("ankle_" + side);
                collars[s] = body.Find(Spine + "collar_" + side);
                rig.Shoulder[s] = collars[s] == null ? null : collars[s].Find("shoulder_" + side);
                rig.Arm[s] = rig.Shoulder[s] == null ? null : rig.Shoulder[s].Find("arm_" + side);
                rig.Hand[s] = rig.Arm[s] == null ? null : rig.Arm[s].Find("hand_" + side);
                if (rig.Ankle[s] == null)
                {
                    missing = "the " + side + " leg";
                }
                else if (rig.Hand[s] == null)
                {
                    missing = "the " + side + " arm";
                }
            }

            if (missing != null)
            {
                return null;
            }

            rig.Keyed = new[]
            {
                body, rig.Pelvis, rig.SpineMiddle, rig.SpineUpper,
                rig.Thigh[0], rig.Knee[0], rig.Ankle[0], rig.Thigh[1], rig.Knee[1], rig.Ankle[1],
                collars[0], rig.Shoulder[0], rig.Arm[0], rig.Hand[0], collars[1], rig.Shoulder[1], rig.Arm[1], rig.Hand[1],
            };

            for (int s = 0; s < 2; s++)
            {
                rig.Hip[s] = root.InverseTransformPoint(rig.Thigh[s].position);
                rig.AnkleRest[s] = Quaternion.Inverse(root.rotation) * rig.Ankle[s].rotation;
            }

            rig.HipHeight = (rig.Hip[0].y + rig.Hip[1].y) * 0.5f;

            // The foot that stood on the ground when the person was copied (the other may be mid-step).
            rig.AnkleHeight = Mathf.Clamp(
                Mathf.Min(root.InverseTransformPoint(rig.Ankle[0].position).y, root.InverseTransformPoint(rig.Ankle[1].position).y), 0.01f, 0.12f);
            return rig;
        }
    }

    /// <summary>
    /// The mod's own animations for what the game's people never do: crouch, walk crouched, run and lie down. Made
    /// from <see cref="PoseSettings"/> on the copy's own bones, as legacy clips on the Animation of the copy's root:
    /// the legs by reaching for where the feet go (two bones, a knee that points ahead), the rest by turning bones
    /// about the body's sideways axis. Nothing of the game's is copied: only its bones are moved.
    /// </summary>
    internal static class PoseClips
    {
        public const string Crouch = "ss_crouch";
        public const string CrouchWalk = "ss_crouch_walk";
        public const string Run = "ss_run";
        public const string Prone = "ss_prone";

        private const int CycleKeys = 8;

        /// <summary>Makes every clip. <paramref name="rest"/> puts every bone back where it was made.</summary>
        public static Dictionary<string, AnimationClip> Make(Transform root, PoseRig rig, PoseSettings settings, Action rest)
        {
            var clips = new Dictionary<string, AnimationClip>();
            Vector3 position = root.position;
            Quaternion rotation = root.rotation;
            root.position = Vector3.zero;
            root.rotation = Quaternion.identity;
            try
            {
                clips[Crouch] = Clip(Crouch, root, rig, rest, 1, 1f, delegate(float phase) { PoseCrouch(rig, settings, 0f, 0f, 0f, 0f); });
                float stride = settings[PoseSettings.CrouchWalkStride];
                float lift = settings[PoseSettings.CrouchWalkLift];
                float crouchCycle = (2f * stride) / settings[PoseSettings.CrouchWalkSpeed];
                clips[CrouchWalk] = Clip(CrouchWalk, root, rig, rest, CycleKeys, crouchCycle, delegate(float phase) { PoseCrouch(rig, settings, phase, stride, lift, 1f); });
                float runStride = 2f * rig.HipHeight * Mathf.Sin(settings[PoseSettings.RunStride] * Mathf.Deg2Rad);
                float runCycle = Mathf.Max(0.3f, (2f * runStride * 1.1f) / settings[PoseSettings.RunSpeed]);
                clips[Run] = Clip(Run, root, rig, rest, CycleKeys, runCycle, delegate(float phase) { PoseRun(rig, settings, phase); });
                clips[Prone] = Clip(Prone, root, rig, rest, 1, 1f, delegate(float phase) { PoseProne(rig, settings); });
            }
            finally
            {
                rest();
                root.position = position;
                root.rotation = rotation;
            }

            return clips;
        }

        // ---- The poses: the root at the origin facing +Z, every bone where it was made ----

        private static readonly Vector3 Right = Vector3.right;

        // Crouched on both feet; walking crouched, each foot in turn forward and up.
        private static void PoseCrouch(PoseRig rig, PoseSettings settings, float phase, float stride, float lift, float walking)
        {
            float drop = Mathf.Max(0f, rig.HipHeight - settings[PoseSettings.CrouchHips]);
            rig.Body.position += Vector3.down * drop;
            Lean(rig, settings[PoseSettings.CrouchLean]);
            float feet = settings[PoseSettings.CrouchFeet];
            float width = settings[PoseSettings.CrouchWidth];
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                float cycle = 2f * Mathf.PI * (phase + (s * 0.5f));
                Vector3 hip = rig.Hip[s];
                var foot = new Vector3(
                    hip.x + (side * width),
                    rig.AnkleHeight + (walking * lift * Mathf.Max(0f, Mathf.Cos(cycle))),
                    hip.z + feet + (walking * 0.5f * stride * Mathf.Sin(cycle)));
                Reach(rig, s, foot, new Vector3(side * 0.35f, 0f, 1f));
                SwingArm(rig, s, settings[PoseSettings.CrouchArms], 30f);
            }
        }

        private static void PoseRun(PoseRig rig, PoseSettings settings, float phase)
        {
            float bob = settings[PoseSettings.RunBob];
            rig.Body.position += Vector3.down * (bob * (0.5f + (0.5f * Mathf.Cos(4f * Mathf.PI * phase))));
            Lean(rig, settings[PoseSettings.RunLean]);
            float stride = settings[PoseSettings.RunStride];
            float knee = settings[PoseSettings.RunKnee];
            for (int s = 0; s < 2; s++)
            {
                float cycle = 2f * Mathf.PI * (phase + (s * 0.5f));

                // Forward of the hip is a positive swing; the knee bends most while the leg comes through.
                float swing = stride * Mathf.Sin(cycle);
                float bend = 12f + ((knee - 12f) * Mathf.Max(0f, Mathf.Cos(cycle)));
                Aim(rig.Thigh[s], rig.Knee[s], Ahead(Vector3.down, swing));
                Aim(rig.Knee[s], rig.Ankle[s], Ahead(Vector3.down, swing - bend));
                rig.Ankle[s].rotation = Quaternion.AngleAxis(bend - swing, Right) * rig.AnkleRest[s];

                // Each arm with the other side's leg.
                SwingArm(rig, s, -settings[PoseSettings.RunArms] * Mathf.Sin(cycle), settings[PoseSettings.RunElbow]);
            }
        }

        // Face down, propped on the forearms: the whole body turned forward about the hips, the chest up.
        private static void PoseProne(PoseRig rig, PoseSettings settings)
        {
            Vector3 hips = (rig.Hip[0] + rig.Hip[1]) * 0.5f;
            rig.Body.RotateAround(hips, Right, 90f);
            rig.Body.position += Vector3.up * (settings[PoseSettings.ProneHeight] - hips.y);
            float lift = settings[PoseSettings.ProneLift];
            rig.SpineMiddle.rotation = Quaternion.AngleAxis(-lift * 0.5f, Right) * rig.SpineMiddle.rotation;
            rig.SpineUpper.rotation = Quaternion.AngleAxis(-lift * 0.5f, Right) * rig.SpineUpper.rotation;
            float spread = settings[PoseSettings.ProneSpread];
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                Vector3 back = new Vector3(side * spread, -0.08f, -1f).normalized;
                Aim(rig.Thigh[s], rig.Knee[s], back);
                Aim(rig.Knee[s], rig.Ankle[s], back);

                // Upper arms down to the floor, forearms ahead on it.
                Aim(rig.Shoulder[s], rig.Arm[s], new Vector3(side * 0.15f, -1f, 0.25f).normalized);
                Aim(rig.Arm[s], rig.Hand[s], Ahead(Vector3.down, settings[PoseSettings.ProneElbows]));
            }
        }

        // ---- Moving bones ----

        // The back, the arms and the head lean forward with the pelvis; the legs hang from the skeleton and do not.
        private static void Lean(PoseRig rig, float degrees)
        {
            rig.Pelvis.rotation = Quaternion.AngleAxis(degrees, Right) * rig.Pelvis.rotation;
        }

        // A direction turned forward by that many degrees about the body's sideways axis.
        private static Vector3 Ahead(Vector3 direction, float degrees)
        {
            return Quaternion.AngleAxis(-degrees, Right) * direction;
        }

        // Turns a bone so that its child is the other way from it.
        private static void Aim(Transform bone, Transform child, Vector3 direction)
        {
            Vector3 now = child.position - bone.position;
            if (now.sqrMagnitude > 1e-8f && direction.sqrMagnitude > 1e-8f)
            {
                bone.rotation = Quaternion.FromToRotation(now, direction) * bone.rotation;
            }
        }

        // The upper arm forward by that much from hanging, a little out, and the forearm bent forward from it.
        private static void SwingArm(PoseRig rig, int s, float forward, float elbow)
        {
            float side = s == 0 ? -1f : 1f;
            Vector3 hanging = new Vector3(side * 0.18f, -1f, 0f).normalized;
            Vector3 upper = Ahead(hanging, forward);
            Aim(rig.Shoulder[s], rig.Arm[s], upper);
            Aim(rig.Arm[s], rig.Hand[s], Ahead(upper, elbow));
        }

        // The leg from the hip to a foot at that place, the knee towards the pole, the foot flat as standing.
        private static void Reach(PoseRig rig, int s, Vector3 foot, Vector3 pole)
        {
            Transform thigh = rig.Thigh[s];
            Transform knee = rig.Knee[s];
            Transform ankle = rig.Ankle[s];
            Vector3 hip = thigh.position;
            float upper = (knee.position - hip).magnitude;
            float lower = (ankle.position - knee.position).magnitude;
            Vector3 toFoot = foot - hip;
            float reach = Mathf.Clamp(toFoot.magnitude, Mathf.Abs(upper - lower) + 0.001f, upper + lower - 0.001f);
            Vector3 along = toFoot.normalized;
            Vector3 bend = pole - (along * Vector3.Dot(pole, along));
            bend = bend.sqrMagnitude < 1e-8f ? Vector3.forward : bend.normalized;
            float a = ((upper * upper) - (lower * lower) + (reach * reach)) / (2f * reach);
            float h = Mathf.Sqrt(Mathf.Max(0f, (upper * upper) - (a * a)));
            Vector3 kneeAt = hip + (along * a) + (bend * h);
            Aim(thigh, knee, kneeAt - hip);
            Aim(knee, ankle, (hip + (along * reach)) - kneeAt);
            ankle.rotation = rig.AnkleRest[s];
        }

        // ---- Clips ----

        private static AnimationClip Clip(string name, Transform root, PoseRig rig, Action rest, int keys, float seconds, Action<float> pose)
        {
            Transform[] keyed = rig.Keyed;
            var rotations = new Quaternion[keys, keyed.Length];
            var bodyAt = new Vector3[keys];
            for (int k = 0; k < keys; k++)
            {
                rest();
                pose((float)k / keys);
                for (int b = 0; b < keyed.Length; b++)
                {
                    Quaternion q = keyed[b].localRotation;

                    // The same turn as the key before, the short way round: the curves are blended per component.
                    if (k > 0 && Quaternion.Dot(q, rotations[k - 1, b]) < 0f)
                    {
                        q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    }

                    rotations[k, b] = q;
                }

                bodyAt[k] = rig.Body.localPosition;
            }

            var clip = new AnimationClip { name = name, legacy = true, wrapMode = keys > 1 ? WrapMode.Loop : WrapMode.ClampForever };
            float length = Mathf.Max(0.1f, seconds);
            for (int b = 0; b < keyed.Length; b++)
            {
                string path = PathFrom(root, keyed[b]);
                for (int c = 0; c < 4; c++)
                {
                    var values = new float[keys];
                    for (int k = 0; k < keys; k++)
                    {
                        Quaternion q = rotations[k, b];
                        values[k] = c == 0 ? q.x : c == 1 ? q.y : c == 2 ? q.z : q.w;
                    }

                    clip.SetCurve(path, typeof(Transform), c == 0 ? "localRotation.x" : c == 1 ? "localRotation.y" : c == 2 ? "localRotation.z" : "localRotation.w", Curve(values, length));
                }
            }

            string bodyPath = PathFrom(root, rig.Body);
            for (int c = 0; c < 3; c++)
            {
                var values = new float[keys];
                for (int k = 0; k < keys; k++)
                {
                    values[k] = bodyAt[k][c];
                }

                clip.SetCurve(bodyPath, typeof(Transform), c == 0 ? "localPosition.x" : c == 1 ? "localPosition.y" : "localPosition.z", Curve(values, length));
            }

            return clip;
        }

        // A loop through the values, ending where it began, smooth all the way round; or one value held.
        private static AnimationCurve Curve(float[] values, float length)
        {
            int count = values.Length;
            if (count == 1)
            {
                return new AnimationCurve(new Keyframe(0f, values[0]), new Keyframe(length, values[0]));
            }

            var keys = new Keyframe[count + 1];
            float step = length / count;
            for (int k = 0; k <= count; k++)
            {
                float before = values[(k - 1 + count) % count];
                float after = values[(k + 1) % count];
                float slope = (after - before) / (2f * step);
                keys[k] = new Keyframe(k * step, values[k % count], slope, slope);
            }

            return new AnimationCurve(keys);
        }

        private static string PathFrom(Transform root, Transform bone)
        {
            string path = bone.name;
            for (Transform at = bone.parent; at != null && at != root; at = at.parent)
            {
                path = at.name + "/" + path;
            }

            return path;
        }
    }
}
