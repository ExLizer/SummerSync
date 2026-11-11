using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Vehicles;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Objects
{
    /// <summary>
    /// The loose objects, as far as Unity is concerned: it finds the rigidbodies the player can pick up, hands
    /// them to the replication, and then does with each what the replication says. One it simulates is left
    /// to the game's physics and reported; one it follows is made kinematic and put where its owner has it.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md): things that can be
    /// picked up are non-kinematic rigidbodies on layer 19 tagged PART or ITEM; the hand holds one through a
    /// joint on the object "Hand" under the camera, whose connected body is the thing in hand. What is found is
    /// written to the log.
    ///
    /// On a client, an object that another machine simulates is kinematic here: it does not give way to
    /// anything. So a client asks the host for the objects close to its player, or to the vehicle its player
    /// drives, and runs their physics itself while they are close.
    ///
    /// An object inside a vehicle (in the box that the vehicle's file calls its interior) is reported, and
    /// shown, where it lies on the vehicle. Its place in the world and the vehicle's would otherwise arrive by
    /// two paths with slightly different delays, and at speed that is the object sinking into the floor.
    ///
    /// An object that another machine simulates must not push what this machine simulates: being kinematic, it
    /// would win against anything, a vehicle included (seen on 2026-10-13: a torch in the guest's hand shoved
    /// the host's van around, and dropped on its floor it stopped the van dead). So while someone else holds
    /// it, and while it rides a vehicle whose physics runs here, it is put on the layer the game itself uses
    /// for what is in the hand, which the game has made pass through vehicles. And a client that lets something
    /// go inside a vehicle it does not drive hands it to the host at once, for the driver's machine to simulate.
    ///
    /// That layer still collides with a few others, and a thing can be near a vehicle without being in its
    /// interior or in anyone's hand (seen on 2026-10-13: a remote control thrown inside the van at speed, and
    /// the driver died as in a crash; the driver's head hangs from a joint that breaks at 300 N). So whatever
    /// another machine simulates has its collisions switched off while it is within reach of a vehicle whose
    /// physics runs here. The machine that simulates the thing is the one that makes it give way.
    ///
    /// The hand can put away what it holds and show a tool of its own instead: a torch, an axe, a spray can
    /// (the "Use" key; state machine "PickUp", dump of 2026-10-12). The game does that by switching off the
    /// object the thing hangs from. The thing is still in the hand, and goes on being shared as held.
    ///
    /// What rides a vehicle changes hands with the motion it has relative to the vehicle, and starts on its new
    /// machine from that machine's copy of the vehicle and at its speed. The vehicle is not at the same place,
    /// nor at the same speed, on two machines at the same moment.
    /// </summary>
    internal sealed partial class ObjectPresenter
    {
        private const double ScanInterval = 3.0;
        private const double FirstScanDelay = 1.0;
        private const int PickableLayer = 19;

        // The other layers the hand picks things up on (dump of 2026-10-12, "PickUp": layers 19, 15 and 9).
        private const int ToolLayer = 15;
        private const int OtherPickLayer = 9;

        // What the game sends a thing to eat it up or throw it away (dump of 2026-10-12: the global transition of the
        // state machine "Use" of 83 things, sent by the bins, the compost, the dump, the fires and an emptied bag).
        private const string UseMachine = "Use";
        private const string GarbageEvent = "GARBAGE";

        // What a fire destroys whole when it burns it (dump of 2026-10-14, "WoodTrigger"): burnt, not built in.
        private const string Firewood = "firewood(Clone)";

        // Something made for another player is theirs if it is found within this many seconds.
        private const double BornSeconds = 20.0;

        // The layer the game's hand puts a thing on while it holds it (dump of 2026-10-12, state machine "PickUp").
        private const int HeldLayer = 16;
        private const string PartTag = "PART";
        private const string ItemTag = "ITEM";
        private const string HandPath = "Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand";
        private const float LabelDistance = 10f;
        private const int QuietEvery = 8;

        // Lying still this far from the local player, a thing this machine simulates is held where it lies, kinematic, and
        // gets its physics back within the nearer distance or in a hand. Far from its player a game switches off what is
        // under the "LOD" of places and vehicles (a shop's counter, a boat it does not follow): a thing left there by a
        // player who is near it, and handed back to the host who is not, fell through to the ground or into the lake
        // (seen on 2026-10-18: the bags in the boat at the island).
        private const float FreezeFar = 100f;
        private const float ThawNear = 80f;
        private const float FreezeRest = 0.01f;
        private const int FreezesLogged = 5;

        // How often, in frames, each object is asked whether it is near; and what near is: within arm's reach
        // of the player, or within this many metres of the vehicle they drive plus what it covers in two seconds.
        private const int NearEvery = 6;
        private const float NearPlayer = 2.5f;
        private const float NearVehicle = 5f;
        private const float NearVehicleSeconds = 2f;

        // Loose things are root objects, or children of one of these containers ("MISC": the holiday things, like the
        // fireworks bag and the Christmas lights).
        private static readonly string[] Containers = { "ITEMS", "CARPARTS", "MISC" };

        // Or children of a place where the game puts what was paid for, to be picked up there: the repair shop's till moves
        // each new part it sells onto its bench ("REPAIRSHOP/LOD/Store/ShopCashRegister/Register :: Data", states "Spawn" to
        // "Spawn 15": SetParent to its "_SpawnPoint"; level file decoded on 2026-10-18). It is the one part of that name in
        // the world, wherever it was until then; hanging there, it was no longer shared, and only the buyer saw it on the
        // bench until they picked it up (2026-11-08).
        private static readonly string[][] PlacedSpots = { new[] { "REPAIRSHOP", "LOD", "Store", "PartSpawn" } };

        // Or children of where the game switches on the envelope of a mail order: on the parts magazine ("ITEMS/parts
        // magazine(itemx)/EnvelopeSpawn") and in the house's mailbox ("YARD/PlayerMailBox/EnvelopeComputerSpawn"); it hangs
        // there, with its own body, until a hand takes it (in the game on 2026-11-11).
        private static readonly string[] LetterSpots = { "EnvelopeSpawn", "EnvelopeComputerSpawn" };

        // Or children of a shelf of this container: the Satsuma's parts that nobody has moved yet lie on
        // "CARPARTS/PartsCar", "PartsMotor", "PartsGT" and "PartsExtra" (in the game on 2026-10-15: 122 of them).
        // Picked up and let go, the hand leaves them at the root.
        private const string ShelvesContainer = "CARPARTS";

        // Things of the jobs that players carry or push are the children of the containers the job files name ("loose" in
        // data/jobs, World.JobSync.LooseContainers): shared whatever their layer and tag (the hay bales are on a layer of
        // their own, and the drunk's furniture is not tagged as a part or an item; the septic wells' covers are lifted off
        // to pump them).

        // What the game tears loose from the world, under a name of its own at the root, shared whatever its layer and tag (the
        // video poker pulled out of the pub with a rope: "machine"; its machine sets the tag "RAGDOLL" and the layer 19, and in the
        // game with the bridge on 2026-10-17 it was left on 0, untagged).
        private static readonly string[] TornLoose = { "machine" };

        // The tools the hand drags by a joint, on a layer of their own (in the game on 2026-10-15: the motor hoist and the
        // floor jack, layer 15, tag "TOOL"; the hand does not take them up nor put them on its layer).
        private const string ToolTag = "TOOL";

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly List<Tracked> tracked = new List<Tracked>();
        private readonly Dictionary<int, Tracked> byBody = new Dictionary<int, Tracked>();

        // What another player's game made and this one made after it, by name: who made it, and until when that
        // is worth remembering.
        // What was made here because another player's game made it, by the object when it is known, and otherwise by name
        // (each expected once, in the order expected): two players can throw an empty bottle at the same moment, and the
        // game's own and the other's both turn up in one look (seen in a review on 2026-10-15).
        private readonly Dictionary<int, Born> bornThings = new Dictionary<int, Born>();
        private readonly List<Born> born = new List<Born>();
        private readonly List<Rigidbody> hungFound = new List<Rigidbody>();

        // The shared number a thing had when it stopped being loose (built into something, mounted), and the other way.
        private readonly Dictionary<int, ushort> forgottenIds = new Dictionary<int, ushort>();
        private readonly Dictionary<ushort, GameObject> forgottenThings = new Dictionary<ushort, GameObject>();

        // What other games ate or threw away, to be done to the copies here in the next frame.
        private readonly List<Gone> gone = new List<Gone>();
        private readonly GUIContent label = new GUIContent();
        private ObjectReplication current;
        private Transform player;
        private Joint hand;
        private Transform handAt;
        private bool handSearched;
        private double nextScan = double.NaN;
        private bool firstScanDone;
        private int frame;
        private int freezes;
        private GUIStyle labelStyle;
        private Camera camera;
        private bool asksForNear;
        private bool heldLayerReported;
        private bool ghostReported;
        private Tracked lastInHand;
        private HashSet<string> carParts;
        private double frameNow;
        private Vector3 nearPlayer;
        private bool drivesVehicle;
        private Vector3 nearVehicle;
        private float vehicleReach;

        public ObjectPresenter(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
        }

        /// <summary>Whether to draw, over each nearby shared object, who is simulating it.</summary>
        public bool ShowOwners { get; set; }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            ObjectReplication objects = localPlayer == null ? null : manager.Objects;
            if (objects != current || localPlayer != player)
            {
                // Another session, another world, or none: everything goes back to how the game had it.
                ReleaseAll();
                if (current != null)
                {
                    current.GoneWanted -= OnGoneWanted;
                    current.Subscribed -= OnSubscribed;
                    current.RunsVehicle = null;
                }

                current = objects;
                if (current != null)
                {
                    current.GoneWanted += OnGoneWanted;
                    current.Subscribed += OnSubscribed;
                    current.RunsVehicle = RunsHere;
                }

                AttachItems(objects == null ? null : manager.Items);

                player = localPlayer;
                nextScan = now + FirstScanDelay;
                firstScanDone = false;
                hand = null;
                handAt = null;
                handSearched = false;
                lastInHand = null;
            }

            if (current == null)
            {
                return;
            }

            frame++;
            frameNow = now;
            if (now >= nextScan)
            {
                nextScan = now + ScanInterval;
                Scan();
                if (firstScanDone && !current.WorldReady)
                {
                    // What this world has is all handed over: on the host, a list is now the whole of its world.
                    current.SetWorldReady();
                }
            }

            TakeFromSave();

            // Only a client asks: the host has everything that nobody else has.
            asksForNear = manager.Mode == NetworkMode.Client;
            if (asksForNear)
            {
                float speed;
                nearPlayer = player.position;
                drivesVehicle = vehicles.TryGetDriven(out nearVehicle, out speed);
                vehicleReach = NearVehicle + (speed * NearVehicleSeconds);
            }

            ThrowAway();
            current.SetHeld(InHand());

            // Unity's null check is also true for a destroyed object.
            vehicles.SetInHand(hand == null ? null : hand.connectedBody);
            current.Sample(now);
            ApplyItemNews();
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                Drive(tracked[i], i);
            }

            KeepWiringTool();
            WatchItems();
            TellJoined();

            camera = Camera.main;
        }

        /// <summary>Draws who simulates each nearby object. Only from OnGUI.</summary>
        public void OnGUI()
        {
            if (!ShowOwners || current == null || camera == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter, wordWrap = false, richText = false };
            }

            Vector3 eye = camera.transform.position;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked item = tracked[i];
                if (!item.Shared.HasId || item.Body == null)
                {
                    continue;
                }

                Vector3 position = item.Transform.position;
                if ((position - eye).sqrMagnitude > LabelDistance * LabelDistance)
                {
                    continue;
                }

                Vector3 screen = camera.WorldToScreenPoint(position);
                if (screen.z <= 0f)
                {
                    continue;
                }

                label.text = item.Describe();
                Vector2 size = labelStyle.CalcSize(label);
                var area = new Rect(screen.x - (size.x * 0.5f), Screen.height - screen.y - (size.y * 0.5f), size.x, size.y);
                labelStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), label, labelStyle);
                labelStyle.normal.textColor = item.Shared.Role == BodyRole.Simulate ? Color.green : Color.yellow;
                GUI.Label(area, label, labelStyle);
            }
        }

        /// <summary>Looks for new loose objects in the next frame instead of waiting for the usual scan.</summary>
        public void ScanSoon(double now)
        {
            if (current != null && !(nextScan <= now))
            {
                nextScan = now;
            }
        }

        /// <summary>
        /// An object called <paramref name="name"/> was made here because player <paramref name="player"/>'s
        /// game had just made it. On the host, when it is found it starts as that player's to simulate. When the object
        /// itself is known, the other overload says which.
        /// </summary>
        public void Expect(string name, byte player, double now)
        {
            Forget(now);
            born.Add(new Born { Name = name, Player = player, Until = now + BornSeconds });
        }

        /// <summary>That object was made here because player <paramref name="player"/>'s game had just made it.</summary>
        public void Expect(GameObject thing, byte player, double now)
        {
            if (thing == null)
            {
                return;
            }

            Forget(now);
            bornThings[thing.GetInstanceID()] = new Born { Name = thing.name, Player = player, Until = now + BornSeconds };
        }

        // What was expected and never turned up.
        private void Forget(double now)
        {
            for (int i = born.Count - 1; i >= 0; i--)
            {
                if (born[i].Until < now)
                {
                    born.RemoveAt(i);
                }
            }

            if (bornThings.Count == 0)
            {
                return;
            }

            List<int> old = null;
            foreach (KeyValuePair<int, Born> entry in bornThings)
            {
                if (entry.Value.Until < now)
                {
                    old = old ?? new List<int>();
                    old.Add(entry.Key);
                }
            }

            for (int i = 0; old != null && i < old.Count; i++)
            {
                bornThings.Remove(old[i]);
            }
        }

        private void OnGoneWanted(SharedBody body, byte player)
        {
            gone.Add(new Gone { Body = body, Player = player });
        }

        // The copies here of what other games ate or threw away go the same way: the game's own, if the thing has
        // one, so that the save of this machine says it is gone; otherwise it is switched off.
        private void ThrowAway()
        {
            for (int g = 0; g < gone.Count; g++)
            {
                Tracked item = null;
                for (int i = 0; i < tracked.Count; i++)
                {
                    if (tracked[i].Shared == gone[g].Body)
                    {
                        item = tracked[i];
                        break;
                    }
                }

                // Unity's null check is also true for a destroyed object.
                if (item == null || item.Transform == null)
                {
                    continue;
                }

                GameObject thing = item.Transform.gameObject;
                Restore(item);
                PlayMakerFSM use = GarbageMachine(thing);
                if (use != null)
                {
                    use.SendEvent(GarbageEvent);
                }
                else
                {
                    thing.SetActive(false);
                }

                log.Info(
                    LogCategory.Authority,
                    "'" + thing.name + "' was eaten or thrown away in player " + gone[g].Player + "'s game: "
                    + (use != null ? "thrown away here the game's way (" + GarbageEvent + ")." : "it has no way of its own to go, so it is switched off here."));
            }

            gone.Clear();
        }

        private static PlayMakerFSM GarbageMachine(GameObject thing)
        {
            PlayMakerFSM[] machines = thing.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName != UseMachine)
                {
                    continue;
                }

                FsmTransition[] global = machines[i].Fsm.GlobalTransitions;
                for (int j = 0; global != null && j < global.Length; j++)
                {
                    if (global[j].EventName == GarbageEvent)
                    {
                        return machines[i];
                    }
                }
            }

            return null;
        }

        /// <summary>Gives every object back to the game, as it was.</summary>
        public void ReleaseAll()
        {
            ReleaseWiringTool();
            for (int i = 0; i < tracked.Count; i++)
            {
                Restore(tracked[i]);
            }

            tracked.Clear();
            byBody.Clear();
            born.Clear();
            bornThings.Clear();
            forgottenIds.Clear();
            forgottenThings.Clear();
            gone.Clear();
            joined.Clear();
            fromSave.Clear();
            fromSaveTaken = false;
            lastInHand = null;
            if (current != null)
            {
                current.ClearWorld();
            }
        }

        // Looks for things that can be picked up and are not known yet.
        private void Scan()
        {
            Rigidbody[] bodies = Object.FindObjectsOfType<Rigidbody>();
            int added = 0;
            int wrongLayer = 0;
            int wrongTag = 0;
            int notLoose = 0;
            int kinematic = 0;
            int driven = 0;

            // On the first look, where the left-out ones hang and what else the hand can take: for the log.
            Dictionary<string, int> hangers = firstScanDone ? null : new Dictionary<string, int>();
            Dictionary<string, int> otherLayers = firstScanDone ? null : new Dictionary<string, int>();
            hungFound.Clear();
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody body = bodies[i];
                if (byBody.ContainsKey(body.GetInstanceID()))
                {
                    continue;
                }

                // In the local player's hand already: on the hand's layer, hanging from the player.
                GameObject go = body.gameObject;
                bool inHand = go.layer == HeldLayer && body.transform.IsChildOf(player);
                bool ofJob = !inHand && InJobContainer(body.transform);
                bool tool = !inHand && !ofJob && go.layer == ToolLayer && go.tag == ToolTag;
                bool torn = !inHand && body.transform.parent == null && System.Array.IndexOf(TornLoose, go.name) >= 0;
                if (go.layer != PickableLayer && !inHand && !ofJob && !tool && !torn)
                {
                    wrongLayer++;
                    if (otherLayers != null && !body.isKinematic && (go.layer == ToolLayer || go.layer == OtherPickLayer))
                    {
                        Count(otherLayers, "layer " + go.layer + " '" + go.tag + "' " + go.name);
                    }

                    continue;
                }

                if (body.isKinematic)
                {
                    kinematic++;
                    continue;
                }

                string tag = go.tag;
                if (tag != PartTag && tag != ItemTag && !ofJob && !tool && !torn)
                {
                    wrongTag++;
                    continue;
                }

                // The moped is on the layer of things the hand picks up, and tagged like one. It is shared as a
                // vehicle: shared twice, each side would be put where the other one has it (seen on 2026-10-13).
                Transform transform = body.transform;
                if (VehiclePresenter.IsVehicle(transform))
                {
                    driven++;
                    continue;
                }

                if (!inHand && !ofJob && !IsLoose(transform))
                {
                    if (body.GetComponent<Joint>() != null)
                    {
                        // Maybe hung from something loose: once this look knows all of those.
                        hungFound.Add(body);
                        continue;
                    }

                    notLoose++;
                    if (hangers != null)
                    {
                        Count(hangers, Hanger(transform));
                    }

                    continue;
                }

                Track(body, ofJob);
                added++;
            }

            // Hung by a joint from something loose, as the engine block from the hoist: as loose as that, held by the
            // game it hangs in (a saved game brings the block hanging, in every game; seen in a review on 2026-10-15).
            for (int i = 0; i < hungFound.Count; i++)
            {
                Rigidbody body = hungFound[i];
                if (HangsFromShared(body.transform))
                {
                    Track(body, false);
                    added++;
                    continue;
                }

                notLoose++;
                if (hangers != null)
                {
                    Count(hangers, Hanger(body.transform));
                }
            }

            hungFound.Clear();

            if (!firstScanDone || added > 0)
            {
                log.Info(
                    LogCategory.Authority,
                    (firstScanDone ? "Found " + added + " more loose object(s)" : "Found " + added + " loose object(s)") + ", " + tracked.Count
                    + " in all. Of " + bodies.Length + " active rigidbodies: " + wrongLayer + " not on layer " + PickableLayer + ", " + kinematic
                    + " kinematic, " + wrongTag + " tagged neither " + PartTag + " nor " + ItemTag + ", " + notLoose + " part of something else, "
                    + driven + " a vehicle.");
                if (hangers != null)
                {
                    log.Info(LogCategory.Authority, "Left out as part of something else, by what they hang from: " + Tally(hangers) + ".");
                    log.Info(LogCategory.Authority, "Bodies the hand can also reach on layers " + ToolLayer + " and " + OtherPickLayer + ", not shared as loose: " + Tally(otherLayers) + ".");
                }

                firstScanDone = true;
            }
        }

        private static void Count(Dictionary<string, int> counts, string key)
        {
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
        }

        private static string Tally(Dictionary<string, int> counts)
        {
            if (counts.Count == 0)
            {
                return "none";
            }

            var text = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, int> entry in counts)
            {
                text.Append(text.Length == 0 ? string.Empty : ", ").Append(entry.Key).Append(' ').Append(entry.Value);
            }

            return text.ToString();
        }

        // The first two levels of what a thing hangs from: "CARPARTS/PartsMotor", "SATSUMA(557kg, 248)/Body".
        private static string Hanger(Transform transform)
        {
            Transform parent = transform.parent;
            Transform top = parent;
            Transform below = null;
            while (top.parent != null)
            {
                below = top;
                top = top.parent;
            }

            return below == null ? top.name : top.name + "/" + below.name;
        }

        private void Track(Rigidbody body, bool ofJob)
        {
            GameObject go = body.gameObject;
            string name = go.name;
            byte bornWith = BornWith(go, name);
            if (bornWith != Protocol.HostPlayerId)
            {
                Vector3 at = body.transform.position;
                log.Info(
                    LogCategory.Authority,
                    "'" + name + "' at (" + at.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " + at.y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                    + at.z.ToString("0.0", CultureInfo.InvariantCulture) + ") was made here because player " + bornWith + "'s game made it.");
            }

            var item = new Tracked(body, current.AddLocal(name, PoseOf(body.transform), bornWith, KeyOf(go)));
            item.OfJob = ofJob;
            item.KeepWhenGone = IsCarPart(name) || World.MountSync.IsMountable(name, log);
            tracked.Add(item);
            byBody[body.GetInstanceID()] = item;
            NoteWiringMess(item);
        }

        // Whether something above it is a shared loose thing.
        private bool HangsFromShared(Transform transform)
        {
            for (Transform up = transform.parent; up != null; up = up.parent)
            {
                Rigidbody body = up.GetComponent<Rigidbody>();
                if (body != null && byBody.ContainsKey(body.GetInstanceID()))
                {
                    return true;
                }
            }

            return false;
        }

        // The player whose game made the object, if it was made here after theirs; the host otherwise. The object itself
        // first (or the root of what it is part of), then the oldest of that name expected by name only.
        private byte BornWith(GameObject thing, string name)
        {
            Born entry;
            int id = thing.GetInstanceID();
            int root = thing.transform.root.gameObject.GetInstanceID();
            if (bornThings.TryGetValue(id, out entry) || (root != id && bornThings.TryGetValue(root, out entry)))
            {
                bornThings.Remove(bornThings.ContainsKey(id) ? id : root);
                return frameNow <= entry.Until ? entry.Player : Protocol.HostPlayerId;
            }

            for (int i = 0; i < born.Count; i++)
            {
                if (born[i].Name == name)
                {
                    entry = born[i];
                    born.RemoveAt(i);
                    return frameNow <= entry.Until ? entry.Player : Protocol.HostPlayerId;
                }
            }

            return Protocol.HostPlayerId;
        }

        // Whether its parent is one of the jobs' containers. Run only when the scan finds a body it does not know.
        private static bool InJobContainer(Transform transform)
        {
            Transform parent = transform.parent;
            if (parent == null || parent.parent == null)
            {
                return false;
            }

            string[] containers = World.JobSync.LooseContainers;
            for (int i = 0; i < containers.Length; i++)
            {
                string path = containers[i];
                if (!path.EndsWith("/" + parent.name, System.StringComparison.Ordinal))
                {
                    continue;
                }

                // The whole path, from the parent up.
                string built = parent.name;
                for (Transform up = parent.parent; up != null; up = up.parent)
                {
                    built = up.name + "/" + built;
                }

                if (built == path)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLoose(Transform transform)
        {
            Transform parent = transform.parent;
            if (parent == null || IsPlacedSpot(parent))
            {
                return true;
            }

            // The envelope of a mail order lies where the game switches it on until someone takes it.
            if (System.Array.IndexOf(LetterSpots, parent.name) >= 0)
            {
                return true;
            }

            if (parent.parent != null)
            {
                // On a shelf of the car parts' container.
                Transform container = parent.parent;
                return container.parent == null && container.name == ShelvesContainer;
            }

            string name = parent.name;
            for (int i = 0; i < Containers.Length; i++)
            {
                if (name == Containers[i])
                {
                    return true;
                }
            }

            return false;
        }

        // The shared object the local player has in hand, or null.
        private SharedBody InHand()
        {
            if (hand == null)
            {
                if (!handSearched)
                {
                    handSearched = true;
                    handAt = player.Find(HandPath);
                    hand = handAt == null ? null : handAt.GetComponent<Joint>();
                    if (hand == null)
                    {
                        log.Warning(
                            LogCategory.Authority,
                            handAt == null
                                ? "The player has no '" + HandPath + "': picking things up will not be shared."
                                : "The player's hand has no joint yet: picking things up is not shared until it has one.");
                        return null;
                    }

                    log.Info(LogCategory.Authority, "The player's hand holds things with a " + hand.GetType().Name + " on '" + handAt.name + "'.");
                }
                else if (handAt != null)
                {
                    // The game destroys the hand's joint when it breaks (a body dragged by the hand that snags: "PickUp",
                    // "Ragdoll picked") and adds a new one ("Check joint", "State 2"). Unity's null check is also true
                    // for a destroyed object.
                    hand = handAt.GetComponent<Joint>();
                    if (hand == null)
                    {
                        lastInHand = null;
                        return null;
                    }

                    log.Info(LogCategory.Authority, "The player's hand has a new " + hand.GetType().Name + " (the game made it again).");
                }
                else
                {
                    return null;
                }
            }

            // Unity's null check is also true for a destroyed object.
            Rigidbody body = hand.connectedBody;
            if (body != null)
            {
                Tracked item;
                lastInHand = byBody.TryGetValue(body.GetInstanceID(), out item) ? item : null;
                return lastInHand == null ? null : lastInHand.Shared;
            }

            // Put away to be used as a tool, it is still in the hand, whatever the joint says meanwhile.
            if (lastInHand != null && IsPutAway(lastInHand))
            {
                return lastInHand.Shared;
            }

            lastInHand = null;
            return null;
        }

        // Whether the game has switched the thing off under the local player: the hand does that with what it
        // holds while the player uses it as a tool.
        private bool IsPutAway(Tracked item)
        {
            return item.Body != null && !item.Hidden && !item.Body.gameObject.activeInHierarchy && item.Transform.IsChildOf(player);
        }

        private void Drive(Tracked item, int index)
        {
            // Unity's null check is also true for a destroyed object.
            if (item.Body == null)
            {
                // The game eats a thing up or throws it away by taking its body, collider and looks off and moving
                // it out of sight, loose. Destroyed whole, or built into something, it is not that.
                if ((item.Transform != null && !IsBuiltIn(item)) || (item.Transform == null && item.Shared.Name == Firewood))
                {
                    current.Consume(item.Shared);
                }

                Forget(item, index);
                return;
            }

            NoticePlaced(item);
            Tracked hanger = null;
            if (IsBuiltIn(item))
            {
                hanger = HangerOf(item);
                if (hanger == null)
                {
                    // Built into a car, the game has made it kinematic or not as it wants it there (the engine block
                    // bolted to its mounts is kinematic; a door on its hinge is not): that is left as it is.
                    if (vehicles.RootOf(item.Transform) != null)
                    {
                        item.MadeKinematic = false;
                    }

                    Forget(item, index);
                    return;
                }
            }

            if (hanger != item.HungFrom)
            {
                // The engine block on the hoist: still loose, but this machine holds it while it hangs.
                log.Info(
                    LogCategory.Authority,
                    hanger != null
                        ? "'" + item.Shared.Name + "' hangs from '" + hanger.Shared.Name + "' in this game: shared as held here while it does."
                        : "'" + item.Shared.Name + "' no longer hangs from '" + item.HungFrom.Shared.Name + "' in this game.");
                item.HungFrom = hanger;
                current.SetHung(item.Shared, hanger != null);
            }
            else if (hanger != null)
            {
                current.SetHung(item.Shared, true);
            }

            bool putAway = false;
            if (!item.Hidden && !item.Body.gameObject.activeInHierarchy)
            {
                if (!item.Transform.IsChildOf(player))
                {
                    // Switched off by the game: eaten, drunk, used up.
                    Forget(item, index);
                    return;
                }

                putAway = true;
            }

            if (putAway != item.PutAway)
            {
                item.PutAway = putAway;
                log.Info(
                    LogCategory.Authority,
                    putAway
                        ? "The hand put '" + item.Shared.Name + "' away to use it as a tool. It is still in the hand: the others go on seeing it there."
                        : "'" + item.Shared.Name + "' is no longer put away.");
            }

            SharedBody shared = item.Shared;
            if (asksForNear && (frame + item.Stagger) % NearEvery == 0)
            {
                ushort carrier;
                Transform carrierRoot;
                Vector3 at = item.Transform.position;
                ushort ridden = shared.Role == BodyRole.Follow && shared.HasTarget ? shared.Target.Parent : (ushort)0;
                if (ridden != 0 && !RunsHere(ridden))
                {
                    // Its owner has it on a vehicle that moves on another machine: theirs, wherever this machine shows it for
                    // a frame. Shown a frame behind this copy of the vehicle, or left behind when the copy is put where its
                    // owner has it, it can look just out of the vehicle, next to what this player drives, and was asked for:
                    // a jerrycan on a tractor driven by the host jolted through it in the host's game while a guest rode a
                    // moped beside it (2026-11-09).
                }
                else if (vehicles.TryGetCarrier(at, out carrier, out carrierRoot) && !RunsHere(carrier))
                {
                    // In a vehicle someone else moves: theirs to simulate, not this machine's.
                    current.GiveBack(shared);
                }
                else
                {
                    // Something another machine has on its way somewhere is left to it until it has settled:
                    // taken in flight, it would start again from where it is shown here, standing still.
                    bool near = IsNear(at);
                    current.SetNear(shared, near && (shared.Role != BodyRole.Follow || item.Shown.Settled(frameNow)));
                }
            }

            switch (shared.Role)
            {
                case BodyRole.Simulate:
                    Simulate(item, shared);
                    break;

                case BodyRole.Follow:
                    Follow(item, shared);
                    break;

                default:
                    Restore(item);
                    break;
            }
        }

        // Whether the thing is farther than that from the local player (never, without one).
        private bool Farther(Tracked item, float distance)
        {
            return player != null && (item.Transform.position - player.position).sqrMagnitude > distance * distance;
        }

        private void Simulate(Tracked item, SharedBody shared)
        {
            Unhide(item);
            PassThroughVehicles(item, false);
            Solid(item);
            Rigidbody body = item.Body;
            if (item.FrozenFar)
            {
                if (!shared.Held && !shared.Hung && Farther(item, ThawNear))
                {
                    // Still far: it lies where it is, said to be at rest now and then.
                    if ((frame + item.Stagger) % QuietEvery == 0)
                    {
                        shared.Report(PoseOf(item.Transform), default(BodyMotion), false);
                    }

                    return;
                }

                // Its physics back, below, as for a thing this machine takes over.
                item.FrozenFar = false;
            }

            // Switched off by the hand, its body is not in the physics: only its place is read.
            bool live = !item.PutAway;
            bool started = item.MadeKinematic;
            ushort rode = item.ShownOn;
            if (started)
            {
                item.MadeKinematic = false;
                body.isKinematic = false;
            }

            item.ShownOn = 0;
            item.Shown.Forget();
            BodyPose pose;
            BodyMotion motion;
            if (shared.TakeHandOff(out pose, out motion))
            {
                var position = new Vector3(pose.X, pose.Y, pose.Z);
                var rotation = new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW);
                var velocity = new Vector3(motion.VX, motion.VY, motion.VZ);
                var spin = new Vector3(motion.AX, motion.AY, motion.AZ);
                bool placed = pose.Parent == 0;
                Transform handedOn = placed ? null : vehicles.RootOf(pose.Parent);
                if (handedOn != null)
                {
                    // Handed over on a vehicle: it goes on from that place on this machine's copy of it, moving
                    // as it moved relative to it.
                    Quaternion heading = handedOn.rotation;
                    position = handedOn.TransformPoint(position);
                    rotation = heading * rotation;
                    Vector3 carried = vehicles.VelocityAt(pose.Parent, position);
                    log.Info(
                        LogCategory.Authority,
                        "Took over '" + shared.Name + "' on vehicle " + pose.Parent + ", at (" + Format(pose.X) + ", " + Format(pose.Y) + ", " + Format(pose.Z)
                        + ") of it, moving at " + Format(velocity.magnitude) + " m/s relative to it; the vehicle does " + Format(carried.magnitude) + " m/s here.");
                    velocity = (heading * velocity) + carried;
                    spin = heading * spin;
                    placed = true;
                }

                if (placed && live)
                {
                    item.Transform.position = position;
                    item.Transform.rotation = rotation;
                    if (!body.isKinematic)
                    {
                        body.velocity = velocity;
                        body.angularVelocity = spin;
                        body.WakeUp();
                    }
                }

                item.Quiet = false;
            }
            else if (started && live && rode != 0)
            {
                // Taken without being handed over, and shown riding a vehicle until now: it starts at the
                // vehicle's speed, not standing still in the world with the vehicle driving into it.
                Vector3 carried = vehicles.VelocityAt(rode, item.Transform.position);
                body.velocity = carried;
                log.Info(
                    LogCategory.Authority,
                    "'" + shared.Name + "' was riding vehicle " + rode + " and is simulated here from now on: it starts at the vehicle's "
                    + Format(carried.magnitude) + " m/s.");
            }

            // Lying still far from the local player (handed back just now, or left there): held where it lies.
            if (live && !shared.Held && !shared.Hung && !body.isKinematic && body.velocity.sqrMagnitude < FreezeRest
                && body.angularVelocity.sqrMagnitude < FreezeRest && Farther(item, FreezeFar))
            {
                body.isKinematic = true;
                item.MadeKinematic = true;
                item.FrozenFar = true;
                shared.Report(PoseOf(item.Transform), default(BodyMotion), false);
                if (freezes++ < FreezesLogged)
                {
                    Vector3 lying = item.Transform.position;
                    log.Info(
                        LogCategory.Authority,
                        "'" + shared.Name + "' lies still at (" + Format(lying.x) + ", " + Format(lying.y) + ", " + Format(lying.z) + "), more than "
                        + FreezeFar.ToString("0", CultureInfo.InvariantCulture) + " m from this game's player: held where it lies until someone comes near"
                        + (freezes == FreezesLogged ? " (no more of these lines this session)." : "."));
                }

                return;
            }

            // Something lying still is looked at now and then, not on every frame: there are hundreds of them.
            if (item.Quiet && !shared.Held && (frame + item.Stagger) % QuietEvery != 0)
            {
                return;
            }

            bool awake = live && !body.isKinematic && !body.IsSleeping();
            if (awake)
            {
                Vector3 velocity = body.velocity;
                Vector3 spin = body.angularVelocity;
                motion.VX = velocity.x;
                motion.VY = velocity.y;
                motion.VZ = velocity.z;
                motion.AX = spin.x;
                motion.AY = spin.y;
                motion.AZ = spin.z;
            }
            else
            {
                motion = default(BodyMotion);
            }

            ushort carrier;
            Transform carrierRoot;
            Vector3 at = item.Transform.position;
            if ((awake || shared.Held) && vehicles.TryGetCarrier(at, out carrier, out carrierRoot))
            {
                Quaternion toVehicle = Quaternion.Inverse(carrierRoot.rotation);
                Vector3 place = carrierRoot.InverseTransformPoint(at);
                Quaternion turned = toVehicle * item.Transform.rotation;
                if (awake)
                {
                    // Relative to the vehicle and in its axes, like the place: it is what the thing is handed
                    // over with.
                    Vector3 moving = toVehicle * (new Vector3(motion.VX, motion.VY, motion.VZ) - vehicles.VelocityAt(carrier, at));
                    Vector3 spinning = toVehicle * new Vector3(motion.AX, motion.AY, motion.AZ);
                    motion.VX = moving.x;
                    motion.VY = moving.y;
                    motion.VZ = moving.z;
                    motion.AX = spinning.x;
                    motion.AY = spinning.y;
                    motion.AZ = spinning.z;
                }

                shared.ReportCarried(
                    PoseOf(item.Transform), carrier, new BodyPose(place.x, place.y, place.z, turned.x, turned.y, turned.z, turned.w), motion, awake);
            }
            else
            {
                shared.Report(PoseOf(item.Transform), motion, awake);
            }

            item.Quiet = !awake && !shared.Held;
            item.AtRest = false;
        }

        private void Follow(Tracked item, SharedBody shared)
        {
            Rigidbody body = item.Body;
            if (!shared.Present)
            {
                // The host's world no longer has it. A part of the car is left where it was last shown: the host's
                // game has built it in, and this one builds it in too (the assembly), where it is.
                if (!item.Hidden && !item.KeepWhenGone)
                {
                    item.Hidden = true;
                    body.gameObject.SetActive(false);
                }

                return;
            }

            Unhide(item);
            item.FrozenFar = false;
            if (!body.isKinematic)
            {
                item.MadeKinematic = true;
                body.isKinematic = true;
            }

            if (!shared.HasTarget)
            {
                return;
            }

            item.Shown.Note(shared.Target, shared.Held, frameNow);
            PassThroughVehicles(item, shared.Held || (shared.Target.Parent != 0 && vehicles.Simulates(shared.Target.Parent)));
            item.ShownOn = 0;
            if (shared.Target.Parent != 0)
            {
                // It rides a vehicle: every frame it goes where this machine's copy of the vehicle is.
                Transform carrier = vehicles.RootOf(shared.Target.Parent);
                if (carrier != null)
                {
                    BodyPose place = shared.Target;
                    item.Transform.position = carrier.TransformPoint(new Vector3(place.X, place.Y, place.Z));
                    item.Transform.rotation = carrier.rotation * new Quaternion(place.QX, place.QY, place.QZ, place.QW);
                    shared.NoteWorldPose(PoseOf(item.Transform));
                    item.ShownOn = shared.Target.Parent;
                    item.AtRest = false;
                    item.Quiet = false;
                }
            }
            else
            {
                // Once it is at rest where its owner left it, it is not touched again until it moves.
                bool atRest = shared.AtRest;
                if (!atRest || !item.AtRest)
                {
                    BodyPose target = shared.Target;
                    item.Transform.position = new Vector3(target.X, target.Y, target.Z);
                    item.Transform.rotation = new Quaternion(target.QX, target.QY, target.QZ, target.QW);
                    item.AtRest = atRest;
                    item.Quiet = false;
                }
            }

            // Kinematic here, it would win against anything this machine simulates: a vehicle must not find it
            // in its way, wherever its owner's machine has it. Looked at on every frame, at rest too: it is the
            // vehicle that comes to it.
            bool ghost = vehicles.SimulatesAny && vehicles.TouchesSimulated(item.Transform.position);
            if (ghost != item.Ghost)
            {
                item.Ghost = ghost;
                body.detectCollisions = !ghost;
                if (ghost && !ghostReported)
                {
                    ghostReported = true;
                    log.Info(
                        LogCategory.Authority,
                        "'" + shared.Name + "', which another machine simulates, is within reach of a vehicle simulated here: it touches nothing here "
                        + "until it is away from it. The same goes for every such thing from now on, without another line in the log.");
                }
            }
        }

        // Whether a client should be the one to run the physics of something at that place.
        private bool IsNear(Vector3 position)
        {
            // What lies in a vehicle goes with whoever moves the vehicle, however close anyone else stands.
            ushort carrier;
            Transform carrierRoot;
            if (vehicles.TryGetCarrier(position, out carrier, out carrierRoot))
            {
                return RunsHere(carrier);
            }

            if ((position - nearPlayer).sqrMagnitude <= NearPlayer * NearPlayer)
            {
                return true;
            }

            return drivesVehicle && (position - nearVehicle).sqrMagnitude <= vehicleReach * vehicleReach;
        }

        // Whether the physics of the vehicle with that number runs here: driven from here, or simulated here without anyone
        // at the wheel (pushed by this player, or nearest to this one; VehiclePresenter.Nearest). What lies in it goes with
        // that. Taken only by whoever drives, a boat pushed from a client floated off from under what it carried in the
        // host's game, where the boat followed and the things in it did not; and a car parked next to a client kept what
        // was in it on the host's, on the layer of what someone holds, which no hand picks things from (2026-11-08).
        private bool RunsHere(ushort vehicle)
        {
            return vehicles.Drives(vehicle) || vehicles.Simulates(vehicle);
        }

        // The game of this machine has just put the thing on one of the places where it puts what is paid for: this machine
        // simulates it from there (ObjectReplication.Place). Otherwise, where the owner has it, the copy here would go back
        // there in the next frame, and the other games would never see it on the bench.
        private void NoticePlaced(Tracked item)
        {
            Transform parent = item.Transform.parent;
            if (item.ParentSeen && parent == item.LastParent)
            {
                return;
            }

            bool before = item.ParentSeen;
            item.ParentSeen = true;
            item.LastParent = parent;
            if (!before || parent == null || !item.Shared.HasId || !IsPlacedSpot(parent))
            {
                return;
            }

            current.Place(item.Shared, PoseOf(item.Transform));
            Vector3 at = item.Transform.position;
            log.Info(
                LogCategory.Authority,
                "This game put '" + item.Shared.Name + "' on '" + parent.name + "' at (" + Format(at.x) + ", " + Format(at.y) + ", " + Format(at.z) + "): simulated here from there, "
                + "and seen there in the other games.");
        }

        // Whether a thing that hangs from that object is put there to be picked up (PlacedSpots).
        private static bool IsPlacedSpot(Transform spot)
        {
            for (int i = 0; i < PlacedSpots.Length; i++)
            {
                string[] path = PlacedSpots[i];
                Transform at = spot;
                int n = path.Length - 1;
                while (n >= 0 && at != null && at.name == path[n])
                {
                    at = at.parent;
                    n--;
                }

                if (n < 0 && at == null)
                {
                    return true;
                }
            }

            return false;
        }

        // Bolted onto a car, put in a box: no longer something lying around. In the local player's hand it hangs
        // from the player, and that is still loose.
        private bool IsBuiltIn(Tracked item)
        {
            // The player may be a child of a vehicle they ride: what they carry still hangs from them.
            Transform parent = item.Transform.parent;
            if (parent == null || item.Transform.IsChildOf(player))
            {
                return false;
            }

            // A job's thing hangs from its job's container, and is loose there; if the game moves it elsewhere, as loose
            // as anything else. Unity's comparison is also true for a destroyed parent.
            if (parent != item.CheckedParent)
            {
                item.CheckedParent = parent;
                item.BuiltInThere = !(item.OfJob && parent == item.JobParent) && !IsLoose(item.Transform);
            }

            return item.BuiltInThere;
        }

        // The loose thing that a thing hangs from by a joint, as the game hangs the engine block from the hoist (dump of
        // 2026-10-14, "motor hoist(itemx)/Hoist :: Hook": the block is made a child of the hook, "Hoist", and given a
        // FixedJoint to it); null if it does not. Asked only of what the game has made a child of something.
        private Tracked HangerOf(Tracked item)
        {
            if (item.Body.GetComponent<Joint>() == null)
            {
                return null;
            }

            for (Transform up = item.Transform.parent; up != null; up = up.parent)
            {
                Rigidbody body = up.GetComponent<Rigidbody>();
                Tracked hanger;
                if (body != null && byBody.TryGetValue(body.GetInstanceID(), out hanger))
                {
                    return hanger;
                }
            }

            return null;
        }

        // Whether things of that name are parts of the Satsuma (data/satsuma/parts.json): the loose part, the one the game
        // makes when one comes off, and what each slot takes.
        private bool IsCarPart(string name)
        {
            if (carParts == null)
            {
                carParts = new HashSet<string>();
                SatsumaCatalog catalog = SatsumaParts.Load(log);
                PartEntry[] entries = catalog == null ? null : catalog.Entries;
                for (int i = 0; entries != null && i < entries.Length; i++)
                {
                    AddName(entries[i].Part);
                    AddName(entries[i].Spawn);
                    PartTrigger[] triggers = entries[i].Triggers;
                    for (int t = 0; triggers != null && t < triggers.Length; t++)
                    {
                        AddName(triggers[t].Part);
                    }
                }
            }

            return carParts.Contains(name);
        }

        private void AddName(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                carParts.Add(name);
            }
        }

        private void Forget(Tracked item, int index)
        {
            // Unity's null check is also true for a destroyed object.
            if (item.Shared.HasId && item.Transform != null)
            {
                GameObject thing = item.Transform.gameObject;
                forgottenIds[thing.GetInstanceID()] = item.Shared.Id;
                forgottenThings[item.Shared.Id] = thing;
            }

            Restore(item);
            if (lastInHand == item)
            {
                lastInHand = null;
            }

            if (wiringMess == item)
            {
                ReleaseWiringTool();
            }

            byBody.Remove(item.BodyId);
            tracked.RemoveAt(index);
            current.RemoveLocal(item.Shared);
        }

        // Puts a thing that another machine simulates on the layer of things in the hand, or back on its own.
        private void PassThroughVehicles(Tracked item, bool pass)
        {
            if (pass == item.OnHeldLayer)
            {
                return;
            }

            GameObject thing = item.Body.gameObject;
            if (pass)
            {
                // On the hand's layer already, it was in the local hand a moment ago (two grabbed it at once and the host
                // gave it to the other): back on the layer the hand picks things from, not that one, or no hand could
                // take it again (seen in a review on 2026-10-15).
                item.LayerBefore = thing.layer != HeldLayer ? thing.layer : thing.CompareTag(ToolTag) ? ToolLayer : PickableLayer;
                item.OnHeldLayer = true;
                thing.layer = HeldLayer;
                if (!heldLayerReported)
                {
                    heldLayerReported = true;
                    var ignored = new System.Text.StringBuilder();
                    for (int layer = 0; layer < 32; layer++)
                    {
                        if (Physics.GetIgnoreLayerCollision(HeldLayer, layer))
                        {
                            ignored.Append(ignored.Length == 0 ? string.Empty : ", ").Append(layer);
                        }
                    }

                    log.Info(
                        LogCategory.Authority,
                        "What someone else holds is put on layer " + HeldLayer + ", like what the local hand holds. The game makes that layer pass through layers: "
                        + (ignored.Length == 0 ? "none" : ignored.ToString()) + ".");
                }
            }
            else
            {
                item.OnHeldLayer = false;
                thing.layer = item.LayerBefore;
            }
        }

        private static void Restore(Tracked item)
        {
            if (item.Body == null)
            {
                return;
            }

            if (item.OnHeldLayer)
            {
                item.OnHeldLayer = false;
                item.Body.gameObject.layer = item.LayerBefore;
            }

            Unhide(item);
            Solid(item);
            item.ShownOn = 0;
            item.FrozenFar = false;
            if (item.MadeKinematic)
            {
                item.MadeKinematic = false;
                item.Body.isKinematic = false;
                if (item.Body.gameObject.activeInHierarchy)
                {
                    item.Body.WakeUp();
                }
            }
        }

        private static void Unhide(Tracked item)
        {
            if (item.Hidden)
            {
                item.Hidden = false;
                item.Body.gameObject.SetActive(true);

                // Unity builds the body again when its object is switched on: whatever was asked of the old one
                // is asked again by the next frame that wants it.
                Solid(item);
            }
        }

        // Gives a thing its collisions back, if the mod took them away.
        private static void Solid(Tracked item)
        {
            if (item.Ghost)
            {
                item.Ghost = false;
                item.Body.detectCollisions = true;
            }
        }

        private static string Format(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static BodyPose PoseOf(Transform transform)
        {
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            return new BodyPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
        }

        private struct Gone
        {
            public SharedBody Body;
            public byte Player;
        }

        private struct Born
        {
            public string Name;
            public byte Player;
            public double Until;
        }

        private sealed class Tracked
        {
            private string description;

            /// <summary>The thing's own state machine "Use", once looked for; null if it has none.</summary>
            public PlayMakerFSM Use;

            // A can's fluid: the machine "Data" of its child "FluidTrigger" (or the like: ContentsChildren).
            public PlayMakerFSM Contents;

            public bool ContentsSearched;

            public bool UseSearched;

            // The coffee pan's own: its machine "Data", and its lid's "Use" (ObjectPresenter.Coffee).
            public PlayMakerFSM Pan;

            public PlayMakerFSM Cap;

            public bool PanSearched;

            public bool CapHooked;

            public bool CapComplained;

            /// <summary>
            /// One of the jobs' things (a hay bale, the drunk's furniture), found in its job's container
            /// (<see cref="JobParent"/>): loose there whatever its layer and tag.
            /// </summary>
            public bool OfJob
            {
                get { return JobParent != null; }
                set { JobParent = value ? Transform.parent : null; }
            }

            public Transform JobParent { get; private set; }

            /// <summary>A part of the car: left where it is shown when the host's world no longer has it loose.</summary>
            public bool KeepWhenGone { get; set; }

            /// <summary>The loose thing this one hangs from in this game (the hoist, for the engine block), or null.</summary>
            public Tracked HungFrom { get; set; }

            /// <summary>
            /// The parent it had when last asked whether that builds it into something, and the answer: asked in every
            /// frame, and a name read from Unity is a new string.
            /// </summary>
            public Transform CheckedParent;

            public bool BuiltInThere;

            /// <summary>The parent it had at the last look, to see the game put it somewhere (<see cref="ParentSeen"/>: looked once).</summary>
            public Transform LastParent;

            public bool ParentSeen;

            /// <summary>Its name and values as last told or heard, to see what changed; null before the first look.</summary>
            public string Signature;

            public double LastTold = double.NegativeInfinity;
            private byte describedOwner = byte.MaxValue;
            private bool describedHeld;
            private BodyRole describedRole;

            public Tracked(Rigidbody body, SharedBody shared)
            {
                Body = body;
                BodyId = body.GetInstanceID();
                Transform = body.transform;
                Shared = shared;
                Shown = new Stillness();
                Stagger = BodyId & 0x7FFF;
            }

            public Rigidbody Body { get; private set; }

            public int BodyId { get; private set; }

            public Transform Transform { get; private set; }

            public SharedBody Shared { get; private set; }

            /// <summary>Spreads the occasional look at resting objects over the frames.</summary>
            public int Stagger { get; private set; }

            /// <summary>Made kinematic by the mod, not by the game: to be undone.</summary>
            public bool MadeKinematic { get; set; }

            /// <summary>Whether the mod moved it to the layer of things in the hand, and from which layer.</summary>
            public bool OnHeldLayer { get; set; }

            public int LayerBefore { get; set; }

            /// <summary>Switched off by the mod because the host's world no longer has it.</summary>
            public bool Hidden { get; set; }

            /// <summary>Whether the mod switched its collisions off, near a vehicle simulated here.</summary>
            public bool Ghost { get; set; }

            /// <summary>Switched off by the game under the local player: put away by the hand to be used as a tool.</summary>
            public bool PutAway { get; set; }

            /// <summary>While following: the vehicle it is shown riding, as the vehicles' replication numbers it, or 0.</summary>
            public ushort ShownOn { get; set; }

            /// <summary>While following: for how long it has been shown standing still.</summary>
            public Stillness Shown { get; private set; }

            public bool Quiet { get; set; }

            /// <summary>Held where it lies, kinematic, because it lies far from the local player (FreezeFar).</summary>
            public bool FrozenFar { get; set; }

            public bool AtRest { get; set; }

            /// <summary>"#12 beer case(itemx): you, in hand". Rebuilt only when it changes.</summary>
            public string Describe()
            {
                if (description == null || describedOwner != Shared.Owner || describedHeld != Shared.Held || describedRole != Shared.Role)
                {
                    describedOwner = Shared.Owner;
                    describedHeld = Shared.Held;
                    describedRole = Shared.Role;
                    string owner = Shared.Role == BodyRole.Simulate ? "you" : Shared.Owner == Protocol.HostPlayerId ? "host" : "player " + Shared.Owner.ToString(CultureInfo.InvariantCulture);
                    description = "#" + Shared.Id.ToString(CultureInfo.InvariantCulture) + " " + Shared.Name + ": " + owner + (Shared.Held ? ", in hand" : string.Empty);
                }

                return description;
            }
        }
    }
}
