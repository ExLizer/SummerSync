using System.Globalization;
using System.Text;
using SummerSync.Core.Vehicles;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// Another player at the wheel, on the copy of a person that stands for them: when they take the wheel the hands
    /// reach for it and then turn with it, the right hand goes to the gear lever while they change gear, and the feet
    /// press the pedals as far as the player does (the pedals themselves are turned by <see cref="Cockpit.ShowPedals"/>).
    ///
    /// It moves the bones after the clip of sitting has been played on them in the same frame, reaching for each place
    /// with two bones and the elbow or the knee towards a side, and blends from the clip to that as the hands take hold
    /// or let go. That clip plays on the upper arms, the forearms, the thighs and the knees (asset-clip.py on
    /// "fat_get_car_idle1"), so those start from it again in every frame; the hands, the ankles and the pelvis it leaves
    /// alone, and they are set from how they were when the copy was made. The clip sits like a passenger, further back
    /// than a driver: the body comes forward until the hands reach the rim and the feet the pedals, and in a long car
    /// (the Ferndale) the back leans back so that the head stays near the player's eyes. Nothing of the game's is copied:
    /// only the copy's bones are moved.
    /// </summary>
    internal sealed class DriverPose
    {
        // Taking the wheel, letting go of it; to the lever and back; the right foot from one pedal to the other.
        private const float TakeSeconds = 0.45f;
        private const float LetGoSeconds = 0.3f;
        private const float ToLeverSeconds = 0.18f;
        private const float BackFromLeverSeconds = 0.25f;
        private const float FootSwapSeconds = 0.15f;

        // The right hand stays on the lever this long after the gear changed, and goes to it while the clutch is down.
        private const float ShiftHold = 0.45f;
        private const float ClutchForLever = 0.5f;

        // The hands hold the rim this far from its top, "ten to two", and turn with it this far before they slide on it.
        private const float GripFromTop = 62f;
        private const float HandsTurnWith = 100f;

        // From the wrist to where the hand closes round what it holds.
        private const float HandReach = 0.07f;

        // A pedal is pressed with the ball of the foot: the ankle is behind it and above it, the foot pitched down.
        private const float AnkleBack = 0.09f;
        private const float AnkleUp = 0.06f;
        private const float FootPitch = 30f;

        // Going from one pedal to the other, the foot is lifted this much halfway.
        private const float FootLift = 0.05f;

        // A pedal pressed less than this is not being pressed.
        private const float Pressed = 0.05f;

        // Reaching, the elbows and the knees stay bent by this much (what the arm or leg is short of straight); the body
        // comes forward for it at most this far, this fast. Past the first part of that the back leans back, as far as it
        // keeps the hands on the wheel, so that the head stays near where the player sees from.
        private const float ElbowBend = 0.05f;
        private const float KneeBend = 0.03f;
        private const float MostForward = 0.45f;
        private const float SlideSpeed = 0.8f;
        private const float HeadForward = 0.15f;
        private const float MostRecline = 25f;

        // How long after taking the wheel the body counts as settled at it, for the log.
        private const float SettleSeconds = 2f;

        private readonly PoseRig rig;
        private readonly Transform body;
        private readonly Transform head;
        private readonly Vector3 pelvisRestPlace;
        private readonly Quaternion pelvisRest;
        private readonly float headAboveHips;
        private readonly float shouldersAboveHips;
        private readonly float[] armLength = new float[2];
        private readonly float[] legLength = new float[2];
        private readonly Vector3[] footGoal = new Vector3[2];
        private readonly Quaternion[] handRest = new Quaternion[2];
        private readonly Quaternion[] ankleRest = new Quaternion[2];
        private Cockpit last;
        private float hold;
        private float lever;
        private float brakeFoot;
        private int gear = int.MinValue;
        private float sinceShift = float.MaxValue;
        private bool moved;

        // How far each hand and foot ended from where it was sent, in the last frame; -1 if it was not sent anywhere.
        private readonly float[] handMiss = { -1f, -1f };
        private readonly float[] footMiss = { -1f, -1f };

        // Where, in the vehicle's coordinates, the shoulders and hips were and where their hands and feet were sent.
        private readonly Vector3[] joints = new Vector3[4];
        private readonly Vector3[] goals = new Vector3[4];
        private float lastTurn;
        private int shifts;
        private float slide;
        private float recline;
        private float needArms;
        private float needLegs;
        private float settled;

        /// <summary>
        /// <paramref name="rig"/> with every bone as the copy was made; <paramref name="body"/> the object the copy is placed
        /// by, which comes forward at the wheel; <paramref name="head"/> the head, kept looking where it was aimed when the
        /// back leans back, or null.
        /// </summary>
        public DriverPose(PoseRig rig, Transform body, Transform head)
        {
            this.rig = rig;
            this.body = body;
            this.head = head;
            pelvisRestPlace = rig.Pelvis.localPosition;
            pelvisRest = rig.Pelvis.localRotation;
            Vector3 hips = (rig.Thigh[0].position + rig.Thigh[1].position) * 0.5f;
            headAboveHips = head == null ? 0f : (head.position - hips).magnitude;
            shouldersAboveHips = (((rig.Shoulder[0].position + rig.Shoulder[1].position) * 0.5f) - hips).magnitude;
            for (int s = 0; s < 2; s++)
            {
                handRest[s] = rig.Hand[s].localRotation;
                ankleRest[s] = rig.Ankle[s].localRotation;
                armLength[s] = (rig.Arm[s].position - rig.Shoulder[s].position).magnitude + (rig.Hand[s].position - rig.Arm[s].position).magnitude;
                legLength[s] = (rig.Knee[s].position - rig.Thigh[s].position).magnitude + (rig.Ankle[s].position - rig.Knee[s].position).magnitude;
            }
        }

        /// <summary>
        /// Once per frame, after the clip of sitting has been played: the body at the wheel of <paramref name="cockpit"/>
        /// with <paramref name="controls"/> while <paramref name="driving"/>, and letting go of it after.
        /// </summary>
        public void Apply(Cockpit cockpit, VehicleControls controls, bool driving, float deltaTime)
        {
            if (cockpit != last)
            {
                // Another vehicle: nothing it did with the last one counts.
                if (last != null && cockpit != null)
                {
                    hold = 0f;
                }

                last = cockpit ?? last;
                gear = int.MinValue;
                sinceShift = float.MaxValue;
                lever = 0f;
            }

            bool takes = driving && cockpit != null;
            hold = Mathf.MoveTowards(hold, takes ? 1f : 0f, deltaTime / (takes ? TakeSeconds : LetGoSeconds));

            // Unity's null check is also true for a vehicle that is gone.
            if (hold <= 0f || last == null || last.Root == null)
            {
                Reset();
                return;
            }

            Cockpit at = last;
            float weight = Mathf.SmoothStep(0f, 1f, hold);
            if (takes)
            {
                if (gear != int.MinValue && controls.Gear != gear)
                {
                    sinceShift = 0f;
                    shifts++;
                }

                gear = controls.Gear;
            }

            sinceShift += deltaTime;
            Vector3 knob;
            bool hasKnob = at.Knob(out knob);
            bool toLever = takes && hasKnob && (sinceShift < ShiftHold || (at.HasPedal(DriverInput.Clutch) && controls.Clutch >= ClutchForLever));
            lever = Mathf.MoveTowards(lever, toLever ? 1f : 0f, deltaTime / (toLever ? ToLeverSeconds : BackFromLeverSeconds));

            Transform vehicle = at.Root;
            Vector3 right = vehicle.right;
            Vector3 up = vehicle.up;
            Vector3 ahead = vehicle.forward;
            moved = true;

            // The feet: the left one on the clutch, the right one on the throttle or the brake, whichever is pressed. On a
            // handlebar those are levers for the hands, which hold them already; the feet rest where the clip has them.
            if (takes)
            {
                if (controls.Brake > Pressed && controls.Brake >= controls.Throttle)
                {
                    brakeFoot = Mathf.MoveTowards(brakeFoot, 1f, deltaTime / FootSwapSeconds);
                }
                else if (controls.Throttle > Pressed)
                {
                    brakeFoot = Mathf.MoveTowards(brakeFoot, 0f, deltaTime / FootSwapSeconds);
                }
            }

            Vector3 throttle = Vector3.zero;
            Vector3 brake = Vector3.zero;
            Vector3 clutch = Vector3.zero;
            bool feet = !at.Handlebar;
            bool hasThrottle = feet && at.Pad(DriverInput.Throttle, out throttle);
            bool hasBrake = feet && at.Pad(DriverInput.Brake, out brake);
            bool hasClutch = feet && at.Pad(DriverInput.Clutch, out clutch);
            bool hasRightPad = hasThrottle || hasBrake;
            footGoal[0] = clutch - (ahead * AnkleBack) + (up * AnkleUp);
            Vector3 pad = hasThrottle && hasBrake
                ? Vector3.Lerp(throttle, brake, brakeFoot) + (up * FootLift * Mathf.Sin(Mathf.PI * brakeFoot))
                : hasThrottle ? throttle : brake;
            footGoal[1] = pad - (ahead * AnkleBack) + (up * AnkleUp);

            // The hitchhiker's way of sitting is a passenger's, further back than a driver: the body comes forward as far as
            // it takes to reach the rim held straight and the pedals, the same however the wheel is turned.
            float arms = 0f;
            float legs = 0f;
            for (int s = 0; s < 2; s++)
            {
                Vector3 grip;
                if (at.Grip(s, GripFromTop, 0f, out grip))
                {
                    Vector3 shoulder = rig.Shoulder[s].position;
                    arms = Mathf.Max(arms, Short(grip + ((shoulder - grip).normalized * HandReach) - shoulder, armLength[s] - ElbowBend, ahead));
                }

                if (s == 0 ? hasClutch : hasRightPad)
                {
                    legs = Mathf.Max(legs, Short(footGoal[s] - rig.Thigh[s].position, legLength[s] - KneeBend, ahead));
                }
            }

            // The right foot goes from one pedal to the other: the body is where it reaches both, not on the move between them.
            if (hasThrottle && hasBrake)
            {
                Vector3 hip = rig.Thigh[1].position;
                Vector3 offset = (up * AnkleUp) - (ahead * AnkleBack);
                legs = Mathf.Max(legs, Mathf.Max(Short(throttle + offset - hip, legLength[1] - KneeBend, ahead), Short(brake + offset - hip, legLength[1] - KneeBend, ahead)));
            }

            needArms = arms;
            needLegs = legs;
            settled = hold >= 1f ? settled + deltaTime : 0f;
            slide = Mathf.MoveTowards(slide, Mathf.Min(Mathf.Max(arms, legs), MostForward), deltaTime * SlideSpeed);
            body.position += ahead * (slide * weight);

            // Leaning back takes the head back by its height over the hips times the sine, and the shoulders by theirs: as
            // far as the head needs and the hands allow.
            float back = 0f;
            if (head != null && headAboveHips > 0.1f && shouldersAboveHips > 0.1f)
            {
                float sine = Mathf.Min((slide - HeadForward) / headAboveHips, (slide - arms) / shouldersAboveHips);
                back = Mathf.Asin(Mathf.Clamp(sine, 0f, Mathf.Sin(MostRecline * Mathf.Deg2Rad))) * Mathf.Rad2Deg;
            }

            recline = back;
            rig.Pelvis.localPosition = pelvisRestPlace;
            rig.Pelvis.localRotation = pelvisRest;
            if (back > 0f)
            {
                Quaternion looks = head.rotation;
                rig.Pelvis.RotateAround((rig.Thigh[0].position + rig.Thigh[1].position) * 0.5f, right, -back * weight);
                head.rotation = looks;
            }

            // The hands, on the rim and turning with it as far as wrists turn; the right one off to the lever.
            lastTurn = at.Turn(controls.Steering);
            float held = Mathf.Clamp(lastTurn, -HandsTurnWith, HandsTurnWith);
            for (int s = 0; s < 2; s++)
            {
                handMiss[s] = -1f;
                float side = s == 0 ? -1f : 1f;
                Vector3 target;
                bool has = at.Grip(s, GripFromTop, held, out target);
                if (s == 1 && hasKnob && lever > 0f)
                {
                    target = has ? Vector3.Lerp(target, knob, Mathf.SmoothStep(0f, 1f, lever)) : knob;
                    has = true;
                }

                if (!has)
                {
                    rig.Hand[s].localRotation = handRest[s];
                    continue;
                }

                Transform upper = rig.Shoulder[s];
                Transform lower = rig.Arm[s];
                Vector3 wrist = target + ((upper.position - target).normalized * HandReach);
                Quaternion upperClip = upper.localRotation;
                Quaternion lowerClip = lower.localRotation;
                Reach(upper, lower, rig.Hand[s], wrist, (right * side * 0.8f) - up - (ahead * 0.2f));
                handMiss[s] = (rig.Hand[s].position - wrist).magnitude;
                joints[s] = vehicle.InverseTransformPoint(upper.position);
                goals[s] = vehicle.InverseTransformPoint(wrist);
                upper.localRotation = Quaternion.Slerp(upperClip, upper.localRotation, weight);
                lower.localRotation = Quaternion.Slerp(lowerClip, lower.localRotation, weight);
                rig.Hand[s].localRotation = handRest[s];
            }

            Quaternion seated = Quaternion.LookRotation(ahead, up) * Quaternion.AngleAxis(FootPitch, Vector3.right);
            for (int s = 0; s < 2; s++)
            {
                footMiss[s] = -1f;
                if (!(s == 0 ? hasClutch : hasRightPad))
                {
                    rig.Ankle[s].localRotation = ankleRest[s];
                    continue;
                }

                float side = s == 0 ? -1f : 1f;
                Transform thigh = rig.Thigh[s];
                Transform knee = rig.Knee[s];
                Transform ankle = rig.Ankle[s];
                Quaternion thighClip = thigh.localRotation;
                Quaternion kneeClip = knee.localRotation;
                Vector3 goal = footGoal[s];
                Reach(thigh, knee, ankle, goal, up + (ahead * 0.6f) + (right * side * 0.25f));
                footMiss[s] = (ankle.position - goal).magnitude;
                joints[2 + s] = vehicle.InverseTransformPoint(thigh.position);
                goals[2 + s] = vehicle.InverseTransformPoint(goal);
                thigh.localRotation = Quaternion.Slerp(thighClip, thigh.localRotation, weight);
                knee.localRotation = Quaternion.Slerp(kneeClip, knee.localRotation, weight);
                Quaternion rest = ankle.parent.rotation * ankleRest[s];
                ankle.rotation = Quaternion.Slerp(rest, seated * rig.AnkleRest[s], weight);
            }
        }

        /// <summary>
        /// Before the body is placed in a frame: the back straight again. The clip of sitting never moves the pelvis, and the
        /// head, which the body is placed by, hangs from it: leaning back left on it would be taken for where the body sits.
        /// </summary>
        public void Straighten()
        {
            if (moved && rig.Pelvis != null)
            {
                rig.Pelvis.localPosition = pelvisRestPlace;
                rig.Pelvis.localRotation = pelvisRest;
            }
        }

        /// <summary>
        /// Whether the hands and feet have been on the controls for a while, with the body settled where it reaches them: what
        /// <see cref="Report"/> tells is then what is seen.
        /// </summary>
        public bool Holding
        {
            get { return moved && settled >= SettleSeconds; }
        }

        /// <summary>
        /// How far each hand and foot is from what it works, as the bones reach: 0 is on it; more is as near as the arm or the
        /// leg gets. For the log.
        /// </summary>
        public string Report()
        {
            if (handMiss[0] < 0f && handMiss[1] < 0f && footMiss[0] < 0f && footMiss[1] < 0f)
            {
                return "nothing of its controls is there on this machine (the game draws a vehicle's inside only near the player)";
            }

            var text = new StringBuilder();
            text.Append("hands ").Append(Miss(handMiss[0])).Append(" and ").Append(Miss(handMiss[1])).Append(" m from where they hold (")
                .Append(last != null && last.Handlebar ? "a handlebar" : "the rim turned " + lastTurn.ToString("0", CultureInfo.InvariantCulture) + " degrees")
                .Append(lever > 0f ? ", the right one going to the gear lever" : string.Empty).Append("), feet ")
                .Append(Miss(footMiss[0])).Append(" and ").Append(Miss(footMiss[1])).Append(" m from the pedals, ")
                .Append(shifts).Append(" gear change(s) seen, the body ").Append(slide.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" m forward and leaning back ").Append(recline.ToString("0", CultureInfo.InvariantCulture))
                .Append(" degrees (the arms wanted it ").Append(needArms.ToString("0.00", CultureInfo.InvariantCulture)).Append(" m, the legs ")
                .Append(needLegs.ToString("0.00", CultureInfo.InvariantCulture)).Append(" m; arms ").Append(armLength[0].ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" m, legs ").Append(legLength[0].ToString("0.00", CultureInfo.InvariantCulture)).Append(" m long); in the vehicle's coordinates, shoulders ").Append(Place(joints[0])).Append(' ')
                .Append(Place(joints[1])).Append(" to wrists ").Append(Place(goals[0])).Append(' ').Append(Place(goals[1])).Append(", hips ")
                .Append(Place(joints[2])).Append(' ').Append(Place(joints[3])).Append(" to ankles ").Append(Place(goals[2])).Append(' ').Append(Place(goals[3]));
            return text.ToString();
        }

        /// <summary>Hands and feet back as the copy was made, and nothing held: not driving, not sitting, or hidden.</summary>
        public void Reset()
        {
            settled = 0f;
            hold = 0f;
            lever = 0f;
            slide = 0f;
            recline = 0f;
            last = null;
            gear = int.MinValue;
            sinceShift = float.MaxValue;
            if (!moved)
            {
                return;
            }

            moved = false;

            // The clip of sitting never moves the pelvis: what leaning back left on it stays unless it is put back.
            if (rig.Pelvis != null)
            {
                rig.Pelvis.localPosition = pelvisRestPlace;
                rig.Pelvis.localRotation = pelvisRest;
            }

            for (int s = 0; s < 2; s++)
            {
                // Unity's null check is also true for a destroyed bone.
                if (rig.Hand[s] != null)
                {
                    rig.Hand[s].localRotation = handRest[s];
                }

                if (rig.Ankle[s] != null)
                {
                    rig.Ankle[s].localRotation = ankleRest[s];
                }
            }
        }

        private static string Place(Vector3 at)
        {
            return "(" + at.x.ToString("0.00", CultureInfo.InvariantCulture) + ", " + at.y.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                + at.z.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }

        // How much further than it reaches a limb would have to go, as a way along the vehicle: what is short of the goal,
        // divided by how much of the limb's way to it is ahead (coming forward does less for a goal off to the side).
        private static float Short(Vector3 toGoal, float reach, Vector3 ahead)
        {
            float distance = toGoal.magnitude;
            if (distance <= reach || distance < 1e-4f)
            {
                return 0f;
            }

            return (distance - reach) / Mathf.Max(0.3f, Vector3.Dot(toGoal / distance, ahead));
        }

        private static string Miss(float metres)
        {
            return metres < 0f ? "nothing" : metres.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // Turns two bones so that the end of the second is at the target, or as near as they reach, bending the joint
        // between them towards the pole.
        private static void Reach(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            Vector3 start = upper.position;
            float first = (lower.position - start).magnitude;
            float second = (end.position - lower.position).magnitude;
            Vector3 toTarget = target - start;
            if (first < 1e-4f || second < 1e-4f || toTarget.sqrMagnitude < 1e-8f)
            {
                return;
            }

            float reach = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(first - second) + 0.001f, first + second - 0.001f);
            Vector3 along = toTarget.normalized;
            Vector3 bend = pole - (along * Vector3.Dot(pole, along));
            bend = bend.sqrMagnitude < 1e-8f ? Vector3.Cross(along, Vector3.right).normalized : bend.normalized;
            float a = ((first * first) - (second * second) + (reach * reach)) / (2f * reach);
            float h = Mathf.Sqrt(Mathf.Max(0f, (first * first) - (a * a)));
            Vector3 joint = start + (along * a) + (bend * h);
            Aim(upper, lower, joint - start);
            Aim(lower, end, (start + (along * reach)) - joint);
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
    }
}
