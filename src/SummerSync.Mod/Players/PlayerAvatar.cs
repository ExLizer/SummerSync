using System.Globalization;
using SummerSync.Core.Players;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// How another player looks: one of the game's people (<see cref="CharacterModel"/>) when they have chosen
    /// one and this game has it, or else a capsule as tall as they stand, with a dark visor where the eyes are,
    /// turned the way they look. It has no colliders: it is seen, never touched, so the game's physics and the
    /// raycasts of the player's hands go straight through it.
    /// </summary>
    internal sealed class PlayerAvatar
    {
        private const float Radius = 0.25f;
        private const float AboveEyes = 0.12f;
        private const float LabelAboveHead = 0.25f;

        // Where the middle of a worn helmet goes: from a person's head bone, along where the head looks (the eyes are 0.09 m
        // ahead of it and 0.06 up, CharacterModel), or from the capsule's eyes. Looked at with two copies (2026-11-13).
        private static readonly Vector3 HelmetOnHead = new Vector3(0f, 0.14f, 0.06f);
        private static readonly Vector3 HelmetOnEyes = new Vector3(0f, 0.08f, -0.03f);

        // The game's helmet has its top along its +Z (the neck opening along -Z) and its visor, a peak, along its -Y: turned
        // a quarter about X, its top goes up and its visor ahead.
        private static readonly Quaternion HelmetTurn = Quaternion.Euler(-90f, 0f, 0f);

        // The game keeps each recording on an object of its own under its sound player. A voice is heard whole up close
        // and fades out with the distance.
        private const string VoiceRoot = "MasterAudio/";
        private const float VoiceNear = 2f;
        private const float VoiceFar = 40f;

        private static readonly Color VisorColor = new Color(0.08f, 0.08f, 0.08f);

        private readonly GameObject root;
        private readonly Transform body;
        private readonly Transform head;
        private GameObject push;
        private Rigidbody pushBody;
        private SphereCollider pushBall;
        private float height;
        private string labelName;
        private int labelMetres;
        private string labelText;
        private CharacterModel model;
        private int look = int.MinValue;
        private AudioSource voice;

        public PlayerAvatar(Transform parent, string objectName, Shader shader, Color color)
        {
            root = new GameObject(objectName);
            root.transform.parent = parent;

            body = CreatePart(PrimitiveType.Capsule, "Body", root.transform, shader, color);

            // The head turns up and down around the eyes; the visor sits on the front of it.
            head = new GameObject("Head").transform;
            head.parent = root.transform;
            Transform visor = CreatePart(PrimitiveType.Cube, "Visor", head, shader, VisorColor);
            visor.localPosition = new Vector3(0f, 0f, Radius * 0.85f);
            visor.localScale = new Vector3(Radius * 1.5f, 0.09f, Radius * 0.6f);

            root.SetActive(false);
        }

        /// <summary>False once Unity has destroyed the objects, e.g. when MSCLoader cleared the scene.</summary>
        public bool IsAlive
        {
            get { return root != null; }
        }

        public bool IsShown
        {
            get { return root != null && root.activeSelf; }
        }

        /// <summary>The player wears a helmet, as their last pose says.</summary>
        public bool Helmeted { get; private set; }

        /// <summary>
        /// Where the middle of a helmet this player wears goes, and how it is turned: on the head
        /// of the person it looks like, which its clips move, or on the capsule's. False while it is not shown.
        /// </summary>
        public bool TryHelmetPlace(out Vector3 at, out Quaternion turn)
        {
            at = Vector3.zero;
            turn = Quaternion.identity;
            if (!IsShown)
            {
                return false;
            }

            Vector3 headAt;
            Quaternion facing;
            if (model != null && model.IsAlive && model.TryHead(out headAt, out facing))
            {
                at = headAt + (facing * HelmetOnHead);
                turn = facing * HelmetTurn;
                return true;
            }

            at = head.position + (head.rotation * HelmetOnEyes);
            turn = head.rotation * HelmetTurn;
            return true;
        }

        /// <summary>Whether the avatar has hands out that push.</summary>
        public bool Pushing
        {
            // Unity's null check is also true for an object that has been destroyed.
            get { return push != null && push.activeSelf; }
        }

        /// <summary>The ball those hands push with, or null if this player has never pushed.</summary>
        public SphereCollider PushBall
        {
            get { return pushBall; }
        }

        /// <summary>
        /// Holds out, or puts away, hands like the game's own in front of the avatar (see
        /// <see cref="PushHands"/>). They hang from its head, so they are where the player looks, and they are
        /// the one part of an avatar that touches anything. True if that changed something. Call it after
        /// <see cref="Show"/>.
        /// </summary>
        public bool SetPushing(bool pushing)
        {
            if (pushing == Pushing)
            {
                return false;
            }

            if (!pushing)
            {
                push.SetActive(false);
                return true;
            }

            if (push == null)
            {
                push = PushHands.Build(head, out pushBody, out pushBall);
            }

            // Back where the hands rest and standing still: wherever it was when it was last put away.
            pushBody.transform.localPosition = Vector3.zero;
            pushBody.transform.localRotation = Quaternion.identity;
            push.SetActive(true);
            if (pushBody.gameObject.activeInHierarchy)
            {
                pushBody.velocity = Vector3.zero;
                pushBody.angularVelocity = Vector3.zero;
            }

            return true;
        }

        /// <summary>Where the name goes: just above the head.</summary>
        public Vector3 LabelPoint
        {
            get { return model != null && model.IsAlive ? model.Top + (Vector3.up * LabelAboveHead) : root.transform.position + (Vector3.up * (height + LabelAboveHead)); }
        }

        /// <summary>Whether it is at the wheel with hands and feet settled on the controls.</summary>
        public bool AtTheWheel
        {
            get { return model != null && model.IsAlive && model.AtTheWheel; }
        }

        /// <summary>At the wheel with hands and feet on the controls: how near they are, for the log. Null otherwise.</summary>
        public string DriverReport
        {
            get { return model != null && model.IsAlive ? model.DriverReport : null; }
        }

        /// <summary>Which of <see cref="Characters.All"/> it looks like; -1 a capsule; int.MinValue not yet decided.</summary>
        public int Look
        {
            get { return look; }
        }

        /// <summary>Looks like <paramref name="person"/> from now on, or like a capsule if it is null.</summary>
        public void SetModel(CharacterModel person, int which)
        {
            if (model != null)
            {
                model.Destroy();
            }

            model = person;
            look = which;
            bool capsule = model == null;
            body.gameObject.SetActive(capsule);
            Renderer[] visor = head.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < visor.Length; i++)
            {
                // The hands that push hang from the head too, and have no renderer.
                visor[i].enabled = capsule;
            }
        }

        /// <summary>
        /// "Name  12 m". The text is only rebuilt when the name or the whole metres change: it is asked for on
        /// every frame.
        /// </summary>
        public string Label(string name, float distance)
        {
            int metres = Mathf.RoundToInt(distance);
            if (labelText == null || metres != labelMetres || !ReferenceEquals(name, labelName))
            {
                labelName = name;
                labelMetres = metres;
                labelText = name + "  " + metres.ToString(CultureInfo.InvariantCulture) + " m";
            }

            return labelText;
        }

        /// <summary>
        /// Where the pose says. <paramref name="seatYaw"/>: where the vehicle the player rides faces, or NaN on foot.
        /// <paramref name="cockpit"/>: what they work with at the wheel, or null if they do not drive.
        /// </summary>
        public void Show(PlayerPose pose, Vector3 offset, float seatYaw, Cockpit cockpit)
        {
            // Never flatter than a ball: lying on the floor the eyes are 30 cm up.
            height = Mathf.Max(pose.EyeHeight + AboveEyes, Radius * 2f);
            Helmeted = pose.Helmet;
            Transform transform = root.transform;
            transform.position = new Vector3(pose.X, pose.Y, pose.Z) + offset;
            transform.rotation = Quaternion.Euler(0f, pose.Yaw, 0f);

            // Unity's capsule is 2 units tall and 1 wide.
            body.localPosition = new Vector3(0f, height * 0.5f, 0f);
            body.localScale = new Vector3(Radius * 2f, height * 0.5f, Radius * 2f);

            // Unity turns a positive X angle downwards; a positive pitch looks up.
            head.localPosition = new Vector3(0f, Mathf.Max(pose.EyeHeight, Radius), 0f);
            head.localRotation = Quaternion.Euler(-pose.Pitch, 0f, 0f);

            if (!root.activeSelf)
            {
                root.SetActive(true);
            }

            if (model != null && model.IsAlive)
            {
                model.Place(pose, offset, Time.deltaTime, seatYaw, cockpit);
            }
        }

        /// <summary>
        /// The player says that (<see cref="SpeechSync"/>): the game's own recording, from where their head is, and the
        /// middle finger held up if it goes with it. Returns what could not be done, or null.
        /// </summary>
        public string Say(int kind, int variation)
        {
            string group;
            float volume;
            int variations;
            if (!SpeechSync.Describe(kind, out group, out volume, out variations) || !IsShown)
            {
                return null;
            }

            if (kind == SpeechSync.Finger && model != null && model.IsAlive)
            {
                model.ShowFinger();
            }

            string name = variation.ToString(CultureInfo.InvariantCulture);
            GameObject kept = GameObject.Find(VoiceRoot + group + "/" + name);
            AudioSource recorded = kept == null ? null : kept.GetComponent<AudioSource>();
            if (recorded == null || recorded.clip == null)
            {
                // Not kept as expected: the game's own player for it, wherever it makes it heard.
                MasterAudio.PlaySound3DFollowTransformAndForget(group, head, volume, 1f, 0f, name);
                return "the recording was not found under '" + VoiceRoot + group + "/" + name + "'; the game's sound player was asked for it.";
            }

            if (voice == null)
            {
                var made = new GameObject("Voice");
                made.transform.parent = head;
                made.transform.localPosition = Vector3.zero;
                voice = made.AddComponent<AudioSource>();
                voice.playOnAwake = false;
                voice.loop = false;
                voice.spatialBlend = 1f;
                voice.dopplerLevel = 0f;
                voice.rolloffMode = AudioRolloffMode.Linear;
                voice.minDistance = VoiceNear;
                voice.maxDistance = VoiceFar;
            }

            voice.Stop();
            voice.clip = recorded.clip;
            voice.outputAudioMixerGroup = recorded.outputAudioMixerGroup;
            voice.volume = volume;
            voice.Play();
            return null;
        }

        public void Hide()
        {
            if (root != null && root.activeSelf)
            {
                root.SetActive(false);
            }

            if (model != null && model.IsAlive)
            {
                model.Hide();
            }
        }

        public void Destroy()
        {
            if (root != null)
            {
                Object.Destroy(root);
            }

            if (model != null)
            {
                model.Destroy();
            }
        }

        private static Transform CreatePart(PrimitiveType shape, string name, Transform parent, Shader shader, Color color)
        {
            GameObject part = GameObject.CreatePrimitive(shape);
            part.name = name;
            part.transform.parent = parent;
            part.transform.localPosition = Vector3.zero;
            part.transform.localRotation = Quaternion.identity;

            Collider collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                // Disabled first: Destroy only takes effect at the end of the frame.
                collider.enabled = false;
                Object.Destroy(collider);
            }

            // Without a usable shader the part keeps whatever material Unity gave it, if any.
            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null)
            {
                if (shader != null)
                {
                    renderer.material = new Material(shader);
                }

                Material material = renderer.material;
                if (material != null)
                {
                    material.color = color;
                }
            }

            return part.transform;
        }
    }
}
