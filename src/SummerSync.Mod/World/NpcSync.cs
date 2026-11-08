using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The rest of the people, animals and machines that move by themselves, as the host's game has them (like the
    /// traffic). Which ones, and how each is found, moved and shown, comes from the data
    /// files <c>data/npcs/*.json</c> (<see cref="NpcDefinition"/>); docs/GAME_NOTES.md says what each is in the game.
    ///
    /// The host's game moves them and says, about fifteen times a second, which are about, where each is, which of its
    /// listed children are on, its engine's speed and the animation it plays (<see cref="NetworkManager.Npcs"/>). A
    /// client's are moved by nothing of their own (their listed state machines off): it puts each where the host had it a
    /// moment ago, by its transform or, for one that forces move, as a kinematic body in the physics steps. One
    /// destroyed in a game (a moose run over) is said ("npc/gone/&lt;name&gt;") and its copies elsewhere taken, the game's
    /// way, to the state that destroys them. One with a mirror (Teimo's ride to the shop) has the state of one of its
    /// machines said by the host ("npc/mirror/&lt;name&gt;": which of its listed states), whenever it changes and to a game that
    /// joins; the others' copies, switched off, are taken there, the game's way.
    /// </summary>
    internal sealed class NpcSync
    {
        private const string DataPrefix = "SummerSync.npcs.";
        private const string GoneKey = "npc/gone/";
        private const string GoneEvent = "SSYNC GONE";
        private const string FollowKey = "npc/state/";
        private const string FollowEvent = "SSYNC FOLLOW";
        private const string MirrorKey = "npc/mirror/";
        private const string MirrorEvent = "SSYNC MIRROR";
        private const string RidingState = "Player in car";
        private const float StandingSpeed = 0.15f;
        private const double SearchInterval = 2.0;
        private const float TeleportDistance = 20f;
        private const float ThrowFactor = 1.5f;
        private const float ThrowSlack = 1f;
        private const float ThrowReach = 1.5f;

        private static NpcDefinition[] catalog;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Npc> npcs = new List<Npc>();
        private readonly List<KeyValuePair<string, bool>> heard = new List<KeyValuePair<string, bool>>();
        private readonly List<FollowHeard> followsHeard = new List<FollowHeard>();
        private readonly List<KeyValuePair<string, int>> mirrorsHeard = new List<KeyValuePair<string, int>>();
        private readonly List<PausedMachine> elsewhereOff = new List<PausedMachine>();
        private TrafficCar[] report = new TrafficCar[0];
        private TrafficReplication current;
        private TallyReplication tallies;
        private bool host;
        private uint shape;
        private double nextSearch;
        private double frameNow;
        private float frameTime;
        private bool frameKnown;
        private Transform player;
        private CharacterMotor playerMotor;
        private long throwsStopped;

        public NpcSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
            if (catalog == null)
            {
                catalog = LoadCatalog(log);
            }
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TrafficReplication replication = localPlayer == null ? null : manager.Npcs;
            TallyReplication gone = localPlayer == null ? null : manager.Tallies;
            if (replication != current || gone != tallies)
            {
                if (tallies != null)
                {
                    tallies.StateWanted -= OnStateWanted;
                }

                Release();
                current = replication;
                tallies = gone;
                host = manager.Mode == NetworkMode.Host;
                if (tallies != null)
                {
                    tallies.StateWanted += OnStateWanted;
                }

                heard.Clear();
                followsHeard.Clear();
                mirrorsHeard.Clear();
                npcs.Clear();
                frameKnown = false;
                if (current != null)
                {
                    List();
                }

                nextSearch = now;
            }

            player = localPlayer;
            if (current == null || tallies == null || npcs.Count == 0)
            {
                return;
            }

            if (now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            frameNow = now;
            frameTime = Time.time;
            frameKnown = true;
            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value);
            }

            heard.Clear();
            for (int i = 0; i < followsHeard.Count; i++)
            {
                HearFollow(followsHeard[i].Name, followsHeard[i].Count, followsHeard[i].CatchUp);
            }

            followsHeard.Clear();
            for (int i = 0; i < mirrorsHeard.Count; i++)
            {
                HearMirror(mirrorsHeard[i].Key, mirrorsHeard[i].Value);
            }

            mirrorsHeard.Clear();
            WatchGone();
            if (host)
            {
                WatchFollows();
                WatchMirrors();
                Report(now);
            }
            else
            {
                TakeMirrors();
            }

            if (!host && current.HasState)
            {
                Show(now);
                StopThrow();
            }
        }

        /// <summary>
        /// The local player's pose, if they ride one of the NPCs (the game hangs them from the bus), as one relative to
        /// it: whoever shows the player puts them on their own copy of it, and the two cannot drift apart.
        /// </summary>
        public void Localise(Transform localPlayer, ref PlayerPose pose)
        {
            // Unity's null check is also true for a destroyed object.
            if (pose.Parent != 0 || localPlayer == null || localPlayer.parent == null)
            {
                return;
            }

            for (Transform up = localPlayer.parent; up != null; up = up.parent)
            {
                for (int i = 0; i < npcs.Count; i++)
                {
                    if (npcs[i].Alive && npcs[i].Root == up)
                    {
                        Vector3 local = up.InverseTransformPoint(new Vector3(pose.X, pose.Y, pose.Z));
                        pose.X = local.x;
                        pose.Y = local.y;
                        pose.Z = local.z;
                        pose.Yaw = Mathf.Repeat(pose.Yaw - up.eulerAngles.y, 360f);
                        pose.Parent = (ushort)(PlayerPose.NpcParentBase + i);
                        return;
                    }
                }
            }
        }

        /// <summary>A pose relative to one of the NPCs, in the world, on this game's copy of it; false if this game has none.</summary>
        public bool ToWorld(ref PlayerPose pose)
        {
            int index = pose.Parent - PlayerPose.NpcParentBase;
            if (index < 0 || index >= npcs.Count || !npcs[index].Alive)
            {
                return false;
            }

            Transform root = npcs[index].Root;
            Vector3 world = root.TransformPoint(new Vector3(pose.X, pose.Y, pose.Z));
            pose.X = world.x;
            pose.Y = world.y;
            pose.Z = world.z;
            pose.Yaw = Mathf.Repeat(pose.Yaw + root.eulerAngles.y, 360f);
            return true;
        }

        /// <summary>On a client: the host's word on that NPC (its speed, its engine), or nothing if it has no place.</summary>
        public bool TryLatest(string key, out TrafficCar latest)
        {
            latest = default(TrafficCar);
            for (int i = 0; i < npcs.Count; i++)
            {
                if (npcs[i].Key == key && current != null)
                {
                    latest = current.Latest(i);
                    return current.IsActive(i);
                }
            }

            return false;
        }

        /// <summary>The object of that NPC in this game, or null.</summary>
        public Transform Find(string key)
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                if (npcs[i].Key == key && npcs[i].Alive)
                {
                    return npcs[i].Root;
                }
            }

            return null;
        }

        /// <summary>In every physics step: a client's NPCs moved by forces, to where the host had them a moment ago.</summary>
        public void FixedUpdate()
        {
            if (host || current == null || !frameKnown || npcs.Count == 0)
            {
                return;
            }

            double now = frameNow + (Time.fixedTime - frameTime);
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                BodyPose pose;

                // Unity's null check is also true for a destroyed object.
                if (npc.Definition.Motion == NpcMotion.Transform || !npc.Following || npc.Body == null || !npc.Root.gameObject.activeInHierarchy
                    || !current.Sample(i, now, out pose))
                {
                    continue;
                }

                var position = new Vector3(pose.X, pose.Y, pose.Z);
                var rotation = new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW);
                if (!npc.Placed || (position - npc.Body.position).sqrMagnitude > TeleportDistance * TeleportDistance)
                {
                    npc.Body.interpolation = npc.InterpolationBefore;
                    npc.Root.position = position;
                    npc.Root.rotation = rotation;
                    npc.Placed = true;
                    continue;
                }

                npc.Body.interpolation = RigidbodyInterpolation.Interpolate;
                npc.Body.MovePosition(position);
                npc.Body.MoveRotation(rotation);
            }
        }

        /// <summary>Every NPC here, and what was switched off for them, goes back to how the game had it.</summary>
        public void Release()
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                npcs[i].Restore();
            }

            for (int i = 0; i < elsewhereOff.Count; i++)
            {
                elsewhereOff[i].Resume();
            }

            elsewhereOff.Clear();
        }

        private static NpcDefinition[] LoadCatalog(Logger log)
        {
            var found = new List<NpcDefinition>();
            var names = new List<string>();
            Assembly assembly = typeof(NpcSync).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            for (int i = 0; i < resources.Length; i++)
            {
                if (!resources[i].StartsWith(DataPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resources[i]))
                    using (var reader = new StreamReader(stream))
                    {
                        NpcDefinition definition = NpcDefinition.Parse(reader.ReadToEnd());
                        found.Add(definition);
                        names.Add(definition.Name);
                    }
                }
                catch (FormatException exception)
                {
                    log.Error(LogCategory.Fsm, "The NPC file '" + resources[i] + "' cannot be read; that NPC is not shared. " + exception.Message);
                }
            }

            log.Info(LogCategory.Fsm, "NPCs this build can share: " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())) + ".");
            return found.ToArray();
        }

        // The list both sides agree on, by the data files: each NPC has its place whether or not this game has it now.
        private void List()
        {
            string[] keys = NpcDefinition.List(catalog, out shape);
            if (keys.Length > TrafficWire.MaxCars)
            {
                log.Warning(LogCategory.Fsm, "NPCs: " + keys.Length + " listed, more than " + TrafficWire.MaxCars + "; none is shared.");
                return;
            }

            for (int k = 0; k < keys.Length; k++)
            {
                for (int d = 0; d < catalog.Length; d++)
                {
                    string[] own = catalog[d].Keys;
                    int index = Array.IndexOf(own, keys[k]);
                    if (index >= 0)
                    {
                        npcs.Add(new Npc(keys[k], catalog[d], index));
                    }
                }
            }

            current.Define(npcs.Count, shape);
            report = new TrafficCar[npcs.Count];
            log.Info(LogCategory.Fsm, "NPCs: " + npcs.Count + " listed (shape " + shape.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + "); "
                + (host ? "this game moves them for everyone." : "the host moves them."));
        }

        // The ones not found yet: a root is found only while it is on (the moose are at night).
        private void Search()
        {
            for (int d = 0; d < catalog.Length; d++)
            {
                NpcDefinition definition = catalog[d];
                bool missing = false;
                for (int i = 0; i < npcs.Count; i++)
                {
                    if (npcs[i].Definition == definition && !npcs[i].Bound)
                    {
                        missing = true;
                    }
                }

                if (!missing)
                {
                    continue;
                }

                GameObject root = GameObject.Find("/" + definition.Root);
                if (root == null)
                {
                    continue;
                }

                var matches = new List<Transform>();
                if (definition.Path != null)
                {
                    Transform at = root.transform.Find(definition.Path);
                    if (at != null)
                    {
                        matches.Add(at);
                    }
                }
                else
                {
                    Collect(root.transform, definition.Find, matches);
                    GameObject also = definition.AlsoRoot == null || matches.Count >= definition.Count ? null : GameObject.Find("/" + definition.AlsoRoot);
                    if (also != null)
                    {
                        Collect(also.transform, definition.Find, matches);
                    }
                }

                for (int i = 0; i < npcs.Count; i++)
                {
                    Npc npc = npcs[i];
                    if (npc.Definition == definition && !npc.Bound && npc.Index < matches.Count)
                    {
                        npc.Bind(matches[npc.Index]);
                        log.Info(LogCategory.Fsm, "NPCs: '" + npc.Key + "' found at '" + Describe(npc.Root) + "'.");
                    }
                }

                if (!host)
                {
                    SwitchOffElsewhere(definition);
                }
            }
        }

        private static void Collect(Transform parent, string name, List<Transform> matches)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name)
                {
                    matches.Add(child);
                }
                else
                {
                    Collect(child, name, matches);
                }
            }
        }

        private static string Describe(Transform at)
        {
            string path = at.name;
            for (Transform up = at.parent; up != null; up = up.parent)
            {
                path = up.name + "/" + path;
            }

            return path;
        }

        // What switches a client's NPC on and off by its own player's day or distance: the host's says.
        private void SwitchOffElsewhere(NpcDefinition definition)
        {
            for (int m = 0; m < definition.OffElsewhere.Length; m++)
            {
                NpcMachine machine = definition.OffElsewhere[m];
                int slash = machine.Path.IndexOf('/');
                GameObject top = GameObject.Find("/" + (slash < 0 ? machine.Path : machine.Path.Substring(0, slash)));
                Transform at = top == null ? null : slash < 0 ? top.transform : top.transform.Find(machine.Path.Substring(slash + 1));
                PlayMakerFSM fsm = at == null ? null : DoorSync.FindMachine(at, machine.Machine);
                if (fsm != null && fsm.enabled)
                {
                    elsewhereOff.Add(new PausedMachine(fsm));
                    log.Info(LogCategory.Fsm, "NPCs: '" + machine.Path + " :: " + machine.Machine + "' is off here for the session; the host's game decides.");
                }
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(GoneKey, StringComparison.Ordinal))
            {
                heard.Add(new KeyValuePair<string, bool>(key.Substring(GoneKey.Length), tallies.CatchingUp));
            }
            else if (key.StartsWith(FollowKey, StringComparison.Ordinal) && values.Length >= 1)
            {
                followsHeard.Add(new FollowHeard { Name = key.Substring(FollowKey.Length), Count = values[0], CatchUp = tallies.CatchingUp });
            }
            else if (key.StartsWith(MirrorKey, StringComparison.Ordinal) && values.Length >= 1)
            {
                // A state, not an event: the host's answer to a game that joins is taken like any other word.
                mirrorsHeard.Add(new KeyValuePair<string, int>(key.Substring(MirrorKey.Length), values[0]));
            }
        }

        // The host: the state of a mirrored machine, said when it changes (and at the start of a session, to everyone).
        private void WatchMirrors()
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                PlayMakerFSM fsm = npc.Alive ? npc.MirrorMachine : null;
                if (fsm == null)
                {
                    continue;
                }

                int state = Array.IndexOf(npc.Definition.Mirror.States, fsm.ActiveStateName);
                if (state >= 0 && state != npc.MirrorSaid)
                {
                    npc.MirrorSaid = state;
                    tallies.Set(MirrorKey + npc.Key, new[] { state });
                    log.Info(LogCategory.Fsm, "NPCs: '" + npc.Key + "' is in '" + npc.Definition.Mirror.States[state] + "' here; told.");
                }
            }
        }

        // A client: the host's is in that state; this game's copy goes there.
        private void HearMirror(string key, int state)
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                if (npc.Key == key && npc.Definition.Mirror != null && state >= 0 && state < npc.Definition.Mirror.States.Length)
                {
                    npc.MirrorWanted = state;
                }
            }
        }

        // A client: copies whose host's state is not theirs are taken there, the game's way (switched on for the moment if
        // the mod has them off). One not found yet, or switched off by the game, is tried again later.
        private void TakeMirrors()
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                PlayMakerFSM fsm = npc.Alive ? npc.MirrorMachine : null;
                if (npc.MirrorWanted < 0 || fsm == null || !fsm.gameObject.activeInHierarchy)
                {
                    continue;
                }

                NpcMirror mirror = npc.Definition.Mirror;
                int wanted = npc.MirrorWanted;
                npc.MirrorWanted = -1;
                if (fsm.ActiveStateName == mirror.States[wanted])
                {
                    continue;
                }

                string state = mirror.Enter[wanted];
                if (!DoorSync.HasState(fsm, state))
                {
                    log.Warning(LogCategory.Fsm, "NPCs: '" + npc.Key + "' has no state '" + state + "' in '" + fsm.FsmName + "' here; left as it is.");
                    continue;
                }

                if (!DoorSync.HasGlobalTransition(fsm, MirrorEvent + " " + state))
                {
                    fsm.AddGlobalTransition(MirrorEvent + " " + state, state);
                }

                bool off = !fsm.enabled;
                fsm.enabled = true;
                fsm.SendEvent(MirrorEvent + " " + state);
                fsm.enabled = !off;
                log.Info(LogCategory.Fsm, "NPCs: '" + npc.Key + "' is in '" + mirror.States[wanted] + "' in the host's game; here it went through '" + state + "' and is in '" + fsm.ActiveStateName + "'.");
            }
        }

        // Destroyed in another game: here too, the game's way. One in the host's answer to this game's request was destroyed
        // long ago maybe: the way there drops what is left of it (a dead moose) a second time, so this game's copy is left
        // to the host's word, which has it nowhere (switched off here, as any the host does not have about).
        private void Hear(string key, bool catchUp)
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                if (npc.Key != key || npc.Told)
                {
                    continue;
                }

                npc.Told = true;
                if (catchUp)
                {
                    log.Info(LogCategory.Fsm, "NPCs: '" + key + "' was destroyed in the host's game; not done again here, its copy is off as the host has it nowhere (a catch-up on joining).");
                }
                else if (npc.Alive && npc.Destroy())
                {
                    log.Info(LogCategory.Fsm, "NPCs: '" + key + "' was destroyed in another game; here too.");
                }
            }
        }

        // The host: a state its copies follow, entered here, is said (how many times, so a late one hears it once).
        private void WatchFollows()
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                for (int f = 0; f < npc.Definition.Follows.Length && npc.Alive; f++)
                {
                    PlayMakerFSM fsm = npc.FollowMachine(f);
                    bool there = fsm != null && fsm.ActiveStateName == npc.Definition.Follows[f].Name;
                    if (there && !npc.FollowWasThere[f])
                    {
                        npc.FollowCount[f]++;
                        tallies.Set(FollowKey + npc.Key + "/" + f.ToString(System.Globalization.CultureInfo.InvariantCulture), new[] { npc.FollowCount[f] });
                        log.Info(LogCategory.Fsm, "NPCs: '" + npc.Key + "' went to '" + npc.Definition.Follows[f].Name + "' here; told.");
                    }

                    npc.FollowWasThere[f] = there;
                }
            }
        }

        // A client: one of the host's went to a state; its copy here goes there, the game's way. A count in the host's
        // answer to this game's request is history (Kuski crashed long ago maybe): counted, not entered again.
        private void HearFollow(string name, int count, bool catchUp)
        {
            int slash = name.LastIndexOf('/');
            int f;
            if (slash < 0 || !int.TryParse(name.Substring(slash + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out f))
            {
                return;
            }

            string key = name.Substring(0, slash);
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                if (npc.Key != key || !npc.Alive || f < 0 || f >= npc.Definition.Follows.Length || count <= npc.FollowCount[f])
                {
                    continue;
                }

                npc.FollowCount[f] = count;
                PlayMakerFSM fsm = npc.FollowMachine(f);
                string state = npc.Definition.Follows[f].Name;
                if (catchUp)
                {
                    log.Info(LogCategory.Fsm, "NPCs: '" + key + "' went to '" + state + "' " + count + " time(s) in the host's game; history, not entered here (a catch-up on joining).");
                    continue;
                }

                if (fsm != null && DoorSync.HasState(fsm, state) && fsm.ActiveStateName != state)
                {
                    if (!fsm.gameObject.activeInHierarchy)
                    {
                        log.Info(LogCategory.Fsm, "NPCs: '" + key + "' went to '" + state + "' in the host's game; here it is off, and stays as it is.");
                        continue;
                    }

                    if (!DoorSync.HasGlobalTransition(fsm, FollowEvent + " " + state))
                    {
                        fsm.AddGlobalTransition(FollowEvent + " " + state, state);
                    }

                    fsm.SendEvent(FollowEvent + " " + state);
                    log.Info(LogCategory.Fsm, "NPCs: '" + key + "' went to '" + state + "' in the host's game; here too.");
                }
            }
        }

        // Destroyed here (run over): the others are told.
        private void WatchGone()
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                if (npc.Bound && !npc.Alive && !npc.Told && npc.Definition.Gone != null)
                {
                    npc.Told = true;
                    tallies.Set(GoneKey + npc.Key, new[] { 1 });
                    log.Info(LogCategory.Fsm, "NPCs: '" + npc.Key + "' was destroyed in this game; told.");
                }
            }
        }

        private void Report(double now)
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                bool about = npc.Alive && npc.Root.gameObject.activeInHierarchy;
                report[i].Active = about;
                if (!about)
                {
                    continue;
                }

                Vector3 position = npc.Root.position;
                Quaternion rotation = npc.Root.rotation;
                report[i].Pose = new BodyPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
                report[i].Speed = npc.Body == null ? 0f : Vector3.Dot(npc.Body.velocity, npc.Root.forward);
                report[i].Rpm = npc.ReadRpm();
                report[i].Flags = npc.ReadShown();
                report[i].Clip = npc.ReadClip();
                if (npc.Car != null)
                {
                    report[i].Flags |= npc.Car.BrakeLights != null && npc.Car.BrakeLights.activeSelf ? NpcDefinition.BrakeBit : (byte)0;
                }
            }

            current.Report(now, 0, report);
        }

        private void Show(double now)
        {
            for (int i = 0; i < npcs.Count; i++)
            {
                Npc npc = npcs[i];
                if (!npc.Alive)
                {
                    continue;
                }

                bool on = current.IsActive(i);
                if (npc.Root.gameObject.activeSelf != on)
                {
                    npc.Root.gameObject.SetActive(on);
                }

                if (!on || !npc.Root.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!npc.Following)
                {
                    npc.StartFollowing();
                }
                else
                {
                    npc.KeepOff();
                }

                TrafficCar latest = current.Latest(i);
                npc.HoldRiders(Mathf.Abs(latest.Speed) > StandingSpeed);
                npc.ShowShown(latest.Flags);
                npc.ShowRpm(latest.Rpm);
                npc.ShowClip(latest.Clip);
                if (npc.Car != null)
                {
                    npc.Car.ShowControls(latest.Speed, latest.Rpm, (latest.Flags & NpcDefinition.BrakeBit) != 0);
                }
                BodyPose pose;
                if (npc.Definition.Motion == NpcMotion.Transform && current.Sample(i, now, out pose))
                {
                    var position = new Vector3(pose.X, pose.Y, pose.Z);
                    var rotation = new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW);
                    npc.Root.position = position;
                    npc.Root.rotation = rotation;

                    // One with a body of its own that the game moves by its transform (the train) keeps where its body
                    // is: the body is put there too, at rest, as the game's moves leave it.
                    if (npc.Body != null && !npc.Body.isKinematic)
                    {
                        npc.Body.position = position;
                        npc.Body.rotation = rotation;
                        npc.Body.velocity = Vector3.zero;
                        npc.Body.angularVelocity = Vector3.zero;
                    }
                }
            }
        }

        // As next to a vehicle that another machine moves (VehiclePresenter.StopThrow): an NPC car is kinematic here,
        // and moving into the player (the bus at its stop) it would make the game's motor launch them.
        private void StopThrow()
        {
            // Unity's null check is also true for a destroyed object.
            if (player == null)
            {
                return;
            }

            if (playerMotor == null)
            {
                playerMotor = player.GetComponent<CharacterMotor>();
                if (playerMotor == null)
                {
                    return;
                }
            }

            CharacterMotorMovement movement = playerMotor.movement;
            CharacterMotorJumping jumping = playerMotor.jumping;
            if (movement == null || jumping == null)
            {
                return;
            }

            float rising = movement.velocity.y;
            float takeOff = Mathf.Sqrt(2f * Mathf.Max(0f, jumping.baseHeight + jumping.extraHeight) * Mathf.Max(0f, movement.gravity));
            if (rising <= (takeOff * ThrowFactor) + ThrowSlack)
            {
                return;
            }

            Vector3 at = player.position;
            for (int i = 0; i < npcs.Count; i++)
            {
                FollowedCar car = npcs[i].Car;
                if (car != null && car.Following && car.Body != null && car.StandsBeside(at, ThrowReach))
                {
                    movement.velocity.y = 0f;
                    throwsStopped++;
                    if (throwsStopped == 1 || throwsStopped % 20 == 0)
                    {
                        log.Info(LogCategory.Vehicle, "NPCs: '" + npcs[i].Key + "' pushed the local player, and the game's motor took that for a leap: taken away. " + throwsStopped + " time(s) so far.");
                    }

                    return;
                }
            }
        }

        private struct FollowHeard
        {
            public string Name;
            public int Count;

            /// <summary>Part of the host's answer to this game's request: how many times, not a time now.</summary>
            public bool CatchUp;
        }

        private sealed class Npc
        {
            private readonly List<PausedMachine> switchedOff = new List<PausedMachine>();
            private readonly List<PlayMakerFSM> seats = new List<PlayMakerFSM>();
            private readonly List<PausedMachine> seatsHeld = new List<PausedMachine>();
            private PlayMakerFSM[] follows = new PlayMakerFSM[0];
            private GameObject[] shown = new GameObject[0];
            private FsmFloat rpm;
            private Animation animation;
            private string[] clips = new string[0];
            private byte clipShown;
            private bool madeKinematic;
            private bool freed;
            private RigidbodyConstraints constraintsBefore;
            private bool gravityBefore;

            public Npc(string key, NpcDefinition definition, int index)
            {
                Key = key;
                Definition = definition;
                Index = index;
                MirrorSaid = -1;
                MirrorWanted = -1;
            }

            public string Key { get; }

            public NpcDefinition Definition { get; }

            /// <summary>Which of its name it is, in the order the scene has them.</summary>
            public int Index { get; }

            public Transform Root { get; private set; }

            public Rigidbody Body { get; private set; }

            /// <summary>For a car: what follows its controller, engine, wheels and lights.</summary>
            public FollowedCar Car { get; private set; }

            /// <summary>How many times the host's went to each of <see cref="NpcDefinition.Follows"/>, as heard or said.</summary>
            public int[] FollowCount { get; private set; }

            public bool[] FollowWasThere { get; private set; }

            /// <summary>The machine of <see cref="NpcDefinition.Mirror"/>, once bound; null if none.</summary>
            public PlayMakerFSM MirrorMachine { get; private set; }

            /// <summary>The host: the mirrored state last said (-1 none). A client: the one heard and not taken yet (-1 none).</summary>
            public int MirrorSaid { get; set; }

            public int MirrorWanted { get; set; }

            public bool Bound { get; private set; }

            /// <summary>The game destroys some (a moose run over).</summary>
            public bool Alive
            {
                // Unity's null check is also true for a destroyed object.
                get { return Bound && Root != null; }
            }

            public bool Following { get; private set; }

            public bool Placed { get; set; }

            public bool Told { get; set; }

            public RigidbodyInterpolation InterpolationBefore { get; private set; }

            public void Bind(Transform root)
            {
                Root = root;
                Bound = true;
                FollowCount = new int[Definition.Follows.Length];
                FollowWasThere = new bool[Definition.Follows.Length];
                follows = new PlayMakerFSM[Definition.Follows.Length];
                for (int f = 0; f < follows.Length; f++)
                {
                    NpcMachine spec = Definition.Follows[f];
                    Transform at = spec.Path.Length == 0 ? root : root.Find(spec.Path);
                    follows[f] = at == null ? null : DoorSync.FindMachine(at, spec.Machine);
                }

                if (Definition.Mirror != null)
                {
                    Transform at = Definition.Mirror.Path.Length == 0 ? root : root.Find(Definition.Mirror.Path);
                    MirrorMachine = at == null ? null : DoorSync.FindMachine(at, Definition.Mirror.Machine);
                }

                seats.Clear();
                if (Definition.Motion == NpcMotion.Car)
                {
                    PlayMakerFSM[] machines = root.GetComponentsInChildren<PlayMakerFSM>(true);
                    for (int i = 0; i < machines.Length; i++)
                    {
                        if (machines[i].FsmName == "PlayerTrigger" && DoorSync.HasState(machines[i], RidingState))
                        {
                            seats.Add(machines[i]);
                        }
                    }
                }

                Body = root.GetComponent<Rigidbody>();
                if (Definition.Motion == NpcMotion.Car && Body != null && root.GetComponent<CarDynamics>() != null)
                {
                    Car = new FollowedCar(root, Body, Definition.Off);
                }
                shown = new GameObject[Definition.Shown.Length];
                for (int i = 0; i < shown.Length; i++)
                {
                    Transform child = root.Find(Definition.Shown[i]);
                    shown[i] = child == null ? null : child.gameObject;
                }

                if (Definition.Rpm != null)
                {
                    Transform at = Definition.Rpm.Path.Length == 0 ? root : root.Find(Definition.Rpm.Path);
                    PlayMakerFSM fsm = at == null ? null : DoorSync.FindMachine(at, Definition.Rpm.Machine);
                    rpm = fsm == null ? null : fsm.FsmVariables.FindFsmFloat(Definition.Rpm.Name);
                }

                if (Definition.Animation != null)
                {
                    Transform at = Definition.Animation.Length == 0 ? root : root.Find(Definition.Animation);
                    animation = at == null ? null : at.GetComponent<Animation>();
                    if (animation != null)
                    {
                        var names = new List<string>();
                        foreach (AnimationState state in animation)
                        {
                            names.Add(state.name);
                        }

                        names.Sort(string.CompareOrdinal);
                        clips = names.ToArray();
                    }
                }
            }

            public PlayMakerFSM FollowMachine(int index)
            {
                // Unity's null check is also true for a destroyed object.
                return index >= 0 && index < follows.Length && follows[index] != null ? follows[index] : null;
            }

            /// <summary>
            /// A seat whose player rides cannot be left while the host's car moves, as in the game; held, it keeps its
            /// state and does nothing until the car stands.
            /// </summary>
            public void HoldRiders(bool moving)
            {
                for (int i = 0; i < seats.Count; i++)
                {
                    PlayMakerFSM seat = seats[i];

                    // Unity's null check is also true for a destroyed object.
                    if (seat == null)
                    {
                        continue;
                    }

                    int held = -1;
                    for (int h = 0; h < seatsHeld.Count; h++)
                    {
                        if (seatsHeld[h].Machine == seat)
                        {
                            held = h;
                        }
                    }

                    if (held < 0 && moving && seat.enabled && seat.ActiveStateName == RidingState)
                    {
                        seatsHeld.Add(new PausedMachine(seat));
                    }
                    else if (held >= 0 && !moving)
                    {
                        seatsHeld[held].Resume();
                        seatsHeld.RemoveAt(held);
                    }
                }
            }

            public float ReadRpm()
            {
                return rpm == null ? 0f : rpm.Value;
            }

            // "Move" is every machine of that name below it; "path::Move" only the one on that path ("::Move", its own).
            private bool IsOff(PlayMakerFSM machine)
            {
                string[] off = Definition.Off;
                for (int i = 0; i < off.Length; i++)
                {
                    int split = off[i].IndexOf("::", StringComparison.Ordinal);
                    if (split < 0)
                    {
                        if (off[i] == machine.FsmName)
                        {
                            return true;
                        }

                        continue;
                    }

                    if (off[i].Substring(split + 2) != machine.FsmName)
                    {
                        continue;
                    }

                    string path = off[i].Substring(0, split);
                    Transform at = path.Length == 0 ? Root : Root.Find(path);
                    if (at == machine.transform)
                    {
                        return true;
                    }
                }

                return false;
            }

            public byte ReadShown()
            {
                byte bits = 0;
                for (int i = 0; i < shown.Length; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    if (shown[i] != null && shown[i].activeSelf)
                    {
                        bits |= (byte)(1 << i);
                    }
                }

                return bits;
            }

            /// <summary>One up from the place, in its sorted list, of the clip it plays; 0 for none.</summary>
            public byte ReadClip()
            {
                // Unity's null check is also true for a destroyed object.
                if (animation == null)
                {
                    return 0;
                }

                for (int i = 0; i < clips.Length && i < 255; i++)
                {
                    if (animation.IsPlaying(clips[i]))
                    {
                        return (byte)(i + 1);
                    }
                }

                return 0;
            }

            public void StartFollowing()
            {
                Following = true;
                Placed = false;
                clipShown = 0;
                if (Car != null)
                {
                    InterpolationBefore = Body.interpolation;
                    Car.StartFollowing();
                    return;
                }

                PlayMakerFSM[] machines = Root.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < machines.Length; i++)
                {
                    if (machines[i].enabled && IsOff(machines[i]))
                    {
                        switchedOff.Add(new PausedMachine(machines[i]));
                    }
                }

                if (Body != null)
                {
                    InterpolationBefore = Body.interpolation;
                }

                // One with a body that the game moves by its transform (the train): its constraints lock it to the line
                // and the turn it started on, and a position written elsewhere ends up projected onto them. Here it goes
                // where the host has it, free, without gravity and without the game's extrapolation; still a body that
                // things (the player) collide with.
                if (Definition.Motion == NpcMotion.Transform && Body != null && !Body.isKinematic)
                {
                    freed = true;
                    constraintsBefore = Body.constraints;
                    gravityBefore = Body.useGravity;
                    Body.constraints = RigidbodyConstraints.None;
                    Body.useGravity = false;
                    Body.interpolation = RigidbodyInterpolation.None;
                }

                KeepOff();
            }

            /// <summary>One moved by forces stays kinematic: the game may make it otherwise.</summary>
            public void KeepOff()
            {
                if (Car != null)
                {
                    Car.KeepOff();
                    return;
                }

                if (Definition.Motion == NpcMotion.Body && Body != null && !Body.isKinematic)
                {
                    madeKinematic = true;
                    Body.isKinematic = true;
                }
            }

            public void ShowShown(byte bits)
            {
                for (int i = 0; i < shown.Length; i++)
                {
                    bool on = (bits & (1 << i)) != 0;

                    // Unity's null check is also true for a destroyed object.
                    if (shown[i] != null && shown[i].activeSelf != on)
                    {
                        shown[i].SetActive(on);
                    }
                }
            }

            public void ShowRpm(float value)
            {
                if (rpm != null)
                {
                    rpm.Value = value;
                }
            }

            public void ShowClip(byte clip)
            {
                // Unity's null check is also true for a destroyed object.
                if (clip == clipShown || animation == null || clip > clips.Length)
                {
                    return;
                }

                clipShown = clip;
                if (clip > 0 && animation[clips[clip - 1]] != null)
                {
                    animation.CrossFade(clips[clip - 1], 0.3f);
                }
            }

            /// <summary>To the state that destroys it, as its own machine does; false if it has none.</summary>
            public bool Destroy()
            {
                NpcMachine gone = Definition.Gone;
                if (gone == null)
                {
                    return false;
                }

                Transform at = gone.Path.Length == 0 ? Root : Root.Find(gone.Path);
                PlayMakerFSM fsm = at == null ? null : DoorSync.FindMachine(at, gone.Machine);
                if (fsm == null || !DoorSync.HasState(fsm, gone.Name))
                {
                    return false;
                }

                // Far from the local player the game may have it off; on, for its machine to take the event.
                if (!at.gameObject.activeSelf)
                {
                    at.gameObject.SetActive(true);
                }

                if (!fsm.enabled)
                {
                    fsm.enabled = true;
                }

                if (!DoorSync.HasGlobalTransition(fsm, GoneEvent))
                {
                    fsm.AddGlobalTransition(GoneEvent, gone.Name);
                }

                fsm.SendEvent(GoneEvent);
                return true;
            }

            public void Restore()
            {
                for (int i = 0; i < seatsHeld.Count; i++)
                {
                    seatsHeld[i].Resume();
                }

                seatsHeld.Clear();

                // Unity's null check is also true for a destroyed object.
                if (Car != null && Body != null)
                {
                    Car.Restore();
                }

                for (int i = 0; i < switchedOff.Count; i++)
                {
                    switchedOff[i].Resume();
                }

                switchedOff.Clear();
                if (Body != null)
                {
                    if (Following)
                    {
                        Body.interpolation = InterpolationBefore;
                    }

                    if (madeKinematic)
                    {
                        Body.isKinematic = false;
                    }

                    if (freed)
                    {
                        Body.velocity = Vector3.zero;
                        Body.angularVelocity = Vector3.zero;
                        Body.constraints = constraintsBefore;
                        Body.useGravity = gravityBefore;
                    }
                }

                madeKinematic = false;
                freed = false;
                Following = false;
            }
        }
    }
}
