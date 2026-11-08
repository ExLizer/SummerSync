using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.World;
using SummerSync.Unity.Objects;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// What the games make while they run, made on every machine: what is bought, what comes out of a bag, the
    /// rally's trophies, moose meat.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md):
    ///
    /// Under the root object "Spawner" there are 51 state machines that make things, each with a running number
    /// in its integer "ObjectNumberInt" that the save keeps (items.txt), a prefix in its string
    /// "ObjectCreateName", and a global transition "SPAWNITEM" to a state that adds one to the number, makes the
    /// thing from its prefab ("CreateObject", into its game object "New") and names it prefix and number. That
    /// state goes on, with FINISHED, to the state it waits in. While the world loads, each of them makes what
    /// the save says, one by one, and only then reaches that state. The things that come out of a shopping bag
    /// are made where the bag is, the one in the game object "ShoppingBagSpawn".
    ///
    /// A shopping bag ("shoppingbagx" and its number) holds what was bought in two lists of numbers,
    /// "PlayMakerArrayListProxy" components called "Items" and "Spraycans"; the till fills them a few frames
    /// after the bag is made, and opening it takes from them.
    ///
    /// When one of those state machines makes something here, the others are told its number and where it is.
    /// When another machine's did, the one here is set to the number before it and sent "SPAWNITEM": the game
    /// makes the same thing with the same name, and it is put where the other has it. The loose objects'
    /// replication pairs the two by name like any other object; on the host, the copy starts as the maker's to
    /// simulate. Whatever has lists is watched, and a change of them is passed on.
    ///
    /// Offline and in the loopback test nothing here touches the game.
    ///
    /// A world that joins (Fase 8) loaded the host's last save: the host says each maker's number (a tally per maker), and
    /// when a client has its whole list it says again what each container holds. The client makes what the host's game
    /// made since the save, found by key (<see cref="MakeForJoin"/>), and then takes the host's numbers
    /// (<see cref="RaiseCounts"/>): what it makes next is not a number the host's game used already.
    /// </summary>
    internal sealed class SpawnSync
    {
        private const string SpawnerRoot = "Spawner";
        private const string CountVariable = "ObjectNumberInt";
        private const string PrefixVariable = "ObjectCreateName";
        private const string MadeVariable = "New";
        private const string BagVariable = "ShoppingBagSpawn";
        private const string MakeEvent = "SPAWNITEM";
        private const string FinishedEvent = "FINISHED";

        private const string CountPrefix = "spawn/count/";
        private const double SearchInterval = 2.0;
        private const double ContentsInterval = 0.5;
        private const double KeepWaiting = 60.0;
        private const int MaxWaiting = 500;

        // After something is made, the loose objects are looked for again a few times: a shopping bag is
        // kinematic and inside the shop for a moment.
        private static readonly double[] RescanAfter = { 0.2, 1.5, 4.0 };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly ObjectPresenter objects;
        private readonly List<Spawner> spawners = new List<Spawner>();
        private readonly Dictionary<string, Spawner> byKey = new Dictionary<string, Spawner>();
        private readonly Dictionary<string, Container> containers = new Dictionary<string, Container>();
        private readonly List<Container> containerList = new List<Container>();
        private readonly Dictionary<string, Contents> laterContents = new Dictionary<string, Contents>();
        private readonly List<Wanted> waiting = new List<Wanted>();
        private readonly List<double> rescans = new List<double>();
        private readonly HashSet<string> unknownReported = new HashSet<string>();
        private readonly StringBuilder signature = new StringBuilder();
        private SpawnReplication current;
        private TallyReplication tallies;
        private ObjectReplication objectList;
        private readonly Dictionary<string, int> hostCounts = new Dictionary<string, int>();
        private bool countsTold;
        private bool contentsAgain;
        private double now;
        private double nextSearch;
        private double nextContents;
        private bool found;
        private bool searchReported;
        private int settledCount;

        public SpawnSync(Logger log, NetworkManager manager, ObjectPresenter objects)
        {
            this.log = log;
            this.manager = manager;
            this.objects = objects;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double time, Transform localPlayer)
        {
            now = time;
            SpawnReplication spawns = localPlayer == null ? null : manager.Spawns;
            if (spawns != current)
            {
                if (current != null)
                {
                    current.Wanted -= OnWanted;
                    current.ContentsWanted -= OnContentsWanted;
                }

                current = spawns;
                if (current != null)
                {
                    current.Wanted += OnWanted;
                    current.ContentsWanted += OnContentsWanted;
                }

                if (tallies != null)
                {
                    tallies.StateWanted -= OnCountWanted;
                }

                if (objectList != null)
                {
                    objectList.Subscribed -= OnSubscribed;
                }

                tallies = current == null ? null : manager.Tallies;
                objectList = current == null ? null : manager.Objects;
                if (tallies != null)
                {
                    tallies.StateWanted += OnCountWanted;
                }

                if (objectList != null)
                {
                    objectList.Subscribed += OnSubscribed;
                }

                hostCounts.Clear();
                countsTold = false;
                contentsAgain = false;

                spawners.Clear();
                byKey.Clear();
                containers.Clear();
                containerList.Clear();
                laterContents.Clear();
                waiting.Clear();
                rescans.Clear();
                unknownReported.Clear();
                found = false;
                searchReported = false;
                settledCount = 0;
                nextSearch = now;
                nextContents = now;
            }

            if (current == null)
            {
                return;
            }

            if (!found && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            if (!found)
            {
                return;
            }

            Settle();
            MakeWaiting();
            Watch();
            if (now >= nextContents)
            {
                nextContents = now + ContentsInterval;
                WatchContents();
                ContentsAgain();
            }

            while (rescans.Count > 0 && rescans[0] <= now)
            {
                rescans.RemoveAt(0);
                objects.ScanSoon(now);
            }
        }

        private void Search()
        {
            GameObject root = GameObject.Find(SpawnerRoot);
            if (root != null)
            {
                Transform parent = root.transform;
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform child = parent.GetChild(i);
                    PlayMakerFSM[] machines = child.GetComponents<PlayMakerFSM>();
                    for (int j = 0; j < machines.Length; j++)
                    {
                        Spawner spawner = Describe(SpawnerRoot + "/" + child.name, machines[j]);
                        if (spawner != null && !byKey.ContainsKey(spawner.Key))
                        {
                            spawners.Add(spawner);
                            byKey[spawner.Key] = spawner;
                        }
                    }
                }
            }

            found = spawners.Count > 0;
            if (found)
            {
                int unusual = 0;
                for (int i = 0; i < spawners.Count; i++)
                {
                    if (spawners[i].WaitState == null)
                    {
                        unusual++;
                    }
                }

                log.Info(
                    LogCategory.Fsm,
                    "Makers: " + spawners.Count + " of the game's numbered makers found under '" + SpawnerRoot + "'"
                    + (unusual > 0 ? ", " + unusual + " of them not made the usual way, left alone" : string.Empty) + ".");
            }
            else if (!searchReported)
            {
                searchReported = true;
                log.Warning(LogCategory.Fsm, "The game's makers were not found as this build expects them (state machines with '" + CountVariable + "' under '" + SpawnerRoot + "'): what is bought appears only for whoever bought it.");
            }
        }

        // A state machine that makes numbered things, or null if this one does not.
        private Spawner Describe(string path, PlayMakerFSM fsm)
        {
            FsmVariables variables = fsm.FsmVariables;
            FsmInt count = variables.FindFsmInt(CountVariable);
            FsmString prefix = variables.FindFsmString(PrefixVariable);
            if (count == null || prefix == null)
            {
                return null;
            }

            var spawner = new Spawner
            {
                Key = path + " :: " + fsm.FsmName,
                Fsm = fsm,
                Count = count,
                Prefix = prefix,
                Made = variables.FindFsmGameObject(MadeVariable),
                Bag = variables.FindFsmGameObject(BagVariable),
            };

            // The state it waits in: where the state that SPAWNITEM leads to goes on to.
            string makes = null;
            FsmTransition[] global = fsm.Fsm.GlobalTransitions;
            for (int i = 0; global != null && i < global.Length; i++)
            {
                if (global[i].EventName == MakeEvent)
                {
                    makes = global[i].ToState;
                }
            }

            FsmState[] states = fsm.Fsm.States;
            for (int i = 0; makes != null && states != null && i < states.Length; i++)
            {
                if (states[i].Name != makes)
                {
                    continue;
                }

                FsmTransition[] transitions = states[i].Transitions;
                for (int j = 0; transitions != null && j < transitions.Length; j++)
                {
                    if (transitions[j].EventName == FinishedEvent)
                    {
                        spawner.WaitState = transitions[j].ToState;
                    }
                }
            }

            return spawner;
        }

        // A maker counts once it has reached the state it waits in: from then on, a higher number is something
        // made, not something loaded.
        private void Settle()
        {
            if (settledCount == spawners.Count)
            {
                return;
            }

            for (int i = 0; i < spawners.Count; i++)
            {
                Spawner spawner = spawners[i];
                if (spawner.Settled || spawner.WaitState == null || spawner.Fsm == null || spawner.Fsm.ActiveStateName != spawner.WaitState)
                {
                    continue;
                }

                spawner.Settled = true;
                spawner.Known = spawner.Count.Value;
                settledCount++;
                for (int n = 1; n <= spawner.Known; n++)
                {
                    GameObject made = GameObject.Find(spawner.Prefix.Value + n);
                    if (made != null)
                    {
                        Discover(made);
                    }
                }
            }

            if (settledCount == spawners.Count || settledCount == CountUsual())
            {
                settledCount = spawners.Count;
                log.Info(LogCategory.Fsm, "Makers: the save's things are all made; " + containerList.Count + " of them hold lists (shopping bags).");
                TellCounts();
            }
        }

        private int CountUsual()
        {
            int usual = 0;
            for (int i = 0; i < spawners.Count; i++)
            {
                if (spawners[i].WaitState != null)
                {
                    usual++;
                }
            }

            return usual;
        }

        // What this machine's game made since the last frame.
        private void Watch()
        {
            for (int i = 0; i < spawners.Count; i++)
            {
                Spawner spawner = spawners[i];
                if (!spawner.Settled)
                {
                    continue;
                }

                int count = spawner.Count.Value;
                if (count < spawner.Known)
                {
                    spawner.Known = count;
                    continue;
                }

                for (int number = spawner.Known + 1; number <= count; number++)
                {
                    Made(spawner, number);
                }

                if (count != spawner.Known)
                {
                    spawner.Known = count;
                    TellCount(spawner);
                }
            }
        }

        // The host: each maker's number, the session's from the start (what its game made since the save too).
        private void TellCounts()
        {
            if (countsTold || tallies == null || manager.Mode != NetworkMode.Host)
            {
                return;
            }

            countsTold = true;
            for (int i = 0; i < spawners.Count; i++)
            {
                TellCount(spawners[i]);
            }
        }

        private void TellCount(Spawner spawner)
        {
            if (tallies != null && manager.Mode == NetworkMode.Host && spawner.Settled)
            {
                tallies.Set(CountPrefix + spawner.Key, new[] { spawner.Count.Value });
            }
        }

        private void OnCountWanted(string key, int[] values)
        {
            if (values.Length >= 1 && key.StartsWith(CountPrefix, StringComparison.Ordinal))
            {
                hostCounts[key.Substring(CountPrefix.Length)] = values[0];
            }
        }

        // The host: a client has the whole list of things; what each container holds is said again, for a world loaded
        // from the save.
        private void OnSubscribed(byte player)
        {
            contentsAgain = true;
        }

        private void ContentsAgain()
        {
            if (!contentsAgain || manager.Mode != NetworkMode.Host)
            {
                return;
            }

            contentsAgain = false;
            int said = 0;
            for (int i = 0; i < containerList.Count; i++)
            {
                Container container = containerList[i];
                if (container.Owner != null && container.Last != null && !container.Empty)
                {
                    current.ReportContents(container.Name, container.Last);
                    said++;
                }
            }

            if (said > 0)
            {
                log.Info(LogCategory.Fsm, "Makers: what " + said + " container(s) hold is said again, for a world loaded from the save.");
            }
        }

        /// <summary>
        /// A client whose world was loaded from the host's save: the host's game made the thing with this key since then.
        /// It is made here by its maker, where the host has it. False if no maker here makes that key, or it is made here
        /// already, or the makers are not ready.
        /// </summary>
        public bool MakeForJoin(string key, BodyPose pose)
        {
            Spawner spawner;
            int number;
            if (!found || current == null || !SpawnerOf(key, out spawner, out number) || !spawner.Settled || number <= spawner.Count.Value)
            {
                return false;
            }

            MakeHere(spawner, new SpawnEvent { Spawner = spawner.Key, Number = number, Pose = pose, Near = string.Empty, Player = Protocol.HostPlayerId });
            return spawner.Count.Value == number;
        }

        /// <summary>Whether a maker here makes things with keys like this one ("beercase" and a number), and which.</summary>
        public bool MakesKey(string key)
        {
            Spawner spawner;
            int number;
            return SpawnerOf(key, out spawner, out number);
        }

        /// <summary>
        /// A client: each maker takes the host's number once nothing it makes is still missing here (busy: the prefixes
        /// of what is). Returns how many were raised.
        /// </summary>
        public int RaiseCounts(ICollection<string> busy)
        {
            int raised = 0;
            for (int i = 0; i < spawners.Count; i++)
            {
                Spawner spawner = spawners[i];
                int count;
                if (!spawner.Settled || !hostCounts.TryGetValue(spawner.Key, out count) || count <= spawner.Count.Value || busy.Contains(spawner.Prefix.Value))
                {
                    continue;
                }

                log.Info(LogCategory.Fsm, "Makers: '" + spawner.Key + "' goes from " + spawner.Count.Value + " to the host's " + count + "; what this game makes next is a new number there too.");
                spawner.Count.Value = count;
                spawner.Known = count;
                raised++;
            }

            return raised;
        }

        // The maker of a key: the longest prefix it starts with, followed by a number.
        private bool SpawnerOf(string key, out Spawner found, out int number)
        {
            found = null;
            number = 0;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            for (int i = 0; i < spawners.Count; i++)
            {
                string prefix = spawners[i].Prefix.Value;
                int n;
                if (!string.IsNullOrEmpty(prefix) && key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.Ordinal)
                    && int.TryParse(key.Substring(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n)
                    && (found == null || prefix.Length > found.Prefix.Value.Length))
                {
                    found = spawners[i];
                    number = n;
                }
            }

            return found != null;
        }

        private void Made(Spawner spawner, int number)
        {
            string name = spawner.Prefix.Value + number;
            GameObject made = spawner.Made != null && spawner.Made.Value != null && spawner.Made.Value.name == name ? spawner.Made.Value : GameObject.Find(name);
            if (made == null)
            {
                log.Warning(LogCategory.Fsm, "Makers: this game made '" + name + "' (" + spawner.Key + "), and it is nowhere to be found; the others are not told.");
                return;
            }

            GameObject bag = spawner.Bag == null ? null : spawner.Bag.Value;
            var spawn = new SpawnEvent { Spawner = spawner.Key, Number = number, Pose = PoseOf(made.transform), Near = bag == null ? string.Empty : bag.name };
            current.Report(spawn);
            log.Info(LogCategory.Fsm, "Makers: this game made '" + name + "' (" + spawner.Key + ")" + (bag == null ? string.Empty : " out of '" + bag.name + "'") + " at " + Where(made.transform) + "; the others are told.");
            Discover(made);
            Rescan();
        }

        private void OnWanted(SpawnEvent spawn)
        {
            if (waiting.Count >= MaxWaiting)
            {
                log.Warning(LogCategory.Fsm, "Makers: too many things waiting to be made here; '" + spawn.Spawner + "' number " + spawn.Number + " is not.");
                return;
            }

            waiting.Add(new Wanted { Spawn = spawn, Since = now });
        }

        private void OnContentsWanted(string name, IList<ContainerList> lists)
        {
            // In order with what is made: the bag may be among what is still waiting.
            waiting.Add(new Wanted { IsContents = true, Name = name, Lists = new List<ContainerList>(lists), Since = now });
        }

        // What the others made, made here, in the order it was heard; what cannot be made yet waits.
        private void MakeWaiting()
        {
            while (waiting.Count > 0)
            {
                Wanted next = waiting[0];
                if (next.IsContents)
                {
                    Fill(next.Name, next.Lists);
                    waiting.RemoveAt(0);
                    continue;
                }

                Spawner spawner;
                if (!byKey.TryGetValue(next.Spawn.Spawner, out spawner))
                {
                    if (unknownReported.Add(next.Spawn.Spawner))
                    {
                        log.Warning(LogCategory.Fsm, "Makers: player " + next.Spawn.Player + "'s game made something with '" + next.Spawn.Spawner + "', which this game does not have.");
                    }

                    waiting.RemoveAt(0);
                    continue;
                }

                if (!spawner.Settled)
                {
                    if (now - next.Since > KeepWaiting)
                    {
                        log.Warning(LogCategory.Fsm, "Makers: '" + spawner.Key + "' never finished loading here; number " + next.Spawn.Number + " is not made.");
                        waiting.RemoveAt(0);
                        continue;
                    }

                    return;
                }

                waiting.RemoveAt(0);
                MakeHere(spawner, next.Spawn);
            }
        }

        private void MakeHere(Spawner spawner, SpawnEvent spawn)
        {
            string name = spawner.Prefix.Value + spawn.Number;
            int had = spawner.Count.Value;
            if (had >= spawn.Number)
            {
                // Two games made the same number at once, or this one had it already.
                log.Warning(LogCategory.Fsm, "Makers: player " + spawn.Player + "'s game made '" + name + "', and this game's '" + spawner.Key + "' is at " + had + " already: not made again.");
                spawner.Known = Math.Max(spawner.Known, had);
                return;
            }

            GameObject near = string.IsNullOrEmpty(spawn.Near) ? null : GameObject.Find(spawn.Near);
            if (spawner.Bag != null && near != null)
            {
                spawner.Bag.Value = near;
            }

            // Known first: what is made now is not this game's own news.
            spawner.Count.Value = spawn.Number - 1;
            spawner.Known = spawn.Number;
            spawner.Fsm.SendEvent(MakeEvent);
            int count = spawner.Count.Value;
            if (count != spawn.Number)
            {
                spawner.Known = count;
                log.Warning(LogCategory.Fsm, "Makers: '" + spawner.Key + "' did not make number " + spawn.Number + " (its number is " + count + ", state '" + spawner.Fsm.ActiveStateName + "').");
                return;
            }

            GameObject made = spawner.Made != null && spawner.Made.Value != null && spawner.Made.Value.name == name ? spawner.Made.Value : GameObject.Find(name);
            if (made == null)
            {
                log.Warning(LogCategory.Fsm, "Makers: '" + spawner.Key + "' made number " + spawn.Number + ", and '" + name + "' is nowhere to be found.");
                return;
            }

            Place(made, spawn.Pose);
            objects.Expect(made, spawn.Player, now);
            log.Info(
                LogCategory.Fsm,
                "Makers: made '" + name + "' here because player " + spawn.Player + "'s game did" + (near == null ? string.Empty : ", out of '" + near.name + "'") + ", at "
                + Where(made.transform) + (had + 1 < spawn.Number ? "; this game had only up to " + had : string.Empty) + ".");
            Discover(made);
            Rescan();
        }

        private static void Place(GameObject made, BodyPose pose)
        {
            var position = new Vector3(pose.X, pose.Y, pose.Z);
            var rotation = new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW);
            Transform transform = made.transform;
            transform.parent = null;
            transform.position = position;
            transform.rotation = rotation;
            Rigidbody body = made.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = position;
                body.rotation = rotation;
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
        }

        private void Rescan()
        {
            for (int i = 0; i < RescanAfter.Length; i++)
            {
                double at = now + RescanAfter[i];
                int index = rescans.BinarySearch(at);
                rescans.Insert(index < 0 ? ~index : index, at);
            }

            while (rescans.Count > 30)
            {
                rescans.RemoveAt(rescans.Count - 1);
            }
        }

        // Something made, here or from the save: if it holds lists, they are watched from now on.
        private void Discover(GameObject made)
        {
            PlayMakerArrayListProxy[] proxies = made.GetComponents<PlayMakerArrayListProxy>();
            if (proxies.Length == 0 || containers.ContainsKey(made.name))
            {
                return;
            }

            var container = new Container { Name = made.name, Owner = made, Lists = proxies };
            var lists = new List<ContainerList>(proxies.Length);
            container.Signature = Read(container, lists);
            container.Last = container.Signature == null ? null : lists;
            container.Empty = container.Last == null || IsEmpty(lists);
            containers[made.name] = container;
            containerList.Add(container);
            Contents later;
            if (laterContents.TryGetValue(made.name, out later))
            {
                laterContents.Remove(made.name);
                if (now - later.Since <= KeepWaiting)
                {
                    Fill(made.name, later.Lists);
                }
            }
        }

        // What changed in the lists of this machine's containers since the last look.
        private void WatchContents()
        {
            for (int i = containerList.Count - 1; i >= 0; i--)
            {
                Container container = containerList[i];

                // Unity's null check is also true for a destroyed object: an emptied bag is thrown away, or keeps
                // its shape and loses what made it a bag. Either way its copies must not give anything out again.
                if (container.Owner == null || Gone(container))
                {
                    containers.Remove(container.Name);
                    containerList.RemoveAt(i);
                    if (container.Last != null && !container.Empty)
                    {
                        current.ReportContents(container.Name, Emptied(container.Last));
                        log.Info(LogCategory.Fsm, "Makers: '" + container.Name + "' is gone here; its copies are emptied.");
                    }

                    continue;
                }

                var lists = new List<ContainerList>(container.Lists.Length);
                string holds = Read(container, lists);
                if (holds == null)
                {
                    continue;
                }

                container.Last = lists;
                container.Empty = IsEmpty(lists);
                if (holds == container.Signature)
                {
                    continue;
                }

                container.Signature = holds;
                current.ReportContents(container.Name, lists);
                log.Info(LogCategory.Fsm, "Makers: '" + container.Name + "' holds " + holds + " now; the others are told.");
            }
        }

        // The lists of a container as text, to compare; and as numbers, into lists if given. Null if a list
        // holds something that is not a whole number.
        private string Read(Container container, List<ContainerList> lists)
        {
            signature.Length = 0;
            for (int i = 0; i < container.Lists.Length; i++)
            {
                PlayMakerArrayListProxy proxy = container.Lists[i];
                if (proxy == null)
                {
                    return null;
                }

                ArrayList list = proxy.arrayList;
                int count = list == null ? 0 : list.Count;
                var values = new int[count];
                signature.Append(proxy.referenceName).Append('[');
                for (int j = 0; j < count; j++)
                {
                    object value = list[j];
                    if (!(value is int))
                    {
                        if (!container.Odd)
                        {
                            container.Odd = true;
                            log.Warning(LogCategory.Fsm, "Makers: list '" + proxy.referenceName + "' of '" + container.Name + "' holds a " + (value == null ? "null" : value.GetType().Name) + "; it is not shared.");
                        }

                        return null;
                    }

                    values[j] = (int)value;
                    signature.Append(j == 0 ? string.Empty : ",").Append(values[j]);
                }

                signature.Append(']');
                if (lists != null)
                {
                    lists.Add(new ContainerList(proxy.referenceName, values));
                }
            }

            return signature.ToString();
        }

        // Another machine says what a container holds: so it holds here.
        private void Fill(string name, List<ContainerList> lists)
        {
            Container container;
            if (!containers.TryGetValue(name, out container) || container.Owner == null)
            {
                GameObject owner = GameObject.Find(name);
                if (owner == null)
                {
                    laterContents[name] = new Contents { Lists = lists, Since = now };
                    return;
                }

                Discover(owner);
                if (!containers.TryGetValue(name, out container))
                {
                    log.Warning(LogCategory.Fsm, "Makers: another game says what '" + name + "' holds, and here it holds no lists.");
                    return;
                }
            }

            for (int i = 0; i < lists.Count; i++)
            {
                PlayMakerArrayListProxy proxy = Find(container, lists[i].Name);
                if (proxy == null || proxy.arrayList == null)
                {
                    continue;
                }

                proxy.arrayList.Clear();
                int[] values = lists[i].Values;
                for (int j = 0; j < values.Length; j++)
                {
                    proxy.arrayList.Add(values[j]);
                }
            }

            var held = new List<ContainerList>(container.Lists.Length);
            string signatureNow = Read(container, held);
            if (signatureNow != null)
            {
                container.Last = held;
                container.Empty = IsEmpty(held);
            }

            if (signatureNow != container.Signature)
            {
                log.Info(LogCategory.Fsm, "Makers: '" + name + "' holds " + signatureNow + " here now, as another game has it.");
            }

            container.Signature = signatureNow;
        }

        private static bool Gone(Container container)
        {
            for (int i = 0; i < container.Lists.Length; i++)
            {
                if (container.Lists[i] == null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEmpty(List<ContainerList> lists)
        {
            for (int i = 0; i < lists.Count; i++)
            {
                int[] values = lists[i].Values;
                for (int j = 0; j < values.Length; j++)
                {
                    if (values[j] != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // The same lists, as long, with nothing in them.
        private static List<ContainerList> Emptied(List<ContainerList> lists)
        {
            var empty = new List<ContainerList>(lists.Count);
            for (int i = 0; i < lists.Count; i++)
            {
                empty.Add(new ContainerList(lists[i].Name, new int[lists[i].Values.Length]));
            }

            return empty;
        }

        private static PlayMakerArrayListProxy Find(Container container, string referenceName)
        {
            for (int i = 0; i < container.Lists.Length; i++)
            {
                if (container.Lists[i] != null && container.Lists[i].referenceName == referenceName)
                {
                    return container.Lists[i];
                }
            }

            return null;
        }

        private static BodyPose PoseOf(Transform transform)
        {
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            return new BodyPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
        }

        private static string Where(Transform transform)
        {
            Vector3 position = transform.position;
            return "(" + position.x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ", " + position.y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + ", " + position.z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        private MakersSection makersCheck;

        /// <summary>The section of the makers' numbers (Fase 8 checksums).</summary>
        public ICheckSection MakersCheck
        {
            get { return makersCheck ?? (makersCheck = new MakersSection(this)); }
        }

        private bool CheckReady
        {
            get { return current != null && found && settledCount == spawners.Count; }
        }

        private void ReadCheck(List<CheckEntry> entries)
        {
            for (int i = 0; i < spawners.Count; i++)
            {
                if (spawners[i].Settled)
                {
                    entries.Add(new CheckEntry(ChecksumReplication.KeyOf(spawners[i].Key), (ulong)(uint)spawners[i].Count.Value));
                }
            }
        }

        // A client: makers whose number is not the host's. What the host's game made with them and this world lacks is made,
        // and then they take the host's numbers.
        private void FixCheckHere(IList<ulong> keys)
        {
            ObjectReplication list = objects.Replication;
            if (list != null)
            {
                var missing = new List<MissingThing>(list.MissingKeyed);
                missing.Sort(delegate(MissingThing a, MissingThing b) { return string.CompareOrdinal(a.Key, b.Key); });
                for (int i = 0; i < missing.Count; i++)
                {
                    Spawner spawner;
                    int number;
                    if (SpawnerOf(missing[i].Key, out spawner, out number) && keys.Contains(ChecksumReplication.KeyOf(spawner.Key)))
                    {
                        MakeForJoin(missing[i].Key, missing[i].Pose);
                    }
                }
            }

            RaiseCounts(new List<string>());
        }

        // The host: the numbers told again.
        private void FixCheckFor(IList<ulong> keys)
        {
            for (int i = 0; i < spawners.Count; i++)
            {
                if (keys.Contains(ChecksumReplication.KeyOf(spawners[i].Key)))
                {
                    TellCount(spawners[i]);
                }
            }
        }

        private sealed class MakersSection : ICheckSection
        {
            private readonly SpawnSync owner;

            public MakersSection(SpawnSync owner)
            {
                this.owner = owner;
            }

            public byte Id
            {
                get { return CheckSections.Makers; }
            }

            public string Name
            {
                get { return "makers"; }
            }

            public bool Ready
            {
                get { return owner.CheckReady; }
            }

            public bool CountsMissing
            {
                get { return false; }
            }

            public bool SendsEntries
            {
                get { return false; }
            }

            public void Read(List<CheckEntry> entries)
            {
                owner.ReadCheck(entries);
            }

            public ulong Coarse(ulong value)
            {
                return value;
            }

            public bool Same(ulong host, ulong here)
            {
                return host == here;
            }

            public void FixHere(IList<ulong> different, IList<ulong> onlyHost, IList<ulong> onlyHere)
            {
                owner.FixCheckHere(different);
            }

            public void FixFor(byte player, IList<ulong> keys)
            {
                owner.FixCheckFor(keys);
            }
        }

        private sealed class Spawner
        {
            public string Key;
            public PlayMakerFSM Fsm;
            public FsmInt Count;
            public FsmString Prefix;
            public FsmGameObject Made;
            public FsmGameObject Bag;
            public string WaitState;
            public bool Settled;
            public int Known;
        }

        private sealed class Container
        {
            public string Name;
            public GameObject Owner;
            public PlayMakerArrayListProxy[] Lists;
            public string Signature;
            public bool Odd;

            // What it held at the last look, and whether that was nothing.
            public List<ContainerList> Last;
            public bool Empty;
        }

        private sealed class Contents
        {
            public List<ContainerList> Lists;
            public double Since;
        }

        private sealed class Wanted
        {
            public SpawnEvent Spawn;
            public bool IsContents;
            public string Name;
            public List<ContainerList> Lists;
            public double Since;
        }
    }
}
