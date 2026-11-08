using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// The dents of the vehicles, and the sound of their crashes, the same in every game.
    ///
    /// What is assumed about the game (ILSpy of Assembly-CSharp and the level file,
    /// docs/GAME_NOTES.md "Las abolladuras"): what dents is the component <c>Deformable</c>. Its <c>OnCollisionEnter</c> calls
    /// its private <c>Deform(Collision)</c> unless what hit it is on layer 19 or 20, and <c>Deform</c> reads only the
    /// collision's relative speed squared and its contacts (point and normal, in the world), turns them into the coordinates
    /// of its mesh, and moves the vertices near them: the same blow on the same mesh makes the same dent. The mesh is written
    /// in its <c>FixedUpdate</c>, at most twice a second, which also loads the hardness map the first time. The crash's sound
    /// is the vehicle's <c>EventSounds</c>, its <c>collisionSound</c> group, in its own <c>OnCollisionEnter</c>.
    ///
    /// So a dent is made by the game that simulates what is hit and told: what (by its place in the scene), how hard and
    /// where, in the coordinates of its mesh. Every game makes it with the game's own arithmetic, but from those coordinates
    /// (<see cref="DeformLocal"/>): the game's <c>Deform</c> takes the contacts in the world's, and a point a kilometre and a
    /// half from the world's centre is a float a tenth of a millimetre off, which made the same blow end a little different
    /// in each game. The others write the mesh at once. Without a session the game's own <c>Deform</c> stands. On a copy that another game simulates, the game's <c>OnCollisionEnter</c>
    /// does nothing (a kinematic copy would dent again, or differently), unless what hits it is the local player's own (the
    /// sledgehammer): that blow exists only in this game, which makes the dent and tells it. The crash's sound goes the same
    /// way. The host keeps every dent since its world loaded and tells them to a client that joins; the ones of meshes the
    /// game saves (the Satsuma's body and panels, <c>ES2Auto</c>) are dropped from that when the host saves.
    ///
    /// Fleetari's body work (<c>REPAIRSHOP/Jobs/Bodyfix :: Work</c>, run where the Satsuma is simulated) puts each piece's
    /// mesh back as the game made it (a <c>SetProperty</c> of its <c>MeshFilter.mesh</c>); the <c>Deformable</c> takes the new
    /// mesh as its base. That is told as a repair (<see cref="DentWire.FlagRepair"/>), kept in the journal in its place, and
    /// the others put the same mesh in, which they read from their own copy of that machine.
    /// </summary>
    internal sealed class DentSync
    {
        private const int Layer19 = 19;
        private const int Layer20 = 20;
        private const float LeastStrength = 10f;
        private const int JournalMost = 3000;
        private const int JournalPerFrame = 30;
        private const double ScanEvery = 1.0;
        private const double WaitForTarget = 20.0;
        private const double SoundEvery = 0.3;
        private const string SavePath = "/Systems/ElectricityBills";
        private const string SaveMachine = "Data";
        private const string SaveState = "Save game";
        private const string BodyWorkRoot = "/REPAIRSHOP";
        private const string BodyWorkPath = "Jobs/Bodyfix";
        private const string BodyWorkMachine = "Work";
        private const string MeshProperty = "mesh";

        private static readonly HashSet<int> SaveHooked = new HashSet<int>();
        private static readonly HashSet<FsmStateAction> RepairHooked = new HashSet<FsmStateAction>();
        private static DentSync active;
        private static bool patched;
        private static FieldInfo meshField;
        private static FieldInfo appliedMapField;
        private static MethodInfo loadMesh;
        private static MethodInfo loadMap;
        private static MethodInfo deform;
        private static MethodInfo updateMesh;
        private static FieldInfo verticesField;
        private static FieldInfo colorsField;
        private static FieldInfo baseVerticesField;
        private static FieldInfo baseColorsField;
        private static FieldInfo mapField;
        private static FieldInfo sizeFactorField;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly Dictionary<string, Deformable> found = new Dictionary<string, Deformable>();
        private readonly Dictionary<string, EventSounds> sounding = new Dictionary<string, EventSounds>();
        private readonly List<Kept> journal = new List<Kept>();
        private readonly List<Waiting> waiting = new List<Waiting>();
        private readonly List<Sending> sending = new List<Sending>();
        private readonly Dictionary<int, double> lastSound = new Dictionary<int, double>();
        private readonly StringBuilder path = new StringBuilder(128);
        private readonly Dictionary<MeshFilter, Mesh> repairs = new Dictionary<MeshFilter, Mesh>();
        private bool repairsBound;
        private int repairsMade;
        private int repairsReplayed;
        private DentReplication current;
        private Transform player;
        private Transform world;
        private double clock;
        private double nextScan;
        private double nextSoundScan;
        private bool asked;
        private bool saveHooked;
        private int made;
        private int replayed;
        private int dropped;
        private int soundsTold;
        private int soundsPlayed;
        private int caughtUp;

        private sealed class Kept
        {
            public Dent Dent;
            public bool Saved;
        }

        private sealed class Waiting
        {
            public Dent Dent;
            public double Until;
        }

        private sealed class Sending
        {
            public byte Player;
            public List<Dent> Dents;
            public int Next;
        }

        public DentSync(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
            Patch(log);
        }

        /// <summary>Whether the game's dents and crash sounds are this class's: false if the game's code was not as expected.</summary>
        public static bool Working
        {
            get { return patched; }
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is null without a world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            clock = now;
            if (localPlayer != null && localPlayer != world)
            {
                // A new world: what was found and kept was of the last one. (The player is out of it now and then: a
                // loading screen, the game's own death; that is the same world.)
                found.Clear();
                sounding.Clear();
                journal.Clear();
                waiting.Clear();
                sending.Clear();
                lastSound.Clear();
                repairs.Clear();
                repairsBound = false;
                saveHooked = false;
                asked = false;
                world = localPlayer;
            }

            player = localPlayer;

            DentReplication dents = player == null ? null : manager.Dents;
            if (dents != current)
            {
                if (current != null)
                {
                    current.DentWanted -= OnDent;
                    current.JournalWanted -= OnJournalWanted;
                }

                current = dents;
                asked = false;
                sending.Clear();
                waiting.Clear();
                if (current != null)
                {
                    current.DentWanted += OnDent;
                    current.JournalWanted += OnJournalWanted;
                }
            }

            active = player == null || !patched ? null : this;
            if (active == null)
            {
                return;
            }

            if (!saveHooked)
            {
                saveHooked = true;
                HookSave();
            }

            if (!repairsBound)
            {
                repairsBound = true;
                BindRepairs();
            }

            NetSession session = manager.Session;
            bool connected = current != null && session != null && session.State == SessionState.Connected;
            if (connected && !asked && session.Role != SessionRole.Host)
            {
                // Everything before this is in what the host answers.
                asked = current.AskJournal();
                if (asked)
                {
                    log.Info(LogCategory.Vehicle, "Dents: asked the host for the dents of its world since it loaded.");
                }
            }

            SendJournal();
            Retry();
        }

        /// <summary>Lets go of the session's news; the world's dents stay as they are.</summary>
        public void Release()
        {
            if (made > 0 || replayed > 0)
            {
                log.Info(LogCategory.Vehicle, Describe());
            }

            if (current != null)
            {
                current.DentWanted -= OnDent;
                current.JournalWanted -= OnJournalWanted;
            }

            current = null;
            active = null;
            sending.Clear();
            waiting.Clear();
        }

        /// <summary>One line for the log and the overlay.</summary>
        public string Describe()
        {
            return "Dents: " + made + " made here, " + replayed + " made as another game made them (" + caughtUp + " of them on joining), " + journal.Count
                + " kept for whoever joins" + (dropped > 0 ? ", " + dropped + " not found here" : string.Empty) + "; repairs: " + repairsMade + " here, "
                + repairsReplayed + " as another game; crash sounds: " + soundsTold + " told, " + soundsPlayed + " played for another game.";
        }

        // The game's code, patched once per process: the prefixes below.
        private static void Patch(Logger log)
        {
            if (patched)
            {
                return;
            }

            try
            {
                Type type = typeof(Deformable);
                const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
                meshField = type.GetField("mesh", Private);
                appliedMapField = type.GetField("appliedMap", Private);
                loadMesh = type.GetMethod("LoadMesh", Private);
                loadMap = type.GetMethod("LoadMap", Private);
                deform = type.GetMethod("Deform", Private);
                updateMesh = type.GetMethod("UpdateMesh", Private);
                verticesField = type.GetField("vertices", Private);
                colorsField = type.GetField("colors", Private);
                baseVerticesField = type.GetField("baseVertices", Private);
                baseColorsField = type.GetField("baseColors", Private);
                mapField = type.GetField("map", Private);
                sizeFactorField = type.GetField("sizeFactor", Private);
                MethodInfo collided = type.GetMethod("OnCollisionEnter", Private);
                MethodInfo sounded = typeof(EventSounds).GetMethod("OnCollisionEnter", Private);
                if (meshField == null || appliedMapField == null || loadMesh == null || loadMap == null || deform == null || updateMesh == null
                    || verticesField == null || colorsField == null || baseVerticesField == null || baseColorsField == null || mapField == null || sizeFactorField == null
                    || collided == null || sounded == null)
                {
                    throw new InvalidOperationException("a member of Deformable or EventSounds is missing");
                }

                HarmonyInstance harmony = HarmonyInstance.Create("summersync.dents");
                harmony.Patch(collided, new HarmonyMethod(typeof(DentSync).GetMethod("BeforeCollision", BindingFlags.NonPublic | BindingFlags.Static)), null);
                harmony.Patch(deform, new HarmonyMethod(typeof(DentSync).GetMethod("BeforeDeform", BindingFlags.NonPublic | BindingFlags.Static)), null);
                harmony.Patch(sounded, new HarmonyMethod(typeof(DentSync).GetMethod("BeforeSound", BindingFlags.NonPublic | BindingFlags.Static)), null);
                patched = true;
                log.Info(LogCategory.Vehicle, "Dents: the game's denting and crash sounds are watched.");
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Vehicle, "Dents: the game's code is not as this build expects (" + exception.Message + "); each game dents its own vehicles.");
            }
        }

        // The game's Deformable.OnCollisionEnter: not on a copy that another game simulates, unless the local player's own
        // body hit it.
        private static bool BeforeCollision(Deformable __instance, Collision collision)
        {
            DentSync sync = active;
            return sync == null || sync.LetsDent(__instance, collision);
        }

        // The game's Deformable.Deform: a blow this game makes is told, and in a session made by the mod's arithmetic.
        private static bool BeforeDeform(Deformable __instance, Collision collision)
        {
            DentSync sync = active;
            if (sync == null)
            {
                return true;
            }

            try
            {
                return sync.Dented(__instance, collision);
            }
            catch (Exception exception)
            {
                sync.log.Warning(LogCategory.Vehicle, "Dents: a dent here could not be told: " + exception.Message);
                return true;
            }
        }

        // The game's EventSounds.OnCollisionEnter: a vehicle's crash sounds where it is simulated, and is told.
        private static bool BeforeSound(EventSounds __instance, Collision collision)
        {
            DentSync sync = active;
            return sync == null || sync.Sounds(__instance, collision);
        }

        private bool LetsDent(Deformable dented, Collision collision)
        {
            if (!Shared() || !IsCopy(dented.transform))
            {
                return true;
            }

            // The sledgehammer: a body only this game has.
            Collider other = collision.collider;
            return other != null && player != null && other.transform.IsChildOf(player);
        }

        // Whether the thing is a copy here of what another game simulates: a vehicle (or a part on it) that is followed, or a
        // loose body that is kinematic here.
        private bool IsCopy(Transform thing)
        {
            bool followed;
            if (vehicles.FollowedVehicleOf(thing, out followed))
            {
                return followed;
            }

            Rigidbody body = thing.GetComponentInParent<Rigidbody>();
            return body != null && body.isKinematic;
        }

        private bool Shared()
        {
            NetSession session = manager.Session;
            return current != null && session != null && session.State == SessionState.Connected && (manager.Mode == NetworkMode.Host || manager.Mode == NetworkMode.Client);
        }

        // A blow this game makes: kept and told; in a session, made here by the mod (false: the game's Deform is not run).
        private bool Dented(Deformable dented, Collision collision)
        {
            MeshFilter filter = dented.meshFilter;
            GameObject other = collision.gameObject;
            if (filter == null || meshField.GetValue(dented) == null || (other != null && (other.layer == Layer19 || other.layer == Layer20)))
            {
                return true;
            }

            float strength = collision.relativeVelocity.sqrMagnitude;
            ContactPoint[] contacts = collision.contacts;
            if (strength < LeastStrength || contacts == null || contacts.Length == 0)
            {
                return true;
            }

            Transform frame = filter.transform;
            int count = Math.Min(contacts.Length, DentWire.MaxContacts);
            var dent = new Dent { Target = KeyOf(dented), Strength = strength, Contacts = new DentContact[count] };
            for (int i = 0; i < count; i++)
            {
                Vector3 at = frame.InverseTransformPoint(contacts[i].point);
                Vector3 push = frame.InverseTransformDirection(contacts[i].normal);
                dent.Contacts[i] = new DentContact { X = at.x, Y = at.y, Z = at.z, NX = push.x, NY = push.y, NZ = push.z };
            }

            made++;
            Keep(dent, dented);
            if (!Shared())
            {
                return true;
            }

            // As the game's FixedUpdate would before its next blow: the others do it before theirs.
            // The mesh written now, as in the other games: the game writes it in its FixedUpdate, and the Satsuma's are off
            // once it stands still, so a dent made as it stops would show only when it moves again.
            Settle(dented);
            DeformLocal(dented, dent);
            updateMesh.Invoke(dented, null);
            current.Tell(dent);
            return false;
        }

        /// <summary>
        /// The game's <c>Deformable.Deform</c> (ILSpy), line by line, with the blow already in the coordinates of the mesh:
        /// the same numbers make the same dent in every game.
        /// </summary>
        private static void DeformLocal(Deformable dented, Dent dent)
        {
            var vertices = (Vector3[])verticesField.GetValue(dented);
            var colors = (Color32[])colorsField.GetValue(dented);
            var baseVertices = (Vector3[])baseVerticesField.GetValue(dented);
            var baseColors = (Color32[])baseColorsField.GetValue(dented);
            var map = (float[])mapField.GetValue(dented);
            float sizeFactor = (float)sizeFactorField.GetValue(dented);
            if (vertices == null || baseVertices == null || colors == null || baseColors == null)
            {
                return;
            }

            float num = Mathf.Min(1f, dent.Strength / 1000f);
            if ((double)num < 0.01)
            {
                return;
            }

            float num2 = num * (sizeFactor * (0.1f / Mathf.Max(0.1f, dented.Hardness)));
            for (int i = 0; i < dent.Contacts.Length; i++)
            {
                DentContact contact = dent.Contacts[i];
                var at = new Vector3(contact.X, contact.Y, contact.Z);
                var push = new Vector3(contact.NX, contact.NY, contact.NZ);
                for (int j = 0; j < vertices.Length; j++)
                {
                    float sqrMagnitude = (vertices[j] - at).sqrMagnitude;
                    if (!(sqrMagnitude <= num2))
                    {
                        continue;
                    }

                    Vector3 vector2 = push * (1f - (sqrMagnitude / num2)) * num2;
                    if (map != null)
                    {
                        vector2 *= 1f - map[j];
                    }

                    vertices[j] += vector2;
                    if (dented.MaxVertexMov > 0f)
                    {
                        float maxVertexMov = dented.MaxVertexMov;
                        vector2 = vertices[j] - baseVertices[j];
                        sqrMagnitude = vector2.magnitude;
                        if (sqrMagnitude > maxVertexMov)
                        {
                            vertices[j] = baseVertices[j] + (vector2 * (maxVertexMov / sqrMagnitude));
                        }

                        if (colors.Length > 0)
                        {
                            sqrMagnitude /= dented.MaxVertexMov;
                            colors[j] = Color.Lerp(baseColors[j], dented.DeformedVertexColor, sqrMagnitude);
                        }
                    }
                    else if (colors.Length > 0)
                    {
                        colors[j] = Color.Lerp(baseColors[j], dented.DeformedVertexColor, (vertices[j] - baseVertices[j]).magnitude / (num2 * 10f));
                    }
                }
            }
        }

        // What the game's FixedUpdate does before writing a mesh: a mesh put in from outside is the new base, and the
        // hardness map is read once.
        private static void Settle(Deformable dented)
        {
            object mesh = meshField.GetValue(dented);
            if (dented.meshFilter != null && !ReferenceEquals(mesh, dented.meshFilter.mesh))
            {
                loadMesh.Invoke(dented, null);
            }

            if (!ReferenceEquals(dented.HardnessMap, appliedMapField.GetValue(dented)))
            {
                loadMap.Invoke(dented, null);
            }
        }

        // The host (or a game alone, which may host later) keeps what the world went through since it loaded.
        private void Keep(Dent dent, Deformable dented)
        {
            if (manager.Mode == NetworkMode.Client)
            {
                return;
            }

            if (journal.Count >= JournalMost)
            {
                journal.RemoveAt(0);
                if (journal.Count == JournalMost - 1)
                {
                    log.Info(LogCategory.Vehicle, "Dents: more than " + JournalMost + " dents in this world; whoever joins gets the latest ones.");
                }
            }

            bool saved = dented.GetComponent("ES2Auto") != null || (dented.meshFilter != null && dented.meshFilter.GetComponent("ES2Auto") != null);
            journal.Add(new Kept { Dent = dent, Saved = saved });
        }

        private void OnDent(Dent dent)
        {
            if (dent.SoundOnly)
            {
                Sound(dent.Target);
                return;
            }

            // A client drops what arrives before it asked: the host's answer has it.
            NetSession session = manager.Session;
            if (session != null && session.Role != SessionRole.Host && !asked)
            {
                return;
            }

            if (!Apply(dent))
            {
                waiting.Add(new Waiting { Dent = dent, Until = clock + WaitForTarget });
            }
        }

        // The same blow on this game's copy, and the mesh written now (a copy may never run its own FixedUpdate: the Satsuma's
        // are on only while it moves).
        private bool Apply(Dent dent)
        {
            Deformable dented = Find(dent.Target);
            if (dented == null)
            {
                return false;
            }

            if (dent.Repair)
            {
                Repair(dented, dent);
                return true;
            }

            MeshFilter filter = dented.meshFilter;
            if (filter == null || meshField.GetValue(dented) == null)
            {
                return true;
            }

            Settle(dented);
            DeformLocal(dented, dent);
            updateMesh.Invoke(dented, null);

            replayed++;
            if (dent.FromJournal)
            {
                caughtUp++;
            }

            Keep(dent, dented);
            EventSounds sounds = dented.GetComponentInParent<EventSounds>();
            if (!dent.FromJournal && sounds != null)
            {
                Sound(sounds);
            }

            return true;
        }

        // Another game's body work (or one from the host's journal): the same mesh as the game makes it, as the new base.
        private void Repair(Deformable dented, Dent dent)
        {
            MeshFilter filter = dented.meshFilter;
            Mesh mesh;
            if (filter == null || !repairs.TryGetValue(filter, out mesh) || mesh == null)
            {
                log.Warning(LogCategory.Vehicle, "Dents: '" + dent.Target + "' was repaired in another game, but this game's body work has no mesh for it; its dents stay here.");
                return;
            }

            filter.mesh = mesh;
            loadMesh.Invoke(dented, null);
            updateMesh.Invoke(dented, null);
            repairsReplayed++;
            Keep(dent, dented);
            if (!dent.FromJournal)
            {
                log.Info(LogCategory.Vehicle, "Dents: '" + dent.Target + "' repaired as in another game (Fleetari's body work).");
            }
        }

        // The meshes Fleetari's body work puts back, read from this game's copy of it, and a hook after each that tells it.
        private void BindRepairs()
        {
            GameObject shop = GameObject.Find(BodyWorkRoot);
            Transform at = shop == null ? null : shop.transform.Find(BodyWorkPath);
            PlayMakerFSM machine = at == null ? null : World.DoorSync.FindMachine(at, BodyWorkMachine);
            if (machine == null)
            {
                log.Warning(LogCategory.Vehicle, "Dents: no '" + BodyWorkRoot.Substring(1) + "/" + BodyWorkPath + " :: " + BodyWorkMachine + "'; Fleetari's body work is each game's own.");
                return;
            }

            if (machine.Fsm == null || !machine.Fsm.Initialized)
            {
                // Its object has never been on, so its machine has not read its states: on and off at once wakes it up and
                // runs nothing (PlayMaker starts a machine in Start, on the next frame it is on). Its parent holds every job.
                GameObject jobs = at.parent.gameObject;
                if (!jobs.activeSelf && jobs.transform.parent != null && jobs.transform.parent.gameObject.activeInHierarchy)
                {
                    jobs.SetActive(true);
                    jobs.SetActive(false);
                }
            }

            if (machine.Fsm == null || !machine.Fsm.Initialized)
            {
                log.Warning(LogCategory.Vehicle, "Dents: Fleetari's body work could not be read; it is each game's own.");
                return;
            }

            FsmState[] states = machine.FsmStates;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    var set = actions[a] as SetProperty;
                    FsmProperty property = set == null ? null : set.targetProperty;
                    MeshFilter filter = property == null || property.PropertyName != MeshProperty || property.TargetObject == null ? null : property.TargetObject.Value as MeshFilter;
                    Mesh mesh = filter == null || property.ObjectParameter == null ? null : property.ObjectParameter.Value as Mesh;
                    if (mesh == null)
                    {
                        continue;
                    }

                    repairs[filter] = mesh;
                    if (RepairHooked.Add(set))
                    {
                        MeshFilter repaired = filter;
                        int after = Array.IndexOf(states[s].Actions, set) + 1;
                        MSCLoader.PlayMakerExtensions.FsmInject(machine, states[s].Name, () => OnRepaired(repaired), false, after, false);
                    }
                }
            }

            log.Info(LogCategory.Vehicle, "Dents: Fleetari's body work puts back " + repairs.Count + " mesh(es); shared as repairs.");
        }

        private static void OnRepaired(MeshFilter filter)
        {
            DentSync sync = active;
            if (sync != null)
            {
                sync.Repaired(filter);
            }
        }

        // This game's body work put the mesh back: the dents before it are gone everywhere.
        private void Repaired(MeshFilter filter)
        {
            // Unity's null check is also true for a destroyed object.
            Deformable dented = filter == null || !Shared() ? null : DeformableOf(filter);
            if (dented == null)
            {
                return;
            }

            Settle(dented);
            updateMesh.Invoke(dented, null);
            var dent = new Dent { Target = KeyOf(dented), Flags = DentWire.FlagRepair, Contacts = new DentContact[0] };
            repairsMade++;
            Keep(dent, dented);
            current.Tell(dent);
            log.Info(LogCategory.Vehicle, "Dents: '" + dent.Target + "' repaired here (Fleetari's body work); told.");
        }

        // The thing that dents that mesh: it may be on another object than the mesh (the Satsuma's body's is on the car).
        private static Deformable DeformableOf(MeshFilter filter)
        {
            Deformable own = filter.GetComponent<Deformable>();
            if (own != null && own.meshFilter == filter)
            {
                return own;
            }

            Deformable[] all = UnityEngine.Object.FindObjectsOfType<Deformable>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].meshFilter == filter)
                {
                    return all[i];
                }
            }

            return null;
        }

        // What has not been found yet, again, until it has waited long enough.
        private void Retry()
        {
            for (int i = waiting.Count - 1; i >= 0; i--)
            {
                if (Apply(waiting[i].Dent))
                {
                    waiting.RemoveAt(i);
                }
                else if (clock > waiting[i].Until)
                {
                    dropped++;
                    log.Warning(LogCategory.Vehicle, "Dents: '" + waiting[i].Dent.Target + "' is not in this game (" + dropped + " so far); its dent is dropped here.");
                    waiting.RemoveAt(i);
                }
            }
        }

        private void OnJournalWanted(byte player)
        {
            var copy = new List<Dent>(journal.Count);
            for (int i = 0; i < journal.Count; i++)
            {
                copy.Add(journal[i].Dent);
            }

            sending.Add(new Sending { Player = player, Dents = copy });
            log.Info(LogCategory.Vehicle, "Dents: player " + player + " asked for this world's dents: " + copy.Count + " to tell.");
        }

        // The host's journal to whoever asked, a few each frame.
        private void SendJournal()
        {
            for (int s = sending.Count - 1; s >= 0; s--)
            {
                Sending to = sending[s];
                int end = Math.Min(to.Dents.Count, to.Next + JournalPerFrame);
                for (; to.Next < end; to.Next++)
                {
                    current.TellTo(to.Player, to.Dents[to.Next]);
                }

                if (to.Next >= to.Dents.Count)
                {
                    sending.RemoveAt(s);
                }
            }
        }

        private bool Sounds(EventSounds sounds, Collision collision)
        {
            // A vehicle's only: everything else that sounds when it hits something is each game's.
            bool followed;
            if (!Shared() || !vehicles.FollowedVehicleOf(sounds.transform, out followed))
            {
                return true;
            }

            if (followed)
            {
                return false;
            }

            AudioEventGroup group = sounds.collisionSound;
            GameObject other = collision.gameObject;
            if (!sounds.useCollisionSound || group == null || other == null || (group.useLayerFilter && !group.matchingLayers.Contains(other.layer))
                || (group.useTagFilter && !group.matchingTags.Contains(other.tag)))
            {
                return true;
            }

            int id = sounds.GetInstanceID();
            double last;
            if (!lastSound.TryGetValue(id, out last) || clock - last >= SoundEvery)
            {
                lastSound[id] = clock;
                if (current.Tell(new Dent { Target = KeyOf(sounds.transform, -1), Strength = collision.relativeVelocity.sqrMagnitude, Contacts = new DentContact[0], Flags = DentWire.FlagSound }))
                {
                    soundsTold++;
                }
            }

            return true;
        }

        // The vehicle's crash sound here, at most so often.
        private void Sound(string target)
        {
            EventSounds sounds;
            if ((!sounding.TryGetValue(target, out sounds) || sounds == null) && clock >= nextSoundScan)
            {
                nextSoundScan = clock + ScanEvery;
                sounding.Clear();
                EventSounds[] all = UnityEngine.Object.FindObjectsOfType<EventSounds>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].useCollisionSound)
                    {
                        sounding[KeyOf(all[i].transform, -1)] = all[i];
                    }
                }

                sounding.TryGetValue(target, out sounds);
            }

            if (sounds != null)
            {
                Sound(sounds);
            }
        }

        private void Sound(EventSounds sounds)
        {
            if (!sounds.useCollisionSound || sounds.collisionSound == null)
            {
                return;
            }

            int id = sounds.GetInstanceID();
            double last;
            if (lastSound.TryGetValue(id, out last) && clock - last < SoundEvery)
            {
                return;
            }

            lastSound[id] = clock;
            soundsPlayed++;
            sounds.PlaySounds(sounds.collisionSound, EventSounds.EventType.OnCollision);
        }

        private Deformable Find(string key)
        {
            Deformable dented;
            if (found.TryGetValue(key, out dented) && dented != null && KeyOf(dented) == key)
            {
                return dented;
            }

            if (clock < nextScan)
            {
                return null;
            }

            nextScan = clock + ScanEvery;
            found.Clear();
            Deformable[] all = UnityEngine.Object.FindObjectsOfType<Deformable>();
            for (int i = 0; i < all.Length; i++)
            {
                found[KeyOf(all[i])] = all[i];
            }

            return found.TryGetValue(key, out dented) ? dented : null;
        }

        private string KeyOf(Deformable dented)
        {
            Deformable[] here = dented.GetComponents<Deformable>();
            return KeyOf(dented.transform, Array.IndexOf(here, dented));
        }

        /// <summary>
        /// A thing's name in every game: its path from the scene's root, each step with its number among its siblings of the
        /// same name when there are several, and the component's number on it (or nothing, -1).
        /// </summary>
        public string KeyOf(Transform thing, int component)
        {
            path.Length = 0;
            Append(thing);
            if (component >= 0)
            {
                path.Append('#').Append(component.ToString(CultureInfo.InvariantCulture));
            }

            return path.ToString();
        }

        private void Append(Transform thing)
        {
            Transform parent = thing.parent;
            if (parent != null)
            {
                Append(parent);
                path.Append('/');
            }

            path.Append(thing.name);
            if (parent == null)
            {
                return;
            }

            int before = 0;
            bool others = false;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform sibling = parent.GetChild(i);
                if (sibling == thing)
                {
                    continue;
                }

                if (sibling.name == thing.name)
                {
                    others = true;
                    if (sibling.GetSiblingIndex() < thing.GetSiblingIndex())
                    {
                        before++;
                    }
                }
            }

            if (others)
            {
                path.Append('[').Append(before.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
        }

        // The host's save: the meshes the game saves have their dents in it now.
        private void HookSave()
        {
            GameObject bills = GameObject.Find(SavePath);
            PlayMakerFSM machine = bills == null ? null : World.DoorSync.FindMachine(bills.transform, SaveMachine);
            if (machine == null || !World.DoorSync.HasState(machine, SaveState))
            {
                log.Warning(LogCategory.Vehicle, "Dents: no '" + SavePath + " :: " + SaveMachine + "' with '" + SaveState + "'; the Satsuma's dents stay in what whoever joins is told.");
                return;
            }

            if (SaveHooked.Add(machine.GetInstanceID()))
            {
                MSCLoader.PlayMakerExtensions.FsmInject(machine, SaveState, OnSaved, false, 0, false);
            }
        }

        private static void OnSaved()
        {
            DentSync sync = active;
            if (sync == null)
            {
                return;
            }

            int before = sync.journal.Count;
            sync.journal.RemoveAll(kept => kept.Saved);
            if (before != sync.journal.Count)
            {
                sync.log.Info(LogCategory.Vehicle, "Dents: the game saved the meshes it keeps; " + (before - sync.journal.Count) + " of their dents are in the save now, not in what whoever joins is told.");
            }
        }
    }
}
