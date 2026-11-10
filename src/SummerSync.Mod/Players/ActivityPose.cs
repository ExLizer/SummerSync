using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// Another player drinking, smoking or peeing, on the copy of a person that stands for them: the right hand brings
    /// what they drink from up to the mouth, bottom up; a cigarette rests in the right hand by the chest, goes to the lips
    /// while they draw on it and the smoke comes out of the mouth after; peeing, both hands go to the crotch and the
    /// stream comes out where the player looks, with its sound. And the middle finger: the left arm comes up in front
    /// with the game's own hand for it at the wrist, in place of the copy's. The things are the game's own (what the local player's
    /// hand drinks from, smokes, pees: <see cref="PlayerActions"/>), copied without anything but their looks.
    ///
    /// Like <see cref="DriverPose"/>, it moves the arms after the clips have been played on them in the same frame,
    /// reaching with two bones, and blends from the clip to that. Every clip of the copies moves the upper arms and the
    /// forearms, so those start from it again in every frame; the hands are left as the clips have them. A clip that is
    /// not being played moves nothing, though (a person standing still): an arm nothing has moved since this put it
    /// somewhere starts again from where this found it.
    /// </summary>
    internal sealed class ActivityPose
    {
        // Up to the mouth and down; the cigarette to the lips and back; the hands to the crotch and back.
        private const float UpSeconds = 0.5f;
        private const float DownSeconds = 0.35f;
        private const float LipsSeconds = 0.3f;
        private const float HandsSeconds = 0.4f;

        // The left arm up for the middle finger and down again.
        private const float FingerUpSeconds = 0.2f;
        private const float FingerDownSeconds = 0.3f;

        // The forearm of the game's hand is drawn this much of its length: the copy has a forearm of its own.
        private const float FingerForearm = 0.04f;
        private const string FingerForearmPath = "Armature/Bone";
        private const string FingerWristName = "Bone_001";

        // The mouth from the head bone: ahead along where the body looks, and down.
        private const float MouthAhead = 0.11f;
        private const float MouthDown = 0.04f;

        // From the wrist to where the hand closes round what it holds.
        private const float HandReach = 0.07f;

        // The crotch from between the hips: ahead and down.
        private const float CrotchAhead = 0.15f;
        private const float CrotchDown = 0.06f;

        // The smoke breathed out lasts this long after the cigarette leaves the lips.
        private const float BreathSeconds = 1.6f;

        // The stream, in particles a second, while it runs (the game's runs from 400 down); the breath, the same.
        private const float StreamRate = 250f;
        private const float BreathRate = 10f;

        private const LogCategory Category = LogCategory.Player;

        private readonly PoseRig rig;
        private readonly Transform root;
        private readonly Transform head;
        private readonly Logger log;
        private readonly int layer;
        private float drink;
        private float smoke;
        private float lips;
        private float pee;
        private int drinkShown = -1;
        private int drinkHeld;
        private Transform drinkProp;
        private Vector3 drinkCentre;
        private Transform cigarette;
        private Vector3 cigaretteTip;
        private ParticleEmitter breath;
        private ParticleEmitter stream;
        private AudioSource sound;
        private byte lastSmoke;
        private float breathLeft;
        private bool streaming;
        private bool moved;
        private float finger;
        private float fingerLeft;
        private Transform fingerHand;
        private Transform fingerWrist;
        private Quaternion fingerTurn = Quaternion.identity;
        private bool fingerFailed;
        private bool handHidden;

        // For each arm, left and right: how its two bones were before this moved them, how this left them, and whether
        // it moved them in the last frame and in this one.
        private readonly Quaternion[] upperFound = new Quaternion[2];
        private readonly Quaternion[] lowerFound = new Quaternion[2];
        private readonly Quaternion[] upperLeft = new Quaternion[2];
        private readonly Quaternion[] lowerLeft = new Quaternion[2];
        private readonly bool[] armHeld = new bool[2];
        private readonly bool[] armMoved = new bool[2];
        private Vector3 handScale = Vector3.one;

        public ActivityPose(PoseRig rig, Transform root, Transform head, Logger log)
        {
            this.rig = rig;
            this.root = root;
            this.head = head;
            this.log = log;
            layer = rig.Hand[1] != null ? rig.Hand[1].gameObject.layer : root.gameObject.layer;
        }

        /// <summary>Holds the middle finger up for that long from now.</summary>
        public void ShowFinger(float seconds)
        {
            fingerLeft = seconds;
        }

        /// <summary>
        /// Once per frame, after the clips (and the hands at the wheel) have been put on the bones. <paramref name="face"/>:
        /// where the body faces, in degrees about Y.
        /// </summary>
        public void Apply(PlayerPose pose, float face, float deltaTime)
        {
            StartArms();
            bool drinking = pose.Activity == PlayerActivity.Drinking;
            bool smoking = pose.Activity == PlayerActivity.Smoking;
            bool peeing = pose.Activity == PlayerActivity.Peeing;
            if (drinking)
            {
                drinkHeld = pose.ActivityDetail;
            }

            drink = Mathf.MoveTowards(drink, drinking ? 1f : 0f, deltaTime / (drinking ? UpSeconds : DownSeconds));
            smoke = Mathf.MoveTowards(smoke, smoking ? 1f : 0f, deltaTime / HandsSeconds);
            bool drawing = smoking && pose.ActivityDetail == PlayerPose.SmokeDrawing;
            lips = Mathf.MoveTowards(lips, drawing ? 1f : 0f, deltaTime / LipsSeconds);
            pee = Mathf.MoveTowards(pee, peeing ? 1f : 0f, deltaTime / HandsSeconds);
            byte smokeNow = smoking ? pose.ActivityDetail : PlayerPose.SmokeHolding;
            if (smokeNow == PlayerPose.SmokeBreathing && lastSmoke != PlayerPose.SmokeBreathing)
            {
                breathLeft = BreathSeconds;
            }

            lastSmoke = smokeNow;
            breathLeft = Mathf.Max(0f, breathLeft - deltaTime);

            Quaternion facing = Quaternion.Euler(0f, face, 0f);
            Vector3 ahead = facing * Vector3.forward;
            Vector3 right = facing * Vector3.right;
            Vector3 up = Vector3.up;
            Vector3 mouth = head != null
                ? head.position + (ahead * MouthAhead) - (up * MouthDown)
                : ((rig.Shoulder[0].position + rig.Shoulder[1].position) * 0.5f) + (up * 0.2f) + (ahead * 0.1f);

            Quaternion look = Quaternion.Euler(-pose.Pitch, face, 0f);
            if (drink > 0f)
            {
                // Up from by the chest to the lips, turned as the game turns it at the mouth, its neck or rim at them.
                float weight = Mathf.SmoothStep(0f, 1f, drink);
                Vector3 low = rig.Shoulder[1].position + (ahead * 0.3f) - (up * 0.25f) + (right * 0.05f);
                PlayerActions actions = PlayerActions.Current;
                Vector3 lips;
                Quaternion atMouth;
                Quaternion down;
                if (actions != null && actions.DrinkPose(drinkHeld, out atMouth, out lips, out down))
                {
                    Quaternion turned = Quaternion.Slerp(facing * down, look * atMouth, weight);
                    Vector3 scale = drinkProp != null ? drinkProp.lossyScale : Vector3.one;
                    Vector3 pivot = Vector3.Lerp(low, mouth - ((look * atMouth) * Vector3.Scale(lips, scale)), weight);
                    Vector3 middle = ShowDrink(drinkHeld, pivot, turned);
                    Arm(1, middle + ((rig.Shoulder[1].position - middle).normalized * HandReach), (right * 0.8f) - (up * 0.6f), weight);
                }
                else
                {
                    ShowDrink(-1, Vector3.zero, Quaternion.identity);
                    Arm(1, Vector3.Lerp(low, mouth + (ahead * 0.1f) + (up * 0.05f), weight), (right * 0.8f) - (up * 0.6f), weight);
                }
            }
            else
            {
                ShowDrink(-1, Vector3.zero, Quaternion.identity);
            }

            if (smoke > 0f && drink <= 0f)
            {
                // Held by the chest, the lit end out; at the lips the other end in the mouth.
                Vector3 held = rig.Shoulder[1].position + (ahead * 0.3f) - (up * 0.27f) + (right * 0.02f);
                Vector3 atLips = mouth + (ahead * 0.06f) + (right * 0.03f) - (up * 0.03f);
                float weight = Mathf.SmoothStep(0f, 1f, smoke);
                float atMouth = Mathf.SmoothStep(0f, 1f, lips);
                Vector3 grip = Arm(1, Vector3.Lerp(held, atLips, atMouth), (right * 0.8f) - (up * 0.6f), weight);
                Vector3 tip = Vector3.Slerp((ahead + (up * 0.4f)).normalized, (ahead - (up * 0.2f)).normalized, atMouth);
                ShowCigarette(true, grip, tip);
            }
            else
            {
                ShowCigarette(false, Vector3.zero, Vector3.forward);
            }

            Breathe(breathLeft > 0f, mouth, look);

            Vector3 hips = (rig.Thigh[0].position + rig.Thigh[1].position) * 0.5f;
            Vector3 crotch = hips + (ahead * CrotchAhead) - (up * CrotchDown);
            if (pee > 0f)
            {
                float weight = Mathf.SmoothStep(0f, 1f, pee);
                for (int s = 0; s < 2; s++)
                {
                    float side = s == 0 ? -1f : 1f;
                    Arm(s, crotch + (right * side * 0.06f) + (ahead * 0.03f), (right * side * 0.9f) - (up * 0.2f) - (ahead * 0.3f), weight);
                }
            }

            // Where the player looks, as the game's stream hangs from the camera.
            Stream(peeing && pee >= 0.5f, crotch + (ahead * 0.04f), look);

            fingerLeft = Mathf.Max(0f, fingerLeft - deltaTime);
            bool raised = fingerLeft > 0f && pee <= 0f;
            finger = Mathf.MoveTowards(finger, raised ? 1f : 0f, deltaTime / (raised ? FingerUpSeconds : FingerDownSeconds));
            if (finger > 0f)
            {
                // The left hand up in front of the shoulder, the forearm upright, the back of the hand ahead.
                float weight = Mathf.SmoothStep(0f, 1f, finger);
                Vector3 wrist = rig.Shoulder[0].position + (ahead * 0.3f) + (up * 0.12f) - (right * 0.03f);
                Arm(0, wrist, -(right * 0.7f) - (up * 0.7f), weight);
                ShowFingerHand(finger >= 0.5f, (up + (ahead * 0.25f)).normalized, ahead);
            }
            else
            {
                ShowFingerHand(false, up, ahead);
            }

            moved = drink > 0f || smoke > 0f || pee > 0f || breathLeft > 0f || streaming || finger > 0f;
            armHeld[0] = armMoved[0];
            armHeld[1] = armMoved[1];
        }

        // Before the arms are moved in a frame: one that this moved in the last frame and no clip has moved since is put
        // back as this found it; otherwise how it is now is how it was found.
        private void StartArms()
        {
            for (int s = 0; s < 2; s++)
            {
                Transform upper = rig.Shoulder[s];
                Transform lower = rig.Arm[s];
                armMoved[s] = false;
                if (upper == null || lower == null)
                {
                    continue;
                }

                if (armHeld[s] && upper.localRotation == upperLeft[s] && lower.localRotation == lowerLeft[s])
                {
                    upper.localRotation = upperFound[s];
                    lower.localRotation = lowerFound[s];
                }
                else
                {
                    upperFound[s] = upper.localRotation;
                    lowerFound[s] = lower.localRotation;
                }
            }
        }

        /// <summary>Nothing held, nothing coming out: not shown, or the character window.</summary>
        public void Reset()
        {
            drink = 0f;
            smoke = 0f;
            lips = 0f;
            pee = 0f;
            breathLeft = 0f;
            finger = 0f;
            fingerLeft = 0f;
            lastSmoke = PlayerPose.SmokeHolding;
            if (armHeld[0] || armHeld[1])
            {
                StartArms();
                armHeld[0] = false;
                armHeld[1] = false;
            }

            if (!moved)
            {
                return;
            }

            moved = false;
            ShowDrink(-1, Vector3.zero, Quaternion.identity);
            ShowCigarette(false, Vector3.zero, Vector3.forward);
            Breathe(false, Vector3.zero, Quaternion.identity);
            Stream(false, Vector3.zero, Quaternion.identity);
            ShowFingerHand(false, Vector3.up, Vector3.back);
        }

        // Reaches with the arm of that side for a wrist at the target, blended from the clip by the weight; returns where
        // the hand closes, beyond the wrist.
        private Vector3 Arm(int s, Vector3 wrist, Vector3 pole, float weight)
        {
            Transform upper = rig.Shoulder[s];
            Transform lower = rig.Arm[s];
            Transform hand = rig.Hand[s];
            Quaternion upperClip = upper.localRotation;
            Quaternion lowerClip = lower.localRotation;
            Reach(upper, lower, hand, wrist, pole);
            upper.localRotation = Quaternion.Slerp(upperClip, upper.localRotation, weight);
            lower.localRotation = Quaternion.Slerp(lowerClip, lower.localRotation, weight);
            upperLeft[s] = upper.localRotation;
            lowerLeft[s] = lower.localRotation;
            armMoved[s] = true;
            Vector3 along = (hand.position - lower.position).normalized;
            return hand.position + (along * HandReach);
        }

        // What is drunk from, in the hand: the detail's thing of the game's, copied when it changes, its pivot there and turned
        // so. Returns where the middle of it is, for the hand.
        private Vector3 ShowDrink(int detail, Vector3 pivot, Quaternion turned)
        {
            if (detail != drinkShown)
            {
                // Unity's null check is also true for a destroyed object.
                if (drinkProp != null)
                {
                    Object.Destroy(drinkProp.gameObject);
                }

                drinkProp = null;
                drinkShown = detail;
                PlayerActions actions = PlayerActions.Current;
                Transform source = detail < 1 || actions == null ? null : actions.Drink(detail);
                if (source != null)
                {
                    drinkProp = Copy(source, "Drink " + source.name);
                    MeshFilter filter = drinkProp.GetComponent<MeshFilter>();
                    drinkCentre = filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds.center : Vector3.zero;
                    log.Info(Category, "A player drinks from '" + source.name + "' on a copy here: " + Format(drinkProp.lossyScale.x) + " scale, " + Size(drinkProp) + " m long.");
                }
            }

            if (drinkProp == null)
            {
                return pivot;
            }

            if (detail < 0)
            {
                drinkProp.gameObject.SetActive(false);
                return pivot;
            }

            if (!drinkProp.gameObject.activeSelf)
            {
                drinkProp.gameObject.SetActive(true);
            }

            drinkProp.rotation = turned;
            drinkProp.position = pivot;
            return drinkProp.TransformPoint(drinkCentre);
        }

        private void ShowCigarette(bool shown, Vector3 grip, Vector3 tip)
        {
            if (cigarette == null)
            {
                if (!shown)
                {
                    return;
                }

                PlayerActions actions = PlayerActions.Current;
                if (actions == null || actions.Cigarette == null)
                {
                    return;
                }

                cigarette = Copy(actions.Cigarette, "Cigarette");
                Transform lit = cigarette.Find("Shaft/RedHot");
                Transform filter = cigarette.Find("Filter");
                cigaretteTip = lit != null && filter != null ? cigarette.InverseTransformDirection(lit.position - filter.position).normalized : Vector3.forward;
            }

            if (cigarette.gameObject.activeSelf != shown)
            {
                cigarette.gameObject.SetActive(shown);
            }

            if (shown)
            {
                cigarette.rotation = Quaternion.FromToRotation(cigaretteTip, tip);
                cigarette.position = grip;
            }
        }

        // The game's hand for the middle finger at the left wrist, the finger that way and its back that way, with the
        // copy's own left hand drawn as nothing meanwhile.
        private void ShowFingerHand(bool shown, Vector3 fingerTo, Vector3 backTo)
        {
            Transform own = rig.Hand[0];
            if (fingerHand == null && shown && !fingerFailed)
            {
                MakeFingerHand();
            }

            // Unity's null check is also true for a destroyed object.
            bool there = fingerHand != null && fingerWrist != null;
            if (there && fingerHand.gameObject.activeSelf != shown)
            {
                fingerHand.gameObject.SetActive(shown);
            }

            if (handHidden != (shown && there) && own != null)
            {
                handHidden = shown && there;
                if (handHidden)
                {
                    handScale = own.localScale;
                    own.localScale = handScale * 0.001f;
                }
                else
                {
                    own.localScale = handScale;
                }
            }

            if (shown && there && own != null)
            {
                fingerHand.rotation = Quaternion.LookRotation(fingerTo, backTo) * fingerTurn;
                fingerHand.position += own.position - fingerWrist.position;
            }
        }

        // The game's hand copied, its forearm drawn as a stub, and how it is turned read from its own bones: the finger
        // held out is the one whose tip is furthest from the wrist, and the back of the hand is where the other fingers' last bones
        // are, off that line (seen in the game: they curl round to the palm from there).
        private void MakeFingerHand()
        {
            PlayerActions actions = PlayerActions.Current;
            Transform source = actions == null ? null : actions.FingerHand;
            if (source == null)
            {
                fingerFailed = true;
                return;
            }

            Transform made = Copy(source, "Middle finger");
            Transform forearm = made.Find(FingerForearmPath);
            Transform wrist = forearm == null ? null : forearm.Find(FingerWristName);
            if (wrist == null)
            {
                Object.Destroy(made.gameObject);
                fingerFailed = true;
                log.Warning(Category, "The game's hand for the middle finger is not made as this build expects ('" + FingerForearmPath + "/" + FingerWristName + "'): the others raise their arm without it.");
                return;
            }

            forearm.localScale = forearm.localScale * FingerForearm;
            wrist.localScale = wrist.localScale / FingerForearm;
            Transform[] bones = wrist.GetComponentsInChildren<Transform>(true);
            Vector3 furthest = Vector3.zero;
            Vector3 others = Vector3.zero;
            int tips = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i].childCount > 0 || bones[i] == wrist)
                {
                    continue;
                }

                Vector3 tip = bones[i].position - wrist.position;
                others += tip;
                tips++;
                if (tip.sqrMagnitude > furthest.sqrMagnitude)
                {
                    furthest = tip;
                }
            }

            if (tips < 2 || furthest.sqrMagnitude < 1e-6f)
            {
                Object.Destroy(made.gameObject);
                fingerFailed = true;
                log.Warning(Category, "The game's hand for the middle finger has no fingers this build can read: the others raise their arm without it.");
                return;
            }

            Vector3 along = furthest.normalized;
            Vector3 rest = (others - furthest) / (tips - 1);
            Vector3 back = rest - (along * Vector3.Dot(rest, along));
            back = back.sqrMagnitude < 1e-8f ? Vector3.Cross(along, Vector3.right) : back;
            fingerTurn = Quaternion.Inverse(Quaternion.LookRotation(along, back.normalized)) * made.rotation;
            fingerHand = made;
            fingerWrist = wrist;
            log.Info(Category, "A player shows the middle finger on a copy here: the game's hand with " + tips + " finger tips, its finger " + Format(furthest.magnitude) + " m from the wrist.");
        }

        private void Breathe(bool breathing, Vector3 mouth, Quaternion look)
        {
            if (breath == null)
            {
                if (!breathing)
                {
                    return;
                }

                PlayerActions actions = PlayerActions.Current;
                if (actions == null || actions.Breath == null)
                {
                    return;
                }

                breath = Copy(actions.Breath.transform, "Breath").GetComponent<ParticleEmitter>();
            }

            if (breathing)
            {
                PlayerActions actions = PlayerActions.Current;
                breath.transform.position = mouth;
                breath.transform.rotation = look * (actions == null ? Quaternion.identity : actions.BreathTurn);
                breath.minEmission = BreathRate;
                breath.maxEmission = BreathRate;
            }

            breath.emit = breathing;
        }

        private void Stream(bool on, Vector3 from, Quaternion look)
        {
            if (stream == null)
            {
                if (!on)
                {
                    return;
                }

                PlayerActions actions = PlayerActions.Current;
                if (actions == null || actions.Stream == null)
                {
                    return;
                }

                stream = Copy(actions.Stream.transform, "Stream").GetComponent<ParticleEmitter>();
                if (actions.PeeSound != null)
                {
                    sound = Copy(actions.PeeSound.transform, "Pee sound").GetComponent<AudioSource>();
                    sound.transform.parent = stream.transform;
                    sound.transform.localPosition = Vector3.zero;
                    sound.spatialBlend = 1f;
                    sound.loop = true;
                }
            }

            if (on)
            {
                PlayerActions actions = PlayerActions.Current;
                stream.transform.position = from;
                stream.transform.rotation = look * (actions == null ? Quaternion.identity : actions.StreamTurn);
                stream.minEmission = StreamRate;
                stream.maxEmission = StreamRate;
            }

            stream.emit = on;
            if (sound != null)
            {
                if (on && !sound.isPlaying)
                {
                    sound.gameObject.SetActive(true);
                    sound.Play();
                }
                else if (!on && sound.isPlaying)
                {
                    sound.Stop();
                }
            }

            streaming = on;
        }

        // The game's thing made again under the copy, as it looks and nothing else, on the copy's layer.
        private Transform Copy(Transform source, string name)
        {
            var made = (GameObject)Object.Instantiate(source.gameObject);
            made.name = name;
            Transform copied = made.transform;
            copied.parent = root;
            copied.localScale = Vector3.Scale(source.lossyScale, new Vector3(1f / root.lossyScale.x, 1f / root.lossyScale.y, 1f / root.lossyScale.z));
            Component[] components = made.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component is PlayMakerFSM || component is Collider || component is Rigidbody)
                {
                    Object.DestroyImmediate(component);
                }
            }

            SetLayer(copied, layer);
            made.SetActive(true);
            return copied;
        }

        private static string Size(Transform thing)
        {
            MeshFilter filter = thing.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return "?";
            }

            Vector3 size = Vector3.Scale(filter.sharedMesh.bounds.size, thing.lossyScale);
            return Format(Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
        }

        private static void SetLayer(Transform at, int layer)
        {
            at.gameObject.layer = layer;
            for (int i = 0; i < at.childCount; i++)
            {
                SetLayer(at.GetChild(i), layer);
            }
        }

        private static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
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
