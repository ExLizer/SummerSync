using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using SummerSync.Unity.Objects;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Where firewood is put away, the same in every game: the wood carrier and the flatbed's pile.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "El canasto de leña y la pila del acoplado"):
    /// - a carrier, "wood carrier(itemx)" (there are two, told apart by the name they are saved under, "UniqueTagWood"):
    ///   its "WoodTrigger" (machine "Trigger") waits in "Wait wood" for a
    ///   "firewood(Clone)" that moves (its body's speed above 0.001), destroys it, switches on one of the ten logs under
    ///   "Woods" and adds one to "WoodsIndex". "PickupTrigger" sends PICKWOOD, which goes to "Array": the top log off, a
    ///   new firewood ("Prefab") made at the pickup place and put under ITEMS, one less. What it holds is which logs are
    ///   on, and that number. "Array" takes the top log that a camera sees ("GameObjectIsVisible"); with none in sight it
    ///   gets down to "log1" and makes a piece from it even when it is off.
    /// - the flatbed, "FLATBED/Bed/LogTrigger" (machine "Logic"): in "Check log" a "log(Clone)" or "firewood(Clone)" that
    ///   came into the box is destroyed and added to "Logs" (and "Firewood"); "Add scale" makes the pile's size, the
    ///   price and the box's weight from them. With the box tipped ("Unload") "Wait" takes the pile down and "Pay"
    ///   pays; that runs in every game by itself.
    ///
    /// A piece of wood is taken by the game that moves it: a copy of one another machine moves has no speed for the
    /// carrier, and the flatbed here is made not to see it. The carrier is told as it is, by whoever changed it last; a
    /// game with more logs than told takes them out as "Array" does with the log in sight, one piece per log, so the
    /// piece taken out is made in every game. Not through "Array" itself: in a game whose player is not looking at the
    /// carrier it made pieces out of nothing (2026-11-12). A piece made here that way and fallen into this flatbed
    /// within a few seconds goes without being counted: the game that took it out has it in its own flatbed, counted
    /// there. The flatbed's pile is added up by the host from what each game took in.
    ///
    /// Offline and in the loopback test nothing here touches the game.
    /// </summary>
    internal sealed class WoodSync
    {
        private const string CarrierKey = "wood/carrier/";
        private const string FlatbedKey = "wood/flatbed";
        private const string EventPrefix = "SUMMERSYNC WOOD ";
        private const double SearchInterval = 3.0;

        // Pieces made here for another game's take-out: one at a time, as a hand takes them out; and for how long one
        // that falls into this flatbed is that game's to count.
        private const double PickSpacing = 0.15;
        private const double MadeForOthersSeconds = 3.0;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly ObjectPresenter objects;
        private TallyReplication current;
        private bool host;
        private double nextSearch;
        private readonly List<Carrier> carriers = new List<Carrier>();
        private readonly Dictionary<string, Heard> carriersHeard = new Dictionary<string, Heard>();
        private Flatbed flatbed;
        private bool searched;
        private bool flatbedHeard;
        private int[] flatbedValues;
        private bool flatbedCatchUp;
        private double clock;
        private Transform items;

        // Pieces made here because another game took them out of a carrier, by their instance number.
        private readonly Dictionary<int, Made> madeForOthers = new Dictionary<int, Made>();
        private readonly List<int> expired = new List<int>();

        public WoodSync(Logger log, NetworkManager manager, ObjectPresenter objects)
        {
            this.log = log;
            this.manager = manager;
            this.objects = objects;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            clock = now;
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                    current.ChangeWanted -= OnChangeWanted;
                }

                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                    current.ChangeWanted += OnChangeWanted;
                }

                carriers.Clear();
                carriersHeard.Clear();
                madeForOthers.Clear();
                items = null;
                flatbed = null;
                searched = false;
                flatbedHeard = false;
                nextSearch = now + 1.0;
            }

            if (current == null)
            {
                return;
            }

            if ((!searched || flatbed == null) && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            for (int i = carriers.Count - 1; i >= 0; i--)
            {
                // Unity's null check is also true for a destroyed object.
                if (carriers[i].Machine == null)
                {
                    carriers.RemoveAt(i);
                    continue;
                }

                WorkCarrier(carriers[i], now);
            }

            if (flatbed != null)
            {
                WorkFlatbed();
            }
        }

        private void Search()
        {
            if (flatbed == null)
            {
                flatbed = Flatbed.Find();
                if (flatbed != null)
                {
                    Hook(flatbed);
                }
            }

            GameObject itemsRoot = searched ? null : GameObject.Find("/ITEMS");
            if (itemsRoot == null)
            {
                return;
            }

            searched = true;
            items = itemsRoot.transform;
            Carrier.FindAll(carriers);
            log.Info(
                LogCategory.Fsm,
                "Firewood: " + carriers.Count + " wood carrier(s) and " + (flatbed != null ? "the flatbed's pile (" + flatbed.KnownLogs + ")" : "no flatbed") + " are shared.");
            if (!host)
            {
                return;
            }

            for (int i = 0; i < carriers.Count; i++)
            {
                // What the host's carriers hold is the session's from the start.
                current.Set(CarrierKey + carriers[i].Name, new[] { carriers[i].KnownIndex, carriers[i].KnownMask, LocalId() });
            }
        }

        // The flatbed here does not take a piece of wood another machine moves: that machine's flatbed does.
        private void Hook(Flatbed bed)
        {
            try
            {
                FsmGameObject piece = bed.Piece;
                if (!bed.Machine.FsmInject(Flatbed.CheckState, () => Skip(piece), false, 0, false))
                {
                    throw new InvalidOperationException("its '" + Flatbed.CheckState + "' could not take the mod's action");
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Fsm, "Firewood: the flatbed's box is not as this build expects (" + exception.Message + "): a piece thrown in may count twice.");
            }
        }

        private void Skip(FsmGameObject piece)
        {
            GameObject thing = piece == null ? null : piece.Value;
            if (current == null || thing == null)
            {
                return;
            }

            Made made;
            int id = thing.GetInstanceID();
            if (madeForOthers.TryGetValue(id, out made))
            {
                madeForOthers.Remove(id);
                if (clock <= made.Until)
                {
                    // The game that took it out of the carrier had its own piece fall into its own flatbed, and counted it.
                    piece.Value = null;
                    UnityEngine.Object.Destroy(thing);
                    log.Info(LogCategory.Fsm, "Firewood: a piece made here for player " + made.Player + "'s take-out fell into the flatbed; gone here, counted in that game.");
                    return;
                }
            }

            if (objects.MovedElsewhere(thing))
            {
                piece.Value = null;
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(CarrierKey, StringComparison.Ordinal) && values.Length >= 3)
            {
                // What this game said comes back from the host: not news here.
                NetSession session = manager.Session;
                if (session != null && values[2] == session.LocalPlayerId && !current.CatchingUp)
                {
                    return;
                }

                carriersHeard[key.Substring(CarrierKey.Length)] = new Heard { Values = values, CatchUp = current.CatchingUp };
            }
            else if (key == FlatbedKey && values.Length >= 2 && !host)
            {
                flatbedHeard = true;
                flatbedValues = values;
                flatbedCatchUp = current.CatchingUp;
            }
        }

        // On the host: a client's flatbed took wood in.
        private void OnChangeWanted(string key, int[] change, byte player)
        {
            if (key != FlatbedKey || change.Length < 2 || flatbed == null)
            {
                return;
            }

            flatbed.AddLogs += Math.Max(0, change[0]);
            flatbed.AddFirewood += Math.Max(0, change[1]);
        }

        private void WorkCarrier(Carrier carrier, double now)
        {
            Heard heard;
            if (carriersHeard.Count > 0 && carriersHeard.TryGetValue(carrier.Name, out heard))
            {
                carriersHeard.Remove(carrier.Name);
                if (!carrier.Wanted)
                {
                    carrier.Picks = 0;
                }

                carrier.Wanted = true;
                carrier.WantedIndex = heard.Values[0];
                carrier.WantedMask = heard.Values[1];
                carrier.WantedBy = heard.Values[2];
                carrier.CatchUp = heard.CatchUp;
            }

            int mask = carrier.Mask();
            int index = carrier.Index.Value;
            if (carrier.Wanted)
            {
                bool awake = carrier.Machine.enabled && carrier.Machine.gameObject.activeInHierarchy;
                if (!carrier.CatchUp && awake && Count(mask) > Count(carrier.WantedMask))
                {
                    // Taken out in another game: out here too, one piece per log. Every one takes a log off, so this ends.
                    if (now >= carrier.NextPick)
                    {
                        carrier.NextPick = now + PickSpacing;
                        TakeOut(carrier, now);
                    }

                    return;
                }

                if (carrier.WantedMask == mask && carrier.WantedIndex == index)
                {
                    carrier.Wanted = false;
                    carrier.KnownIndex = index;
                    carrier.KnownMask = mask;
                    return;
                }

                bool more = Count(carrier.WantedMask) > Count(mask);
                for (int i = 0; i < carrier.Logs.Length; i++)
                {
                    if (carrier.Logs[i] != null)
                    {
                        carrier.Logs[i].SetActive((carrier.WantedMask & (1 << i)) != 0);
                    }
                }

                carrier.Index.Value = carrier.WantedIndex;
                if (carrier.Pickup != null)
                {
                    carrier.Pickup.SetActive(carrier.WantedMask != 0);
                }

                if (more && !carrier.CatchUp && carrier.Sound != null)
                {
                    carrier.Sound.Play();
                }

                log.Info(
                    LogCategory.Fsm,
                    "Firewood: the carrier '" + carrier.Name + "' has " + Count(carrier.WantedMask) + " log(s) " + (carrier.CatchUp ? "in the host's game (a catch-up on joining)" : "after another game's change")
                    + (carrier.Picks > 0 ? "; " + carrier.Picks + " taken out here too." : "."));
                carrier.Wanted = false;
                carrier.KnownIndex = carrier.WantedIndex;
                carrier.KnownMask = carrier.WantedMask;
                return;
            }

            string state = carrier.Machine.ActiveStateName;
            if ((mask != carrier.KnownMask || index != carrier.KnownIndex) && (state == Carrier.RestState || state == Carrier.WaitState || string.IsNullOrEmpty(state)))
            {
                log.Info(LogCategory.Fsm, "Firewood: the carrier '" + carrier.Name + "' went from " + Count(carrier.KnownMask) + " to " + Count(mask) + " log(s) in this game; told.");
                carrier.KnownMask = mask;
                carrier.KnownIndex = index;
                current.Set(CarrierKey + carrier.Name, new[] { index, mask, LocalId() });
            }
        }

        // One log out of the carrier as "Array" takes it with the log in sight: the top one off, one less, and a piece of
        // firewood made at the pickup place, under ITEMS. "Array" itself is not used: it takes the log a camera sees, and
        // a game whose player is not looking at the carrier got pieces made out of logs already off, up to twelve every
        // time another player emptied it (a host's log of 2026-11-12).
        private void TakeOut(Carrier carrier, double now)
        {
            int top = -1;
            for (int i = carrier.Logs.Length - 1; i >= 0; i--)
            {
                if (carrier.Logs[i] != null && carrier.Logs[i].activeSelf)
                {
                    top = i;
                    break;
                }
            }

            if (top < 0)
            {
                return;
            }

            carrier.Logs[top].SetActive(false);
            carrier.Index.Value = top - 1;
            carrier.Picks++;
            GameObject prefab = carrier.Prefab == null ? null : carrier.Prefab.Value;
            if (prefab == null || carrier.Pickup == null || items == null)
            {
                if (!carrier.PrefabMissing)
                {
                    carrier.PrefabMissing = true;
                    log.Warning(LogCategory.Fsm, "Firewood: the carrier '" + carrier.Name + "' has no piece of wood to make or no place to make it: what another player takes out of it is not made here.");
                }

                return;
            }

            Transform at = carrier.Pickup.transform;
            var made = (GameObject)UnityEngine.Object.Instantiate(prefab, at.position, Quaternion.Euler(at.eulerAngles));
            made.transform.SetParent(items, true);
            ForgetMade(now);
            madeForOthers[made.GetInstanceID()] = new Made { Until = now + MadeForOthersSeconds, Player = carrier.WantedBy };
            if (carrier.WantedBy > Protocol.HostPlayerId && carrier.WantedBy <= byte.MaxValue && carrier.WantedBy != LocalId())
            {
                // On the host, the player who took it out moves it from the start: it may be in their hand already.
                objects.Expect(made, (byte)carrier.WantedBy, now);
            }
        }

        private void ForgetMade(double now)
        {
            if (madeForOthers.Count == 0)
            {
                return;
            }

            expired.Clear();
            foreach (KeyValuePair<int, Made> entry in madeForOthers)
            {
                if (entry.Value.Until < now)
                {
                    expired.Add(entry.Key);
                }
            }

            for (int i = 0; i < expired.Count; i++)
            {
                madeForOthers.Remove(expired[i]);
            }

            expired.Clear();
        }

        private void WorkFlatbed()
        {
            // Unity's null check is also true for a destroyed object.
            if (flatbed.Machine == null)
            {
                flatbed = null;
                return;
            }

            if (flatbedHeard)
            {
                flatbedHeard = false;
                flatbed.Heard = true;
                flatbed.HeardLogs = flatbedValues[0];
                flatbed.HeardFirewood = flatbedValues[1];
                flatbed.CatchUp = flatbedCatchUp;
            }

            bool resting = flatbed.Machine.ActiveStateName == Flatbed.RestState && !flatbed.Unload.Value;
            int logs = Mathf.RoundToInt(flatbed.Logs.Value);
            int firewood = Mathf.RoundToInt(flatbed.Firewood.Value);
            if (logs != flatbed.KnownLogs || firewood != flatbed.KnownFirewood)
            {
                var before = new[] { flatbed.KnownLogs, flatbed.KnownFirewood };
                bool more = logs > flatbed.KnownLogs || firewood > flatbed.KnownFirewood;
                if (more)
                {
                    // Wood came in here: the host adds it up.
                    flatbed.KnownLogs = logs;
                    flatbed.KnownFirewood = firewood;
                    current.Tell(FlatbedKey, before, new[] { logs, firewood });
                    log.Info(LogCategory.Fsm, "Firewood: the flatbed took wood in here (" + before[0] + " to " + logs + "); told.");
                }
                else if (resting || !host)
                {
                    // Unloaded: every game unloads its own. The host says where it ends, for whoever joins.
                    flatbed.KnownLogs = logs;
                    flatbed.KnownFirewood = firewood;
                    if (host)
                    {
                        current.Tell(FlatbedKey, before, new[] { logs, firewood });
                        log.Info(LogCategory.Fsm, "Firewood: the flatbed was unloaded (" + before[0] + " to " + logs + "); told.");
                    }
                }
            }

            if (host && (flatbed.AddLogs > 0 || flatbed.AddFirewood > 0))
            {
                var before = new[] { flatbed.KnownLogs, flatbed.KnownFirewood };
                Write(flatbed, logs + flatbed.AddLogs, firewood + flatbed.AddFirewood, resting);
                log.Info(LogCategory.Fsm, "Firewood: the flatbed took wood in another game (" + before[0] + " to " + flatbed.KnownLogs + "); told to all.");
                flatbed.AddLogs = 0;
                flatbed.AddFirewood = 0;
                current.Tell(FlatbedKey, before, new[] { flatbed.KnownLogs, flatbed.KnownFirewood });
                return;
            }

            if (!flatbed.Heard)
            {
                return;
            }

            flatbed.Heard = false;
            if (flatbed.CatchUp)
            {
                if (flatbed.HeardLogs != logs || flatbed.HeardFirewood != firewood)
                {
                    Write(flatbed, flatbed.HeardLogs, flatbed.HeardFirewood, resting);
                    log.Info(LogCategory.Fsm, "Firewood: the flatbed's pile is " + flatbed.HeardLogs + " in the host's game; so it is here (a catch-up on joining).");
                }
            }
            else if (flatbed.HeardLogs > logs || flatbed.HeardFirewood > firewood)
            {
                Write(flatbed, Math.Max(logs, flatbed.HeardLogs), Math.Max(firewood, flatbed.HeardFirewood), resting);
                log.Info(LogCategory.Fsm, "Firewood: the flatbed's pile is " + flatbed.KnownLogs + " after wood taken in another game.");
            }
            else if (flatbed.HeardLogs == 0 && flatbed.HeardFirewood == 0 && (logs > 0 || firewood > 0) && resting)
            {
                // The host's was unloaded and this one was not: emptied, the price left as it was for this game's own pay.
                flatbed.Logs.Value = 0f;
                flatbed.Firewood.Value = 0f;
                flatbed.KnownLogs = 0;
                flatbed.KnownFirewood = 0;
                if (flatbed.Pile != null)
                {
                    Vector3 scale = flatbed.Pile.localScale;
                    scale.z = 0f;
                    flatbed.Pile.localScale = scale;
                }

                log.Info(LogCategory.Fsm, "Firewood: the flatbed is empty in the host's game; emptied here.");
            }
        }

        // The numbers written, and the pile, the price and the weight made from them the game's way.
        private static void Write(Flatbed bed, int logs, int firewood, bool resting)
        {
            bed.Logs.Value = logs;
            bed.Firewood.Value = firewood;
            bed.KnownLogs = logs;
            bed.KnownFirewood = firewood;
            if (resting)
            {
                ToState(bed.Machine, Flatbed.ScaleState);
            }
        }

        private int LocalId()
        {
            NetSession session = manager.Session;
            return session == null ? 0 : session.LocalPlayerId;
        }

        private static int Count(int mask)
        {
            int count = 0;
            for (int i = 0; i < 16; i++)
            {
                count += (mask >> i) & 1;
            }

            return count;
        }

        // Takes a machine to one of its states through an event of the mod's.
        private static void ToState(PlayMakerFSM machine, string state)
        {
            if (!DoorSync.HasState(machine, state))
            {
                return;
            }

            string eventName = EventPrefix + state;
            if (!DoorSync.HasGlobalTransition(machine, eventName))
            {
                machine.AddGlobalTransition(eventName, state);
            }

            machine.SendEvent(eventName);
        }

        private struct Heard
        {
            public int[] Values;
            public bool CatchUp;
        }

        private struct Made
        {
            public double Until;
            public int Player;
        }

        private sealed class Carrier
        {
            private const string ObjectName = "wood carrier(itemx)";

            /// <summary>The name the game saves its logs under: the same carrier in every game.</summary>
            public string Name;
            public const string RestState = "Wait wood";
            public const string WaitState = "Wait";

            public PlayMakerFSM Machine;
            public FsmInt Index;
            public FsmGameObject Prefab;
            public GameObject[] Logs;
            public GameObject Pickup;
            public AudioSource Sound;
            public int KnownIndex;
            public int KnownMask;
            public bool Wanted;
            public int WantedIndex;
            public int WantedMask;
            public int WantedBy;
            public bool CatchUp;
            public int Picks;
            public double NextPick;
            public bool PrefabMissing;

            /// <summary>Every carrier of the world, wherever it lies (in the hand it hangs from the hand).</summary>
            public static void FindAll(List<Carrier> into)
            {
                PlayMakerFSM[] machines = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
                for (int m = 0; m < machines.Length; m++)
                {
                    PlayMakerFSM machine = machines[m];
                    Transform trigger = machine == null ? null : machine.transform;
                    Transform root = trigger == null ? null : trigger.parent;
                    if (root == null || machine.FsmName != "Trigger" || trigger.name != "WoodTrigger" || root.name != ObjectName || root.gameObject.hideFlags != HideFlags.None)
                    {
                        continue;
                    }

                    FsmInt index = machine.FsmVariables.FindFsmInt("WoodsIndex");
                    FsmString saved = machine.FsmVariables.FindFsmString("UniqueTagWood");
                    Transform woods = root.Find("Woods");
                    if (index == null || saved == null || string.IsNullOrEmpty(saved.Value) || woods == null)
                    {
                        continue;
                    }

                    var logs = new GameObject[10];
                    for (int i = 0; i < logs.Length; i++)
                    {
                        Transform piece = woods.Find("log" + (i + 1));
                        logs[i] = piece == null ? null : piece.gameObject;
                    }

                    Transform pickup = root.Find("PickupTrigger");
                    var found = new Carrier
                    {
                        Name = saved.Value,
                        Machine = machine,
                        Index = index,
                        Prefab = machine.FsmVariables.FindFsmGameObject("Prefab"),
                        Logs = logs,
                        Pickup = pickup == null ? null : pickup.gameObject,
                        Sound = trigger.GetComponent<AudioSource>(),
                    };
                    found.KnownIndex = index.Value;
                    found.KnownMask = found.Mask();
                    into.Add(found);
                }
            }

            public int Mask()
            {
                int mask = 0;
                for (int i = 0; i < Logs.Length; i++)
                {
                    if (Logs[i] != null && Logs[i].activeSelf)
                    {
                        mask |= 1 << i;
                    }
                }

                return mask;
            }
        }

        private sealed class Flatbed
        {
            public const string RestState = "Wait log";
            public const string CheckState = "Check log";
            public const string ScaleState = "Add scale";

            public PlayMakerFSM Machine;
            public FsmFloat Logs;
            public FsmFloat Firewood;
            public FsmBool Unload;
            public FsmGameObject Piece;
            public Transform Pile;
            public int KnownLogs;
            public int KnownFirewood;
            public int AddLogs;
            public int AddFirewood;
            public bool Heard;
            public int HeardLogs;
            public int HeardFirewood;
            public bool CatchUp;

            public static Flatbed Find()
            {
                GameObject root = GameObject.Find("/FLATBED");
                Transform trigger = root == null ? null : root.transform.Find("Bed/LogTrigger");
                PlayMakerFSM machine = trigger == null ? null : DoorSync.FindMachine(trigger, "Logic");
                if (machine == null)
                {
                    return null;
                }

                FsmVariables variables = machine.FsmVariables;
                var found = new Flatbed
                {
                    Machine = machine,
                    Logs = variables.FindFsmFloat("Logs"),
                    Firewood = variables.FindFsmFloat("Firewood"),
                    Unload = variables.FindFsmBool("Unload"),
                    Piece = variables.FindFsmGameObject("Log"),
                    Pile = root.transform.Find("Bed/Logs/logpile"),
                };
                if (found.Logs == null || found.Firewood == null || found.Unload == null || found.Piece == null)
                {
                    return null;
                }

                found.KnownLogs = Mathf.RoundToInt(found.Logs.Value);
                found.KnownFirewood = Mathf.RoundToInt(found.Firewood.Value);
                return found;
            }
        }
    }
}
