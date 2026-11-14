using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using SummerSync.Core.World;
using SummerSync.Unity.Objects;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The jobs (they work as they should whoever does them). Which ones and what of each
    /// is shared comes from the data files <c>data/jobs/*.json</c> (<see cref="JobDefinition"/>); docs/GAME_NOTES.md says
    /// what each job is in the game.
    ///
    /// A job's world (a well that fills and calls, a house that orders wood) is the host's: those state machines run in
    /// the host's game and are paused in the others. What a player does in a job is done in their own game, as always,
    /// and reaches the others through the job's values: a quantity they change (the well they pump) travels as a
    /// difference that the host's game adds, and the host's game says what it ends up with; a flag (the man has money for
    /// whoever did the job) is whatever the game that changed it last says. What the paused machines would move (the
    /// well's level) is put where the host has it.
    ///
    /// A job that moves by events (an order that a call starts and a delivery ends) runs in every game, and the states those
    /// events take it to travel both ways (the relays). A relayed state can carry the loose things it takes (the berry box
    /// delivered: each game takes its own copy), can be one that pays (the others go there with the pay at 0: the money is
    /// one for everyone), and can be a paused machine's (the host's world, shown in the others by taking their paused copy
    /// to the same states). A machine one player deals with at a time (the kilju buyer) is run by the game of whoever deals:
    /// the others' copies are paused and go where it goes until the deal is over (<see cref="JobDeal"/>).
    ///
    /// The same files describe the progress that is everyone's: the keys, global variables
    /// of the game that whoever's game changed them last says; the uncle's story, a machine of the host's world whose states
    /// move the van only in the game that simulates it and never with a player in it or by it (a relay's "spare"); and what
    /// is won or lost at cards.
    /// </summary>
    internal sealed class JobSync
    {
        private const string DataPrefix = "SummerSync.jobs.";
        private const double SearchInterval = 2.0;
        private const double TellInterval = 0.25;
        private const float PositionStep = 0.01f;
        private const string RelayEvent = "SSYNC JOB";

        // A voice for one player: its subtitle is shown in another game only if that game's player is this near.
        private const float QuietNear = 10f;

        // A deal another game says nothing of for this long is over here: that game's machine may have been switched off with
        // its player gone from there (the kilju buyer is switched off far from the player). The longest wait between two states
        // of a deal read in the game is 15 s.
        private const double DealSilentAtMost = 60.0;

        // A vehicle a job's machine would move is left where it is with a player this near (the uncle's own check: 10 m from
        // the player to the van, "Respawn away 2").
        private const float SpareNear = 10f;

        // A job's pieces not found are looked for again after this long, then twice as long each time, up to the most.
        private const double RetryFirst = 10.0;
        private const double RetryMost = 300.0;

        // The most words kept of pieces not found here.
        private const int PendingMost = 2000;

        // The string variable in which the game keeps a part's own name before it renames it (dump of the level, 2026-10-18).
        private const string OwnNameVariable = "ID";

        private static Dictionary<string, List<Transform>> named;
        private static int namedFrame = -1;

        private static JobDefinition[] catalog;
        private static string[] loose;

        // The hooks put in the game's states stay there for the run of the game; each calls whichever sync is current. By
        // machine, the game's state machine they went into: one made from a template (the pay in a hand) is made anew when its
        // object is first switched on, without them (PlayMakerFSM.Init, ILSpy), and they go in again.
        private static readonly Dictionary<int, Fsm> Hooked = new Dictionary<int, Fsm>();
        private static JobSync active;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly ObjectPresenter objects;
        private readonly List<Job> jobs = new List<Job>();
        private readonly Dictionary<string, Value> byKey = new Dictionary<string, Value>();
        private readonly Dictionary<string, Place> placesByKey = new Dictionary<string, Place>();
        private readonly Dictionary<string, Relay> relaysByKey = new Dictionary<string, Relay>();
        private readonly Dictionary<int, Relay> relaysByMachine = new Dictionary<int, Relay>();
        private readonly Dictionary<string, Switch> switchesByKey = new Dictionary<string, Switch>();
        private readonly List<Relay> relays = new List<Relay>();
        private readonly List<Heard> heard = new List<Heard>();
        private readonly List<KeyValuePair<string, int[]>> changes = new List<KeyValuePair<string, int[]>>();
        private readonly HashSet<int> guardedReported = new HashSet<int>();
        private readonly List<KeyValuePair<FsmGameObject, GameObject>> spared = new List<KeyValuePair<FsmGameObject, GameObject>>();
        private readonly List<HeldAction> heldActions = new List<HeldAction>();
        private readonly Dictionary<string, int[]> pending = new Dictionary<string, int[]>();
        private bool retrying;
        private readonly VehiclePresenter vehicles;
        private TallyReplication current;
        private Transform player;
        private bool host;
        private double nextSearch;
        private double clock;

        public JobSync(Logger log, NetworkManager manager, ObjectPresenter objects, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.objects = objects;
            this.vehicles = vehicles;
            Load(log);
        }

        /// <summary>
        /// The containers the job files name ("loose"), by path from the scene's top: their children are the jobs' things that
        /// players carry, shared as loose objects (<see cref="ObjectPresenter"/>).
        /// </summary>
        public static string[] LooseContainers
        {
            get
            {
                Load(ModRuntime.Log);
                return loose;
            }
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's root object,
        /// or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            clock = now;
            player = localPlayer;
            PutBackSpared();
            LetHeldActionsGo();
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

                jobs.Clear();
                pending.Clear();
                byKey.Clear();
                placesByKey.Clear();
                relaysByKey.Clear();
                relaysByMachine.Clear();
                switchesByKey.Clear();
                relays.Clear();
                guardedReported.Clear();
                active = current == null ? null : this;
                heard.Clear();
                changes.Clear();
                nextSearch = now;
            }

            if (current == null || catalog.Length == 0)
            {
                return;
            }

            if (now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            if (manager.Session.State != SessionState.Connected)
            {
                return;
            }

            for (int i = 0; i < changes.Count; i++)
            {
                ApplyChange(changes[i].Key, changes[i].Value);
            }

            changes.Clear();
            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Values, heard[i].CatchUp);
            }

            heard.Clear();
            ForgetLeftDealers();
            FrameMeter.Mark("jobs: before the hooks");
            for (int r = 0; r < relays.Count; r++)
            {
                InjectHooks(relays[r]);
            }

            FrameMeter.Mark("jobs: InjectHooks");
            for (int j = 0; j < jobs.Count; j++)
            {
                Job job = jobs[j];
                for (int v = 0; v < job.Values.Count; v++)
                {
                    Watch(job.Values[v], now);
                }

                FrameMeter.Mark("jobs: Watch values");
                for (int s = 0; s < job.Switches.Count; s++)
                {
                    WatchSwitch(job.Switches[s]);
                }

                FrameMeter.Mark("jobs: WatchSwitch");

                if (host)
                {
                    for (int p = 0; p < job.Places.Count; p++)
                    {
                        WatchPlace(job.Places[p], now);
                    }

                    for (int h = 0; h < job.Holds.Count; h++)
                    {
                        WatchHold(job, job.Holds[h]);
                    }
                }
            }
        }

        /// <summary>Every paused machine of the jobs goes on from where it is.</summary>
        public void Release()
        {
            PutBackSpared();
            for (int h = 0; h < heldActions.Count; h++)
            {
                heldActions[h].Action.Enabled = true;
            }

            heldActions.Clear();
            for (int j = 0; j < jobs.Count; j++)
            {
                for (int p = 0; p < jobs[j].PausedMachines.Count; p++)
                {
                    jobs[j].PausedMachines[p].Resume();
                }

                jobs[j].PausedMachines.Clear();
                for (int h = 0; h < jobs[j].Holds.Count; h++)
                {
                    if (jobs[j].Holds[h].Paused != null)
                    {
                        jobs[j].Holds[h].Paused.Resume();
                        jobs[j].Holds[h].Paused = null;
                    }
                }
            }

            for (int r = 0; r < relays.Count; r++)
            {
                if (relays[r].Held != null)
                {
                    relays[r].Held.Resume();
                    relays[r].Held = null;
                }

                if (relays[r].Deal != null)
                {
                    relays[r].Deal.Reset();
                }
            }
        }

        private static void Load(Logger log)
        {
            if (catalog != null)
            {
                return;
            }

            catalog = LoadCatalog(log);
            var containers = new List<string>();
            for (int i = 0; i < catalog.Length; i++)
            {
                containers.AddRange(catalog[i].Loose);
            }

            loose = containers.ToArray();
        }

        private static JobDefinition[] LoadCatalog(Logger log)
        {
            var found = new List<JobDefinition>();
            var names = new List<string>();
            Assembly assembly = typeof(JobSync).Assembly;
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
                        JobDefinition definition = JobDefinition.Parse(reader.ReadToEnd());
                        found.Add(definition);
                        names.Add(definition.Name);
                    }
                }
                catch (FormatException exception)
                {
                    log.Error(LogCategory.Fsm, "The job file '" + resources[i] + "' cannot be read; that job is each game's own. " + exception.Message);
                }
            }

            names.Sort(string.CompareOrdinal);
            log.Info(LogCategory.Fsm, "Jobs this build can share: " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())) + ".");
            return found.ToArray();
        }

        private void Search()
        {
            for (int d = 0; d < catalog.Length; d++)
            {
                JobDefinition definition = catalog[d];
                Job known = null;
                for (int j = 0; j < jobs.Count; j++)
                {
                    if (jobs[j].Definition == definition)
                    {
                        known = jobs[j];
                    }
                }

                if (known != null)
                {
                    LookAgain(known);
                    continue;
                }

                GameObject root = GameObject.Find("/" + definition.Root);
                Transform at = root == null ? null : root.transform.Find(definition.Path);
                for (int m = 0; at == null && root != null && m < definition.Moved.Length; m++)
                {
                    at = root.transform.Find(definition.Moved[m]);
                }

                if (at == null)
                {
                    continue;
                }

                var job = new Job(definition) { At = at };
                bool whole;
                job.Missing = BindPieces(job, at, out whole);
                job.RetryDelay = RetryFirst;
                job.NextRetry = clock + RetryFirst;
                jobs.Add(job);
                log.Info(LogCategory.Fsm, "Jobs: '" + definition.Name + "' shared" + (whole ? string.Empty : " in part (see above)") + ": "
                    + job.Values.Count + " value(s), " + job.Places.Count + " moved object(s), " + job.Relays.Count + " relayed machine(s), " + job.Switches.Count
                    + " switched object(s); " + (host ? "this game runs its world." : "the host's game runs its world.")
                    + (job.Missing > 0 ? " The " + job.Missing + " piece(s) not found are looked for again now and then." : string.Empty));
                TakePending();
            }
        }

        // A job found with pieces that were not there (a part that was elsewhere, a thing not made yet): looked for again.
        private void LookAgain(Job job)
        {
            // Unity's null check is also true for a destroyed object.
            if (job.Missing == 0 || job.At == null || clock < job.NextRetry)
            {
                return;
            }

            job.RetryDelay = Math.Min(RetryMost, job.RetryDelay * 2.0);
            job.NextRetry = clock + job.RetryDelay;
            int before = job.Missing;
            bool whole;
            retrying = true;
            try
            {
                job.Missing = BindPieces(job, job.At, out whole);
            }
            finally
            {
                retrying = false;
            }

            if (job.Missing < before)
            {
                log.Info(LogCategory.Fsm, "Jobs: '" + job.Definition.Name + "': " + (before - job.Missing) + " more piece(s) found and shared now; " + job.Missing + " still not found.");
                TakePending();
            }
        }

        // The pieces of a job not shared yet, found and shared now: how many are still not found.
        private int BindPieces(Job job, Transform at, out bool whole)
        {
            JobDefinition definition = job.Definition;
            int missing = 0;
            whole = true;
            for (int r = 0; r < definition.Relays.Length; r++)
            {
                string key = definition.RelayKey(r);
                if (!relaysByKey.ContainsKey(key))
                {
                    whole &= Hook(job, at, key, definition.Relays[r]);
                    missing += relaysByKey.ContainsKey(key) ? 0 : 1;
                }
            }

            for (int a = 0; a < definition.Actives.Length; a++)
            {
                JobActive spec = definition.Actives[a];
                if (switchesByKey.ContainsKey(definition.ActiveKey(a)))
                {
                    continue;
                }

                Transform switched = Below(at, spec.Path);
                PlayMakerFSM machine = switched == null || spec.Machine == null ? null : DoorSync.FindMachine(switched, spec.Machine);
                if (switched == null || (spec.Machine != null && machine == null))
                {
                    whole = false;
                    missing++;
                    Warn("Jobs: '" + definition.Name + "' has no '" + spec.Path + (spec.Machine == null ? string.Empty : " :: " + spec.Machine) + "' in this game; whether it is on is each game's own.");
                    continue;
                }

                var item = new Switch(definition.ActiveKey(a), switched.gameObject, machine, spec.Said);
                job.Switches.Add(item);
                switchesByKey[item.Key] = item;
            }

            for (int v = 0; v < definition.Values.Length; v++)
            {
                JobValue spec = definition.Values[v];
                if (byKey.ContainsKey(definition.ValueKey(v)))
                {
                    continue;
                }

                if (spec.Global)
                {
                    if (Find(FsmVariables.GlobalVariables, spec.Variable) == null)
                    {
                        whole = false;
                        missing++;
                        Warn("Jobs: '" + definition.Name + "' has no global '" + spec.Variable + "' (a float, a bool or an int) in this game; that value is each game's own.");
                        continue;
                    }

                    var global = new Value(definition.ValueKey(v), spec, null);
                    job.Values.Add(global);
                    byKey[global.Key] = global;
                    continue;
                }

                Transform owner = Below(at, spec.Path);
                PlayMakerFSM fsm = owner == null ? null : DoorSync.FindMachine(owner, spec.Machine);
                NamedVariable variable = fsm == null ? null : Find(fsm.FsmVariables, spec.Variable);
                if (variable != null && spec.Kind == JobValueKind.Amount && (variable is FsmColor || variable is FsmVector3))
                {
                    variable = null;
                }

                if (variable == null)
                {
                    whole = false;
                    missing++;
                    Warn("Jobs: '" + definition.Name + "' has no '" + spec.Path + " :: " + spec.Machine + "." + spec.Variable + "' (a float, a bool or an int) in this game; that value is each game's own.");
                    continue;
                }

                var value = new Value(definition.ValueKey(v), spec, fsm);
                job.Values.Add(value);
                byKey[value.Key] = value;
            }

            for (int p = 0; p < definition.Positions.Length; p++)
            {
                if (placesByKey.ContainsKey(definition.PositionKey(p)))
                {
                    continue;
                }

                Transform moved = at.Find(definition.Positions[p].Path);
                if (moved == null)
                {
                    whole = false;
                    missing++;
                    continue;
                }

                var place = new Place(definition.PositionKey(p), moved, definition.Positions[p].Once);
                job.Places.Add(place);
                placesByKey[place.Key] = place;
            }

            for (int h = 0; h < definition.Holds.Length; h++)
            {
                JobHold spec = definition.Holds[h];
                if (job.HoldsFound.Contains(h))
                {
                    continue;
                }

                Transform owner = Below(at, spec.Path);
                PlayMakerFSM fsm = owner == null ? null : DoorSync.FindMachine(owner, spec.Machine);
                if (fsm == null)
                {
                    whole = false;
                    missing++;
                    Warn("Jobs: '" + definition.Name + "' has no '" + spec.Path + " :: " + spec.Machine + "' in this game; it is not held for the others.");
                    continue;
                }

                job.HoldsFound.Add(h);
                job.Holds.Add(new HeldMachine { Spec = spec, Machine = fsm });
            }

            if (!host)
            {
                for (int m = 0; m < definition.Paused.Length; m++)
                {
                    NpcMachine spec = definition.Paused[m];
                    if (job.PausedFound.Contains(m))
                    {
                        continue;
                    }

                    Transform owner = Below(at, spec.Path);
                    PlayMakerFSM fsm = owner == null ? null : DoorSync.FindMachine(owner, spec.Machine);
                    if (fsm == null)
                    {
                        whole = false;
                        missing++;
                        continue;
                    }

                    job.PausedFound.Add(m);
                    if (fsm.enabled)
                    {
                        job.PausedMachines.Add(new PausedMachine(fsm));
                    }
                }
            }

            return missing;
        }

        // A relayed machine found and its states hooked: the mod's action first in each, once per machine and state.
        private bool Hook(Job job, Transform at, string key, JobRelay spec)
        {
            Transform owner = Below(at, spec.Path);
            PlayMakerFSM fsm = owner == null ? null : DoorSync.FindMachine(owner, spec.Machine);
            if (fsm == null)
            {
                Warn("Jobs: '" + job.Definition.Name + "' has no '" + spec.Path + " :: " + spec.Machine + "' in this game; its states are each game's own.");
                return false;
            }

            bool whole = true;
            var relay = new Relay(key, spec, fsm, job);
            for (int o = 0; o < spec.Objects.Length; o++)
            {
                if (fsm.FsmVariables.FindFsmGameObject(spec.Objects[o]) == null)
                {
                    whole = false;
                    Warn("Jobs: '" + key + "' (" + spec.Machine + ") has no game object variable '" + spec.Objects[o] + "'; what it holds is not carried.");
                }
            }

            if (spec.Pays != null)
            {
                if (fsm.FsmVariables.FindFsmFloat(spec.Pays) == null)
                {
                    whole = false;
                    Warn("Jobs: '" + key + "' (" + spec.Machine + ") has no float '" + spec.Pays + "'; its states are each game's own, or they would pay in every game.");
                    return false;
                }
            }

            relaysByKey[relay.Key] = relay;
            relaysByMachine[fsm.GetInstanceID()] = relay;
            relays.Add(relay);
            job.Relays.Add(relay);
            for (int s = 0; s < spec.States.Length; s++)
            {
                if (!DoorSync.HasState(fsm, spec.States[s]))
                {
                    whole = false;
                    Warn("Jobs: '" + job.Definition.Name + " :: " + spec.Machine + "' has no state '" + spec.States[s] + "' in this game.");
                }
            }

            for (int g = 0; g < spec.Guards.Length; g++)
            {
                if (!DoorSync.HasState(fsm, spec.Guards[g].State) || fsm.FsmVariables.FindFsmGameObject(spec.Guards[g].Variable) == null)
                {
                    whole = false;
                    Warn("Jobs: '" + key + "' (" + spec.Machine + ") cannot guard '" + spec.Guards[g].State + "' with '" + spec.Guards[g].Variable + "' in this game; it may take another game's things for its own.");
                }
            }

            for (int s = 0; s < spec.Spares.Length; s++)
            {
                JobSpare spare = spec.Spares[s];
                bool found = DoorSync.HasState(fsm, spare.State)
                    && (spare.Vehicle == null ? Below(at, spare.Object) != null : fsm.FsmVariables.FindFsmGameObject(spare.Vehicle) != null);
                for (int a = 0; a < spare.Also.Length; a++)
                {
                    found &= fsm.FsmVariables.FindFsmGameObject(spare.Also[a]) != null;
                }

                if (!found)
                {
                    whole = false;
                    Warn("Jobs: '" + key + "' (" + spec.Machine + ") cannot spare '" + (spare.Vehicle ?? spare.Object) + "' in '" + spare.State + "' in this game; there it moves the vehicle in every game.");
                }
            }

            InjectHooks(relay);
            return whole;
        }

        // The mod's actions first in the relayed and guarded states of the machine's current state machine, once per state machine.
        private static void InjectHooks(Relay relay)
        {
            // Unity's null check is also true for a destroyed object.
            PlayMakerFSM fsm = relay.Machine;
            Fsm now = fsm == null ? null : fsm.Fsm;
            Fsm was;
            int id = fsm == null ? 0 : fsm.GetInstanceID();
            if (now == null || (Hooked.TryGetValue(id, out was) && was == now))
            {
                return;
            }

            Hooked[id] = now;
            InjectHooks(relay, fsm, id);
        }

        // Apart from the look above, which runs for every relayed machine in every frame: the hooks below capture the
        // machine's number, and C# makes the object that holds it on entering the method that declares it, whether a hook
        // is made or not (the jobs asked the memory for about 8 MB a minute, 2026-11-14).
        private static void InjectHooks(Relay relay, PlayMakerFSM fsm, int id)
        {
            JobRelay spec = relay.Spec;
            var held = new FsmStateAction[spec.Spares.Length][];
            for (int s = 0; s < spec.Spares.Length; s++)
            {
                held[s] = spec.Spares[s].Actions.Length == 0 ? null : GameActions(fsm, spec.Spares[s].State, spec.Spares[s].Actions);
            }

            for (int s = 0; s < spec.States.Length; s++)
            {
                string state = spec.States[s];
                if (DoorSync.HasState(fsm, state))
                {
                    Action hook = () => Entered(id, state);

                    // First in the state, to be run however soon the state moves on; at the end of one with nothing in it.
                    if (!fsm.FsmInject(state, hook, false, 0, false))
                    {
                        fsm.FsmInject(state, hook, false, -1, false);
                    }
                }
            }

            for (int g = 0; g < spec.Guards.Length; g++)
            {
                string variable = spec.Guards[g].Variable;
                if (DoorSync.HasState(fsm, spec.Guards[g].State))
                {
                    Action hook = () => Guard(id, variable);
                    fsm.FsmInject(spec.Guards[g].State, hook, false, 0, false);
                }
            }

            for (int s = 0; s < spec.Spares.Length; s++)
            {
                JobSpare spare = spec.Spares[s];
                FsmStateAction[] actions = held[s];
                if (DoorSync.HasState(fsm, spare.State))
                {
                    // First of all, before the state's own actions move the vehicle.
                    Action hook = () => Spare(id, spare, actions);
                    fsm.FsmInject(spare.State, hook, false, 0, false);
                }
            }
        }

        // A path below the job, or from the scene's top if it starts with '/'. One the game took to the scene's top (the moving
        // Jokke, a note on its own) is looked for there too, from where its name is in the path.
        private static Transform Below(Transform job, string path)
        {
            if (path.Length == 0)
            {
                return job;
            }

            if (path[0] == '*')
            {
                return Anywhere(path.Substring(1));
            }

            if (path[0] != '/')
            {
                Transform found = job.Find(path);
                if (found != null)
                {
                    return found;
                }

                string[] names = path.Split('/');
                for (int k = 0; k < names.Length; k++)
                {
                    GameObject top = GameObject.Find("/" + names[k]);
                    if (top == null)
                    {
                        continue;
                    }

                    string rest = k + 1 < names.Length ? string.Join("/", names, k + 1, names.Length - k - 1) : string.Empty;
                    Transform below = rest.Length == 0 ? top.transform : top.transform.Find(rest);
                    if (below != null)
                    {
                        return below;
                    }
                }

                return null;
            }

            int slash = path.IndexOf('/', 1);
            GameObject root = GameObject.Find("/" + (slash < 0 ? path.Substring(1) : path.Substring(1, slash - 1)));
            return root == null ? null : slash < 0 ? root.transform : root.transform.Find(path.Substring(slash + 1));
        }

        // "*name/rest": the object of that name wherever the game has it (a wheel on the car or in its box), and what is below
        // it; one switched on before one switched off. Looked for in an index of every object by name, made once a frame.
        private static Transform Anywhere(string path)
        {
            int slash = path.IndexOf('/');
            string name = slash < 0 ? path : path.Substring(0, slash);
            string rest = slash < 0 ? null : path.Substring(slash + 1);
            if (named == null || namedFrame != Time.frameCount)
            {
                namedFrame = Time.frameCount;
                named = new Dictionary<string, List<Transform>>();
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(Transform));
                for (int i = 0; i < all.Length; i++)
                {
                    var thing = all[i] as Transform;

                    // What is not in the scene (a prefab, an asset) is hidden from it.
                    if (thing == null || thing.gameObject.hideFlags != HideFlags.None)
                    {
                        continue;
                    }

                    Index(thing.name, thing);
                }

                // A part the game renames once its machine has run (every wheel becomes "wheel_regula" or "wheel_offset")
                // keeps its own name in the machine's "ID", the tag it is saved under: it is known by that name too.
                UnityEngine.Object[] machines = Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM));
                for (int i = 0; i < machines.Length; i++)
                {
                    var machine = machines[i] as PlayMakerFSM;
                    if (machine == null || machine.gameObject.hideFlags != HideFlags.None || machine.Fsm == null)
                    {
                        continue;
                    }

                    try
                    {
                        FsmString id = machine.FsmVariables.FindFsmString(OwnNameVariable);
                        if (id != null && !string.IsNullOrEmpty(id.Value) && id.Value != machine.name)
                        {
                            Index(id.Value, machine.transform);
                        }
                    }
                    catch (Exception)
                    {
                        // A machine whose variables cannot be read (one never woken up) names nothing.
                    }
                }
            }

            List<Transform> candidates;
            if (!named.TryGetValue(name, out candidates))
            {
                return null;
            }

            Transform best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                // Unity's null check is also true for a destroyed object. Only what hangs from a root of the loaded scene: a
                // prefab of the same name is not the thing.
                Transform found = candidates[i] == null ? null : rest == null ? candidates[i] : candidates[i].Find(rest);
                if (found == null || GameObject.Find("/" + found.root.name) != found.root.gameObject)
                {
                    continue;
                }

                if (best == null || (found.gameObject.activeInHierarchy && !best.gameObject.activeInHierarchy))
                {
                    best = found;
                }
            }

            return best;
        }

        private static void Index(string name, Transform thing)
        {
            List<Transform> same;
            if (!named.TryGetValue(name, out same))
            {
                same = new List<Transform>(1);
                named[name] = same;
            }

            if (!same.Contains(thing))
            {
                same.Add(thing);
            }
        }

        // A warning when a job is first shared; not again each time its missing pieces are looked for.
        private void Warn(string text)
        {
            if (!retrying)
            {
                log.Warning(LogCategory.Fsm, text);
            }
        }

        // What was heard of pieces not found then, for those found since: as a catch-up (how it is, not news).
        private void TakePending()
        {
            if (pending.Count == 0)
            {
                return;
            }

            var found = new List<string>();
            foreach (KeyValuePair<string, int[]> item in pending)
            {
                if (relaysByKey.ContainsKey(item.Key) || switchesByKey.ContainsKey(item.Key) || byKey.ContainsKey(item.Key) || placesByKey.ContainsKey(item.Key))
                {
                    found.Add(item.Key);
                }
            }

            for (int i = 0; i < found.Count; i++)
            {
                int[] values = pending[found[i]];
                pending.Remove(found[i]);
                Hear(found[i], values, true);
            }
        }

        // A hook in a relayed state: the game entered it.
        private static void Entered(int machine, string state)
        {
            JobSync sync = active;
            if (sync != null)
            {
                sync.OnEntered(machine, state);
            }
        }

        // A hook first in a guarded state: what the variable holds may be another game's to move.
        private static void Guard(int machine, string variable)
        {
            JobSync sync = active;
            if (sync != null)
            {
                sync.OnGuard(machine, variable);
            }
        }

        // A hook first in a state that moves a vehicle: whether this game moves it.
        // The game's own actions of a state at those places (the mod's hooks left out), or null if one is not there.
        private static FsmStateAction[] GameActions(PlayMakerFSM fsm, string stateName, int[] places)
        {
            FsmState[] states = fsm.FsmStates;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                if (states[s].Name != stateName)
                {
                    continue;
                }

                var own = new List<FsmStateAction>();
                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    Type type = actions[a] == null ? null : actions[a].GetType();
                    if (type != null && (type.FullName == null || type.FullName.IndexOf("MSCL_PMHook", StringComparison.Ordinal) < 0))
                    {
                        own.Add(actions[a]);
                    }
                }

                var picked = new FsmStateAction[places.Length];
                for (int i = 0; i < places.Length; i++)
                {
                    if (places[i] >= own.Count)
                    {
                        return null;
                    }

                    picked[i] = own[places[i]];
                }

                return picked;
            }

            return null;
        }

        private static void Spare(int machine, JobSpare spare, FsmStateAction[] actions)
        {
            JobSync sync = active;
            if (sync != null)
            {
                sync.OnSpare(machine, spare, actions);
            }
        }

        private void OnEntered(int machine, string state)
        {
            Relay relay;
            NetSession session = manager.Session;
            if (current == null || session == null || session.State != SessionState.Connected
                || !relaysByMachine.TryGetValue(machine, out relay) || relay.Driving)
            {
                return;
            }

            if ((relay.Spec.From == JobRelayFrom.Host && !host) || (relay.Spec.From == JobRelayFrom.Clients && host))
            {
                return;
            }

            int index = Array.IndexOf(relay.Spec.States, state);
            if (index < 0)
            {
                return;
            }

            byte local = session.LocalPlayerId;
            if (relay.Deal != null && !relay.Deal.Entered(local, Array.IndexOf(relay.Spec.DealEnds, state) >= 0))
            {
                return;
            }

            // What the job's values became in this frame goes first: the state may read them (the farm's stage).
            for (int v = 0; v < relay.Job.Values.Count; v++)
            {
                if (relay.Job.Values[v].Spec.Kind == JobValueKind.Said)
                {
                    Watch(relay.Job.Values[v], clock);
                }
            }

            relay.Serial++;
            var news = new JobRelayNews { State = index, Serial = relay.Serial, Player = local, Things = new ushort[relay.Spec.Objects.Length] };
            for (int o = 0; o < relay.Spec.Objects.Length; o++)
            {
                // Unity's null check is also true for a destroyed object.
                FsmGameObject held = relay.Machine == null ? null : relay.Machine.FsmVariables.FindFsmGameObject(relay.Spec.Objects[o]);
                GameObject thing = held == null ? null : held.Value;
                news.Things[o] = thing == null || objects == null ? (ushort)0 : objects.IdOf(thing);
            }

            current.Set(relay.Key, news.Encode());
            log.Info(LogCategory.Fsm, "Jobs: '" + relay.Key + "' entered '" + state + "' in this game; told" + Carried(news) + (relay.Deal != null && relay.Deal.Dealer == local ? " (this game deals)." : "."));
        }

        private void OnGuard(int machine, string variable)
        {
            Relay relay;
            NetSession session = manager.Session;
            if (current == null || objects == null || session == null || session.State != SessionState.Connected
                || !relaysByMachine.TryGetValue(machine, out relay) || relay.Driving)
            {
                return;
            }

            // Unity's null check is also true for a destroyed object.
            FsmGameObject held = relay.Machine == null ? null : relay.Machine.FsmVariables.FindFsmGameObject(variable);
            GameObject thing = held == null ? null : held.Value;
            if (thing == null || !objects.MovedElsewhere(thing))
            {
                return;
            }

            held.Value = null;
            if (guardedReported.Add(thing.GetInstanceID()))
            {
                log.Info(LogCategory.Fsm, "Jobs: '" + relay.Key + "' saw '" + thing.name + "', which another game moves; left to that game (not taken here).");
            }
        }

        // A state that moves a vehicle, entered here: in a game that does not simulate the vehicle, or with a player in it or
        // by it, the state does not get the vehicle (nor what it changes along with it) until this frame is over.
        private void OnSpare(int machine, JobSpare spare, FsmStateAction[] actions)
        {
            Relay relay;
            NetSession session = manager.Session;
            if (current == null || vehicles == null || session == null || session.State != SessionState.Connected
                || !relaysByMachine.TryGetValue(machine, out relay))
            {
                return;
            }

            // Unity's null check is also true for a destroyed object.
            FsmVariables variables = relay.Machine == null ? null : relay.Machine.FsmVariables;
            FsmGameObject held = variables == null || spare.Vehicle == null ? null : variables.FindFsmGameObject(spare.Vehicle);
            Transform named = spare.Object == null ? null : Below(relay.Machine.transform, spare.Object);
            GameObject vehicle = held != null ? held.Value : (named == null ? null : named.gameObject);
            if (vehicle == null)
            {
                return;
            }

            string why = vehicles.SimulatesRoot(vehicle.transform) ? (spare.EvenNear ? null : Someone(vehicle.transform)) : "another game simulates it";
            if (spare.Actions.Length > 0)
            {
                HoldActions(relay, spare, actions, why, vehicle);
                return;
            }

            if (why == null)
            {
                return;
            }

            Empty(held);
            for (int a = 0; a < spare.Also.Length; a++)
            {
                Empty(variables.FindFsmGameObject(spare.Also[a]));
            }

            log.Info(LogCategory.Fsm, "Jobs: '" + relay.Key + "' entered '" + spare.State + "', which moves '" + vehicle.name + "'; here it is left where it is (" + why + ").");
        }

        // The state's actions that move the vehicle: held back here (until the machine leaves the state) if it is not this game's
        // to move, and done if it is (the mod's own earlier holding undone).
        private void HoldActions(Relay relay, JobSpare spare, FsmStateAction[] actions, string why, GameObject vehicle)
        {
            if (actions == null)
            {
                return;
            }

            int count = 0;
            for (int i = 0; i < actions.Length; i++)
            {
                FsmStateAction action = actions[i];
                if (action == null)
                {
                    continue;
                }

                if (why != null && action.Enabled)
                {
                    action.Enabled = false;
                    heldActions.Add(new HeldAction { Machine = relay.Machine, State = spare.State, Action = action });
                    count++;
                }
                else if (why == null && !action.Enabled)
                {
                    for (int h = heldActions.Count - 1; h >= 0; h--)
                    {
                        if (heldActions[h].Action == action)
                        {
                            action.Enabled = true;
                            heldActions.RemoveAt(h);
                        }
                    }
                }
            }

            if (count > 0)
            {
                log.Info(LogCategory.Fsm, "Jobs: '" + relay.Key + "' entered '" + spare.State + "', which works on '" + vehicle.name + "'; here " + count + " of its action(s) held back (" + why + ").");
            }
        }

        // The actions held back of a state the machine has left: as the game had them.
        private void LetHeldActionsGo()
        {
            for (int h = heldActions.Count - 1; h >= 0; h--)
            {
                HeldAction item = heldActions[h];

                // Unity's null check is also true for a destroyed object.
                if (item.Machine == null || item.Machine.ActiveStateName != item.State)
                {
                    item.Action.Enabled = true;
                    heldActions.RemoveAt(h);
                }
            }
        }

        private void Empty(FsmGameObject variable)
        {
            if (variable != null && variable.Value != null)
            {
                spared.Add(new KeyValuePair<FsmGameObject, GameObject>(variable, variable.Value));
                variable.Value = null;
            }
        }

        // What a state that moves a vehicle did not get here, back in its variables.
        private void PutBackSpared()
        {
            for (int i = 0; i < spared.Count; i++)
            {
                spared[i].Key.Value = spared[i].Value;
            }

            spared.Clear();
        }

        // The host: a machine held where it is while it is in one of its states and another player is near its object.
        private void WatchHold(Job job, HeldMachine hold)
        {
            // Unity's null check is also true for a destroyed object.
            if (hold.Machine == null)
            {
                return;
            }

            string near = null;
            if (Array.IndexOf(hold.Spec.States, hold.Machine.ActiveStateName) >= 0)
            {
                near = RemoteNear(hold.Machine.transform.position, hold.Spec.Within);
            }

            if (near != null && hold.Paused == null && hold.Machine.enabled)
            {
                hold.Paused = new PausedMachine(hold.Machine);
                log.Info(LogCategory.Fsm, "Jobs: '" + job.Definition.Name + "' holds '" + hold.Spec.Machine + "' in '" + hold.Machine.ActiveStateName + "': " + near + ".");
            }
            else if (near == null && hold.Paused != null)
            {
                hold.Paused.Resume();
                hold.Paused = null;
                log.Info(LogCategory.Fsm, "Jobs: '" + job.Definition.Name + "' lets '" + hold.Spec.Machine + "' go on: nobody else is near.");
            }
        }

        // Another player within that distance of a place, as this game shows them, or null.
        private string RemoteNear(Vector3 at, float within)
        {
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                PlayerPose pose = remote.Shown;
                if (remote.Visible && vehicles.ToWorld(ref pose) && (new Vector3(pose.X, pose.Y, pose.Z) - at).sqrMagnitude < within * within)
                {
                    return "player " + remote.Id.ToString(CultureInfo.InvariantCulture) + " is within " + within.ToString("0", CultureInfo.InvariantCulture) + " m";
                }
            }

            return null;
        }

        // Who is in the vehicle or by it, as this game knows them, or null: the local player, or another as it is shown here.
        private string Someone(Transform root)
        {
            float near = SpareNear * SpareNear;
            Vector3 at = root.position;

            // Unity's null check is also true for a destroyed object.
            if (player != null && (player.IsChildOf(root) || (player.position - at).sqrMagnitude < near))
            {
                return "this game's player is in it or by it";
            }

            ushort id;
            bool known = vehicles.IdOf(root, out id);
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                if (!remote.Visible)
                {
                    continue;
                }

                PlayerPose pose = remote.Shown;
                if ((known && pose.Parent == id) || (vehicles.ToWorld(ref pose) && (new Vector3(pose.X, pose.Y, pose.Z) - at).sqrMagnitude < near))
                {
                    return "player " + remote.Id.ToString(CultureInfo.InvariantCulture) + " is in it or by it";
                }
            }

            return null;
        }

        private static string Carried(JobRelayNews news)
        {
            if (news.Things == null || news.Things.Length == 0)
            {
                return string.Empty;
            }

            var text = new System.Text.StringBuilder(" with thing(s)");
            for (int i = 0; i < news.Things.Length; i++)
            {
                text.Append(' ').Append(news.Things[i].ToString(CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        private static NamedVariable Find(FsmVariables variables, string name)
        {
            FsmFloat number = variables.FindFsmFloat(name);
            if (number != null)
            {
                return number;
            }

            FsmBool flag = variables.FindFsmBool(name);
            if (flag != null)
            {
                return flag;
            }

            FsmInt whole = variables.FindFsmInt(name);
            if (whole != null)
            {
                return whole;
            }

            // Said only: a colour (the paint Fleetari is told) or a place (where his revenge leaves the car), as several numbers.
            FsmColor colour = variables.FindFsmColor(name);
            if (colour != null)
            {
                return colour;
            }

            return variables.FindFsmVector3(name);
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith("job/", StringComparison.Ordinal))
            {
                heard.Add(new Heard { Key = key, Values = values, CatchUp = current.CatchingUp });
            }
        }

        private void OnChangeWanted(string key, int[] change, byte player)
        {
            if (key.StartsWith("job/", StringComparison.Ordinal))
            {
                changes.Add(new KeyValuePair<string, int[]>(key, change));
            }
        }

        // The host: what a client's player did to an amount, added here; everyone hears what this game ends up with.
        private void ApplyChange(string key, int[] change)
        {
            Value value;
            if (!host || change.Length < 1 || !byKey.TryGetValue(key, out value) || value.Spec.Kind != JobValueKind.Amount)
            {
                return;
            }

            int before = value.Read();
            value.Write(before + change[0]);
            int after = value.Read();
            value.Told = after;
            value.Seen = after;
            value.ToldAt = clock;
            current.Tell(key, new[] { before }, new[] { after });
        }

        private void Hear(string key, int[] values, bool catchUp)
        {
            if (values.Length < 1)
            {
                return;
            }

            if (!relaysByKey.ContainsKey(key) && !switchesByKey.ContainsKey(key) && !byKey.ContainsKey(key) && !placesByKey.ContainsKey(key))
            {
                // A piece this game has not found (yet): the last word of it waits for it.
                if (pending.Count < PendingMost || pending.ContainsKey(key))
                {
                    pending[key] = values;
                }

                return;
            }

            Value value;
            Place place;
            Relay relay;
            Switch item;
            JobRelayNews news;
            if (relaysByKey.TryGetValue(key, out relay))
            {
                if (JobRelayNews.TryDecode(values, out news))
                {
                    if (catchUp)
                    {
                        Baseline(relay, news);
                    }
                    else
                    {
                        Follow(relay, news);
                    }
                }
            }
            else if (switchesByKey.TryGetValue(key, out item))
            {
                // A world's switch is the host's; one a player switches is whoever's game switched it last.
                if (!host || item.Said)
                {
                    item.Write(values[0]);
                    item.Told = item.Read();
                    item.Started = true;
                }
            }
            else if (byKey.TryGetValue(key, out value) && value.Wide)
            {
                if (value.WriteWide(values))
                {
                    value.ToldWide = value.ReadWide();
                    value.Started = true;
                    log.Info(LogCategory.Fsm, "Jobs: '" + key + "' (" + value.Spec.Variable + ") is " + Describe(values) + " in another game; here too" + (catchUp ? " (a catch-up)." : "."));
                }
            }
            else if (byKey.TryGetValue(key, out value))
            {
                if (host && value.Spec.Kind == JobValueKind.Amount)
                {
                    // The host's own amounts are its game's: it only hears what it said.
                    return;
                }

                // What this player did since the last word and has not said yet goes out first, and stays.
                int pending = value.Spec.Kind == JobValueKind.Amount ? value.Read() - value.Seen : 0;
                if (pending != 0)
                {
                    current.Tell(key, new[] { value.Seen }, new[] { value.Seen + pending });
                }

                value.Write(values[0] + pending);
                value.Seen = value.Read();
                value.Told = value.Seen;
                value.Started = true;
                if (value.Spec.Kind == JobValueKind.Said)
                {
                    log.Info(LogCategory.Fsm, "Jobs: '" + key + "' (" + value.Spec.Variable + ") is " + values[0].ToString(CultureInfo.InvariantCulture) + " in another game; here too" + (catchUp ? " (a catch-up)." : "."));
                }
            }
            else if (!host && values.Length >= 3 && placesByKey.TryGetValue(key, out place))
            {
                if (!place.Once || !place.Applied)
                {
                    place.Put(values);
                    place.Applied = true;
                    if (place.Once)
                    {
                        log.Info(LogCategory.Fsm, "Jobs: '" + place.Key + "' ('" + place.Moved.name + "') put once where the host's game had it at first; each game moves it its own way from there.");
                    }
                }
            }
        }

        // The last relayed state the host's answer to this game's request names: entered there long ago maybe, whatever its
        // age, and what entering it did travels apart (the farmer's pay is a value of its own), so this game is not taken
        // there: it would pay twice. Its number counts from here on: a later one is news, and is followed.
        private void Baseline(Relay relay, JobRelayNews news)
        {
            if (news.Serial <= relay.Serial || news.State < 0 || news.State >= relay.Spec.States.Length)
            {
                return;
            }

            relay.Serial = news.Serial;

            // Unity's null check is also true for a destroyed object.
            string here = relay.Machine == null ? "?" : relay.Machine.ActiveStateName;
            log.Info(LogCategory.Fsm, "Jobs: '" + relay.Key + "' last went to '" + relay.Spec.States[news.State] + "' in the host's game; history, not entered here (here in '" + here + "'; a catch-up on joining).");
        }

        // Another game entered a relayed state: this one goes there too, unless it is there already, or another deals.
        private void Follow(Relay relay, JobRelayNews news)
        {
            // A dealt machine's news come from whoever deals, in the order that game said them (each game's news reach the
            // others in order): a running number would drop the news of a game that started a deal at the same moment.
            bool dealt = relay.Deal != null && news.Player >= 0 && news.Player <= byte.MaxValue;
            int lastFrom;
            if ((!dealt && news.Serial <= relay.Serial) || news.State < 0 || news.State >= relay.Spec.States.Length
                || (dealt && relay.LastFrom.TryGetValue(news.Player, out lastFrom) && news.Serial <= lastFrom))
            {
                // Old news, or said again (the checksums resend a tally that differs).
                return;
            }

            if (dealt)
            {
                relay.LastFrom[news.Player] = news.Serial;
            }

            relay.Serial = Math.Max(relay.Serial, news.Serial);
            if ((relay.Spec.From == JobRelayFrom.Host && host) || (relay.Spec.From == JobRelayFrom.Clients && !host))
            {
                return;
            }

            string state = relay.Spec.States[news.State];
            JobDealStep step = JobDealStep.Follow;
            if (dealt)
            {
                int before = relay.Deal.Dealer;
                step = relay.Deal.Heard(manager.Session.LocalPlayerId, (byte)news.Player, Array.IndexOf(relay.Spec.DealEnds, state) >= 0);
                if (step == JobDealStep.FollowPaused && before != news.Player)
                {
                    log.Info(LogCategory.Fsm, "Jobs: player " + news.Player + "'s game deals with '" + relay.Key + "' (" + relay.Spec.Machine + "); here it is paused and goes where theirs goes.");
                }
            }

            switch (step)
            {
                case JobDealStep.Ignore:
                    return;
                case JobDealStep.FollowPaused:
                    relay.DealHeardAt = clock;
                    Drive(relay, state, news, true);
                    return;
                case JobDealStep.FollowAndResume:
                    Drive(relay, state, news, false);
                    log.Info(LogCategory.Fsm, "Jobs: player " + news.Player + "'s deal with '" + relay.Key + "' is over; here it goes on by itself from '" + state + "'.");
                    return;
                default:
                    Drive(relay, state, news, false);
                    return;
            }
        }

        // Takes this game's machine to a state the game's way: a global transition and its event, processed at once. A
        // paused machine is let go for that moment and paused again after it.
        private void Drive(Relay relay, string state, JobRelayNews news, bool holdAfter)
        {
            // Unity's null check is also true for a destroyed object.
            PlayMakerFSM machine = relay.Machine;
            if (machine == null)
            {
                return;
            }

            // The pay is in the game that took it; here the state does all the rest. Off here (the farmer's hand not out), the
            // pay is taken away all the same: it would be taken again when it comes out.
            FsmFloat pay = relay.Spec.Pays == null ? null : machine.FsmVariables.FindFsmFloat(relay.Spec.Pays);
            if (pay != null)
            {
                pay.Value = 0f;
            }

            int mirror = MirrorOf(relay);
            bool paused = mirror >= 0 || relay.Held != null;
            if (!machine.gameObject.activeInHierarchy || (!machine.enabled && !paused))
            {
                Absorb(relay.Job);
                log.Info(LogCategory.Fsm, "Jobs: '" + relay.Key + "' went to '" + state + "' in another game; here its machine is off and stays as it is" + (pay != null ? " (its pay taken away: paid there)." : "."));
                return;
            }

            if (machine.ActiveStateName == state)
            {
                Hold(relay, holdAfter, mirror);
                return;
            }

            string name = RelayEvent + " " + state;
            if (!DoorSync.HasGlobalTransition(machine, name))
            {
                machine.AddGlobalTransition(name, state);
            }

            bool missing = false;
            for (int o = 0; o < relay.Spec.Objects.Length; o++)
            {
                FsmGameObject held = machine.FsmVariables.FindFsmGameObject(relay.Spec.Objects[o]);
                if (held == null || objects == null)
                {
                    continue;
                }

                ushort id = news.Things != null && o < news.Things.Length ? news.Things[o] : (ushort)0;
                GameObject thing = id == 0 ? null : objects.ThingOf(id);
                if (thing != null && objects.SharedIdOf(thing) != 0)
                {
                    // Taken by the job here too: the game's own again, as it was before the other game moved it.
                    objects.GiveBack(thing);
                }

                missing |= id != 0 && thing == null;
                held.Value = thing;
            }

            FsmString subtitle = relay.Spec.Quiet ? FsmVariables.GlobalVariables.FindFsmString("GUIsubtitle") : null;
            string subtitleBefore = subtitle == null ? null : subtitle.Value;
            relay.Driving = true;
            try
            {
                if (mirror >= 0)
                {
                    relay.Job.PausedMachines[mirror].Resume();
                }
                else if (relay.Held != null)
                {
                    relay.Held.Resume();
                    relay.Held = null;
                }

                machine.SendEvent(name);
            }
            finally
            {
                relay.Driving = false;
                if (mirror >= 0)
                {
                    relay.Job.PausedMachines[mirror] = new PausedMachine(machine);
                }

                Hold(relay, holdAfter, mirror);
            }

            if (subtitle != null && !Near(machine.transform))
            {
                subtitle.Value = subtitleBefore;
            }

            Absorb(relay.Job);
            log.Info(LogCategory.Fsm, "Jobs: '" + relay.Key + "' went to '" + state + "' in another game; here too" + (pay != null ? " (paid there)" : string.Empty)
                + (missing ? " (a thing it carries is not here)" : string.Empty) + (mirror >= 0 || relay.Held != null ? ", paused there." : "."));
        }

        // Paused while another game deals with it, or let go.
        private static void Hold(Relay relay, bool holdAfter, int mirror)
        {
            if (mirror >= 0)
            {
                return;
            }

            if (holdAfter && relay.Held == null && relay.Machine != null)
            {
                relay.Held = new PausedMachine(relay.Machine);
            }
            else if (!holdAfter && relay.Held != null)
            {
                relay.Held.Resume();
                relay.Held = null;
            }
        }

        // Where the machine is in its job's paused machines (the host's world, here a copy), or -1.
        private static int MirrorOf(Relay relay)
        {
            List<PausedMachine> paused = relay.Job.PausedMachines;
            for (int i = 0; i < paused.Count; i++)
            {
                if (paused[i].Machine == relay.Machine)
                {
                    return i;
                }
            }

            return -1;
        }

        // What a state taken there by another game's word changed here is not news: it is what that game says.
        private void Absorb(Job job)
        {
            for (int v = 0; v < job.Values.Count; v++)
            {
                Value value = job.Values[v];
                if (value.Spec.Kind == JobValueKind.Said)
                {
                    value.Told = value.Read();
                    value.Seen = value.Told;
                    value.Started = true;
                }
            }

            for (int s = 0; s < job.Switches.Count; s++)
            {
                if (job.Switches[s].Said)
                {
                    job.Switches[s].Told = job.Switches[s].Read();
                    job.Switches[s].Started = true;
                }
            }
        }

        private bool Near(Transform thing)
        {
            // Unity's null check is also true for a destroyed object.
            return player != null && thing != null && (player.position - thing.position).sqrMagnitude < QuietNear * QuietNear;
        }

        // A player who dealt with a machine and left, or says nothing of it any more: the machine here goes on by itself.
        private void ForgetLeftDealers()
        {
            for (int r = 0; r < relays.Count; r++)
            {
                Relay relay = relays[r];
                int dealer = relay.Deal == null ? JobDeal.Nobody : relay.Deal.Dealer;
                if (dealer == JobDeal.Nobody || dealer == manager.Session.LocalPlayerId)
                {
                    continue;
                }

                bool here = manager.Players != null && manager.Players.Find((byte)dealer) != null;
                if (here && clock - relay.DealHeardAt < DealSilentAtMost)
                {
                    continue;
                }

                relay.Deal.Left((byte)dealer);
                Hold(relay, false, MirrorOf(relay));
                log.Info(LogCategory.Fsm, "Jobs: player " + dealer + (here ? " says nothing of the deal with '" + relay.Key + "' for a minute" : " left while dealing with '" + relay.Key + "'")
                    + "; here it goes on by itself.");
            }
        }

        // Every game: a value its own game changed is said, as a difference (an amount on a client) or as it is.
        private void Watch(Value value, double now)
        {
            if (value.Gone)
            {
                // Destroyed here: what it reads as now is not what it became.
                return;
            }

            if (value.Wide)
            {
                WatchWide(value);
                return;
            }

            int read = value.Read();
            if (value.Spec.Kind == JobValueKind.Amount)
            {
                if (host)
                {
                    if ((read != value.Told || !value.Started) && (now - value.ToldAt >= TellInterval || !value.Started))
                    {
                        current.Tell(value.Key, new[] { value.Told }, new[] { read });
                        value.Told = read;
                        value.Seen = read;
                        value.ToldAt = now;
                        value.Started = true;
                    }
                }
                else if (value.Started && read != value.Seen && now - value.ToldAt >= TellInterval)
                {
                    current.Tell(value.Key, new[] { value.Seen }, new[] { read });
                    value.Seen = read;
                    value.ToldAt = now;
                }
                else if (!value.Started)
                {
                    // A client's own amounts before it heard the host's are not what its player did.
                    value.Seen = read;
                    value.Started = true;
                }

                return;
            }

            if (!value.Started)
            {
                // The first look: the host says it, for whoever joins; a client takes it as it is (not something its player
                // did). Taken also when it reads as the default: else the first real change went unsaid (seen with the kilju
                // buyer's pay, 2026-10-17).
                value.Told = read;
                value.Seen = read;
                value.Started = true;
                if (host)
                {
                    current.Set(value.Key, new[] { read });
                }
            }
            else if (read != value.Told)
            {
                value.Told = read;
                value.Seen = read;
                current.Set(value.Key, new[] { read });
                log.Info(LogCategory.Fsm, "Jobs: '" + value.Key + "' (" + value.Spec.Variable + ") became " + read.ToString(CultureInfo.InvariantCulture) + " in this game; told.");
            }
        }

        // A colour or a place, said: whoever's game changed it last says all of it.
        private void WatchWide(Value value)
        {
            // Read into a buffer in every frame; a new array only for what changed.
            int count = value.ReadWide(wideBuffer);
            if (count == 0 || (value.Started && SameAs(wideBuffer, count, value.ToldWide)))
            {
                return;
            }

            int[] read = new int[count];
            System.Array.Copy(wideBuffer, read, count);
            if (!value.Started)
            {
                value.ToldWide = read;
                value.Started = true;
                if (host)
                {
                    current.Set(value.Key, read);
                }
            }
            else if (!Same(read, value.ToldWide))
            {
                value.ToldWide = read;
                current.Set(value.Key, read);
                log.Info(LogCategory.Fsm, "Jobs: '" + value.Key + "' (" + value.Spec.Variable + ") became " + Describe(read) + " in this game; told.");
            }
        }

        private readonly int[] wideBuffer = new int[4];

        private static bool SameAs(int[] buffer, int count, int[] told)
        {
            if (told == null || told.Length != count)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (buffer[i] != told[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Same(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string Describe(int[] values)
        {
            var parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
            }

            return "(" + string.Join(", ", parts) + ")";
        }

        // The host says how a world's switch is; any game says how a player's switch became (a client's first look is not news).
        private void WatchSwitch(Switch item)
        {
            if (!host && !item.Said)
            {
                return;
            }

            // Unity's null check is also true for a destroyed object.
            if (item.Object == null)
            {
                return;
            }

            int on = item.Read();
            if (on == item.Told && item.Started)
            {
                return;
            }

            bool first = !item.Started;
            item.Told = on;
            item.Started = true;
            if (host || !first)
            {
                current.Set(item.Key, new[] { on });
            }
        }

        // The host: where the world's machines have put an object.
        private void WatchPlace(Place place, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (place.Moved == null || now - place.ToldAt < TellInterval || (place.Once && place.Started))
            {
                return;
            }

            Vector3 at = place.Moved.localPosition;
            if (place.Started && (at - place.Told).sqrMagnitude < PositionStep * PositionStep)
            {
                return;
            }

            place.Started = true;
            place.Told = at;
            place.ToldAt = now;
            current.Set(place.Key, new[] { Mathf.RoundToInt(at.x * 1000f), Mathf.RoundToInt(at.y * 1000f), Mathf.RoundToInt(at.z * 1000f) });
        }

        private struct Heard
        {
            public string Key;
            public int[] Values;

            /// <summary>Part of the host's answer to this game's request: how things are, not what just happened.</summary>
            public bool CatchUp;
        }

        private sealed class Job
        {
            public Job(JobDefinition definition)
            {
                Definition = definition;
            }

            public JobDefinition Definition { get; }

            public List<Value> Values { get; } = new List<Value>();

            public List<Place> Places { get; } = new List<Place>();

            public List<PausedMachine> PausedMachines { get; } = new List<PausedMachine>();

            public List<Switch> Switches { get; } = new List<Switch>();

            public List<Relay> Relays { get; } = new List<Relay>();

            public List<HeldMachine> Holds { get; } = new List<HeldMachine>();

            /// <summary>Where the job is: its pieces are looked for below it.</summary>
            public Transform At;

            /// <summary>Its pieces not found yet, looked for again at <see cref="NextRetry"/>.</summary>
            public int Missing;

            public double NextRetry;

            public double RetryDelay;

            public HashSet<int> HoldsFound { get; } = new HashSet<int>();

            public HashSet<int> PausedFound { get; } = new HashSet<int>();
        }

        private sealed class HeldAction
        {
            public PlayMakerFSM Machine;
            public string State;
            public FsmStateAction Action;
        }

        private sealed class HeldMachine
        {
            public JobHold Spec;
            public PlayMakerFSM Machine;
            public PausedMachine Paused;
        }

        private sealed class Value
        {
            private readonly PlayMakerFSM machine;

            public Value(string key, JobValue spec, PlayMakerFSM machine)
            {
                Key = key;
                Spec = spec;
                this.machine = machine;
            }

            public string Key { get; }

            public JobValue Spec { get; }

            /// <summary>What this game last said or heard of it.</summary>
            public int Told { get; set; }

            /// <summary>What it was, here, after the last word: what this player changed since is the difference.</summary>
            public int Seen { get; set; }

            public double ToldAt { get; set; } = double.NegativeInfinity;

            public bool Started { get; set; }

            /// <summary>What this game last said or heard of a colour or a place (<see cref="Wide"/>).</summary>
            public int[] ToldWide { get; set; }

            /// <summary>A colour (four numbers, thousandths) or a place (three, times its scale): said whole.</summary>
            public bool Wide
            {
                get
                {
                    // Unity's null check is also true for a destroyed object.
                    if (machine == null || Spec.Global || Spec.Kind != JobValueKind.Said)
                    {
                        return false;
                    }

                    FsmVariables variables = machine.FsmVariables;
                    return variables.FindFsmFloat(Spec.Variable) == null && variables.FindFsmBool(Spec.Variable) == null && variables.FindFsmInt(Spec.Variable) == null
                        && (variables.FindFsmColor(Spec.Variable) != null || variables.FindFsmVector3(Spec.Variable) != null);
                }
            }

            public int[] ReadWide()
            {
                var read = new int[4];
                int count = ReadWide(read);
                if (count == 0)
                {
                    return null;
                }

                if (count == read.Length)
                {
                    return read;
                }

                var some = new int[count];
                Array.Copy(read, some, count);
                return some;
            }

            /// <summary>The colour (four numbers) or the place (three) into <paramref name="into"/>: how many, or 0.</summary>
            public int ReadWide(int[] into)
            {
                // Unity's null check is also true for a destroyed object.
                if (machine == null)
                {
                    return 0;
                }

                FsmColor colour = machine.FsmVariables.FindFsmColor(Spec.Variable);
                if (colour != null)
                {
                    Color c = colour.Value;
                    into[0] = Thousandths(c.r);
                    into[1] = Thousandths(c.g);
                    into[2] = Thousandths(c.b);
                    into[3] = Thousandths(c.a);
                    return 4;
                }

                FsmVector3 place = machine.FsmVariables.FindFsmVector3(Spec.Variable);
                if (place == null)
                {
                    return 0;
                }

                Vector3 v = place.Value;
                into[0] = (int)Math.Round(v.x * Spec.Scale);
                into[1] = (int)Math.Round(v.y * Spec.Scale);
                into[2] = (int)Math.Round(v.z * Spec.Scale);
                return 3;
            }

            public bool WriteWide(int[] values)
            {
                // Unity's null check is also true for a destroyed object.
                if (machine == null || values == null)
                {
                    return false;
                }

                FsmColor colour = machine.FsmVariables.FindFsmColor(Spec.Variable);
                if (colour != null && values.Length == 4)
                {
                    colour.Value = new Color(values[0] / 1000f, values[1] / 1000f, values[2] / 1000f, values[3] / 1000f);
                    return true;
                }

                FsmVector3 place = machine.FsmVariables.FindFsmVector3(Spec.Variable);
                if (place != null && values.Length == 3)
                {
                    place.Value = new Vector3((float)(values[0] / Spec.Scale), (float)(values[1] / Spec.Scale), (float)(values[2] / Spec.Scale));
                    return true;
                }

                return false;
            }

            private static int Thousandths(float value)
            {
                return (int)Math.Round(value * 1000.0);
            }

            /// <summary>Whether its machine's object is gone (the game destroyed the card table's bet): it reads as nothing then.</summary>
            public bool Gone
            {
                // Unity's null check is also true for a destroyed object.
                get { return machine == null && !Spec.Global; }
            }

            public int Read()
            {
                // Unity's null check is also true for a destroyed object.
                if (machine == null && !Spec.Global)
                {
                    return 0;
                }

                // Looked up each time: a machine on an object never switched on gets its variables when it first runs.
                FsmVariables variables = Spec.Global ? FsmVariables.GlobalVariables : machine.FsmVariables;
                FsmFloat number = variables.FindFsmFloat(Spec.Variable);
                if (number != null)
                {
                    return (int)Math.Round(number.Value * Spec.Scale);
                }

                FsmBool flag = variables.FindFsmBool(Spec.Variable);
                if (flag != null)
                {
                    return flag.Value ? 1 : 0;
                }

                FsmInt whole = variables.FindFsmInt(Spec.Variable);
                return whole == null ? 0 : whole.Value;
            }

            public void Write(int value)
            {
                // Unity's null check is also true for a destroyed object.
                if (machine == null && !Spec.Global)
                {
                    return;
                }

                FsmVariables variables = Spec.Global ? FsmVariables.GlobalVariables : machine.FsmVariables;
                FsmFloat number = variables.FindFsmFloat(Spec.Variable);
                FsmBool flag = number == null ? variables.FindFsmBool(Spec.Variable) : null;
                FsmInt whole = number == null && flag == null ? variables.FindFsmInt(Spec.Variable) : null;
                if (number != null)
                {
                    number.Value = (float)(value / Spec.Scale);
                }
                else if (flag != null)
                {
                    flag.Value = value != 0;
                }
                else if (whole != null)
                {
                    whole.Value = value;
                }
            }
        }

        private sealed class Relay
        {
            public Relay(string key, JobRelay spec, PlayMakerFSM machine, Job job)
            {
                Key = key;
                Spec = spec;
                Machine = machine;
                Job = job;
                Deal = spec.IsDeal ? new JobDeal() : null;
            }

            public Job Job { get; }

            public string Key { get; }

            public JobRelay Spec { get; }

            public PlayMakerFSM Machine { get; }

            /// <summary>Who deals with it, for a machine one player deals with at a time; null otherwise.</summary>
            public JobDeal Deal { get; }

            /// <summary>The machine here, paused while another game deals with it; null when it runs.</summary>
            public PausedMachine Held { get; set; }

            /// <summary>The number of the last state said or heard; a later one is news.</summary>
            public int Serial { get; set; }

            /// <summary>While this sync takes the machine to a state: what it enters then is not news.</summary>
            public bool Driving { get; set; }

            /// <summary>When the game that deals with it last said a state of it.</summary>
            public double DealHeardAt { get; set; }

            /// <summary>For a dealt machine, the number of the last state heard from each player: each says them in order.</summary>
            public Dictionary<int, int> LastFrom { get; } = new Dictionary<int, int>();
        }

        private sealed class Switch
        {
            public Switch(string key, GameObject switched, PlayMakerFSM machine, bool said)
            {
                Key = key;
                Object = switched;
                Machine = machine;
                Said = said;
            }

            public string Key { get; }

            public GameObject Object { get; }

            /// <summary>The state machine switched on and off instead of the object, or null.</summary>
            public PlayMakerFSM Machine { get; }

            /// <summary>Whether a player switches it (whoever's game switched it last), not the host's world.</summary>
            public bool Said { get; }

            public int Told { get; set; } = -1;

            public bool Started { get; set; }

            public int Read()
            {
                // Unity's null check is also true for a destroyed object.
                if (Machine != null)
                {
                    return Machine.enabled ? 1 : 0;
                }

                return Object != null && Object.activeSelf ? 1 : 0;
            }

            public void Write(int on)
            {
                // Unity's null check is also true for a destroyed object.
                if (Machine != null)
                {
                    if (Machine.enabled != (on != 0))
                    {
                        // As the game's "EnableFSM" does it: on again, it starts from its first state.
                        Machine.enabled = on != 0;
                    }
                }
                else if (Object != null && Object.activeSelf != (on != 0))
                {
                    Object.SetActive(on != 0);
                }
            }
        }

        private sealed class Place
        {
            public Place(string key, Transform moved, bool once)
            {
                Key = key;
                Moved = moved;
                Once = once;
            }

            public string Key { get; }

            public Transform Moved { get; }

            public bool Once { get; }

            /// <summary>On a client: put where the host said, at least once.</summary>
            public bool Applied { get; set; }

            public Vector3 Told { get; set; }

            public double ToldAt { get; set; } = double.NegativeInfinity;

            public bool Started { get; set; }

            public void Put(int[] values)
            {
                // Unity's null check is also true for a destroyed object.
                if (Moved != null)
                {
                    Moved.localPosition = new Vector3(values[0] / 1000f, values[1] / 1000f, values[2] / 1000f);
                }
            }
        }
    }
}
