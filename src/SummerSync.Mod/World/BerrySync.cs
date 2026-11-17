using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The bushes of the strawberry field: what has been picked off each one is the same in every game, so a bush one
    /// player emptied is empty for the others. The box, the stand and the money are the job's (<see cref="JobSync"/>,
    /// "strawberry"), and what the box holds travels with the loose things.
    ///
    /// What is assumed about the game (decoded level; docs/GAME_NOTES.md, "Las frutillas"):
    /// - "JOBS/StrawberryField/Rows/RowN" (twelve rows) each have two ArrayLists: "bushes", their bushes, and "berries", a
    ///   number per bush of what was picked off it. The row's "Bush" machine hangs the row's one "BerryTrigger" on the bush
    ///   nearest this game's player each second and keeps its place in the lists in "Index".
    /// - The trigger's "Pick" reads the bush's number into its "Berries" when the mouse comes over it ("Check bush 2": over
    ///   50, "EMPTY BUSH") and, when the picked berry has been dropped in the box ("Drop berry"), adds the player's skill to
    ///   it and writes it back to the list. Nothing else writes the lists, and nothing of a bush looks different.
    /// </summary>
    internal sealed class BerrySync
    {
        private const string RowsPath = "/JOBS/StrawberryField/Rows";
        private const string PickedList = "berries";
        private const string TriggerName = "BerryTrigger";
        private const string TriggerMachine = "Pick";
        private const string TriggerPicked = "Berries";
        private const string RowMachine = "Bush";
        private const string RowIndex = "Index";
        private const string KeyPrefix = "berry/";
        private const int Scale = 1000;
        private const double FirstLookSeconds = 3.0;
        private const double BindSeconds = 5.0;
        private const double LookSeconds = 0.5;
        private const LogCategory Category = LogCategory.Fsm;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Row> rows = new List<Row>();
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();

        // Whether each came in the host's answer to a request: how the bushes are, not a berry picked now.
        private readonly List<bool> heardCatchUp = new List<bool>();
        private TallyReplication current;
        private bool host;
        private bool bound;
        private double bindAt;
        private double nextLook;

        public BerrySync(Logger log, NetworkManager manager)
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

            if (rows.Count == 0 || manager.Session.State != SessionState.Connected || now < nextLook)
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
            for (int i = 0; i < rows.Count; i++)
            {
                Watch(rows[i]);
            }
        }

        /// <summary>Nothing of the game is changed to be put back: only what this class holds is let go.</summary>
        public void Release()
        {
            rows.Clear();
            heard.Clear();
            heardCatchUp.Clear();
        }

        private void Bind()
        {
            bound = true;
            GameObject field = GameObject.Find(RowsPath);
            if (field == null)
            {
                // Switched off while the player is far (found by its path only when on): looked for again later.
                bound = false;
                return;
            }

            int picked = 0;
            Transform all = field.transform;
            for (int c = 0; c < all.childCount; c++)
            {
                Transform child = all.GetChild(c);
                ArrayList list = null;
                PlayMakerArrayListProxy[] proxies = child.GetComponents<PlayMakerArrayListProxy>();
                for (int p = 0; p < proxies.Length && list == null; p++)
                {
                    if (proxies[p] != null && proxies[p].referenceName == PickedList)
                    {
                        list = proxies[p].arrayList;
                    }
                }

                if (list == null)
                {
                    continue;
                }

                var row = new Row { Name = child.name, Picked = list, Known = new int[list.Count] };
                PlayMakerFSM[] machines = child.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int m = 0; m < machines.Length; m++)
                {
                    if (machines[m].FsmName == TriggerMachine && machines[m].gameObject.name == TriggerName)
                    {
                        row.TriggerPicked = machines[m].FsmVariables.FindFsmFloat(TriggerPicked);
                    }
                    else if (machines[m].FsmName == RowMachine && machines[m].transform == child)
                    {
                        row.Index = machines[m].FsmVariables.FindFsmInt(RowIndex);
                    }
                }

                for (int i = 0; i < row.Known.Length; i++)
                {
                    row.Known[i] = Read(row, i);
                    if (row.Known[i] != 0)
                    {
                        picked++;
                        if (host)
                        {
                            // What this game has is the session's from the start.
                            Tell(row, i, 0);
                        }
                    }
                }

                rows.Add(row);
            }

            if (rows.Count == 0)
            {
                log.Warning(Category, "Strawberries: no row with a '" + PickedList + "' list under '" + RowsPath + "' in this world; each game has its own bushes.");
                return;
            }

            if (!host)
            {
                current.RequestAll();
            }

            log.Info(Category, "Strawberries: the bushes of " + rows.Count + " row(s) shared, " + picked + " picked from already; " + (host ? "this game's are the session's." : "the host's are followed here."));
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
            // Its own word coming back is nothing new, unless it is catching up: then it was said before this world was
            // loaded, by this game or by a player who had this number before.
            if (values.Length < 2 || (values[1] == manager.Session.LocalPlayerId && !catchUp))
            {
                return;
            }

            int slash = key.LastIndexOf('/');
            int index;
            if (slash <= KeyPrefix.Length || !int.TryParse(key.Substring(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            {
                return;
            }

            string name = key.Substring(KeyPrefix.Length, slash - KeyPrefix.Length);
            for (int r = 0; r < rows.Count; r++)
            {
                Row row = rows[r];
                if (row.Name != name || index < 0 || index >= row.Known.Length)
                {
                    continue;
                }

                if (Read(row, index) != values[0])
                {
                    float picked = values[0] / (float)Scale;
                    row.Picked[index] = picked;

                    // The trigger hanging on that bush right now holds its own copy, which it writes back after a pick.
                    if (row.TriggerPicked != null && row.Index != null && row.Index.Value == index)
                    {
                        row.TriggerPicked.Value = picked;
                    }

                    if (!catchUp)
                    {
                        log.Info(Category, "Strawberries: bush " + index + " of '" + name + "' picked from in player " + values[1] + "'s game (" + picked.ToString("0.0", CultureInfo.InvariantCulture) + " of 50); here too.");
                    }
                }

                row.Known[index] = values[0];
            }
        }

        private void Watch(Row row)
        {
            for (int i = 0; i < row.Known.Length; i++)
            {
                int picked = Read(row, i);
                if (picked == row.Known[i])
                {
                    continue;
                }

                row.Known[i] = picked;
                Tell(row, i, manager.Session.LocalPlayerId);
                log.Info(Category, "Strawberries: bush " + i + " of '" + row.Name + "' picked from here (" + (picked / (float)Scale).ToString("0.0", CultureInfo.InvariantCulture) + " of 50); told.");
            }
        }

        private void Tell(Row row, int index, int player)
        {
            current.Set(KeyPrefix + row.Name + "/" + index.ToString(CultureInfo.InvariantCulture), new[] { row.Known[index], player });
        }

        // What was picked off that bush, in thousandths. The list holds whatever number the game put there.
        private static int Read(Row row, int index)
        {
            object value = index < row.Picked.Count ? row.Picked[index] : null;
            if (value is float)
            {
                return Mathf.RoundToInt((float)value * Scale);
            }

            if (value is int)
            {
                return (int)value * Scale;
            }

            return 0;
        }

        private sealed class Row
        {
            public string Name;
            public ArrayList Picked;
            public int[] Known;
            public FsmFloat TriggerPicked;
            public FsmInt Index;
        }
    }
}
