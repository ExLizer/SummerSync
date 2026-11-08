using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Unity.Objects;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// A client whose world was loaded from the host's last save, while the host may have played on for an hour (Fase 8):
    /// once the host's whole list of things is here, the things the host's game made since the save are made here by
    /// their makers (keys the host has and this world lacks), the things it ate, threw away or used up are thrown away
    /// here the game's way (keys this world has and the host lacks), and the makers take the host's numbers. What each
    /// thing holds comes from the host as news of the thing (<see cref="ObjectPresenter"/>). What the host's game made with
    /// no number of its own and this game can make (firewood) is made once, as many of a name as the host has more than
    /// here, after the first pairings settled.
    ///
    /// It works for a while after the list comes and then leaves the world to the live news and the checksums.
    /// </summary>
    internal sealed class JoinSync
    {
        private const double LookSeconds = 2.0;
        private const double WorkSeconds = 90.0;
        private const double UnkeyedAfter = 6.0;
        private const LogCategory Category = LogCategory.Authority;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly ObjectPresenter objects;
        private readonly SpawnSync spawns;
        private readonly HashSet<string> tried = new HashSet<string>();
        private readonly HashSet<string> thrown = new HashSet<string>();
        private readonly HashSet<string> busy = new HashSet<string>();
        private readonly List<KeyValuePair<string, GameObject>> gone = new List<KeyValuePair<string, GameObject>>();
        private readonly List<MissingThing> missing = new List<MissingThing>();
        private readonly Dictionary<string, List<MissingThing>> unkeyed = new Dictionary<string, List<MissingThing>>();
        private ObjectReplication current;
        private double startedAt = double.NaN;
        private double nextLook;
        private bool done;
        private int made;
        private int thrownAway;
        private int left;
        private int raised;
        private bool unkeyedDone;

        public JoinSync(Logger log, NetworkManager manager, ObjectPresenter objects, SpawnSync spawns)
        {
            this.log = log;
            this.manager = manager;
            this.objects = objects;
            this.spawns = spawns;
        }

        /// <summary>Once per frame, after the makers and the loose objects.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            ObjectReplication list = localPlayer == null ? null : objects.Replication;
            if (list != current)
            {
                current = list;
                startedAt = double.NaN;
                nextLook = 0.0;
                done = false;
                made = thrownAway = left = raised = 0;
                unkeyedDone = false;
                tried.Clear();
                thrown.Clear();
            }

            if (current == null || done || manager.Mode != NetworkMode.Client || !current.ListComplete || !objects.FirstScanDone || now < nextLook)
            {
                return;
            }

            nextLook = now + LookSeconds;
            if (double.IsNaN(startedAt))
            {
                startedAt = now;
            }

            int madeBefore = made;
            int thrownBefore = thrownAway + left;
            int raisedBefore = raised;
            MakeMissing();
            if (!unkeyedDone && now - startedAt >= UnkeyedAfter)
            {
                unkeyedDone = true;
                MakeUnkeyed();
            }

            ThrowAwayGone();
            raised += spawns.RaiseCounts(busy);
            if (made != madeBefore || thrownAway + left != thrownBefore || raised != raisedBefore)
            {
                log.Info(
                    Category,
                    "Join: " + made + " thing(s) the host's game made since its save made here, " + thrownAway + " it ate or threw away since thrown away here"
                    + (left > 0 ? " (" + left + " with no way of their own to go, left)" : string.Empty) + ", " + raised + " maker number(s) raised to the host's.");
            }

            if (now - startedAt >= WorkSeconds)
            {
                done = true;
                log.Info(Category, "Join: done; " + current.MissingHere + " thing(s) of the host's still not here, " + current.OnlyHere + " only here.");
            }
        }

        // What the host's game made since the save, in each maker's order.
        private void MakeMissing()
        {
            missing.Clear();
            missing.AddRange(current.MissingKeyed);
            missing.Sort(CompareKeys);
            busy.Clear();
            for (int i = 0; i < missing.Count; i++)
            {
                MissingThing thing = missing[i];
                if (!spawns.MakesKey(thing.Key))
                {
                    continue;
                }

                busy.Add(PrefixOf(thing.Key));
                if (!tried.Add(thing.Key))
                {
                    continue;
                }

                if (spawns.MakeForJoin(thing.Key, thing.Pose))
                {
                    made++;
                }
                else
                {
                    log.Warning(Category, "Join: '" + thing.Name + "' (" + thing.Key + ") of the host's world could not be made here; it stays only there.");
                }
            }
        }

        // What the host's game made since the save with no number of its own, that this game can make too: as many of a name
        // as the host has more than this world, where the host's are; the loose objects' replication pairs them by name.
        private void MakeUnkeyed()
        {
            unkeyed.Clear();
            IList<MissingThing> all = current.MissingUnkeyed;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Name != Firewood.PieceName)
                {
                    continue;
                }

                List<MissingThing> named;
                if (!unkeyed.TryGetValue(all[i].Name, out named))
                {
                    named = new List<MissingThing>();
                    unkeyed.Add(all[i].Name, named);
                }

                named.Add(all[i]);
            }

            foreach (KeyValuePair<string, List<MissingThing>> named in unkeyed)
            {
                int onlyHere = current.CountOnlyHere(named.Key);
                int wanted = named.Value.Count - onlyHere;
                int before = made;
                for (int i = 0; i < wanted; i += 2)
                {
                    BodyPose pose = named.Value[i].Pose;
                    var at = new Vector3(pose.X, pose.Y, pose.Z);
                    var rotation = new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW);
                    made += Firewood.MakeForJoin(at, rotation, Math.Min(2, wanted - i));
                }

                if (wanted > 0)
                {
                    log.Info(Category, "Join: the host has " + named.Value.Count + " '" + named.Key + "' this world lacks (" + onlyHere + " here it lacks); " + (made - before) + " made here.");
                }
            }
        }

        // What the host's game ate, threw away or used up since the save.
        private void ThrowAwayGone()
        {
            objects.GoneFromHost(gone);
            for (int i = 0; i < gone.Count; i++)
            {
                string key = gone[i].Key;
                GameObject thing = gone[i].Value;
                if (!thrown.Add(key))
                {
                    continue;
                }

                string name = thing.name;
                if (objects.ThrowAwayGone(thing))
                {
                    thrownAway++;
                    log.Info(Category, "Join: '" + name + "' (" + key + ") is no longer in the host's world: thrown away here the game's way.");
                }
                else
                {
                    left++;
                    log.Warning(Category, "Join: '" + name + "' (" + key + ") is no longer in the host's world, and has no way of its own to go: left here.");
                }
            }
        }

        // "beercase12": "beercase".
        private static string PrefixOf(string key)
        {
            int end = key.Length;
            while (end > 0 && char.IsDigit(key[end - 1]))
            {
                end--;
            }

            return key.Substring(0, end);
        }

        // By prefix, then by number: a maker makes its numbers in order.
        private static int CompareKeys(MissingThing a, MissingThing b)
        {
            string prefixA = PrefixOf(a.Key);
            string prefixB = PrefixOf(b.Key);
            int byPrefix = string.CompareOrdinal(prefixA, prefixB);
            if (byPrefix != 0)
            {
                return byPrefix;
            }

            int numberA;
            int numberB;
            int.TryParse(a.Key.Substring(prefixA.Length), out numberA);
            int.TryParse(b.Key.Substring(prefixB.Length), out numberB);
            return numberA.CompareTo(numberB);
        }
    }
}
