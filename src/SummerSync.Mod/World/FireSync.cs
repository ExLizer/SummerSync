using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// One fire for everyone, which anyone can light and feed; and the firewood it burns, chopped alike for everyone.
    ///
    /// What is assumed about the game, from the dump of 2026-10-14 taken with the fireplace burning (docs/GAME_NOTES.md):
    ///
    /// A wood fire is a "WoodTrigger" (state machine "Trigger") and a "SetFire" (state machine "Use") side by side.
    /// The trigger destroys whole each "firewood(Clone)" that falls in, counts up to four in its integer "Woods",
    /// shows that many of "Woods/log1" to "log4" and switches "SetFire" on. "SetFire" is lit with the use key, or by
    /// itself if the embers are hot ("Hiillos"): "Start fire 2", then "Burn", which waits "BurnTime" (150 s), takes
    /// one off ("Remove wood"), shows what is left ("Check wood", then "State 2" to "State 5") and burns again; with
    /// none left "State 6", "Off" and "Disable". Five fires are made like that: the house's fireplace, the cottage's
    /// fireplace, grill and sauna stove, and the cabin's stove.
    ///
    /// The host's fires are the fires. On a client the trigger is switched off (the wood that falls in there is
    /// the host's to burn) and "BurnTime" made endless, so that its game never takes wood off by itself; what the
    /// host says is written in: how many logs, lit or not, through the game's own states. A client lighting its fire
    /// is told to the host, which lights its own.
    ///
    /// A log is made on a chopping block by the state machine "Use" of a "Logwall" ("Create log", into its variable
    /// "Log"): two halves held by a "FixedJoint" until the axe breaks it; then the state machine "Check joint" of the
    /// inner half names both "firewood(Clone)", tags them "PART" and lets them go. Both are told: the others make the
    /// same log on their block and split it the same way, without the stress and the statistic that go to whoever
    /// chopped.
    ///
    /// Sausages are cooked by the host only: a client's "SausageTrigger" machines are switched off, and what the
    /// host's make is made everywhere by <see cref="Players.ThrowSync"/>.
    /// </summary>
    internal sealed class FireSync
    {
        private const string FireKey = "fire/";
        private const string LogKey = "log/";
        private const string StartEvent = "SUMMERSYNC FIRE START";
        private const string CheckEvent = "SUMMERSYNC FIRE CHECK";
        private const string StartState = "Start fire 2";
        private const string CheckState = "Check wood";
        private const float EndlessBurn = 1e9f;
        private const double SearchInterval = 2.0;
        private const double AskAgain = 3.0;

        private static readonly string[] FirePaths =
        {
            "YARD/Building/LIVINGROOM/Fireplace", "COTTAGE/Fireplace", "COTTAGE/Grill/Fireplace", "COTTAGE/Sauna/Stove", "CABIN/Cabin/woodstove/Fireplace",
        };

        private static readonly string[] BurningStates = { "Start fire 2", "Burn", "Remove wood", "Check wood", "State 2", "State 3", "State 4", "State 5" };

        private static readonly string[] LogwallPaths = { "YARD/MachineHall/Logging", "COTTAGE/Logging", "CABIN/LOD/Logging" };

        private static readonly string[] SausagePaths =
        {
            "YARD/Building/LIVINGROOM/Fireplace/SausageTrigger", "COTTAGE/Fireplace/SausageTrigger", "COTTAGE/Grill/Fireplace/SausageTrigger",
            "CABIN/Cabin/woodstove/Fireplace/SausageTrigger", "YARD/Building/KITCHEN/OvenStove/SausageTrigger", "ITEMS/grill(itemx)/Fireplace/SausageTrigger",
        };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Fire> fires = new List<Fire>();
        private readonly List<Logwall> walls = new List<Logwall>();
        private readonly List<PlayMakerFSM> cooks = new List<PlayMakerFSM>();
        private readonly List<Heard> heard = new List<Heard>();
        private TallyReplication current;
        private bool host;
        private double nextSearch;
        private bool found;

        public FireSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                    current.ChangeWanted -= OnChangeWanted;
                }

                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                    current.ChangeWanted += OnChangeWanted;
                }

                fires.Clear();
                walls.Clear();
                cooks.Clear();
                heard.Clear();
                found = false;
                nextSearch = now;
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

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i]);
            }

            heard.Clear();
            for (int i = 0; i < fires.Count; i++)
            {
                Watch(fires[i], now);
            }

            for (int i = 0; i < walls.Count; i++)
            {
                Watch(walls[i]);
            }
        }

        /// <summary>Every fire, trigger and cook here goes back to how the game had it.</summary>
        public void Release()
        {
            for (int i = 0; i < fires.Count; i++)
            {
                Fire fire = fires[i];

                // Unity's null check is also true for a destroyed object.
                if (fire.Trigger != null && fire.TriggerWasOn)
                {
                    fire.Trigger.enabled = true;
                }

                if (fire.BurnTime != null && fire.BurnTime.Value >= EndlessBurn)
                {
                    fire.BurnTime.Value = fire.UsualBurnTime;
                    if (Burning(fire) && fire.Hooked)
                    {
                        // The burning log waits endlessly: it starts again, with the game's time.
                        fire.Fsm.SendEvent(CheckEvent);
                    }
                }
            }

            for (int i = 0; i < cooks.Count; i++)
            {
                if (cooks[i] != null)
                {
                    cooks[i].enabled = true;
                }
            }
        }

        private void Search()
        {
            int fireCount = 0;
            for (int i = 0; i < FirePaths.Length; i++)
            {
                Transform place = FindPath(FirePaths[i]);
                Transform trigger = place == null ? null : place.Find("WoodTrigger");
                Transform setFire = place == null ? null : place.Find("SetFire");
                Transform woods = place == null ? null : place.Find("Woods");
                PlayMakerFSM triggerFsm = trigger == null ? null : DoorSync.FindMachine(trigger, "Trigger");
                PlayMakerFSM fireFsm = setFire == null ? null : DoorSync.FindMachine(setFire, "Use");
                if (triggerFsm == null || fireFsm == null || woods == null)
                {
                    continue;
                }

                var fire = new Fire
                {
                    Key = FireKey + FirePaths[i],
                    Name = FirePaths[i],
                    Trigger = triggerFsm,
                    Fsm = fireFsm,
                    Logs = woods,
                    TriggerWoods = triggerFsm.FsmVariables.FindFsmInt("Woods"),
                    FireWoods = fireFsm.FsmVariables.FindFsmInt("Woods"),
                    BurnTime = fireFsm.FsmVariables.FindFsmFloat("BurnTime"),
                    TriggerWasOn = triggerFsm.enabled,
                };
                if (fire.TriggerWoods == null || fire.BurnTime == null)
                {
                    continue;
                }

                fire.UsualBurnTime = fire.BurnTime.Value < EndlessBurn ? fire.BurnTime.Value : 150f;
                fire.KnownWoods = fire.TriggerWoods.Value;
                fire.KnownBurning = Burning(fire);
                if (!host)
                {
                    // The host's fire is the fire: this game neither takes wood in nor burns it out by itself.
                    triggerFsm.enabled = false;
                    fire.BurnTime.Value = EndlessBurn;
                }

                fires.Add(fire);
                fireCount++;
            }

            for (int i = 0; i < LogwallPaths.Length; i++)
            {
                Transform place = FindPath(LogwallPaths[i]);
                Transform wall = place == null ? null : place.Find("Logwall");
                Transform block = place == null ? null : place.Find("Pölkky/Spawn");
                PlayMakerFSM use = wall == null ? null : DoorSync.FindMachine(wall, "Use");
                FsmGameObject made = use == null ? null : use.FsmVariables.FindFsmGameObject("Log");
                GameObject prefab = use == null ? null : Prefab(use);
                if (made == null || block == null || prefab == null)
                {
                    continue;
                }

                walls.Add(new Logwall { Key = LogKey + LogwallPaths[i], Made = made, Block = block, Prefab = prefab, Last = made.Value });
            }

            if (!host)
            {
                for (int i = 0; i < SausagePaths.Length; i++)
                {
                    Transform at = FindPath(SausagePaths[i]);
                    PlayMakerFSM cook = at == null ? null : DoorSync.FindMachine(at, "Logic");
                    if (cook != null && cook.enabled)
                    {
                        cook.enabled = false;
                        cooks.Add(cook);
                    }
                }
            }

            found = fireCount > 0 || walls.Count > 0;
            log.Info(
                LogCategory.Fsm,
                "Fires: " + fireCount + " of " + FirePaths.Length + " wood fires and " + walls.Count + " of " + LogwallPaths.Length + " woodpiles are shared"
                + (host ? "; this game burns them for everyone." : "; the host burns them, and " + cooks.Count + " sausage cook(s) here are left to the host."));
            if (host && fires.Count > 0)
            {
                TellAll();
            }
        }

        // The host: its fires as they are now are the session's from the start (lit or fed since the save the guests load,
        // or before hosting); only what changes from here on would be told otherwise.
        private void TellAll()
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < fires.Count; i++)
            {
                Fire fire = fires[i];
                int[] now = { fire.KnownWoods, fire.KnownBurning ? 1 : 0 };
                current.Tell(fire.Key, now, now);
                if (fire.KnownWoods > 0 || fire.KnownBurning)
                {
                    text.Append(text.Length == 0 ? string.Empty : ", ").Append('\'').Append(fire.Name).Append("' ").Append(fire.KnownWoods).Append(fire.KnownBurning ? " burning" : string.Empty);
                }
            }

            log.Info(LogCategory.Fsm, "Fires: told as they are now, for whoever joins: " + (text.Length == 0 ? "no wood in any" : text.ToString()) + ".");
        }

        // A path from a root object, found even where the game has switched something on the way off.
        private static Transform FindPath(string path)
        {
            int slash = path.IndexOf('/');
            GameObject root = GameObject.Find("/" + (slash < 0 ? path : path.Substring(0, slash)));
            if (root == null)
            {
                return null;
            }

            return slash < 0 ? root.transform : root.transform.Find(path.Substring(slash + 1));
        }

        private static GameObject Prefab(PlayMakerFSM use)
        {
            try
            {
                return PrefabOf(use);
            }
            catch (Exception)
            {
                // A machine that has never run (far away, switched off) may not give its actions.
                return null;
            }
        }

        private static GameObject PrefabOf(PlayMakerFSM use)
        {
            FsmState[] states = use.Fsm == null ? null : use.Fsm.States;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    var create = actions[a] as CreateObject;
                    if (create != null && create.gameObject != null && create.gameObject.Value != null)
                    {
                        return create.gameObject.Value;
                    }
                }
            }

            return null;
        }

        private static bool Burning(Fire fire)
        {
            return fire.Fsm != null && fire.Fsm.gameObject.activeInHierarchy && Array.IndexOf(BurningStates, fire.Fsm.ActiveStateName) >= 0;
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(FireKey, StringComparison.Ordinal) || key.StartsWith(LogKey, StringComparison.Ordinal))
            {
                heard.Add(new Heard { Key = key, Values = values, CatchUp = current.CatchingUp });
            }
        }

        private void OnChangeWanted(string key, int[] change, byte player)
        {
            if (key.StartsWith(FireKey, StringComparison.Ordinal) || key.StartsWith(LogKey, StringComparison.Ordinal))
            {
                heard.Add(new Heard { Key = key, Values = change, IsChange = true, Player = player });
            }
        }

        private void Hear(Heard news)
        {
            if (news.Key.StartsWith(FireKey, StringComparison.Ordinal))
            {
                for (int i = 0; i < fires.Count; i++)
                {
                    if (fires[i].Key == news.Key && news.Values.Length >= 2)
                    {
                        if (news.IsChange)
                        {
                            HostHears(fires[i], news);
                        }
                        else
                        {
                            Show(fires[i], news.Values[0], news.Values[1] != 0, news.CatchUp);
                        }
                    }
                }

                return;
            }

            for (int i = 0; i < walls.Count; i++)
            {
                if (walls[i].Key == news.Key && news.Values.Length >= 5)
                {
                    Logwall wall = walls[i];
                    if (news.CatchUp)
                    {
                        Baseline(wall, news.Values);
                        continue;
                    }

                    int[] before = { wall.KnownMade, wall.KnownSplit, wall.KnownX, wall.KnownY, wall.KnownZ };
                    int[] after = new int[5];
                    for (int v = 0; v < 5; v++)
                    {
                        after[v] = news.IsChange ? before[v] + news.Values[v] : news.Values[v];
                    }

                    Copy(wall, after);
                    if (news.IsChange)
                    {
                        current.Tell(wall.Key, before, after);
                    }
                }
            }
        }

        // Host: a client lit its fire. This one is lit too, if there is wood in it.
        private void HostHears(Fire fire, Heard news)
        {
            if (news.Values[1] > 0 && !Burning(fire) && fire.TriggerWoods.Value > 0)
            {
                Hook(fire);
                if (!fire.Fsm.gameObject.activeSelf)
                {
                    fire.Fsm.gameObject.SetActive(true);
                }

                fire.Fsm.SendEvent(StartEvent);
                log.Info(LogCategory.Fsm, "Fires: player " + news.Player + " lit the fire of '" + fire.Name + "'; lit here too.");
            }
        }

        // A woodpile's counts in the host's answer to this game's request: how many logs were made and split there this
        // session, long ago maybe. They count from here on; nothing is made or split here for them (a joiner got one log
        // split out of nothing, from counts that start at 0 in every game). A log lying unsplit on the host's block is
        // not made here either: whether it is still there cannot be told from the counts.
        private void Baseline(Logwall wall, int[] values)
        {
            bool changed = values[0] != wall.KnownMade || values[1] != wall.KnownSplit;
            wall.KnownMade = values[0];
            wall.KnownSplit = values[1];
            wall.KnownX = values[2];
            wall.KnownY = values[3];
            wall.KnownZ = values[4];
            wall.Last = wall.Made.Value;
            if (changed)
            {
                log.Info(LogCategory.Fsm, "Fires: '" + wall.Key + "' has had " + values[0] + " log(s) put on the block and " + values[1] + " split in the host's game; taken as counts, nothing made or split here (a catch-up on joining).");
            }
        }

        // Client: the host says how much wood there is and whether it burns. So it is here, the game's way: a fire that burns
        // over there is a state, and burns here too, a catch-up or not.
        private void Show(Fire fire, int woods, bool burning, bool catchUp)
        {
            woods = Math.Max(0, Math.Min(4, woods));
            bool changedWoods = woods != fire.TriggerWoods.Value;
            fire.TriggerWoods.Value = woods;
            if (fire.FireWoods != null)
            {
                fire.FireWoods.Value = woods;
            }

            for (int n = 1; n <= 4; n++)
            {
                Transform piece = fire.Logs.Find("log" + n.ToString(CultureInfo.InvariantCulture));
                if (piece != null && piece.gameObject.activeSelf != (n <= woods))
                {
                    piece.gameObject.SetActive(n <= woods);
                }
            }

            GameObject setFire = fire.Fsm.gameObject;
            if ((woods > 0 || burning) && !setFire.activeSelf)
            {
                setFire.SetActive(true);
            }

            bool here = Burning(fire);
            if (setFire.activeInHierarchy)
            {
                Hook(fire);
                fire.BurnTime.Value = EndlessBurn;
                if (burning && !here)
                {
                    fire.Fsm.SendEvent(StartEvent);
                }
                else if (here && (changedWoods || !burning))
                {
                    fire.Fsm.SendEvent(CheckEvent);
                }
            }

            fire.KnownWoods = woods;
            fire.KnownBurning = burning;
            fire.AskedAt = double.NegativeInfinity;
            log.Info(LogCategory.Fsm, "Fires: '" + fire.Name + "' has " + woods + " log(s), " + (burning ? "burning" : "not burning") + (catchUp ? ", as the host has it now (a catch-up on joining)." : ", as the host says."));
        }

        private void Hook(Fire fire)
        {
            if (fire.Hooked || !fire.Fsm.gameObject.activeInHierarchy)
            {
                return;
            }

            try
            {
                if (!DoorSync.HasGlobalTransition(fire.Fsm, StartEvent))
                {
                    fire.Fsm.AddGlobalTransition(StartEvent, StartState);
                }

                if (!DoorSync.HasGlobalTransition(fire.Fsm, CheckEvent))
                {
                    fire.Fsm.AddGlobalTransition(CheckEvent, CheckState);
                }

                fire.Hooked = true;

                // The machine's data is built again when a transition is added: the variables may be new objects.
                fire.FireWoods = fire.Fsm.FsmVariables.FindFsmInt("Woods");
                fire.BurnTime = fire.Fsm.FsmVariables.FindFsmFloat("BurnTime");
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Fsm, "Fires: could not prepare '" + fire.Name + "': " + exception.Message);
            }
        }

        private void Watch(Fire fire, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (fire.Fsm == null || fire.Trigger == null)
            {
                return;
            }

            int woods = fire.TriggerWoods.Value;
            bool burning = Burning(fire);
            if (host)
            {
                if (woods != fire.KnownWoods || burning != fire.KnownBurning)
                {
                    current.Tell(fire.Key, new[] { fire.KnownWoods, fire.KnownBurning ? 1 : 0 }, new[] { woods, burning ? 1 : 0 });
                    fire.KnownWoods = woods;
                    fire.KnownBurning = burning;
                }

                return;
            }

            if (fire.BurnTime.Value < EndlessBurn)
            {
                fire.BurnTime.Value = EndlessBurn;
            }

            // The local player lit it: the host is asked to light its own.
            if (burning && !fire.KnownBurning && now - fire.AskedAt >= AskAgain)
            {
                fire.AskedAt = now;
                current.Tell(fire.Key, new[] { fire.KnownWoods, 0 }, new[] { fire.KnownWoods, 1 });
                log.Info(LogCategory.Fsm, "Fires: the local player lit '" + fire.Name + "'; the host is asked to light it.");
            }
        }

        // A log made here, and then split: told.
        private void Watch(Logwall wall)
        {
            GameObject made = wall.Made.Value;
            if (made != null && made != wall.Last)
            {
                wall.Last = made;
                wall.Log = made;
                wall.Joint = made.GetComponentInChildren<FixedJoint>();
                Vector3 euler = made.transform.localEulerAngles;
                Tell(wall, wall.KnownMade + 1, wall.KnownSplit, Centi(euler.x), Centi(euler.y), Centi(euler.z));
                log.Info(LogCategory.Fsm, "Fires: a log was put on the chopping block here; the others are told.");
            }

            // The axe broke it: the halves let go of each other.
            if (wall.Log != null && wall.Joint == null && wall.SplitTold < wall.KnownMade && !wall.CopyMade)
            {
                Tell(wall, wall.KnownMade, wall.KnownSplit + 1, wall.KnownX, wall.KnownY, wall.KnownZ);
                wall.SplitTold = wall.KnownMade;
                log.Info(LogCategory.Fsm, "Fires: the log was split here; the others are told.");
            }
        }

        private void Tell(Logwall wall, int made, int split, int x, int y, int z)
        {
            int[] before = { wall.KnownMade, wall.KnownSplit, wall.KnownX, wall.KnownY, wall.KnownZ };
            int[] after = { made, split, x, y, z };
            wall.KnownMade = made;
            wall.KnownSplit = split;
            wall.KnownX = x;
            wall.KnownY = y;
            wall.KnownZ = z;
            current.Tell(wall.Key, before, after);
        }

        // Another game made a log, or split it: so here, without the chopper's stress and statistic.
        private void Copy(Logwall wall, int[] after)
        {
            if (after[0] > wall.KnownMade)
            {
                var made = (GameObject)UnityEngine.Object.Instantiate(wall.Prefab);
                made.transform.parent = wall.Block;
                made.transform.localPosition = Vector3.zero;
                made.transform.localRotation = Quaternion.Euler(after[2] / 100f, after[3] / 100f, after[4] / 100f);
                wall.Log = made;
                wall.Joint = made.GetComponentInChildren<FixedJoint>();
                wall.CopyMade = true;
                wall.SplitTold = after[0];
                log.Info(LogCategory.Fsm, "Fires: a log was put on the chopping block in another game; put here too.");
            }

            if (after[1] > wall.KnownSplit && wall.Log != null && wall.CopyMade)
            {
                Split(wall.Log);
                wall.Log = null;
                log.Info(LogCategory.Fsm, "Fires: the log was split in another game; split here too.");
            }

            if (after[1] > wall.KnownSplit)
            {
                wall.CopyMade = false;
            }

            wall.KnownMade = after[0];
            wall.KnownSplit = after[1];
            wall.KnownX = after[2];
            wall.KnownY = after[3];
            wall.KnownZ = after[4];
            wall.Last = wall.Made.Value;
        }

        // What "Check joint" does when the axe breaks the log, but for nobody's stress.
        internal static void Split(GameObject log)
        {
            PlayMakerFSM[] machines = log.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == "Check joint")
                {
                    UnityEngine.Object.Destroy(machines[i]);
                }
            }

            FixedJoint[] joints = log.GetComponentsInChildren<FixedJoint>(true);
            for (int i = 0; i < joints.Length; i++)
            {
                UnityEngine.Object.Destroy(joints[i]);
            }

            var halves = new List<Transform> { log.transform };
            for (int i = 0; i < log.transform.childCount; i++)
            {
                if (log.transform.GetChild(i).GetComponent<Rigidbody>() != null)
                {
                    halves.Add(log.transform.GetChild(i));
                }
            }

            for (int i = halves.Count - 1; i >= 0; i--)
            {
                halves[i].name = "firewood(Clone)";
                halves[i].gameObject.tag = "PART";
                halves[i].parent = null;
            }
        }

        private static int Centi(float degrees)
        {
            return Mathf.RoundToInt(degrees * 100f);
        }

        private sealed class Fire
        {
            public string Key;
            public string Name;
            public PlayMakerFSM Trigger;
            public PlayMakerFSM Fsm;
            public Transform Logs;
            public FsmInt TriggerWoods;
            public FsmInt FireWoods;
            public FsmFloat BurnTime;
            public float UsualBurnTime;
            public bool TriggerWasOn;
            public bool Hooked;
            public int KnownWoods;
            public bool KnownBurning;
            public double AskedAt = double.NegativeInfinity;
        }

        private sealed class Logwall
        {
            public string Key;
            public FsmGameObject Made;
            public Transform Block;
            public GameObject Prefab;
            public GameObject Last;
            public GameObject Log;
            public FixedJoint Joint;
            public bool CopyMade;
            public int SplitTold;
            public int KnownMade;
            public int KnownSplit;
            public int KnownX;
            public int KnownY;
            public int KnownZ;
        }

        private struct Heard
        {
            public string Key;
            public int[] Values;
            public bool IsChange;
            public byte Player;

            /// <summary>Part of the host's answer to this game's request: how things are, not what just happened.</summary>
            public bool CatchUp;
        }
    }
}
