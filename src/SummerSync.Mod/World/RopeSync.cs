using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Objects;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The towing ropes, the same on every machine: what one player ties between two hooks is tied in every game, and pulls
    /// where the physics of what it ties runs.
    ///
    /// What is assumed about the game, from the level file and the game with the bridge (docs/GAME_NOTES.md "Los remolques y
    /// la cuerda"): every player has one rope, an object below their camera ("TowingRope/Rope") with a state machine
    /// ("Status") that keeps which two hooks it is tied to ("Hook1", "Hook2") and, once both are, a spring joint on the first
    /// hook's car to the second's body ("Spring"), which breaks under a hard enough pull. A hook is a state machine ("Logic")
    /// on an object below a car (or the video poker's slot), whose click ties the rope's end there. The rope is drawn by a
    /// skinned mesh between its two ends.
    ///
    /// To the session a player's rope is the tally "rope/(player)": the two hooks it is tied to (by their place in the list of
    /// every hook of the world, which is the same in every game loaded from one save) and the joint's numbers. The other
    /// games draw it, and tie a joint like the game's between the same two bodies.
    ///
    /// A joint pulls in a game only where it can do what it does in the game of whoever runs the physics. The towing vehicle
    /// is the one tied at its rear hook (or the vehicle, against something that is not one). On the machine that simulates
    /// it, the joint pulls, and what it tows is held there (<see cref="ObjectReplication.SetHung"/>), unless someone drives it
    /// on another machine: then the joint is slack here, and pulls in that machine, on a copy of the towing vehicle. Elsewhere
    /// the joint pulls only what is each game's own (a junk car at the repair shop), and never what is held to the world (the
    /// video poker, until it breaks off where the physics runs). A joint the mod tied that breaks where it pulls snaps the
    /// rope in the game of its player too.
    /// </summary>
    internal sealed class RopeSync
    {
        private const string TallyPrefix = "rope/";
        private const string RopePath = "PLAYER/Pivot/AnimPivot/Camera/FPSCamera/TowingRope/Rope";
        private const string RopeMachine = "Status";
        private const string HookMachine = "Logic";
        private const string FirstEndVariable = "RopeFirst";
        private const string FrontHookName = "HookFront";
        private const string RearHookName = "HookRear";
        private const double SearchEvery = 3.0;

        // Where the game has hooks besides the vehicles: the junk cars at the repair shop, the rally car, the friends' cars,
        // the video poker in the pub, and the video poker once it broke loose.
        private static readonly string[] OtherRoots = { "REPAIRSHOP", "TRAFFIC", "NPC_CARS", "STORE", "machine" };
        private const string PokerPath = "STORE/LOD/VideoPoker";
        private const string BrokenPoker = "machine/";

        // How many times the rope's texture is laid along each metre of it, as the game does.
        private const float TilingPerMetre = 10f;

        // Where a rope's free end is in the hand of the player who carries it, from their feet.
        private const float HandHeight = 1.2f;
        private const float HandAhead = 0.4f;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly ObjectPresenter objects;
        private readonly List<Hook> hooks = new List<Hook>();
        private readonly Dictionary<byte, Rope> ropes = new Dictionary<byte, Rope>();
        private readonly List<byte> scratch = new List<byte>();
        private TallyReplication current;
        private NetSession session;
        private Transform player;
        private bool found;
        private double nextSearch;

        // The local player's rope.
        private PlayMakerFSM status;
        private FsmBool attached;
        private FsmGameObject hook1;
        private FsmGameObject hook2;
        private readonly Rope own = new Rope { Own = true };

        // How the game draws a rope: taken from the local player's, to draw the others'.
        private SkinnedMeshRenderer drawing;

        public RopeSync(Logger log, NetworkManager manager, VehiclePresenter vehicles, ObjectPresenter objects)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
            this.objects = objects;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null while there is no game world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current || localPlayer != player)
            {
                Release();
                current = tallies;
                player = localPlayer;
                session = current == null ? null : manager.Session;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }

                if (session != null)
                {
                    session.PlayerLeft += OnPlayerLeft;
                }

                nextSearch = now + SearchEvery;
            }

            if (current == null || session == null || session.State != SessionState.Connected)
            {
                return;
            }

            if (!found)
            {
                if (now < nextSearch)
                {
                    return;
                }

                nextSearch = now + SearchEvery;
                Find();
                if (!found)
                {
                    return;
                }
            }

            WatchOwn(session.LocalPlayerId);
            Pull(own);
            scratch.Clear();
            foreach (KeyValuePair<byte, Rope> entry in ropes)
            {
                scratch.Add(entry.Key);
            }

            for (int i = 0; i < scratch.Count; i++)
            {
                Rope rope = ropes[scratch[i]];
                Draw(rope);
                Pull(rope);
            }
        }

        /// <summary>
        /// Whether a rope is tied to that hook, this game's player's or another's, and, if one is, whether it pulls in this
        /// game (where the physics of what it ties runs).
        /// </summary>
        public bool Tied(Transform hook, out bool pullsHere)
        {
            pullsHere = false;
            bool tied = Ties(own, hook, ref pullsHere);
            foreach (KeyValuePair<byte, Rope> entry in ropes)
            {
                tied |= Ties(entry.Value, hook, ref pullsHere);
            }

            return tied;
        }

        private bool Ties(Rope rope, Transform hook, ref bool pullsHere)
        {
            bool first = rope.Hook1 >= 0 && rope.Hook1 < hooks.Count && hooks[rope.Hook1].Transform == hook;
            bool second = rope.Hook2 >= 0 && rope.Hook2 < hooks.Count && hooks[rope.Hook2].Transform == hook;
            if (!first && !second)
            {
                return false;
            }

            pullsHere |= rope.Hook2 >= 0 && rope.Pulls;
            return true;
        }

        /// <summary>Unties every rope the mod tied and lets go of what it held for them; the game's own rope stays as it is.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            current = null;
            if (session != null)
            {
                session.PlayerLeft -= OnPlayerLeft;
            }

            session = null;
            foreach (KeyValuePair<byte, Rope> entry in ropes)
            {
                Untie(entry.Value);
            }

            ropes.Clear();
            Slacken(own, false);
            Hang(own, false);
            own.Hook1 = -1;
            own.Hook2 = -1;
            own.Told = false;
            hooks.Clear();
            status = null;
            found = false;
        }

        // Every hook of the world, in the order of their paths: the same in every game loaded from one save. And the local
        // player's rope.
        private void Find()
        {
            hooks.Clear();
            GameObject rope = GameObject.Find(RopePath);
            Transform ropeObject = rope == null ? null : rope.transform;
            if (ropeObject == null)
            {
                // The rope is switched off until it is used; looked for below the camera, switched off or not.
                GameObject camera = GameObject.Find("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/TowingRope");
                ropeObject = camera == null ? null : camera.transform.Find("Rope");
            }

            status = ropeObject == null ? null : DoorSync.FindMachine(ropeObject, RopeMachine);
            attached = status == null ? null : status.FsmVariables.FindFsmBool("Attached");
            hook1 = status == null ? null : status.FsmVariables.FindFsmGameObject("Hook1");
            hook2 = status == null ? null : status.FsmVariables.FindFsmGameObject("Hook2");
            Transform first = ropeObject == null ? null : ropeObject.Find("RopeFirst");
            drawing = first == null ? null : first.GetComponent<SkinnedMeshRenderer>();
            if (attached == null || hook1 == null || hook2 == null)
            {
                log.Warning(LogCategory.Vehicle, "Ropes: the player's towing rope is not at '" + RopePath + "' with a machine '" + RopeMachine + "' as this build expects: ropes are each game's own.");
                found = true;
                status = null;
                return;
            }

            // Below the vehicles and the roots where the game has other hooks, switched off or not: what is switched off far
            // from the player (the pub, a friend's car) has to be in the list as much as in the other games'.
            var roots = new List<Transform>();
            VehicleDefinition[] catalog = VehiclePresenter.Catalog;
            for (int i = 0; i < catalog.Length; i++)
            {
                Transform root = VehiclePresenter.FindRoot(catalog[i].RootName);
                if (root != null)
                {
                    roots.Add(root);
                }
            }

            for (int i = 0; i < OtherRoots.Length; i++)
            {
                GameObject root = GameObject.Find(OtherRoots[i]);
                if (root != null && root.transform.parent == null)
                {
                    roots.Add(root.transform);
                }
            }

            var all = new List<KeyValuePair<string, Hook>>();
            for (int r = 0; r < roots.Count; r++)
            {
                PlayMakerFSM[] machines = roots[r].GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < machines.Length; i++)
                {
                    PlayMakerFSM machine = machines[i];
                    if (machine.FsmName != HookMachine || machine.FsmVariables.FindFsmGameObject(FirstEndVariable) == null)
                    {
                        continue;
                    }

                    Transform at = machine.transform;
                    var hook = new Hook { Transform = at, Machine = machine, Name = at.name, Body = BodyOf(at) };
                    all.Add(new KeyValuePair<string, Hook>(ListedPath(at), hook));
                }
            }

            all.Sort(delegate(KeyValuePair<string, Hook> a, KeyValuePair<string, Hook> b) { return string.CompareOrdinal(a.Key, b.Key); });
            var names = new StringBuilder();
            ulong shape = 14695981039346656037UL;
            for (int i = 0; i < all.Count; i++)
            {
                hooks.Add(all[i].Value);
                names.Append(i == 0 ? string.Empty : ", ").Append(all[i].Key);
                for (int c = 0; c < all[i].Key.Length; c++)
                {
                    shape = (shape ^ all[i].Key[c]) * 1099511628211UL;
                }
            }

            found = true;
            foreach (KeyValuePair<byte, Rope> entry in ropes)
            {
                if (entry.Value.Heard != null)
                {
                    Apply(entry.Value, entry.Value.Heard);
                }
            }

            log.Info(
                LogCategory.Vehicle,
                "Ropes: " + hooks.Count + " hook(s) in the world (shape " + (shape & 0xFFFFFFFFUL).ToString("x8", CultureInfo.InvariantCulture) + "): " + names
                + ". The rope is " + (drawing == null ? "NOT drawable here (no skinned mesh)" : "drawn with '" + drawing.name + "'") + ".");
        }

        // Where a hook is listed: by its path, and the video poker's slot by where it was before it broke loose, so that a
        // world saved with it broken off and one saved with it in the pub list their hooks the same.
        private static string ListedPath(Transform hook)
        {
            string path = PathOf(hook);
            return path.StartsWith(BrokenPoker, StringComparison.Ordinal) ? PokerPath + path.Substring(BrokenPoker.Length - 1) : path;
        }

        // The body a hook belongs to: the first one above it.
        private static Rigidbody BodyOf(Transform hook)
        {
            for (Transform at = hook; at != null; at = at.parent)
            {
                Rigidbody body = at.GetComponent<Rigidbody>();
                if (body != null)
                {
                    return body;
                }
            }

            return null;
        }

        private static string PathOf(Transform transform)
        {
            string path = transform.name;
            for (Transform at = transform.parent; at != null; at = at.parent)
            {
                path = at.name + "/" + path;
            }

            return path;
        }

        // The local player's rope: what it is tied to, told when that changes.
        private void WatchOwn(byte localId)
        {
            // Unity's null check is also true for a destroyed object.
            if (status == null)
            {
                return;
            }

            int first = -1;
            int second = -1;
            SpringJoint joint = null;
            if (attached.Value && hook1.Value != null)
            {
                first = IndexOf(hook1.Value.transform);
                if (hook2.Value != null)
                {
                    second = IndexOf(hook2.Value.transform);
                    Rigidbody body = first < 0 ? null : hooks[first].Body;
                    joint = body == null ? null : JointTo(body, second < 0 ? null : hooks[second].Body);
                }
            }

            if (second >= 0 && joint == null)
            {
                // Both ends are on hooks, and the game has not made the joint yet: a frame or two.
                second = -1;
            }

            if (joint != own.Joint)
            {
                // A new joint has the game's spring.
                own.Slack = false;
                own.Joint = joint;
                if (joint != null)
                {
                    own.Spring = joint.spring;
                }
            }

            if (own.Told && first == own.Hook1 && second == own.Hook2)
            {
                return;
            }

            if (own.Hook1 >= 0 && first < 0)
            {
                Hang(own, false);
                Slacken(own, false);
            }

            own.Hook1 = first;
            own.Hook2 = second;
            own.Told = true;
            int[] values = new int[6];
            values[0] = first + 1;
            values[1] = second + 1;
            if (joint != null)
            {
                values[2] = Mathf.RoundToInt(joint.maxDistance * 1000f);
                values[3] = Mathf.RoundToInt(joint.minDistance * 1000f);
                values[4] = Mathf.RoundToInt(own.Spring);
                values[5] = Mathf.RoundToInt(joint.damper * 100f);
            }

            current.Set(TallyPrefix + localId.ToString(CultureInfo.InvariantCulture), values);
            log.Info(
                LogCategory.Vehicle,
                "Ropes: the local player's rope is " + (first < 0 ? "not tied" : second < 0 ? "tied to " + Label(first) + ", the other end in the hand" : "tied between " + Label(first) + " and " + Label(second)
                + " (" + (joint.maxDistance).ToString("0.00", CultureInfo.InvariantCulture) + " m, spring " + joint.spring.ToString("0", CultureInfo.InvariantCulture) + ")") + "; the others are told.");
        }

        // The game's joint from one body to another, if it has one.
        private static SpringJoint JointTo(Rigidbody from, Rigidbody to)
        {
            SpringJoint[] joints = from.GetComponents<SpringJoint>();
            for (int i = 0; i < joints.Length; i++)
            {
                if (joints[i].connectedBody == to)
                {
                    return joints[i];
                }
            }

            return null;
        }

        private int IndexOf(Transform hook)
        {
            for (int i = 0; i < hooks.Count; i++)
            {
                if (hooks[i].Transform == hook)
                {
                    return i;
                }
            }

            return -1;
        }

        private string Label(int index)
        {
            Hook hook = index >= 0 && index < hooks.Count ? hooks[index] : null;

            // Unity's null check is also true for a destroyed object.
            return hook == null || hook.Transform == null ? "a hook this game does not have" : "'" + (hook.Transform.parent == null ? hook.Name : hook.Transform.parent.name + "/" + hook.Name) + "'";
        }

        // A player who leaves takes their rope with them; the host says so, so that whoever takes their number does not
        // inherit it.
        private void OnPlayerLeft(PlayerInfo left, DisconnectReason reason)
        {
            byte id = left.Id;
            Rope rope;
            if (ropes.TryGetValue(id, out rope))
            {
                Untie(rope);
                ropes.Remove(id);
            }

            if (session != null && session.Role == SessionRole.Host && current != null)
            {
                current.Set(TallyPrefix + id.ToString(CultureInfo.InvariantCulture), new int[6]);
            }
        }

        private void OnStateWanted(string name, int[] values)
        {
            if (!name.StartsWith(TallyPrefix, StringComparison.Ordinal) || values.Length < 6)
            {
                return;
            }

            int id;
            if (!int.TryParse(name.Substring(TallyPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id < 0 || id > 255)
            {
                return;
            }

            if (session != null && id == session.LocalPlayerId)
            {
                // Someone else's game says the local player's rope snapped where it pulled.
                if (values[0] == 0 && own.Hook2 >= 0 && own.Joint != null && current != null && !current.CatchingUp)
                {
                    log.Info(LogCategory.Vehicle, "Ropes: the local player's rope snapped where its physics runs: snapped here too.");
                    UnityEngine.Object.Destroy(own.Joint);
                    own.Joint = null;
                }

                return;
            }

            Rope rope;
            if (!ropes.TryGetValue((byte)id, out rope))
            {
                rope = new Rope { Player = (byte)id };
                ropes[(byte)id] = rope;
            }

            // Kept: heard before the hooks of this world are known, it is tied once they are.
            rope.Heard = values;
            if (found)
            {
                Apply(rope, values);
            }
        }

        private void Apply(Rope rope, int[] values)
        {
            byte id = rope.Player;
            int first = values[0] - 1;
            int second = values[1] - 1;
            if (first >= hooks.Count || second >= hooks.Count)
            {
                log.Warning(LogCategory.Vehicle, "Ropes: player " + id + "'s rope is tied to a hook this game does not have (" + hooks.Count + " here): it is not shown.");
                first = -1;
                second = -1;
            }

            if (first != rope.Hook1 || second != rope.Hook2 || values[2] != rope.MaxMillimetres)
            {
                Untie(rope);
                rope.Hook1 = first;
                rope.Hook2 = second;
                rope.MaxMillimetres = values[2];
                rope.MinMillimetres = values[3];
                rope.Spring = values[4];
                rope.Damper = values[5] / 100f;
                Tie(rope);
                log.Info(LogCategory.Vehicle, "Ropes: player " + id + "'s rope is " + (first < 0 ? "not tied" : second < 0 ? "tied to " + Label(first) + ", the other end in their hand" : "tied between " + Label(first) + " and " + Label(second)) + ".");
            }
        }

        // Makes the mod's own joint, as the game made the player's, and the rope to draw.
        private void Tie(Rope rope)
        {
            if (rope.Hook1 < 0)
            {
                return;
            }

            Hook first = hooks[rope.Hook1];
            Hook second = rope.Hook2 < 0 ? null : hooks[rope.Hook2];

            // Unity's null check is also true for a destroyed object.
            if (second != null && first.Body != null && second.Body != null && first.Transform != null && second.Transform != null)
            {
                SpringJoint joint = first.Body.gameObject.AddComponent<SpringJoint>();
                joint.autoConfigureConnectedAnchor = false;
                joint.connectedBody = second.Body;
                joint.anchor = first.Body.transform.InverseTransformPoint(first.Transform.position);
                joint.connectedAnchor = second.Body.transform.InverseTransformPoint(second.Transform.position);
                joint.enableCollision = true;
                joint.spring = rope.Spring;
                joint.damper = rope.Damper;
                joint.minDistance = rope.MinMillimetres / 1000f;
                joint.maxDistance = rope.MaxMillimetres / 1000f;
                joint.breakForce = BreakingForce();
                joint.breakTorque = BreakingForce();
                rope.Joint = joint;
                rope.Pulled = true;
            }

            rope.Drawn = MakeDrawing(rope);
        }

        // The game's own breaking force for a rope, from the local player's.
        private float BreakingForce()
        {
            FsmFloat force = status == null ? null : status.FsmVariables.FindFsmFloat("BreakingForce");
            return force == null ? 55000f : force.Value;
        }

        private void Untie(Rope rope)
        {
            Hang(rope, false);

            // Unity's null check is also true for a destroyed object.
            if (rope.Joint != null)
            {
                UnityEngine.Object.Destroy(rope.Joint);
            }

            rope.Joint = null;
            rope.Pulled = false;
            if (rope.Drawn != null)
            {
                UnityEngine.Object.Destroy(rope.Drawn.Root);
            }

            rope.Drawn = null;
        }

        // Whether the joint pulls here, and who holds what it tows (see the class's summary).
        private void Pull(Rope rope)
        {
            Hook first = rope.Hook1 >= 0 && rope.Hook1 < hooks.Count ? hooks[rope.Hook1] : null;
            Hook second = rope.Hook2 >= 0 && rope.Hook2 < hooks.Count ? hooks[rope.Hook2] : null;

            // Unity's null check is also true for a destroyed object.
            if (first == null || second == null || first.Body == null || second.Body == null)
            {
                Hang(rope, false);
                return;
            }

            if (!rope.Own && rope.Pulled && rope.Joint == null)
            {
                // The mod's joint broke here: if it pulled here, the rope snapped where its physics runs.
                rope.Pulled = false;
                if (rope.Pulls)
                {
                    log.Info(LogCategory.Vehicle, "Ropes: player " + rope.Player + "'s rope snapped here, where it pulls: they are told.");
                    current.Set(TallyPrefix + rope.Player.ToString(CultureInfo.InvariantCulture), new int[6]);
                }

                return;
            }

            // Looked at now and then: getting a body's joints makes garbage. Each end's body may be held to the world.
            if (Time.time >= rope.AnchorLookAt || rope.AnchorOf != first.Body || rope.SecondAnchorOf != second.Body)
            {
                rope.AnchorLookAt = Time.time + 0.5f;
                rope.AnchorOf = first.Body;
                rope.SecondAnchorOf = second.Body;
                rope.Anchored = Anchored(first.Body);
                rope.SecondAnchored = Anchored(second.Body);
            }

            RopePull pull = TowRules.Decide(EndOf(first, rope.Anchored), EndOf(second, rope.SecondAnchored));
            rope.Pulls = pull.Pulls;
            Slacken(rope, !pull.Pulls);
            Hang(rope, pull.HoldsTowed ? (pull.FirstTows ? second : first) : null);
        }

        private RopeEnd EndOf(Hook hook, bool anchored)
        {
            return new RopeEnd
            {
                OnVehicle = OnVehicle(hook),
                Rear = hook.Name == RearHookName,
                Front = hook.Name == FrontHookName,
                RunsHere = RunsHere(hook),
                Anchored = anchored,
            };
        }

        private bool OnVehicle(Hook hook)
        {
            ushort id;
            Transform root = vehicles.RootOf(hook.Body.transform);
            return root != null && vehicles.IdOf(root, out id);
        }

        // Whether a hook's body runs its physics in this game: a shared vehicle or thing this machine simulates, or one that
        // is each game's own and is not kinematic.
        private bool RunsHere(Hook hook)
        {
            Rigidbody body = hook.Body;
            Transform root = vehicles.RootOf(body.transform);
            ushort id;
            if (root != null && vehicles.IdOf(root, out id))
            {
                return vehicles.SimulatesRoot(root);
            }

            return !body.isKinematic;
        }

        // Held to the world by a joint with no body at its other end (the video poker before it breaks off).
        private static bool Anchored(Rigidbody body)
        {
            Joint[] joints = body.GetComponents<Joint>();
            for (int i = 0; i < joints.Length; i++)
            {
                if (joints[i].connectedBody == null && !(joints[i] is SpringJoint))
                {
                    return true;
                }
            }

            return false;
        }

        // A joint that must not pull here has no spring: what it was is put back when it may again.
        private static void Slacken(Rope rope, bool slack)
        {
            SpringJoint joint = rope.Joint;

            // Unity's null check is also true for a destroyed object.
            if (joint == null)
            {
                rope.Slack = false;
                return;
            }

            if (slack == rope.Slack)
            {
                return;
            }

            rope.Slack = slack;
            joint.spring = slack ? 0f : rope.Spring;
        }

        // While the towing vehicle runs here, what it tows is held here, if it is shared and nobody drives it elsewhere.
        private void Hang(Rope rope, Hook towed)
        {
            SharedBody body = null;
            ObjectReplication replication = null;

            // Unity's null check is also true for a destroyed object.
            if (towed != null && towed.Body != null)
            {
                Transform root = vehicles.RootOf(towed.Body.transform);
                ushort id;
                if (root != null && vehicles.IdOf(root, out id))
                {
                    replication = manager.Vehicles;
                    body = replication == null ? null : replication.Find(id);
                }
                else
                {
                    ushort thing = objects.SharedIdOf(towed.Body.gameObject);
                    replication = thing == 0 ? null : manager.Objects;
                    body = replication == null ? null : replication.Find(thing);
                }
            }

            if (rope.HungBody != null && rope.HungBody != body && rope.HungReplication != null)
            {
                rope.HungReplication.SetHung(rope.HungBody, false);
            }

            rope.HungBody = body;
            rope.HungReplication = replication;
            if (body != null)
            {
                replication.SetHung(body, true);
            }
        }

        private void Hang(Rope rope, bool hung)
        {
            if (!hung)
            {
                Hang(rope, null);
            }
        }

        // ---- Drawing another player's rope, as the game draws one ----

        private Drawing MakeDrawing(Rope rope)
        {
            // Unity's null check is also true for a destroyed object.
            if (drawing == null || drawing.sharedMesh == null)
            {
                return null;
            }

            var root = new GameObject("SummerSync rope of player " + rope.Player.ToString(CultureInfo.InvariantCulture));
            var made = new Drawing { Root = root };
            Transform[] bones = drawing.bones;
            Transform firstEnd = drawing.transform;
            made.First = new GameObject("first").transform;
            made.First.parent = root.transform;
            made.Second = new GameObject("second").transform;
            made.Second.parent = root.transform;
            var copies = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (bones[i] == null)
                {
                    copies[i] = made.First;
                    continue;
                }

                bool ofFirst = bones[i] == firstEnd || bones[i].IsChildOf(firstEnd);
                Transform end = ofFirst ? made.First : made.Second;
                Transform from = ofFirst ? firstEnd : bones[i].parent ?? firstEnd;
                var bone = new GameObject(bones[i].name).transform;
                bone.parent = end;
                bone.localPosition = ofFirst ? firstEnd.InverseTransformPoint(bones[i].position) : Vector3.zero;
                bone.localRotation = Quaternion.identity;
                copies[i] = bone;
            }

            var skin = made.First.gameObject.AddComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = drawing.sharedMesh;
            skin.bones = copies;
            skin.updateWhenOffscreen = true;
            Material[] materials = drawing.sharedMaterials;
            var own = new Material[materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                own[i] = materials[i] == null ? null : new Material(materials[i]);
            }

            skin.sharedMaterials = own;
            made.Skin = skin;
            return made;
        }

        private void Draw(Rope rope)
        {
            Drawing drawn = rope.Drawn;
            Hook first = rope.Hook1 >= 0 && rope.Hook1 < hooks.Count ? hooks[rope.Hook1] : null;

            // Unity's null check is also true for a destroyed object.
            if (drawn == null || drawn.Root == null || first == null || first.Transform == null)
            {
                return;
            }

            // The game hangs each end of the rope from its hook, turned as the hook is; the end in a hand, as the player looks.
            Vector3 to;
            Quaternion toTurn;
            Hook second = rope.Hook2 >= 0 && rope.Hook2 < hooks.Count ? hooks[rope.Hook2] : null;
            if (second != null && second.Transform != null)
            {
                to = second.Transform.position;
                toTurn = second.Transform.rotation;
            }
            else if (!HandOf(rope.Player, out to, out toTurn))
            {
                drawn.Root.SetActive(false);
                return;
            }

            if (!drawn.Root.activeSelf)
            {
                drawn.Root.SetActive(true);
            }

            drawn.First.position = first.Transform.position;
            drawn.First.rotation = first.Transform.rotation;
            drawn.Second.position = to;
            drawn.Second.rotation = toTurn;

            // The game stretches the rope's texture with its length, ten times it ("Check distance").
            Material material = drawn.Skin.sharedMaterial;
            if (material != null)
            {
                Vector2 scale = material.mainTextureScale;
                scale.y = (to - first.Transform.position).magnitude * TilingPerMetre;
                material.mainTextureScale = scale;
            }
        }

        // Where another player's hand is, as this machine shows them.
        private bool HandOf(byte id, out Vector3 hand, out Quaternion turned)
        {
            hand = Vector3.zero;
            turned = Quaternion.identity;
            PlayerReplication players = manager.Players;
            if (players == null)
            {
                return false;
            }

            IList<RemotePlayer> remotes = players.Remotes;
            for (int i = 0; i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                if (remote.Id != id || !remote.Visible)
                {
                    continue;
                }

                PlayerPose pose = remote.Shown;
                if (pose.Parent != 0 && !vehicles.ToWorld(ref pose))
                {
                    return false;
                }

                turned = Quaternion.Euler(pose.Pitch, pose.Yaw, 0f);
                hand = new Vector3(pose.X, pose.Y, pose.Z) + (Vector3.up * HandHeight) + (Quaternion.Euler(0f, pose.Yaw, 0f) * Vector3.forward * HandAhead);
                return true;
            }

            return false;
        }

        private sealed class Hook
        {
            public Transform Transform { get; set; }

            public PlayMakerFSM Machine { get; set; }

            public string Name { get; set; }

            /// <summary>The body a rope tied to it pulls: the first above it.</summary>
            public Rigidbody Body { get; set; }
        }

        private sealed class Rope
        {
            /// <summary>The local player's own rope, which is the game's.</summary>
            public bool Own { get; set; }

            public byte Player { get; set; }

            /// <summary>The hooks it is tied to, by their place in the list; -1 for none (the second: the end is in the hand).</summary>
            public int Hook1 { get; set; } = -1;

            public int Hook2 { get; set; } = -1;

            public bool Told { get; set; }

            /// <summary>For another player's rope: what the session last said of it.</summary>
            public int[] Heard { get; set; }

            public int MaxMillimetres { get; set; }

            public int MinMillimetres { get; set; }

            public float Spring { get; set; }

            public float Damper { get; set; }

            /// <summary>Its joint: the game's for the local player's, the mod's for another's.</summary>
            public SpringJoint Joint { get; set; }

            /// <summary>The mod tied a joint for it, and whether that joint pulls here.</summary>
            public bool Pulled { get; set; }

            public bool Pulls { get; set; }

            /// <summary>Whether each end's body is held to the world, as last looked at, and when to look again.</summary>
            public bool Anchored { get; set; }

            public Rigidbody AnchorOf { get; set; }

            public bool SecondAnchored { get; set; }

            public Rigidbody SecondAnchorOf { get; set; }

            public float AnchorLookAt { get; set; }

            /// <summary>Its spring was taken off here.</summary>
            public bool Slack { get; set; }

            public SharedBody HungBody { get; set; }

            public ObjectReplication HungReplication { get; set; }

            public Drawing Drawn { get; set; }
        }

        private sealed class Drawing
        {
            public GameObject Root { get; set; }

            public Transform First { get; set; }

            public Transform Second { get; set; }

            public SkinnedMeshRenderer Skin { get; set; }
        }
    }
}
