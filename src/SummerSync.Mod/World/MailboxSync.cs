using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The mailboxes of Teimo's flyer job: a flyer put in one in any game is in it in every
    /// game, and the others see its hatch open and close. The job's count of flyers delivered ("Delivered") is already the
    /// job's (<see cref="JobSync"/>); here only the boxes.
    ///
    /// What is assumed about the game (dump of 2026-10-15; docs/GAME_NOTES.md, "Los buzones de los volantes"):
    /// - Each box is a "BoxHatch" with a machine "WaitAd" and its own "BoxIndex". Its place in the list of
    ///   "JOBS/TeimoADs" (an ArrayList, saved as "TeimoAdBoxes") says whether it has a flyer; "Get state" reads it into
    ///   "AdvertPlaced", and a box with one waits in "Idle" (120 s) and reads it again.
    /// - The player's click with a flyer in hand leads to "Open" (the flyer is destroyed, the hatch opens) and "Close"
    ///   (AdvertPlaced and the list set, "Delivered" one more, the hatch closes), then "Idle".
    /// Here a flyer put in by another game's player is shown through "Open" and "Close", with the "Delivered" of that
    /// "Close" left out (the job's count comes from the game that delivered it) and nothing for "Open" to destroy. A box
    /// that is switched off (far, under a "LOD") only gets its place in the list.
    /// </summary>
    internal sealed class MailboxSync
    {
        private const string MachineName = "WaitAd";
        private const string IndexVariable = "BoxIndex";
        private const string PlacedVariable = "AdvertPlaced";
        private const string AdvertVariable = "AdvertObject";
        private const string OpenState = "Open";
        private const string CloseState = "Close";
        private const string CountActionType = "AddToFsmInt";
        private const string ListPath = "/JOBS/TeimoADs";
        private const string KeyPrefix = "ads/box/";
        private const string EventPrefix = "SUMMERSYNC ADS ";
        private const double FirstLookSeconds = 3.0;
        private const double BindSeconds = 5.0;
        private const double LookSeconds = 0.25;
        private const LogCategory Category = LogCategory.Fsm;

        // The roots under which the boxes are, switched on or not: the house's, the map's, the villages', the jobs'.
        private static readonly string[] Roots = { "YARD", "MAP", "PERAJARVI", "RYKIPOHJA", "JOBS" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Box> boxes = new List<Box>();
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();

        // Whether each came in the host's answer to a request: how the boxes are, not a flyer put in now.
        private readonly List<bool> heardCatchUp = new List<bool>();
        private TallyReplication current;
        private bool host;
        private bool bound;
        private double bindAt;
        private double nextLook;
        private ArrayList list;

        public MailboxSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's root object,
        /// or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                bound = false;
                bindAt = now + FirstLookSeconds;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }
            }

            if (current == null)
            {
                return;
            }

            if (!bound)
            {
                if (now < bindAt)
                {
                    return;
                }

                bindAt = now + BindSeconds;
                Bind();
                return;
            }

            if (list == null || manager.Session.State != SessionState.Connected || now < nextLook)
            {
                return;
            }

            nextLook = now + LookSeconds;
            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value, heardCatchUp[i]);
            }

            heard.Clear();
            heardCatchUp.Clear();
            for (int i = 0; i < boxes.Count; i++)
            {
                Watch(boxes[i]);
            }
        }

        /// <summary>Whatever was left out of a box's "Close" while showing another game's flyer is put back.</summary>
        public void Release()
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].CountAction != null)
                {
                    boxes[i].CountAction.Enabled = true;
                }
            }

            boxes.Clear();
            heard.Clear();
            heardCatchUp.Clear();
            list = null;
        }

        private void Bind()
        {
            bound = true;
            GameObject listObject = GameObject.Find(ListPath);
            PlayMakerArrayListProxy[] proxies = listObject == null ? new PlayMakerArrayListProxy[0] : listObject.GetComponents<PlayMakerArrayListProxy>();
            for (int i = 0; i < proxies.Length && list == null; i++)
            {
                if (proxies[i] != null && string.IsNullOrEmpty(proxies[i].referenceName))
                {
                    list = proxies[i].arrayList;
                }
            }

            if (list == null)
            {
                log.Warning(Category, "Mailboxes: no list of boxes at '" + ListPath + "' in this world; each game has its own flyers in them.");
                return;
            }

            var indices = new List<string>();
            for (int r = 0; r < Roots.Length; r++)
            {
                GameObject root = GameObject.Find("/" + Roots[r]);
                PlayMakerFSM[] machines = root == null ? new PlayMakerFSM[0] : root.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int m = 0; m < machines.Length; m++)
                {
                    PlayMakerFSM machine = machines[m];
                    if (machine.FsmName != MachineName)
                    {
                        continue;
                    }

                    FsmInt index = machine.FsmVariables.FindFsmInt(IndexVariable);
                    FsmBool placed = machine.FsmVariables.FindFsmBool(PlacedVariable);
                    if (index == null || placed == null || index.Value < 0 || index.Value >= list.Count || !DoorSync.HasState(machine, OpenState) || !DoorSync.HasState(machine, CloseState))
                    {
                        log.Warning(Category, "Mailboxes: '" + machine.gameObject.name + "' under '" + Roots[r] + "' is not made as this build expects; not shared.");
                        continue;
                    }

                    var box = new Box { Index = index.Value, Machine = machine, Placed = placed, Advert = machine.FsmVariables.FindFsmGameObject(AdvertVariable) };
                    box.Known = Read(box);
                    boxes.Add(box);
                    indices.Add(box.Index.ToString(CultureInfo.InvariantCulture) + (box.Known ? " (flyer)" : string.Empty));
                }
            }

            if (host)
            {
                // What this game has is the session's from the start.
                for (int i = 0; i < boxes.Count; i++)
                {
                    Tell(boxes[i], 0);
                }
            }
            else
            {
                current.RequestAll();
            }

            log.Info(Category, "Mailboxes: " + boxes.Count + " of the flyer job shared (" + string.Join(", ", indices.ToArray()) + "); " + (host ? "the host's list is the session's." : "the host's list is followed here."));
        }

        // Heard while the session reads its messages: done in this class's turn in the frame.
        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                heard.Add(new KeyValuePair<string, int[]>(key, values));
                heardCatchUp.Add(current != null && current.CatchingUp);
            }
        }

        private void Hear(string key, int[] values, bool catchUp)
        {
            int index;
            if (values.Length < 2 || values[1] == manager.Session.LocalPlayerId
                || !int.TryParse(key.Substring(KeyPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            {
                return;
            }

            bool placed = values[0] != 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                Box box = boxes[i];
                if (box.Index != index || Read(box) == placed)
                {
                    continue;
                }

                if (placed && !catchUp && box.Machine.enabled && box.Machine.gameObject.activeInHierarchy && Show(box))
                {
                    log.Info(Category, "Mailboxes: box " + index + " got a flyer in player " + values[1] + "'s game; its hatch opens and closes here.");
                }
                else
                {
                    Write(box, placed);
                    log.Info(Category, "Mailboxes: box " + index + (placed ? " got a flyer" : " was emptied") + " in player " + values[1] + "'s game; here too.");
                }

                box.Known = placed;
            }
        }

        // Another game's player put a flyer in: the box goes through "Open" and "Close" without counting it again and with
        // nothing for "Open" to destroy.
        private static bool Show(Box box)
        {
            if (box.CountAction == null)
            {
                box.CountAction = Find(box.Machine, CloseState, CountActionType);
                if (box.CountAction == null)
                {
                    return false;
                }
            }

            if (box.Advert != null)
            {
                box.Advert.Value = null;
            }

            box.CountAction.Enabled = false;
            box.Following = true;
            string eventName = EventPrefix + OpenState;
            if (!DoorSync.HasGlobalTransition(box.Machine, eventName))
            {
                box.Machine.AddGlobalTransition(eventName, OpenState);
            }

            box.Machine.SendEvent(eventName);
            return true;
        }

        private void Watch(Box box)
        {
            // Unity's null check is also true for a destroyed object.
            if (box.Machine == null)
            {
                return;
            }

            if (box.Following)
            {
                string state = box.Machine.ActiveStateName;
                if (state == OpenState || state == CloseState)
                {
                    return;
                }

                // Through "Close": the count is the game's own again.
                box.Following = false;
                box.CountAction.Enabled = true;
                box.Known = Read(box);
                return;
            }

            bool placed = Read(box);
            if (placed == box.Known)
            {
                return;
            }

            box.Known = placed;
            if (!host && !placed)
            {
                // A client's list is emptied by the host's game only.
                return;
            }

            Tell(box, manager.Session.LocalPlayerId);
            log.Info(Category, "Mailboxes: box " + box.Index + (placed ? " got a flyer" : " was emptied") + " here; told.");
        }

        private void Tell(Box box, int player)
        {
            current.Set(KeyPrefix + box.Index.ToString(CultureInfo.InvariantCulture), new[] { box.Known ? 1 : 0, player });
        }

        private bool Read(Box box)
        {
            object value = list[box.Index];
            return value is bool && (bool)value;
        }

        // Far away, the box only gets its place in the list, which it reads when it is switched on.
        private void Write(Box box, bool placed)
        {
            list[box.Index] = placed;
            box.Placed.Value = placed;
        }

        private static FsmStateAction Find(PlayMakerFSM machine, string state, string actionType)
        {
            FsmState[] states = machine.FsmStates;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                if (states[s].Name != state)
                {
                    continue;
                }

                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    if (actions[a] != null && actions[a].GetType().Name == actionType)
                    {
                        return actions[a];
                    }
                }
            }

            return null;
        }

        private MailboxSection check;

        /// <summary>The section of the mailboxes (Fase 8 checksums).</summary>
        public ICheckSection Check
        {
            get { return check ?? (check = new MailboxSection(this)); }
        }

        private bool CheckReady
        {
            get { return current != null && bound && list != null; }
        }

        private void ReadCheck(List<CheckEntry> entries)
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                entries.Add(new CheckEntry((ulong)(uint)boxes[i].Index, Read(boxes[i]) ? 1UL : 0UL));
            }
        }

        // A client: nothing to do by itself; the host says the boxes again.
        private void FixCheckHere(IList<ulong> keys)
        {
        }

        private void FixCheckFor(IList<ulong> keys)
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                if (keys.Contains((ulong)(uint)boxes[i].Index))
                {
                    boxes[i].Known = Read(boxes[i]);
                    Tell(boxes[i], manager.Session.LocalPlayerId);
                }
            }
        }

        private sealed class MailboxSection : ICheckSection
        {
            private readonly MailboxSync owner;

            public MailboxSection(MailboxSync owner)
            {
                this.owner = owner;
            }

            public byte Id
            {
                get { return CheckSections.Mailboxes; }
            }

            public string Name
            {
                get { return "mailboxes"; }
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

        private sealed class Box
        {
            public int Index;
            public PlayMakerFSM Machine;
            public FsmBool Placed;
            public FsmGameObject Advert;
            public FsmStateAction CountAction;
            public bool Known;
            public bool Following;
        }
    }
}
