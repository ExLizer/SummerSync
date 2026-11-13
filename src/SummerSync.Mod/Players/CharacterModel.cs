using System;
using System.Collections.Generic;
using SummerSync.Core.Players;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// The people of the game a player can look like, and the copy of one that stands for a player.
    ///
    /// What is assumed about the game, from its level file and the dump (docs/GAME_NOTES.md, "La gente del
    /// juego"): a person is an object "Char" that hangs 0.82 m above their feet, turned -90 degrees about Y, so that
    /// the body looks along Char's +X; its skinned mesh "bodymesh" and its bones are under "skeleton". The
    /// "Animation" on "skeleton" walks and stands ("fat_walk", "fat_standing"; "woman_*", "granny_*") and only moves
    /// the legs and the back; others on the collars breathe and move the hands. The head hangs from the upper spine,
    /// directly or under a "HeadPivot", which the game's machine "Look" aims at what the person looks at, the head
    /// itself turned (0, 270, 254) from it. There is no crouching, running or lying down: sitting in a car is
    /// borrowed from a hitchhiker ("fat_get_car_idle1", "woman_get_car_in", "granny_get_car_in"), and the rest are the
    /// mod's own (<see cref="PoseClips"/>).
    ///
    /// A copy is "Char" made again, with its own place, under an object of its own (so that those borrowed clips
    /// find the same bones), with nothing left but its bones, meshes and animations. It walks, runs, stands, steps
    /// round when turning on the spot, crouches, walks crouched, lies down, or sits in a vehicle with its head where
    /// the player's eyes are. Its head is aimed where the player looks, as the game aims its people's, whatever the
    /// person was looking at when the copy was made. Whatever a clip leaves on the bones is put back to the pose the
    /// copy was made in. At the wheel, its hands and feet work the vehicle's controls (<see cref="DriverPose"/>).
    /// </summary>
    internal sealed class CharacterModel
    {
        private const string SpineUpperPath = "skeleton/pelvis/spine_middle/spine_upper";
        private const string PelvisPath = "skeleton/pelvis";
        private const float WalkFrom = 0.35f;

        // The people walk at 1.2 m/s with their clip at its own speed (their "Move" machines, in the dump).
        private const float WalkSpeed = 1.2f;
        private const float MostWalkRate = 3f;
        private const float TurnStepRate = 0.8f;

        // Standing eyes are 1.65 m above the feet; crouched 1.10, crouched twice 0.55, in a car 0.85.
        private const float LoweredEyes = 1.35f;
        private const float ProneBelow = 0.8f;
        private const float CrouchMovesFrom = 0.25f;

        // Below this the sitting body's hips would be under the floor.
        private const float LowestEyes = 0.70f;

        // The eyes from the head bone: a little ahead, along where the body looks, and up.
        private const float EyesAhead = 0.09f;
        private const float EyesUp = 0.06f;

        // Only for a person whose head was not found.
        private const float EyesAbovePelvis = 0.70f;

        private const float HeadTurn = 80f;
        private const float HeadPitch = 60f;
        private const float Blend = 0.25f;

        // The head's own turn from where it looks, as the game's machines "Look" set it ("State 2").
        private static readonly Quaternion HeadFromFace = Quaternion.Euler(0f, 270f, 254f);

        private readonly GameObject root;
        private readonly Transform body;
        private readonly Transform head;
        private readonly Transform pelvis;
        private readonly Animation legs;
        private readonly Animation own;
        private readonly Animation[] extras;
        private readonly Transform[] bones;
        private readonly Vector3[] restPositions;
        private readonly Quaternion[] restRotations;
        private readonly string walk;
        private readonly string stand;
        private readonly string sit;
        private readonly bool sitHolds;
        private readonly string found;
        private readonly int lookIndex;
        private readonly PoseSettings settings;
        private readonly PoseRig rig;
        private readonly string rigProblem;
        private readonly DriverPose driver;
        private readonly ActivityPose activity;
        private readonly List<AnimationClip> ownClips = new List<AnimationClip>();
        private int builtSerial = -1;
        private Quaternion headTune = Quaternion.identity;

        // Where the head was last aimed (where the player looks), for what is worn on it.
        private Quaternion headLook = Quaternion.identity;
        private Vector3 lastFeet;
        private float lastYaw;
        private bool placed;
        private float speed;
        private float turning;
        private string playing;

        private CharacterModel(GameObject root, Transform body, Animation legs, Animation own, string walk, string stand, string sit, string found, int lookIndex, PoseSettings settings)
        {
            this.root = root;
            this.body = body;
            this.legs = legs;
            this.own = own;
            this.walk = walk;
            this.stand = stand;
            this.sit = sit;
            this.found = found;
            this.lookIndex = lookIndex;
            this.settings = settings ?? new PoseSettings();
            sitHolds = sit != null && sit.EndsWith("_in", StringComparison.Ordinal);
            head = FindHead(body);
            pelvis = body.Find(PelvisPath);

            // The breathing and the hands play by themselves on the bones the sitting clip moves too.
            Animation[] all = body.GetComponentsInChildren<Animation>(true);
            var others = new List<Animation>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != legs)
                {
                    others.Add(all[i]);
                }
            }

            extras = others.ToArray();

            // The pose it was made in, to go back to when a clip leaves something where it does not belong.
            Transform[] under = root.GetComponentsInChildren<Transform>(true);
            var kept = new List<Transform>();
            for (int i = 0; i < under.Length; i++)
            {
                if (under[i] != root.transform)
                {
                    kept.Add(under[i]);
                }
            }

            bones = kept.ToArray();
            restPositions = new Vector3[bones.Length];
            restRotations = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                restPositions[i] = bones[i].localPosition;
                restRotations[i] = bones[i].localRotation;
            }

            rig = PoseRig.Measure(root.transform, body, out rigProblem);
            driver = rig == null ? null : new DriverPose(rig, root.transform, head);
            activity = rig == null ? null : new ActivityPose(rig, root.transform, head, ModRuntime.Log);
            BuildOwn();
        }

        public bool IsAlive
        {
            get { return root != null; }
        }

        /// <summary>
        /// Where the head bone is and where the head looks (along +Z, up along +Y), for what is worn on it. False if this
        /// person's head was not found.
        /// </summary>
        public bool TryHead(out Vector3 at, out Quaternion facing)
        {
            // Unity's null check is also true for a destroyed object.
            if (head == null)
            {
                at = Vector3.zero;
                facing = Quaternion.identity;
                return false;
            }

            at = head.position;
            facing = headLook;
            return true;
        }

        /// <summary>The top of the head, for the name above it.</summary>
        public Vector3 Top
        {
            get { return head != null ? head.position + (Vector3.up * 0.15f) : root.transform.position + (Vector3.up * 1.9f); }
        }

        public Transform Root
        {
            get { return root.transform; }
        }

        /// <summary>Whether it is at the wheel with hands and feet settled on the controls.</summary>
        public bool AtTheWheel
        {
            get { return driver != null && driver.Holding; }
        }

        /// <summary>At the wheel: how near the hands and feet are to the controls, for the log. Null otherwise.</summary>
        public string DriverReport
        {
            get { return driver != null && driver.Holding ? driver.Report() : null; }
        }

        /// <summary>What it shows whatever the pose says, for the character window; null to follow the pose.</summary>
        public PoseKind? Forced { get; set; }

        /// <summary>Whether it has the mod's own animations (crouching, running, lying down).</summary>
        public bool HasOwnAnimations
        {
            get { return rig != null; }
        }

        /// <summary>
        /// A copy of <paramref name="look"/>, or null if the game does not have that person in this world.
        /// <paramref name="layer"/> below 0 keeps the person's own layers.
        /// </summary>
        public static CharacterModel Build(Characters.Look look, Transform parent, string objectName, int layer, PoseSettings settings)
        {
            string path;
            Transform source = Characters.Find(look, out path);
            if (source == null)
            {
                return null;
            }

            var root = new GameObject(objectName);
            root.transform.parent = parent;
            var copy = (GameObject)UnityEngine.Object.Instantiate(source.gameObject);
            copy.name = "Char";
            LeaveOutWhatWasHungOn(copy);
            Strip(copy);

            // Where the person's own Char hangs from their pivot, which is at their feet: 0.82 m up, turned so that
            // the body looks along the pivot's +Z.
            Transform body = copy.transform;
            body.parent = root.transform;
            body.localPosition = source.localPosition;
            body.localRotation = source.localRotation;
            body.localScale = source.lossyScale;
            if (layer >= 0)
            {
                SetLayer(root.transform, layer);
            }

            Animation legs = FindAnimation(copy, "walk", "standing");
            string walk = ClipEnding(legs, "walk");
            string stand = ClipEnding(legs, "standing");

            // The mod's own clips and the borrowed sitting play on an Animation above the copy.
            Animation own = root.AddComponent<Animation>();
            own.playAutomatically = false;
            string sit;
            AnimationClip borrowed = Characters.SeatClip(look.Woman, out sit);
            if (borrowed != null)
            {
                own.AddClip(borrowed, sit);
            }

            copy.SetActive(true);
            var model = new CharacterModel(root, body, legs, own, walk, stand, sit, path, look.Index, settings);
            root.SetActive(false);
            return model;
        }

        /// <summary>What it shows of its parts, for the log.</summary>
        public string Describe()
        {
            return "from '" + found + "' (body at " + Format(body.localPosition) + "), walks with '" + (walk ?? "nothing") + "', stands with '" + (stand ?? "nothing")
                + "', sits with '" + (sit ?? "nothing") + "', head " + (head == null ? "NOT FOUND" : "'" + head.parent.name + "/" + head.name + "'")
                + ", " + extras.Length + " other animation(s), "
                + (rig != null ? "own animations made (hips " + rig.HipHeight.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m)" : "NO own animations (" + rigProblem + " not found)");
        }

        /// <summary>
        /// Stands it where the player's feet are, moving, standing or sitting as the pose says. In a vehicle,
        /// <paramref name="seatYaw"/> is where the vehicle faces: the body faces it too and only the head turns.
        /// NaN on foot. <paramref name="cockpit"/>: what the player works with at the wheel, or null if they are not at
        /// one.
        /// </summary>
        public void Place(PlayerPose pose, Vector3 offset, float deltaTime, float seatYaw, Cockpit cockpit)
        {
            if (driver != null)
            {
                driver.Straighten();
            }

            Transform transform = root.transform;
            var feet = new Vector3(pose.X, pose.Y, pose.Z) + offset;
            if (placed && deltaTime > 0f)
            {
                Vector3 moved = feet - lastFeet;
                moved.y = 0f;
                float now = moved.magnitude / deltaTime;
                float blend = Mathf.Clamp01(deltaTime * 8f);
                speed = Mathf.Lerp(speed, Mathf.Min(now, 12f), blend);
                turning = Mathf.Lerp(turning, Mathf.Min(Mathf.Abs(Mathf.DeltaAngle(lastYaw, pose.Yaw)) / deltaTime, 720f), blend);
            }

            lastFeet = feet;
            lastYaw = pose.Yaw;
            placed = true;
            if (!root.activeSelf)
            {
                root.SetActive(true);
            }

            if (settings.Serial != builtSerial)
            {
                BuildOwn();
            }

            bool inVehicle = !float.IsNaN(seatYaw);
            PoseKind kind = Forced ?? Choose(pose, inVehicle);
            bool forced = Forced != null;
            switch (kind)
            {
                case PoseKind.Sit:
                    Play(own, sit, 1f);
                    break;
                case PoseKind.Crouch:
                    Play(own, PoseClips.Crouch, 1f);
                    break;
                case PoseKind.CrouchWalk:
                    Play(own, PoseClips.CrouchWalk, forced ? 1f : Mathf.Clamp(speed / settings[PoseSettings.CrouchWalkSpeed], 0.4f, 2.5f));
                    break;
                case PoseKind.Prone:
                    Play(own, PoseClips.Prone, 1f);
                    break;
                case PoseKind.Run:
                    Play(own, PoseClips.Run, forced ? 1f : Mathf.Clamp(speed / settings[PoseSettings.RunSpeed], 0.6f, 2f));
                    break;
                case PoseKind.Walk:
                    // Not moving, it is stepping round on the spot: slower.
                    float rate = forced ? 1f : (speed > WalkFrom ? Mathf.Clamp(speed / WalkSpeed, 0.6f, MostWalkRate) : TurnStepRate);
                    Play(legs, walk, rate);
                    break;
                default:
                    Play(legs, stand, 1f);
                    break;
            }

            float face = pose.Yaw;
            float turn = 0f;
            if (inVehicle)
            {
                face = seatYaw;
                turn = Mathf.Clamp(Mathf.DeltaAngle(seatYaw, pose.Yaw), -HeadTurn, HeadTurn);
            }

            // The body looks along Char's +X. Measured, not assumed: standing, Char is turned so that is the root's
            // +Z; a sitting clip turns it to face the root's +X, lying down to face the floor.
            Vector3 ahead = body.localRotation * Vector3.right;
            ahead.y = 0f;
            float bodyYaw = ahead.sqrMagnitude > 1e-4f ? Mathf.Atan2(ahead.x, ahead.z) * Mathf.Rad2Deg : 0f;
            if (kind == PoseKind.Prone || kind == PoseKind.Crouch || kind == PoseKind.CrouchWalk || kind == PoseKind.Run)
            {
                // The mod's own clips keep the root's +Z ahead.
                bodyYaw = 0f;
            }

            transform.rotation = Quaternion.Euler(0f, face - bodyYaw, 0f);
            transform.position = feet;
            bool alignEyes = kind == PoseKind.Sit || kind == PoseKind.Prone || (inVehicle && kind == PoseKind.Crouch);
            if (alignEyes && (head != null || pelvis != null))
            {
                // The eyes where the player's are; seated, never the hips under the floor.
                float eyesUp = kind == PoseKind.Sit ? Mathf.Max(pose.EyeHeight, LowestEyes) : pose.EyeHeight;
                Vector3 eyes = feet + (Vector3.up * eyesUp);
                Vector3 at = head != null
                    ? head.position + (Quaternion.Euler(0f, face, 0f) * Vector3.forward * EyesAhead) + (Vector3.up * EyesUp)
                    : pelvis.position + (Vector3.up * EyesAbovePelvis);
                transform.position = feet + (eyes - at);
            }

            if (head != null)
            {
                // Aimed where the player looks, as the game aims its people's heads; then set straight as tuned.
                float pitch = Mathf.Clamp(pose.Pitch, -HeadPitch, HeadPitch);
                Quaternion look = Quaternion.Euler(-pitch, face + turn, 0f);
                head.rotation = look * headTune * HeadFromFace;
                headLook = look;
            }

            // Seated, the hands and the feet go to the controls; on its feet the clips have them.
            if (driver != null)
            {
                if (kind == PoseKind.Sit && !forced)
                {
                    driver.Apply(cockpit, pose.Controls, pose.Driving && cockpit != null, deltaTime);
                }
                else
                {
                    driver.Reset();
                }
            }

            // Drinking, smoking, peeing: over whatever the body does, the hands at the wheel too.
            if (activity != null)
            {
                if (forced)
                {
                    activity.Reset();
                }
                else
                {
                    activity.Apply(pose, face, deltaTime);
                }
            }
        }

        /// <summary>Shows the middle finger for as long as the game holds it up.</summary>
        public void ShowFinger()
        {
            PlayerActions actions = PlayerActions.Current;
            if (activity != null && actions != null)
            {
                activity.ShowFinger(actions.FingerSeconds);
            }
        }

        public void Hide()
        {
            if (root != null && root.activeSelf)
            {
                root.SetActive(false);
            }

            // Shown again, an Animation plays its own default clip (fat_walk for the walkers): choose again.
            if (driver != null)
            {
                driver.Reset();
            }

            if (activity != null)
            {
                activity.Reset();
            }

            placed = false;
            speed = 0f;
            turning = 0f;
            playing = null;
        }

        public void Destroy()
        {
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
            }

            for (int i = 0; i < ownClips.Count; i++)
            {
                if (ownClips[i] != null)
                {
                    UnityEngine.Object.Destroy(ownClips[i]);
                }
            }

            ownClips.Clear();
        }

        // What the pose shows. In a vehicle the movement is the vehicle's: the body stands or sits.
        private PoseKind Choose(PlayerPose pose, bool inVehicle)
        {
            bool lowered = pose.EyeHeight < LoweredEyes;
            bool mine = rig != null;
            if (inVehicle)
            {
                return !lowered ? PoseKind.Stand : (sit != null ? PoseKind.Sit : (mine ? PoseKind.Crouch : PoseKind.Stand));
            }

            if (mine && pose.EyeHeight < ProneBelow)
            {
                return PoseKind.Prone;
            }

            if (lowered)
            {
                return mine ? (speed > CrouchMovesFrom ? PoseKind.CrouchWalk : PoseKind.Crouch) : (sit != null ? PoseKind.Sit : PoseKind.Stand);
            }

            if (mine && speed > settings[PoseSettings.RunFrom])
            {
                return PoseKind.Run;
            }

            if (speed > WalkFrom)
            {
                return PoseKind.Walk;
            }

            if (turning > settings[PoseSettings.TurnFrom] && walk != null)
            {
                // Steps round on the spot, with the walk slowed down.
                return PoseKind.Walk;
            }

            return PoseKind.Stand;
        }

        // The mod's own clips and the head's tune, made again whenever the values they come from change.
        private void BuildOwn()
        {
            builtSerial = settings.Serial;
            headTune = Quaternion.Euler(settings.Head(lookIndex, 1), settings.Head(lookIndex, 0), settings.Head(lookIndex, 2));
            if (rig == null)
            {
                return;
            }

            for (int i = 0; i < ownClips.Count; i++)
            {
                if (ownClips[i] != null)
                {
                    own.RemoveClip(ownClips[i].name);
                    UnityEngine.Object.Destroy(ownClips[i]);
                }
            }

            ownClips.Clear();
            if (playing != null && playing != walk && playing != stand && playing != sit)
            {
                own.Stop();
                playing = null;
            }

            Dictionary<string, AnimationClip> made = PoseClips.Make(root.transform, rig, settings, RestoreRest);
            foreach (KeyValuePair<string, AnimationClip> clip in made)
            {
                own.AddClip(clip.Value, clip.Key);
                ownClips.Add(clip.Value);
            }
        }

        // One clip at a time over the body: the mod's own and the borrowed sitting stop the legs, and the other way
        // round. Back on its feet, every bone goes back to where it was first: those clips moved Char itself and the
        // arms, and the clips of the legs never put those back.
        private void Play(Animation on, string clip, float rate)
        {
            if (on == null || clip == null)
            {
                return;
            }

            if (playing != clip)
            {
                bool mine = on == own;
                if (mine)
                {
                    if (legs != null)
                    {
                        legs.Stop();
                    }
                }
                else
                {
                    own.Stop();
                    if (playing != walk && playing != stand)
                    {
                        RestoreRest();
                    }
                }

                // The hands and the breathing would fight these clips for the shoulders.
                for (int i = 0; i < extras.Length; i++)
                {
                    extras[i].enabled = !mine;
                }

                AnimationState state = on[clip];
                if (state != null)
                {
                    state.wrapMode = mine && clip == sit && sitHolds ? WrapMode.ClampForever : WrapMode.Loop;
                }

                on.CrossFade(clip, Blend);
                playing = clip;
            }

            AnimationState current = on[clip];
            if (current != null)
            {
                current.speed = rate;
            }
        }

        /// <summary>How long its standing clip lasts; 0 if it has none.</summary>
        public float StandLength
        {
            get
            {
                AnimationState state = legs == null || stand == null ? null : legs[stand];
                return state == null ? 0f : state.length;
            }
        }

        /// <summary>
        /// Its bones as standing at <paramref name="time"/>: the pose it was made in, then the standing clip and what plays
        /// by itself on it (breathing, the hands) sampled there. For the looks kept for the main menu.
        /// </summary>
        public void SampleStanding(float time)
        {
            RestoreRest();
            SampleClip(legs, stand, time);
            for (int i = 0; i < extras.Length; i++)
            {
                SampleClip(extras[i], extras[i] != null && extras[i].clip != null ? extras[i].clip.name : null, time);
            }
        }

        private static void SampleClip(Animation animation, string clip, float time)
        {
            AnimationState state = animation == null || clip == null ? null : animation[clip];
            if (state == null)
            {
                return;
            }

            bool wasEnabled = state.enabled;
            float wasWeight = state.weight;
            float wasTime = state.time;
            state.enabled = true;
            state.weight = 1f;
            state.time = state.length > 0f ? time % state.length : 0f;
            animation.Sample();
            state.enabled = wasEnabled;
            state.weight = wasWeight;
            state.time = wasTime;
        }

        private void RestoreRest()
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null)
                {
                    bones[i].localPosition = restPositions[i];
                    bones[i].localRotation = restRotations[i];
                }
            }
        }

        // The head hangs from the upper spine directly, or under a "HeadPivot", "HeadPivot 3" or "PhysHead".
        private static Transform FindHead(Transform body)
        {
            Transform upper = body.Find(SpineUpperPath);
            if (upper == null)
            {
                return null;
            }

            for (int i = 0; i < upper.childCount; i++)
            {
                Transform child = upper.GetChild(i);
                if (child.name == "head")
                {
                    return child;
                }

                Transform below = child.Find("head");
                if (below != null)
                {
                    return below;
                }
            }

            return null;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ", "
                + value.y.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ", "
                + value.z.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        // What the game hangs on a person while it runs is a copy of something too (the fighter's bottle, "booze(Clone)"):
        // the person is copied without it.
        private static void LeaveOutWhatWasHungOn(GameObject copy)
        {
            Transform[] all = copy.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i] != copy.transform && all[i].name.EndsWith("(Clone)", StringComparison.Ordinal))
                {
                    UnityEngine.Object.DestroyImmediate(all[i].gameObject);
                }
            }
        }

        // Nothing of the person's own life: no state machines, no colliders, no bodies, no joints, no sounds.
        private static void Strip(GameObject copy)
        {
            var doomed = new List<Component>();
            Component[] components = copy.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component is Transform || component is Animation || component is MeshRenderer || component is SkinnedMeshRenderer
                    || component is MeshFilter || component is LODGroup)
                {
                    continue;
                }

                doomed.Add(component);
            }

            // What depends on something before it: joints before the bodies they hang from, a smoke's renderer and
            // animator before its emitter.
            doomed.Sort(delegate(Component a, Component b) { return Rank(a).CompareTo(Rank(b)); });
            for (int i = 0; i < doomed.Count; i++)
            {
                UnityEngine.Object.DestroyImmediate(doomed[i]);
            }
        }

        private static int Rank(Component component)
        {
            if (component is Joint)
            {
                return 0;
            }

            return component is Rigidbody || component.GetType().Name.EndsWith("ParticleEmitter", StringComparison.Ordinal) ? 2 : 1;
        }

        private static void SetLayer(Transform at, int layer)
        {
            at.gameObject.layer = layer;
            for (int i = 0; i < at.childCount; i++)
            {
                SetLayer(at.GetChild(i), layer);
            }
        }

        // The Animation that has both a walking and a standing clip.
        private static Animation FindAnimation(GameObject copy, string first, string second)
        {
            Animation[] animations = copy.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < animations.Length; i++)
            {
                if (ClipEnding(animations[i], first) != null && ClipEnding(animations[i], second) != null)
                {
                    return animations[i];
                }
            }

            return null;
        }

        private static string ClipEnding(Animation animation, string ending)
        {
            if (animation == null)
            {
                return null;
            }

            foreach (AnimationState state in animation)
            {
                string name = state.name;
                bool fits = ending == "walk"
                    ? (name.EndsWith("_walk", StringComparison.Ordinal) || name.EndsWith("_walk_legs", StringComparison.Ordinal)) && name.IndexOf("hands", StringComparison.Ordinal) < 0
                    : name.EndsWith("_" + ending, StringComparison.Ordinal);
                if (fits)
                {
                    return name;
                }
            }

            return null;
        }
    }

    /// <summary>The people of the game a player can look like, in an order that is the same in every build.</summary>
    internal static class Characters
    {
        private static readonly Seat[] MenSeats =
        {
            new Seat("KILJUGUY/HikerPivot/JokkeHiker2", "fat_get_car_idle1"),
            new Seat("JOBS/HouseDrunk/Moving/JokkeHiker1/Pivot", "fat_get_car_idle1"),
        };

        private static readonly Seat[] WomenSeats =
        {
            new Seat("JOBS/Suski/SuskiPivot/SuskiHiker", "woman_get_car_in"),
            new Seat("ChurchGrandma/GrannyHiker", "granny_get_car_in"),
            new Seat("JOBS/Mummola/ChurchGrandma/GrannyHiker", "granny_get_car_in"),
        };

        // Found once in a world: the people they come from may be gone later in their story.
        private static AnimationClip manSits;
        private static string manSitsName;
        private static AnimationClip womanSits;
        private static string womanSitsName;

        /// <summary>Every look there is. Its place in this list is what goes over the network: never reorder it, only add at the end.</summary>
        public static readonly Look[] All =
        {
            new Look(0, "Kale", false, "HUMANS/Randomizer/Walkers/Kale/Pivot/Char"),
            new Look(1, "Alpo", false, "HUMANS/Randomizer/Walkers/Alpo/Pivot/Char"),
            new Look(2, "Julli", false, "HUMANS/Randomizer/Walkers/Julli/Pivot/Char"),
            new Look(3, "Kristian", false, "HUMANS/Randomizer/Walkers/Kristian/Pivot/Char"),
            new Look(4, "Rauno", false, "HUMANS/Randomizer/Walkers/Rauno/Pivot/Char"),
            new Look(5, "Unto", false, "HUMANS/Randomizer/Walkers/Unto/Pivot/Char"),
            new Look(6, "Jokke", false, "KILJUGUY/HikerPivot/JokkeHiker2/Char", "JOBS/HouseDrunk/Moving/JokkeHiker1/Pivot/Char"),
            new Look(7, "El tío", "Uncle", false, "YARD/UNCLE/UncleWalking/Uncle/Char"),

            // The game hangs the farmer from HUMANS when the world loads (his Walker's machine "Logic", state "Init").
            new Look(8, "El granjero", "Farmer", false, "HUMANS/Farmer/Walker/Char", "JOBS/Farm/Farmer/Walker/Char"),

            // Gone from a world where her story has ended.
            new Look(9, "Suski", true, "JOBS/Suski/SuskiPivot/SuskiHiker/Char"),
            // The pub's is not always there; the dance hall's is the same man.
            new Look(10, "El peleador", "Fighter", false, "HUMANS/FighterPub/Fighter2/Pivot/Char", "DANCEHALL/Functions/FIGHTER/Fighter/Pivot/Char"),

            // The bus is made under BusSpawnLoppe when the world loads.
            new Look(11, "Latanen", false, "NPC_CARS/BusSpawnLoppe/BUS/Latanen/Pivot/Char", "NPC_CARS/BUS/Latanen/Pivot/Char"),
            new Look(12, "La abuela", "Grandma", true, "ChurchGrandma/GrannyHiker/Char", "JOBS/Mummola/ChurchGrandma/GrannyHiker/Char"),
        };

        /// <summary>The first of a look's places that this world has, or null.</summary>
        public static Transform Find(Look look, out string path)
        {
            for (int i = 0; i < look.Paths.Length; i++)
            {
                Transform found = Find(look.Paths[i]);
                if (found != null)
                {
                    path = look.Paths[i];
                    return found;
                }
            }

            path = null;
            return null;
        }

        /// <summary>A path from a root object, found even where the game has switched something on the way off.</summary>
        public static Transform Find(string path)
        {
            int slash = path.IndexOf('/');
            GameObject root = GameObject.Find("/" + (slash < 0 ? path : path.Substring(0, slash)));
            if (root == null)
            {
                return null;
            }

            return slash < 0 ? root.transform : root.transform.Find(path.Substring(slash + 1));
        }

        /// <summary>Sitting in a car, borrowed from a hitchhiker who is in this world: Jokke for a man, Suski or the grandmother for a woman.</summary>
        public static AnimationClip SeatClip(bool woman, out string clipName)
        {
            // Unity's null check is also true for a clip that went with its world.
            if (woman ? womanSits != null : manSits != null)
            {
                clipName = woman ? womanSitsName : manSitsName;
                return woman ? womanSits : manSits;
            }

            Seat[] seats = woman ? WomenSeats : MenSeats;
            for (int i = 0; i < seats.Length; i++)
            {
                Transform hiker = Find(seats[i].Path);
                Animation animation = hiker == null ? null : hiker.GetComponent<Animation>();
                AnimationClip clip = animation == null ? null : animation.GetClip(seats[i].Clip);
                if (clip != null)
                {
                    if (woman)
                    {
                        womanSits = clip;
                        womanSitsName = seats[i].Clip;
                    }
                    else
                    {
                        manSits = clip;
                        manSitsName = seats[i].Clip;
                    }

                    clipName = seats[i].Clip;
                    return clip;
                }
            }

            clipName = null;
            return null;
        }

        public static Look At(int index)
        {
            return index >= 0 && index < All.Length ? All[index] : null;
        }

        internal sealed class Look
        {
            public Look(int index, string name, bool woman, params string[] paths)
                : this(index, name, null, woman, paths)
            {
            }

            public Look(int index, string name, string english, bool woman, params string[] paths)
            {
                Index = index;
                Name = name;
                English = english;
                Woman = woman;
                Paths = paths;
            }

            /// <summary>Its place in <see cref="All"/>.</summary>
            public int Index { get; private set; }

            /// <summary>Also what is kept of it: character.txt and the looks' cache go by this name.</summary>
            public string Name { get; private set; }

            /// <summary>The name in English, where it is not <see cref="Name"/>.</summary>
            public string English { get; private set; }

            /// <summary>The name the player reads.</summary>
            public string Shown
            {
                get { return English == null ? Name : Texts.Pick(English, Name); }
            }

            public bool Woman { get; private set; }

            /// <summary>Where the person may be, in order: where the game puts them when the world loads, then where the level file has them.</summary>
            public string[] Paths { get; private set; }

            /// <summary>Every place tried, for the log.</summary>
            public string Where
            {
                get { return "'" + string.Join("', '", Paths) + "'"; }
            }
        }

        private struct Seat
        {
            public Seat(string path, string clip)
            {
                Path = path;
                Clip = clip;
            }

            public string Path;
            public string Clip;
        }
    }
}
