using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Vehicles;
using SummerSync.Unity.Players;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// Cigarette smoke that gathers in a vehicle's cabin while someone smokes in it with the doors shut, makes it hard to
    /// see for whoever is inside, and goes out through a door that opens.
    ///
    /// Nothing of it is sent: each machine works it out from what it already has (<see cref="CabinAir"/>): who smokes and
    /// where (<see cref="PlayerPresenter.Smokers"/>: this player's breath from the game's machine "Start" of "Smoking", the
    /// others' from their pose), and which doors are open (the boolean "Open" of the machine "Use" of each door the
    /// vehicle's data file lists in "cabinVents", which every game has as the session has it; a door that is not there is a
    /// hole). The smoke is drawn with a copy of the game's own breath of smoke ("Smoking/BreathSmoke", a legacy
    /// <see cref="ParticleEmitter"/>): a haze in the cabin that goes along with the vehicle, and what goes out of a door, left
    /// in the world. Whoever is inside sees it through a veil in front of their eyes, as thick as the cabin's smoke. Only in a
    /// session, as everything of the mod.
    /// </summary>
    internal sealed class CabinSmoke
    {
        private const string VentMachine = "Use";
        private const string VentOpen = "Open";

        // Where the copies of the smoke are drawn: the world's own layer. The game's breath is on the player's layer (20), which
        // showed the cabin's smoke through the body of the van.
        private const int WorldLayer = 0;
        private const double FindVentsEvery = 2.0;
        private const double FlowEvery = 0.2;

        // Nothing is drawn for a cabin further than this from the eyes, or with less smoke than this.
        private const float DrawnWithin = 80f;
        private const float DrawnFrom = 0.01f;

        // The haze: particles a second with the cabin full, how long each lasts, how big; what goes out of a door, per unit
        // of smoke; how fast it leaves.
        private const float HazePerSecond = 14f;
        private const float HazeLife = 6f;
        private const float HazeSizeMin = 0.35f;
        private const float HazeSizeMax = 0.6f;

        // How far in from the box's sides and ends the middle of a puff is made: a puff that big must not stick out of the body.
        private const float HazeInset = 0.3f;
        private const float OutPerLevel = 260f;
        private const float OutLife = 4.5f;
        private const float OutSpeed = 0.35f;
        private const float OutRise = 0.15f;
        private const float OutGrow = 0.25f;

        // The haze is made from the height of a seat to a little under the roof, which is looked for straight above the middle of
        // the cabin's box (the box is measured to the players' feet, its top is not the roof); without a roof found, this far
        // above the top of the box. The middle of a puff stays this far under the roof.
        private const float HazeAboveBox = 0.45f;
        private const float UnderRoof = 0.25f;
        private const float RoofSearch = 3f;

        // The veil: how see-through it is with the cabin full, and its grey. The camera counts as inside a little above the
        // box, where the eyes are.
        private const float VeilMost = 0.6f;
        private const float VeilGrey = 0.7f;
        private const float EyesAboveBox = 0.7f;

        // Over each puff's life (the copies' animator gives them these, over whatever they are made with): the haze fades in,
        // stays and fades out; what goes out of a door, a little darker, thins out as it goes. The particles' shader doubles
        // these, and the game's material of the breath takes them down again with its own tint.
        private static readonly Color[] HazeColours =
        {
            new Color(0.45f, 0.45f, 0.45f, 0f), new Color(0.45f, 0.45f, 0.45f, 0.45f), new Color(0.45f, 0.45f, 0.45f, 0.5f),
            new Color(0.45f, 0.45f, 0.45f, 0.45f), new Color(0.45f, 0.45f, 0.45f, 0f),
        };

        private static readonly Color[] OutColours =
        {
            new Color(0.42f, 0.42f, 0.42f, 0.55f), new Color(0.42f, 0.42f, 0.42f, 0.42f), new Color(0.42f, 0.42f, 0.42f, 0.28f),
            new Color(0.42f, 0.42f, 0.42f, 0.13f), new Color(0.42f, 0.42f, 0.42f, 0f),
        };

        // When a cabin's smoke is worth a line in the log, and when it is not any more.
        private const double SaidFrom = 0.25;
        private const double SaidUntil = 0.05;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly PlayerPresenter players;
        private readonly List<KeyValuePair<Transform, VehicleDefinition>> listed = new List<KeyValuePair<Transform, VehicleDefinition>>();
        private readonly Dictionary<Transform, Cabin> cabins = new Dictionary<Transform, Cabin>();
        private bool active;
        private double lastNow;
        private bool lookSaid;
        private bool noLookSaid;
        private GameObject veil;
        private Material veilMaterial;
        private string veilColorName;

        public CabinSmoke(Logger log, NetworkManager manager, VehiclePresenter vehicles, PlayerPresenter players)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
            this.players = players;
        }

        /// <summary>Once per frame, after the players: <paramref name="localPlayer"/> is null outside the world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            NetSession session = manager.Session;
            if (localPlayer == null || session == null || session.State != SessionState.Connected)
            {
                if (active)
                {
                    Release();
                }

                return;
            }

            float seconds = active ? Mathf.Clamp((float)(now - lastNow), 0f, 0.5f) : 0f;
            active = true;
            lastNow = now;
            vehicles.ListCabins(listed);
            IList<Smoker> smokers = players.Smokers;
            Camera camera = Camera.main;
            Vector3 eyes = camera == null ? localPlayer.position : camera.transform.position;
            Cabin around = null;
            for (int i = 0; i < listed.Count; i++)
            {
                Cabin cabin = CabinOf(listed[i].Key, listed[i].Value);
                int breathing = 0;
                for (int s = 0; s < smokers.Count; s++)
                {
                    if (smokers[s].Breathing && Inside(cabin, smokers[s].Position, 0f, 0f))
                    {
                        breathing++;
                    }
                }

                int open = OpenVents(cabin, now);
                cabin.Breathing = breathing;
                cabin.OpenNow = open;
                cabin.Air.Step(seconds, breathing, open);
                Tell(cabin, breathing, open);
                Draw(cabin, seconds, now, eyes, open);
                if (around == null && camera != null && cabin.Air.Level > DrawnFrom && Inside(cabin, eyes, 0.05f, EyesAboveBox))
                {
                    around = cabin;
                }
            }

            Veil(around, camera);
        }

        /// <summary>For "ss smoke": each cabin with smoke or someone smoking, how much, and how many doors are open.</summary>
        public string Describe()
        {
            if (!active)
            {
                return "No smoke in the cabins: only in a session, in the world.";
            }

            var text = new System.Text.StringBuilder();
            foreach (Cabin cabin in cabins.Values)
            {
                // Unity's null check is also true for a destroyed object.
                if (cabin.Root == null || (cabin.Air.Level < 0.005 && cabin.Breathing == 0))
                {
                    continue;
                }

                text.Append(text.Length == 0 ? string.Empty : "; ").Append("'").Append(cabin.Root.name).Append("': ").Append(Format(cabin.Air.Level))
                    .Append(" of full, ").Append(cabin.Breathing).Append(" breathing smoke out, ").Append(cabin.OpenNow).Append(" of ")
                    .Append(cabin.Definition.CabinVents.Length).Append(" door(s) open");
                Particles(text, "haze", cabin.Haze, cabin.Root);
                Particles(text, "out of the doors", cabin.Out, cabin.Root);
            }

            return text.Length == 0 ? "No smoke in any cabin." : text.Append('.').ToString();
        }

        // How many particles one of the emitters has, and where their middle is in the vehicle's coordinates.
        private static void Particles(System.Text.StringBuilder text, string what, ParticleEmitter emitter, Transform root)
        {
            // Unity's null check is also true for a destroyed object.
            if (emitter == null)
            {
                return;
            }

            Particle[] particles = emitter.particles;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < particles.Length; i++)
            {
                Vector3 at = particles[i].position;
                sum += emitter.useWorldSpace ? root.InverseTransformPoint(at) : root.InverseTransformPoint(emitter.transform.TransformPoint(at));
            }

            text.Append(", ").Append(what).Append(' ').Append(particles.Length);
            if (particles.Length > 0)
            {
                Vector3 middle = sum / particles.Length;
                text.Append(" around (").Append(Format(middle.x)).Append(", ").Append(Format(middle.y)).Append(", ").Append(Format(middle.z)).Append(')');
            }
        }

        /// <summary>Clean air everywhere and nothing drawn: the session or the world is gone.</summary>
        public void Release()
        {
            active = false;
            foreach (Cabin cabin in cabins.Values)
            {
                Destroy(cabin.Haze);
                Destroy(cabin.Out);
            }

            cabins.Clear();
            if (veil != null)
            {
                Object.Destroy(veil);
            }

            veil = null;
            if (veilMaterial != null)
            {
                Object.Destroy(veilMaterial);
            }

            veilMaterial = null;
        }

        private Cabin CabinOf(Transform root, VehicleDefinition definition)
        {
            Cabin cabin;
            if (!cabins.TryGetValue(root, out cabin))
            {
                cabin = new Cabin(root, definition);
                cabins[root] = cabin;
            }

            return cabin;
        }

        // Whether a point of the world is in the cabin's box, grown by that much to the sides and above.
        private static bool Inside(Cabin cabin, Vector3 world, float aside, float above)
        {
            VehicleBox box = cabin.Definition.Interior;
            Vector3 local = cabin.Root.InverseTransformPoint(world);
            return Mathf.Abs(local.x - box.X) <= box.HalfX + aside && Mathf.Abs(local.z - box.Z) <= box.HalfZ + aside
                && local.y >= box.Y - box.HalfY && local.y <= box.Y + box.HalfY + above;
        }

        // How many of the cabin's doors are open now; the first of them is where the smoke goes out.
        private int OpenVents(Cabin cabin, double now)
        {
            string[] paths = cabin.Definition.CabinVents;
            if (now >= cabin.NextFind)
            {
                cabin.NextFind = now + FindVentsEvery;
                for (int i = 0; i < paths.Length; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    if (cabin.Vents[i] != null)
                    {
                        continue;
                    }

                    Transform vent = cabin.Root.Find(paths[i]);
                    cabin.Vents[i] = vent;
                    cabin.Open[i] = null;
                    if (vent == null)
                    {
                        continue;
                    }

                    PlayMakerFSM machine = World.DoorSync.FindMachine(vent, VentMachine);
                    cabin.Open[i] = machine == null ? null : machine.FsmVariables.FindFsmBool(VentOpen);
                    if (cabin.Open[i] == null && !cabin.VentProblemSaid)
                    {
                        cabin.VentProblemSaid = true;
                        log.Warning(
                            LogCategory.Vehicle,
                            "Cabin smoke: the door '" + paths[i] + "' of '" + cabin.Root.name + "' has no machine '" + VentMachine + "' with '" + VentOpen
                            + "': it is taken as shut.");
                    }
                }
            }

            int open = 0;
            cabin.OutAt = -1;
            for (int i = 0; i < paths.Length; i++)
            {
                Transform vent = cabin.Vents[i];
                bool isOpen;
                if (vent == null)
                {
                    // Taken off (the Satsuma's), or never put on: a hole.
                    isOpen = true;
                }
                else
                {
                    FsmBool flag = cabin.Open[i];
                    isOpen = flag != null && flag.Value;
                    if (!isOpen)
                    {
                        // Where the door is when shut: where the smoke goes out when it opens.
                        cabin.ShutAt[i] = cabin.Root.InverseTransformPoint(vent.position);
                        cabin.ShutKnown[i] = true;
                    }
                }

                if (isOpen)
                {
                    open++;
                    if (cabin.OutAt < 0)
                    {
                        cabin.OutAt = i;
                    }
                }
            }

            return open;
        }

        private void Tell(Cabin cabin, int breathing, int open)
        {
            double level = cabin.Air.Level;
            if (!cabin.Said && level >= SaidFrom)
            {
                cabin.Said = true;
                log.Info(
                    LogCategory.Vehicle,
                    "Cabin smoke: '" + cabin.Root.name + "' fills with smoke (" + Format(level) + " of full, " + breathing + " breathing it out now, "
                    + open + " door(s) open).");
            }
            else if (cabin.Said && level < SaidUntil)
            {
                cabin.Said = false;
                log.Info(
                    LogCategory.Vehicle,
                    "Cabin smoke: the smoke in '" + cabin.Root.name + "' is gone" + (open > 0 ? ", out of " + open + " open door(s)." : ", slowly, with the doors shut."));
            }
        }

        private void Draw(Cabin cabin, float seconds, double now, Vector3 eyes, int open)
        {
            bool near = (cabin.Root.position - eyes).sqrMagnitude <= DrawnWithin * DrawnWithin;
            float level = (float)cabin.Air.Level;
            if (!near || (level < DrawnFrom && cabin.Air.LeftLastStep <= 0))
            {
                if (cabin.Haze != null)
                {
                    cabin.Haze.ClearParticles();
                }

                return;
            }

            if (cabin.Haze == null && !Make(cabin))
            {
                return;
            }

            VehicleBox box = cabin.Definition.Interior;

            // The haze, in the cabin's coordinates: it rides along.
            cabin.HazeOwed += level * HazePerSecond * seconds;
            while (cabin.HazeOwed >= 1f)
            {
                cabin.HazeOwed -= 1f;
                var at = new Vector3(
                    box.X + Random.Range(-cabin.HazeHalfX, cabin.HazeHalfX),
                    Random.Range(cabin.HazeFrom, cabin.HazeTo),
                    box.Z + Random.Range(-cabin.HazeHalfZ, cabin.HazeHalfZ));
                var drift = new Vector3(Random.Range(-0.04f, 0.04f), Random.Range(0f, 0.03f), Random.Range(-0.04f, 0.04f));
                cabin.Haze.Emit(at, drift, Random.Range(HazeSizeMin, HazeSizeMax), Random.Range(0.8f * HazeLife, 1.2f * HazeLife), HazeColours[2]);
            }

            if (open <= 0)
            {
                cabin.OutOwed = 0f;
                return;
            }

            // Out of the door, in the world: it stays behind.
            Vector3 spot;
            Vector3 outward;
            OutSpot(cabin, box, out spot, out outward);
            cabin.OutOwed += (float)cabin.Air.LeftLastStep * OutPerLevel;
            Vector3 worldSpot = cabin.Root.TransformPoint(spot);
            Vector3 worldOut = cabin.Root.TransformDirection(outward);
            while (cabin.OutOwed >= 1f)
            {
                cabin.OutOwed -= 1f;
                Vector3 jitter = new Vector3(Random.Range(-0.25f, 0.25f), Random.Range(-0.35f, 0.3f), Random.Range(-0.25f, 0.25f));
                cabin.Out.Emit(
                    worldSpot + jitter, (worldOut * OutSpeed) + (Vector3.up * OutRise) + (jitter * 0.3f), Random.Range(HazeSizeMin, HazeSizeMax),
                    Random.Range(0.8f * OutLife, 1.2f * OutLife), OutColours[0]);
            }

            // What already hangs in the cabin drifts to the door and thins out.
            if (now >= cabin.NextFlow)
            {
                cabin.NextFlow = now + FlowEvery;
                Particle[] particles = cabin.Haze.particles;
                for (int i = 0; i < particles.Length; i++)
                {
                    Vector3 toDoor = spot - particles[i].position;
                    particles[i].velocity = toDoor.normalized * OutSpeed;
                    particles[i].energy = Mathf.Min(particles[i].energy, Mathf.Max(0.3f, toDoor.magnitude / OutSpeed));
                }

                cabin.Haze.particles = particles;
            }
        }

        // Where the smoke leaves the cabin, in its coordinates, and which way: the side of the box nearest to where the open
        // door is when shut (or is now, if it was never seen shut), at the height of a face sitting.
        private static void OutSpot(Cabin cabin, VehicleBox box, out Vector3 spot, out Vector3 outward)
        {
            int i = cabin.OutAt;
            Vector3 door;
            if (i >= 0 && cabin.ShutKnown[i])
            {
                door = cabin.ShutAt[i];
            }
            else if (i >= 0 && cabin.Vents[i] != null)
            {
                door = cabin.Root.InverseTransformPoint(cabin.Vents[i].position);
            }
            else
            {
                // A hole where no door was ever seen: one of the sides.
                door = new Vector3(box.X + ((i % 2 == 0) ? -2f : 2f), box.Y, box.Z);
            }

            float dx = (door.x - box.X) / box.HalfX;
            float dz = (door.z - box.Z) / box.HalfZ;
            float y = box.Y + (0.3f * box.HalfY);
            if (Mathf.Abs(dx) >= Mathf.Abs(dz))
            {
                float side = Mathf.Sign(dx);
                spot = new Vector3(box.X + (side * box.HalfX), y, Mathf.Clamp(door.z, box.Z - box.HalfZ, box.Z + box.HalfZ));
                outward = new Vector3(side, 0f, 0f);
            }
            else
            {
                float end = Mathf.Sign(dz);
                spot = new Vector3(Mathf.Clamp(door.x, box.X - box.HalfX, box.X + box.HalfX), y, box.Z + (end * box.HalfZ));
                outward = new Vector3(0f, 0f, end);
            }
        }

        // The haze and what goes out, copied from the game's breath of smoke. False without one.
        private bool Make(Cabin cabin)
        {
            PlayerActions actions = PlayerActions.Current;
            ParticleEmitter breath = actions == null ? null : actions.Breath;
            if (breath == null)
            {
                if (!noLookSaid)
                {
                    noLookSaid = true;
                    log.Warning(LogCategory.Vehicle, "Cabin smoke: the game's breath of smoke was not found under the player: smoke in the cabins is not drawn.");
                }

                return false;
            }

            cabin.Haze = Copy(breath, "SummerSync cabin smoke", cabin.Root, false, HazeColours, 0f);
            VehicleBox box = cabin.Definition.Interior;
            float roof;
            bool found = RoofOf(cabin, out roof);
            cabin.HazeFrom = box.Y - (0.1f * box.HalfY);
            cabin.HazeTo = Mathf.Max(cabin.HazeFrom + 0.1f, roof - UnderRoof);
            cabin.HazeHalfX = Mathf.Max(0.05f, box.HalfX - HazeInset);
            cabin.HazeHalfZ = Mathf.Max(0.05f, box.HalfZ - HazeInset);
            log.Info(
                LogCategory.Vehicle,
                "Cabin smoke: in '" + cabin.Root.name + "' the haze goes from " + Format(cabin.HazeFrom) + " to " + Format(cabin.HazeTo) + " m up in its coordinates ("
                + (found ? "its roof found at " + Format(roof) : "no roof found above the middle of its space: " + Format(roof) + " taken") + ").");
            cabin.Out = Copy(breath, "SummerSync cabin smoke out", null, true, OutColours, OutGrow);
            if (!lookSaid)
            {
                lookSaid = true;
                Renderer look = cabin.Haze.GetComponent<Renderer>();
                Material material = look == null ? null : look.sharedMaterial;
                log.Info(
                    LogCategory.Vehicle,
                    "Cabin smoke: drawn with a copy of the game's breath of smoke (material '" + (material == null ? "none" : material.name) + "', shader '"
                    + (material == null || material.shader == null ? "none" : material.shader.name) + "'); the doors that let it out: "
                    + cabin.Definition.CabinVents.Length + " on '" + cabin.Root.name + "'. The game's breath: " + Describe(breath) + ".");
            }

            return true;
        }

        // The roof over the cabin, in its coordinates: the nearest of the vehicle's own solid things straight above the middle of
        // its box. False, with the top of the box and a little more, if none is.
        private static bool RoofOf(Cabin cabin, out float roof)
        {
            VehicleBox box = cabin.Definition.Interior;
            roof = box.Y + box.HalfY + HazeAboveBox;
            Vector3 from = cabin.Root.TransformPoint(new Vector3(box.X, box.Y, box.Z));
            RaycastHit[] hits = Physics.RaycastAll(from, cabin.Root.up, RoofSearch);
            float nearest = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;

                // A player sitting in it hangs from it too.
                if (collider == null || collider.isTrigger || collider is CharacterController || !collider.transform.IsChildOf(cabin.Root)
                    || hits[i].distance >= nearest)
                {
                    continue;
                }

                nearest = hits[i].distance;
                roof = cabin.Root.InverseTransformPoint(hits[i].point).y;
            }

            return nearest < float.MaxValue;
        }

        // What the game's breath of smoke does with its particles, for the log.
        private static string Describe(ParticleEmitter emitter)
        {
            string said = emitter.GetType().Name + " on layer " + emitter.gameObject.layer + " (" + LayerMask.LayerToName(emitter.gameObject.layer) + "), size " + Format(emitter.minSize) + " to " + Format(emitter.maxSize) + ", life " + Format(emitter.minEnergy)
                + " to " + Format(emitter.maxEnergy) + " s, in the world " + emitter.useWorldSpace;
            ParticleAnimator animator = emitter.GetComponent<ParticleAnimator>();
            if (animator == null)
            {
                return said + ", no animator";
            }

            return said + ", animator: force " + animator.force + ", random force " + animator.rndForce + ", damping " + Format(animator.damping) + ", size grow "
                + Format(animator.sizeGrow) + ", colours " + (animator.doesAnimateColor ? "animated" : "as made") + ", autodestruct " + animator.autodestruct;
        }

        private static ParticleEmitter Copy(ParticleEmitter source, string name, Transform parent, bool inWorld, Color[] colours, float grow)
        {
            var made = (GameObject)Object.Instantiate(source.gameObject);
            made.name = name;
            Component[] components = made.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component is PlayMakerFSM || component is Collider || component is Rigidbody)
                {
                    Object.DestroyImmediate(component);
                }
            }

            Transform at = made.transform;
            at.parent = parent;
            at.localPosition = Vector3.zero;
            at.localRotation = Quaternion.identity;
            at.localScale = Vector3.one;
            ParticleEmitter emitter = made.GetComponent<ParticleEmitter>();
            emitter.emit = false;
            emitter.useWorldSpace = inWorld;
            emitter.ClearParticles();
            ParticleAnimator animator = made.GetComponent<ParticleAnimator>();
            if (animator != null)
            {
                animator.autodestruct = false;
                animator.doesAnimateColor = true;
                animator.colorAnimation = colours;
                animator.sizeGrow = grow;
            }

            // The breath is on the layer of what the player has in front of the eyes, drawn over everything: the cabin's smoke
            // is in the world, behind what is in front of it.
            SetLayer(at, WorldLayer);
            made.SetActive(true);
            return emitter;
        }

        private static void SetLayer(Transform at, int layer)
        {
            at.gameObject.layer = layer;
            for (int i = 0; i < at.childCount; i++)
            {
                SetLayer(at.GetChild(i), layer);
            }
        }

        // The grey in front of the eyes of whoever is in a cabin with smoke.
        private void Veil(Cabin cabin, Camera camera)
        {
            float alpha = cabin == null ? 0f : VeilMost * (float)cabin.Air.Level;
            if (alpha < 0.005f || camera == null)
            {
                if (veil != null)
                {
                    veil.SetActive(false);
                }

                return;
            }

            if (veil == null && !MakeVeil(cabin))
            {
                return;
            }

            Transform at = veil.transform;
            if (at.parent != camera.transform)
            {
                at.parent = camera.transform;
                at.localRotation = Quaternion.identity;
            }

            // Just past the near plane, wider than what the camera sees.
            float distance = camera.nearClipPlane + 0.02f;
            float high = 2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.3f;
            at.localPosition = new Vector3(0f, 0f, distance);
            at.localScale = new Vector3(high * Mathf.Max(1f, camera.aspect) * 1.3f, high, 1f);
            veilMaterial.SetColor(veilColorName, VeilColor(alpha));
            veil.SetActive(true);
        }

        // With the particles' shader ("Particles/Alpha Blended": twice the tint times the texture), half of the grey and of
        // the opacity wanted; with a plain one, as it is.
        private Color VeilColor(float alpha)
        {
            return veilColorName == "_TintColor" ? new Color(VeilGrey * 0.5f, VeilGrey * 0.5f, VeilGrey * 0.5f, alpha * 0.5f) : new Color(VeilGrey, VeilGrey, VeilGrey, alpha);
        }

        private bool MakeVeil(Cabin cabin)
        {
            Renderer look = cabin.Haze == null ? null : cabin.Haze.GetComponent<Renderer>();
            if (look == null || look.sharedMaterial == null)
            {
                return false;
            }

            // The smoke's own material, with an even white for its texture: the same blending, the same grey everywhere.
            veilMaterial = new Material(look.sharedMaterial) { name = "SummerSync cabin veil" };
            var white = new Texture2D(2, 2);
            white.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
            white.Apply();
            veilMaterial.mainTexture = white;
            veilMaterial.mainTextureScale = Vector2.one;
            veilMaterial.mainTextureOffset = Vector2.zero;
            veilColorName = veilMaterial.HasProperty("_TintColor") ? "_TintColor" : "_Color";

            veil = new GameObject("SummerSync cabin veil");
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) },
                uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) },
                triangles = new[] { 0, 2, 1, 2, 3, 1 },
                colors = new[] { Color.white, Color.white, Color.white, Color.white },
            };
            mesh.RecalculateBounds();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            veil.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = veil.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = veilMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            log.Info(
                LogCategory.Vehicle,
                "Cabin smoke: the veil in front of the eyes uses the shader '" + (veilMaterial.shader == null ? "none" : veilMaterial.shader.name) + "' (its colour in '"
                + veilColorName + "').");
            return true;
        }

        private static void Destroy(ParticleEmitter emitter)
        {
            // Unity's null check is also true for a destroyed object.
            if (emitter != null)
            {
                Object.Destroy(emitter.gameObject);
            }
        }

        private static string Format(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private sealed class Cabin
        {
            public Cabin(Transform root, VehicleDefinition definition)
            {
                Root = root;
                Definition = definition;
                int count = definition.CabinVents.Length;
                Vents = new Transform[count];
                Open = new FsmBool[count];
                ShutAt = new Vector3[count];
                ShutKnown = new bool[count];
            }

            public Transform Root { get; private set; }

            public VehicleDefinition Definition { get; private set; }

            public readonly CabinAir Air = new CabinAir();
            public readonly Transform[] Vents;
            public readonly FsmBool[] Open;
            public readonly Vector3[] ShutAt;
            public readonly bool[] ShutKnown;
            public double NextFind;
            public double NextFlow;
            public bool VentProblemSaid;
            public bool Said;
            public int Breathing;
            public int OpenNow;
            public int OutAt = -1;
            public float HazeOwed;
            public float HazeFrom;
            public float HazeTo;
            public float HazeHalfX;
            public float HazeHalfZ;
            public float OutOwed;
            public ParticleEmitter Haze;
            public ParticleEmitter Out;
        }
    }
}
