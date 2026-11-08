using System.Collections.Generic;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using UnityEngine;

namespace SummerSync.Unity.Objects
{
    /// <summary>
    /// A world that joins one the host has played on since its save (Fase 8). A thing's key is what the game calls it in
    /// its save: the string "ID" of its machine "Use" ("beercase0", "sausagesx0", "fuse1"; dump of 2026-10-15,
    /// docs/GAME_NOTES.md), the same in every world loaded from that save, and a thing made later gets its maker's prefix
    /// and number. With it the host's things pair with this world's however far they were moved or renamed, and a client
    /// tells what the host's game made since the save (keys the host has and this world lacks) from what it ate or threw
    /// away (keys this world has and the host lacks); <see cref="World.JoinSync"/> does what that takes.
    ///
    /// The host, when a client has its whole list, tells it what each thing is in its game now, the spoiling of food too,
    /// which no game tells as it goes (it changes by itself in every game).
    /// </summary>
    internal sealed partial class ObjectPresenter
    {
        private const string KeyVariable = "ID";

        private readonly List<byte> joined = new List<byte>();
        private readonly List<Tracked> fromSave = new List<Tracked>();
        private bool fromSaveTaken;

        /// <summary>Whether the first look for loose things in this world is done.</summary>
        public bool FirstScanDone
        {
            get { return firstScanDone; }
        }

        /// <summary>The replication of the loose things this presenter shows, or null without a session and a world.</summary>
        public ObjectReplication Replication
        {
            get { return current; }
        }

        // The key of a thing: its "Use"'s "ID", or null.
        private static string KeyOf(GameObject thing)
        {
            PlayMakerFSM[] machines = thing.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName != UseMachine)
                {
                    continue;
                }

                FsmString id = machines[i].FsmVariables.FindFsmString(KeyVariable);
                return id == null || string.IsNullOrEmpty(id.Value) ? null : id.Value;
            }

            return null;
        }

        private void OnSubscribed(byte player)
        {
            if (!joined.Contains(player))
            {
                joined.Add(player);
            }
        }

        // The host: what each thing is in this game, to the clients that have just got the whole list.
        private void TellJoined()
        {
            if (joined.Count == 0 || items == null || !current.WorldReady)
            {
                return;
            }

            for (int p = 0; p < joined.Count; p++)
            {
                int told = 0;
                for (int i = 0; i < tracked.Count; i++)
                {
                    Tracked item = tracked[i];
                    if (!item.Shared.HasId || item.Body == null || item.Transform == null || ReadItem(item, itemValues, true) == null)
                    {
                        continue;
                    }

                    if (items.TellStateTo(joined[p], item.Shared.Id, item.Transform.gameObject.name, itemValues))
                    {
                        told++;
                    }
                }

                log.Info(LogCategory.Authority, "Told player " + joined[p] + " what " + told + " thing(s) are in this game now (contents, food's spoiling), for a world loaded from the save.");
            }

            joined.Clear();
        }

        // A client: the things this world had when the host's whole list came, the save's; only those can have been eaten
        // or thrown away in the host's game since the save. What this game makes later is not one of them.
        private void TakeFromSave()
        {
            if (fromSaveTaken || current == null || !current.ListComplete || !firstScanDone || manager.Mode != Core.Net.NetworkMode.Client)
            {
                return;
            }

            fromSaveTaken = true;
            fromSave.Clear();
            fromSave.AddRange(tracked);
        }

        /// <summary>
        /// A client: the things of the save this world has and the host's world no longer has (a key the host lacks), not
        /// in the local player's hand, each with what it is called. Empty until the host's whole list is here. A part of
        /// the car or a thing that mounts is not one: the host's game may have built it in, and then it is no longer loose
        /// there; the assembly and the mounts take this one where the host's is.
        /// </summary>
        public void GoneFromHost(List<KeyValuePair<string, GameObject>> found)
        {
            found.Clear();
            if (!fromSaveTaken || current == null || !current.ListComplete)
            {
                return;
            }

            for (int i = 0; i < fromSave.Count; i++)
            {
                Tracked item = fromSave[i];

                // Unity's null check is also true for a destroyed object.
                if (item.Transform == null || item.Shared.HasId || item.Shared.Key.Length == 0 || item == lastInHand || item.KeepWhenGone || current.HostHasKey(item.Shared.Key))
                {
                    continue;
                }

                found.Add(new KeyValuePair<string, GameObject>(item.Shared.Key, item.Transform.gameObject));
            }
        }

        /// <summary>
        /// A client: a thing of the save that the host's game ate or threw away since then goes the game's way here
        /// (GARBAGE), or is switched off if it has no way of its own. False if it had neither and was left.
        /// </summary>
        public bool ThrowAwayGone(GameObject thing)
        {
            for (int i = 0; i < fromSave.Count; i++)
            {
                if (fromSave[i].Transform != null && fromSave[i].Transform.gameObject == thing)
                {
                    Restore(fromSave[i]);
                    fromSave.RemoveAt(i);
                    break;
                }
            }

            PlayMakerFSM use = GarbageMachine(thing);
            if (use == null)
            {
                return false;
            }

            use.SendEvent(GarbageEvent);
            return true;
        }
    }
}
