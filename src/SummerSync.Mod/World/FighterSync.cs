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
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The men who pick a fight (<see cref="FighterDefinition"/>, <c>data/fighters/*.json</c>), with
    /// <see cref="TallyReplication"/>:
    ///
    /// - "fighter/&lt;name&gt;/runner" [player + 1, or 0]: whose game runs him. The host says it
    ///   (<see cref="FighterOwnership"/>: the nearest player, kept while he fights). That game's machines run him against
    ///   its own player, as in the game.
    /// - "fighter/&lt;name&gt;/pose" (<see cref="FighterPose"/>): where that game has him, ten times a second while he
    ///   moves. The other games pause his machines (but for what their own physics does: his ragdoll, his hat coming off)
    ///   and put him there, with the same clips and the same things on and loose.
    /// - "fighter/&lt;name&gt;/on" [1 or 0]: whether he is out, said by the host's game or the runner's
    ///   (<see cref="FighterDefinition.DecidedByHost"/>); the others switch him on and off so.
    /// - "fighter/&lt;name&gt;/sound" [player + 1, number, which, name…]: a sound of his machines (or of the player's
    ///   machine he hits) in the runner's game; the others play it where he is.
    /// - "fighter/&lt;name&gt;/punch/&lt;player&gt;" [number, which]: that player's fist hit him in its game, which does not run
    ///   him; the runner's game counts it as a punch of its own (everyone's punches count).
    /// - "fighter/&lt;name&gt;/down" [player + 1, number]: he went down in the runner's game; the others take their copy
    ///   to the same state (his ragdoll, he is gone).
    /// - "fighter/&lt;name&gt;/call/&lt;player&gt;" [number]: the state that calls him out ran in a guest's game; the host's
    ///   game does what it does (the pub's bar opens for the player who is there).
    ///
    /// Offline, or in a game alone, nothing here touches the game.
    /// </summary>
    internal sealed class FighterSync
    {
        private const LogCategory Category = LogCategory.Fsm;
        private const string Prefix = "fighter/";
        private const double SearchInterval = 2.0;
        private const double RunnerInterval = 0.5;
        private const double PoseInterval = 0.1;
        private const double PoseKeepAlive = 1.0;
        private const double PunchGap = 0.25;
        private const float PoseStep = 0.02f;
        private const float YawStep = 1f;
        private const float SnapDistance = 3f;
        private const float Smoothing = 12f;
        private const int ClipBits = 5;
        private const int SoundTextNumbers = 5;
        private const int StruckMachine = 255;
        private const string CalmEvent = "SUMMERSYNC FIGHTER CALM";
        private const string PunchEvent = "SUMMERSYNC FIGHTER PUNCH";
        private const string DownEvent = "SUMMERSYNC FIGHTER DOWN";

        // What the mod has hooked into the game's objects of each fighter, by his object: kept for as long as they live,
        // through sessions, so that nothing is hooked twice and every game numbers his sounds the same.
        private static readonly Dictionary<int, Binding> Bindings = new Dictionary<int, Binding>();
        private static FighterSync active;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly FighterDefinition[] catalog;
        private readonly List<Fighter> fighters = new List<Fighter>();
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();
        private readonly List<bool> heardCatchingUp = new List<bool>();
        private TallyReplication current;
        private Transform player;
        private bool host;
        private double clock;
        private double nextSearch;
        private double nextRunner;

        public FighterSync(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
            catalog = Load(log);
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null without a world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            clock = now;
            player = localPlayer;
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                    active = this;
                }

                nextSearch = now;
                return;
            }

            if (current == null || catalog.Length == 0 || manager.Session == null || manager.Session.State != SessionState.Connected)
            {
                return;
            }

            if (now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value, heardCatchingUp[i]);
            }

            heard.Clear();
            heardCatchingUp.Clear();
            bool decide = host && now >= nextRunner;
            if (decide)
            {
                nextRunner = now + RunnerInterval;
            }

            for (int i = fighters.Count - 1; i >= 0; i--)
            {
                Fighter fighter = fighters[i];

                // Unity's null check is also true for a destroyed object.
                if (fighter.Root == null)
                {
                    log.Info(Category, "Fighter '" + fighter.Definition.Name + "': gone from this game.");
                    fighter.Release();
                    fighters.RemoveAt(i);
                    continue;
                }

                Bind(fighter);
                if (fighter.Bound)
                {
                    WakeUp(fighter);
                }

                HookCall(fighter);
                HookStruck(fighter);
                if (decide)
                {
                    ChooseRunner(fighter);
                }

                WatchOut(fighter);
                if (Runs(fighter))
                {
                    TellPose(fighter, now);
                }
                else
                {
                    Follow(fighter, Time.deltaTime);
                }
            }
        }

        /// <summary>Everything back as the game had it: the session is over, or the world.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            for (int i = 0; i < fighters.Count; i++)
            {
                fighters[i].Release();
            }

            fighters.Clear();
            heard.Clear();
            heardCatchingUp.Clear();
            current = null;
            if (active == this)
            {
                active = null;
            }
        }

        /// <summary>"ss fighter": how each fighter is shared in this game.</summary>
        public string Describe()
        {
            if (current == null)
            {
                return "Fighters: not in a session.";
            }

            var text = new System.Text.StringBuilder();
            text.Append("Fighters this build shares: ").Append(catalog.Length).Append(", found here: ").Append(fighters.Count).Append('.');
            for (int i = 0; i < fighters.Count; i++)
            {
                Fighter fighter = fighters[i];
                text.Append("\n  ").Append(fighter.Definition.Name).Append(": ")
                    .Append(fighter.Root == null ? "gone" : (IsOut(fighter) ? "out" : "not out"))
                    .Append(", run by ").Append(fighter.Runner < 0 ? "nobody yet" : (Runs(fighter) ? "this game" : "player " + fighter.Runner.ToString(CultureInfo.InvariantCulture)))
                    .Append(", brain in '").Append(fighter.Brain == null ? "-" : fighter.Brain.ActiveStateName).Append("'")
                    .Append(", ").Append(fighter.Paused.Count).Append(" machine(s) paused, ")
                    .Append(fighter.Sounds.Count).Append(" sound(s) shared, poses told ").Append(fighter.PoseSerial)
                    .Append(", heard ").Append(fighter.PosesHeard).Append(", punches told ").Append(fighter.PunchSerial)
                    .Append(", counted ").Append(fighter.PunchesCounted).Append(", sounds told ").Append(fighter.SoundSerial)
                    .Append(", played ").Append(fighter.SoundsPlayed).Append('.');
            }

            return text.ToString();
        }

        /// <summary>"ss fighter punch [name] [part]": a test aid: this game's player punched him, as if its fist hit him.</summary>
        public string TestPunch(string name, string part)
        {
            Fighter fighter = null;
            for (int i = 0; i < fighters.Count; i++)
            {
                if (name == null || fighters[i].Definition.Name == name)
                {
                    fighter = fighters[i];
                    break;
                }
            }

            int which = 0;
            if (fighter == null || (part != null && (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out which) || which < 0 || which >= fighter.Hits.Count)))
            {
                return "No such fighter here, or no such part (" + (fighter == null ? 0 : fighter.Hits.Count) + " part(s)).";
            }

            Punched(fighter, which, true);
            return "Fighter '" + fighter.Definition.Name + "': punched on part " + which + (Runs(fighter) ? ", counted here." : ", told to the game that runs him.");
        }

        private static FighterDefinition[] Load(Logger log)
        {
            Assembly assembly = typeof(FighterSync).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            var found = new List<FighterDefinition>();
            for (int i = 0; i < resources.Length; i++)
            {
                if (!resources[i].StartsWith("SummerSync.fighters.", StringComparison.Ordinal) || !resources[i].EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resources[i]))
                    using (var reader = new StreamReader(stream))
                    {
                        found.Add(FighterDefinition.Parse(reader.ReadToEnd()));
                    }
                }
                catch (FormatException exception)
                {
                    log.Error(Category, "The fighter file '" + resources[i] + "' cannot be read; that fighter is each game's own. " + exception.Message);
                }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return found.ToArray();
        }

        private byte Me
        {
            get { return manager.Session.LocalPlayerId; }
        }

        private bool Runs(Fighter fighter)
        {
            return fighter.Runner >= 0 && fighter.Runner == Me;
        }

        private static Transform FromTop(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            int slash = path.IndexOf('/');
            GameObject top = GameObject.Find("/" + (slash < 0 ? path : path.Substring(0, slash)));
            return top == null ? null : slash < 0 ? top.transform : top.transform.Find(path.Substring(slash + 1));
        }

        private void Search()
        {
            for (int d = 0; d < catalog.Length; d++)
            {
                bool known = false;
                for (int i = 0; i < fighters.Count; i++)
                {
                    known |= fighters[i].Definition == catalog[d];
                }

                if (known)
                {
                    continue;
                }

                Transform root = null;
                for (int p = 0; root == null && p < catalog[d].Paths.Length; p++)
                {
                    root = FromTop(catalog[d].Paths[p]);
                }

                if (root == null)
                {
                    continue;
                }

                Binding binding;
                if (!Bindings.TryGetValue(root.gameObject.GetInstanceID(), out binding))
                {
                    Forget();
                    binding = new Binding(catalog[d], root);
                    Transform switched = catalog[d].Switch.Length == 0 ? root : FromTop(catalog[d].Switch);
                    binding.SwitchObject = switched == null ? null : switched.gameObject;
                    binding.CallObject = catalog[d].Call == null ? null : FromTop(catalog[d].Call.Path);
                    binding.CallTo = catalog[d].CallTo == null ? null : FromTop(catalog[d].CallTo.Path);
                    binding.StruckObject = catalog[d].Struck == null ? null : FromTop(catalog[d].Struck.Path);
                    Bindings[root.gameObject.GetInstanceID()] = binding;
                }

                fighters.Add(new Fighter(binding));
                log.Info(Category, "Fighter '" + catalog[d].Name + "' found at '" + Path(root) + "' (" + (root.gameObject.activeInHierarchy ? "out" : "not out") + "); "
                    + (catalog[d].DecidedByHost ? "the host's game says whether he is out." : "the game that runs him says whether he is out."));
            }
        }

        // The bindings of objects the game has destroyed (a world loaded again).
        private static void Forget()
        {
            var gone = new List<int>();
            foreach (KeyValuePair<int, Binding> pair in Bindings)
            {
                // Unity's null check is also true for a destroyed object.
                if (pair.Value.Root == null)
                {
                    gone.Add(pair.Key);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                Bindings.Remove(gone[i]);
            }
        }

        private static string Path(Transform at)
        {
            string path = at.name;
            for (Transform up = at.parent; up != null; up = up.parent)
            {
                path = up.name + "/" + path;
            }

            return path;
        }

        // What the fighter is made of, once his object has been switched on (before, his machines have not woken up).
        private void Bind(Fighter one)
        {
            Binding fighter = one.B;
            if (fighter.Bound || !fighter.Root.gameObject.activeInHierarchy)
            {
                return;
            }

            fighter.Bound = true;
            FighterDefinition definition = fighter.Definition;
            Transform root = fighter.Root;
            var kept = new List<Transform>();
            for (int i = 0; i < definition.KeepBelow.Length; i++)
            {
                Transform below = root.Find(definition.KeepBelow[i]);
                if (below != null)
                {
                    kept.Add(below);
                }
            }

            PlayMakerFSM[] all = root.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (Array.IndexOf(definition.KeepMachines, all[i].FsmName) >= 0 || Below(all[i].transform, kept))
                {
                    continue;
                }

                fighter.Machines.Add(all[i]);
                if (all[i].transform == root && all[i].FsmName == definition.Brain)
                {
                    fighter.Brain = all[i];
                }
            }

            for (int i = 0; i < definition.Animations.Length; i++)
            {
                Transform at = root.Find(definition.Animations[i]);
                Animation animation = at == null ? null : at.GetComponent<Animation>();
                var names = new List<string>();
                if (animation != null)
                {
                    foreach (AnimationState state in animation)
                    {
                        names.Add(state.name);
                    }
                }

                names.Sort(string.CompareOrdinal);
                fighter.Animations.Add(new Animated { Animation = animation, Clips = names.ToArray() });
            }

            for (int i = 0; i < definition.Shown.Length; i++)
            {
                Transform at = root.Find(definition.Shown[i]);
                fighter.Shown.Add(at == null ? null : at.gameObject);
            }

            for (int i = 0; i < definition.Loose.Length; i++)
            {
                Transform at = root.Find(definition.Loose[i]);
                fighter.Loose.Add(at == null ? null : at.gameObject);
            }

            for (int i = 0; i < definition.Hits.Length; i++)
            {
                FighterMachine spec = definition.Hits[i];
                Transform at = spec.Path.Length == 0 ? root : root.Find(spec.Path);
                PlayMakerFSM machine = at == null ? null : DoorSync.FindMachine(at, spec.Machine);
                var hit = new Hit { Machine = machine, State = spec.State };
                fighter.Hits.Add(hit);
                if (machine == null || !DoorSync.HasState(machine, spec.State))
                {
                    log.Warning(Category, "Fighter '" + definition.Name + "': no '" + spec.Path + " :: " + spec.Machine + "' with '" + spec.State + "'; a punch there is each game's own.");
                    hit.Machine = null;
                    continue;
                }

                FsmGameObject fist = machine.FsmVariables.FindFsmGameObject(definition.Fist);
                hit.Fist = fist == null ? null : fist.Value;
                int which = i;
                VehicleContactProbe probe = machine.gameObject.AddComponent<VehicleContactProbe>();
                probe.Hit = collision => Static(sync => sync.OnCollision(fighter, which, collision));
            }

            FighterMachine down = definition.Down;
            Transform downAt = down.Path.Length == 0 ? root : root.Find(down.Path);
            fighter.Down = downAt == null ? null : DoorSync.FindMachine(downAt, down.Machine);
            if (fighter.Down == null || !DoorSync.HasState(fighter.Down, down.State))
            {
                fighter.Down = null;
                log.Warning(Category, "Fighter '" + definition.Name + "': no '" + down.Path + " :: " + down.Machine + "' with '" + down.State + "'; going down is each game's own.");
            }

            fighter.Asleep.AddRange(fighter.Machines);
            WakeUp(one);

            log.Info(Category, "Fighter '" + definition.Name + "': " + fighter.Machines.Count + " machine(s) run by one game, " + fighter.Sounds.Count + " sound(s) shared, "
                + fighter.Hits.Count + " part(s) to punch, " + fighter.Animations.Count + " animation(s), brain " + (fighter.Brain == null ? "NOT found" : "found") + ".");
        }

        // A machine whose object has never been on has not read its states yet (PlayMaker does it in Awake).
        private static bool Awake(PlayMakerFSM machine)
        {
            // Unity's null check is also true for a destroyed object.
            return machine != null && machine.Fsm != null && machine.Fsm.Initialized;
        }

        // His machines that have woken up since: their sounds hooked, and the state that knocks him down.
        private void WakeUp(Fighter one)
        {
            Binding fighter = one.B;
            for (int i = fighter.Asleep.Count - 1; i >= 0; i--)
            {
                PlayMakerFSM machine = fighter.Asleep[i];
                if (machine == null)
                {
                    fighter.Asleep.RemoveAt(i);
                }
                else if (Awake(machine))
                {
                    fighter.Asleep.RemoveAt(i);
                    HookSounds(fighter, machine, fighter.Machines.IndexOf(machine));
                }
            }

            if (!fighter.DownHooked && Awake(fighter.Down))
            {
                fighter.DownHooked = true;
                fighter.Down.FsmInject(fighter.Definition.Down.State, () => Static(sync => sync.OnDown(fighter)), false, 0, false);
            }
        }

        private static void Static(Action<FighterSync> what)
        {
            FighterSync sync = active;
            if (sync != null)
            {
                what(sync);
            }
        }

        private Fighter Of(Binding binding)
        {
            for (int i = 0; i < fighters.Count; i++)
            {
                if (fighters[i].B == binding)
                {
                    return fighters[i];
                }
            }

            return null;
        }

        private static bool Below(Transform at, List<Transform> roots)
        {
            for (Transform step = at; step != null; step = step.parent)
            {
                if (roots.Contains(step))
                {
                    return true;
                }
            }

            return false;
        }

        // After each sound of the machine, a hook of the mod's that tells it from the game that runs him.
        // A sound is known by its machine (its place among his, or StruckMachine), its state and its action, the same in every game.
        private static void HookSounds(Binding fighter, PlayMakerFSM machine, int machineIndex)
        {
            FsmState[] states = machine.FsmStates;
            for (int s = 0; states != null && s < states.Length && s < 256; s++)
            {
                FsmStateAction[] actions = states[s].Actions;
                bool stopped = false;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    if (actions[a] is MasterAudioStopAllOfSound)
                    {
                        stopped = true;
                    }

                    var play = actions[a] as MasterAudioPlaySound;
                    if (play == null || !play.Enabled || a >= 256 || !fighter.Hooked.Add(play))
                    {
                        continue;
                    }

                    var sound = new Sound { Machine = machine, Action = play, StopFirst = stopped, Struck = machineIndex == StruckMachine };
                    int key = (machineIndex << 16) | (s << 8) | a;
                    int at = Array.IndexOf(states[s].Actions, play) + 1;
                    if (!machine.FsmInject(states[s].Name, () => Static(sync => sync.OnSound(fighter, key)), false, at, false))
                    {
                        fighter.Hooked.Remove(play);
                        continue;
                    }

                    fighter.Sounds[key] = sound;
                }
            }
        }

        // The state that calls him out: in a guest, told to the host.
        private void HookCall(Fighter one)
        {
            Binding fighter = one.B;
            // Unity's null check is also true for a destroyed object.
            if (fighter.CallHooked || fighter.CallObject == null || !fighter.CallObject.gameObject.activeInHierarchy)
            {
                return;
            }

            fighter.CallHooked = true;
            FighterMachine call = fighter.Definition.Call;
            PlayMakerFSM machine = DoorSync.FindMachine(fighter.CallObject, call.Machine);
            if (machine == null || !DoorSync.HasState(machine, call.State) || !machine.FsmInject(call.State, () => Static(sync => sync.OnCall(fighter)), false, 0, false))
            {
                log.Warning(Category, "Fighter '" + fighter.Definition.Name + "': no '" + call.Path + " :: " + call.Machine + "' with '" + call.State + "'; a guest there does not call him out.");
            }
        }

        // The player's machine he hits: its sounds are his too, where he is.
        private void HookStruck(Fighter one)
        {
            Binding fighter = one.B;

            // Unity's null check is also true for a destroyed object.
            if (fighter.StruckHooked || fighter.StruckObject == null || !fighter.Bound)
            {
                return;
            }

            GameObject struck = fighter.StruckObject.gameObject;
            if (!struck.activeInHierarchy)
            {
                if (struck.activeSelf || struck.transform.parent == null || !struck.transform.parent.gameObject.activeInHierarchy)
                {
                    return;
                }

                // Switched off until he hits: on and off at once, its machine wakes up (its actions can be read) and
                // starts no state (PlayMaker starts it in Start, which comes only with the next frame it is on).
                struck.SetActive(true);
                struck.SetActive(false);
            }

            fighter.StruckHooked = true;
            PlayMakerFSM machine = DoorSync.FindMachine(fighter.StruckObject, fighter.Definition.Struck.Machine);
            fighter.Struck = machine;
            Transform at = fighter.Root.Find(fighter.Definition.StruckAt);
            fighter.StruckAt = at;
            if (machine == null)
            {
                return;
            }

            if (!Awake(machine))
            {
                fighter.StruckHooked = false;
                return;
            }

            int before = fighter.Sounds.Count;
            HookSounds(fighter, machine, StruckMachine);
            log.Info(Category, "Fighter '" + fighter.Definition.Name + "': " + (fighter.Sounds.Count - before) + " sound(s) of the player he hits shared.");
        }

        private bool IsOut(Fighter fighter)
        {
            // Unity's null check is also true for a destroyed object.
            return fighter.Root != null && fighter.Root.gameObject.activeSelf && (fighter.SwitchObject == null || fighter.SwitchObject.activeSelf);
        }

        private bool Decides(Fighter fighter)
        {
            return fighter.Definition.DecidedByHost ? host : Runs(fighter);
        }

        // Whether he is out: said by the game that decides it, followed by the others.
        private void WatchOut(Fighter fighter)
        {
            bool now = IsOut(fighter);
            if (Decides(fighter))
            {
                if (!fighter.OutTold || now != fighter.OutSaid)
                {
                    fighter.OutTold = true;
                    fighter.OutSaid = now;
                    current.Set(Prefix + fighter.Definition.Name + "/on", new[] { now ? 1 : 0 });
                    log.Info(Category, "Fighter '" + fighter.Definition.Name + "': " + (now ? "out" : "not out") + " in this game, which says it.");
                }

                return;
            }

            // The others follow it; the game that runs him only when it changes (his own game may take him away: he robs the watch).
            if (fighter.OutHeard && (!Runs(fighter) || fighter.OutChanged) && now != fighter.OutSaid)
            {
                SetOut(fighter, fighter.OutSaid);
            }

            fighter.OutChanged = false;
        }

        private void SetOut(Fighter fighter, bool on)
        {
            GameObject switched = fighter.SwitchObject ?? fighter.Root.gameObject;
            Transform parent = on ? FromTop(fighter.Definition.OutParent) : null;
            if (parent != null && fighter.Root.parent != parent)
            {
                // Where the game that called him out has him hang: his machines place him in its space.
                fighter.Root.SetParent(parent, true);
            }

            if (on && !fighter.Root.gameObject.activeSelf)
            {
                fighter.Root.gameObject.SetActive(true);
            }

            switched.SetActive(on);
            if (on)
            {
                // Paused before his machines run a frame here, unless this game runs him.
                Bind(fighter);
                if (!Runs(fighter))
                {
                    PauseAll(fighter);
                }
            }

            log.Info(Category, "Fighter '" + fighter.Definition.Name + "': " + (on ? "out" : "not out") + " here, as the game that says it has him.");
        }

        // The host: whose game runs him.
        private void ChooseRunner(Fighter fighter)
        {
            Vector3 at = fighter.Root.position;
            var candidates = new List<FighterCandidate>();
            if (player != null)
            {
                candidates.Add(new FighterCandidate(Me, (player.position - at).magnitude));
            }

            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                PlayerPose pose = remote.Shown;
                if (remote.InWorld && remote.Visible && vehicles.ToWorld(ref pose))
                {
                    candidates.Add(new FighterCandidate(remote.Id, (new Vector3(pose.X, pose.Y, pose.Z) - at).magnitude));
                }
            }

            bool calm = true;
            if (Runs(fighter))
            {
                calm = IsCalm(fighter);
            }
            else if (fighter.HavePose && fighter.Pose.Runner == fighter.Runner)
            {
                calm = fighter.Pose.Calm;
            }

            int chosen = FighterOwnership.Choose(fighter.Runner, calm, candidates);
            if (chosen == fighter.Runner)
            {
                return;
            }

            current.Set(Prefix + fighter.Definition.Name + "/runner", new[] { chosen + 1 });
            SetRunner(fighter, chosen);
        }

        private bool IsCalm(Fighter fighter)
        {
            // Unity's null check is also true for a destroyed object.
            return fighter.Brain == null || !fighter.Brain.enabled || Array.IndexOf(fighter.Definition.Calm, fighter.Brain.ActiveStateName) >= 0;
        }

        private void SetRunner(Fighter fighter, int runner)
        {
            int before = fighter.Runner;
            fighter.Runner = runner;
            bool was = before >= 0 && before == Me;
            bool now = Runs(fighter);
            log.Info(Category, "Fighter '" + fighter.Definition.Name + "': run by " + (runner < 0 ? "nobody" : now ? "this game" : "player " + runner.ToString(CultureInfo.InvariantCulture))
                + (before < 0 ? "." : " (was " + (was ? "this game" : "player " + before.ToString(CultureInfo.InvariantCulture)) + ")."));
            if (now && !was)
            {
                TakeOver(fighter);
            }
            else if (was && !now)
            {
                PauseAll(fighter);
                fighter.Placed = false;
            }
        }

        // This game runs him from now: where the last one had him, calm.
        private void TakeOver(Fighter fighter)
        {
            if (!Decides(fighter) && fighter.OutHeard && fighter.OutSaid && !IsOut(fighter))
            {
                SetOut(fighter, true);
            }

            bool wasPaused = fighter.Paused.Count > 0;
            if (fighter.HavePose && fighter.Root.gameObject.activeInHierarchy)
            {
                fighter.Root.position = new Vector3(fighter.Pose.X, fighter.Pose.Y, fighter.Pose.Z);
                fighter.Root.rotation = Quaternion.Euler(0f, fighter.Pose.Yaw, 0f);
            }

            for (int i = 0; i < fighter.Paused.Count; i++)
            {
                fighter.Paused[i].Resume();
            }

            fighter.Paused.Clear();

            // Unity's null check is also true for a destroyed object.
            if (wasPaused && fighter.Brain != null && fighter.Root.gameObject.activeInHierarchy && DoorSync.HasState(fighter.Brain, fighter.Definition.CalmEntry))
            {
                if (!DoorSync.HasGlobalTransition(fighter.Brain, CalmEvent))
                {
                    fighter.Brain.AddGlobalTransition(CalmEvent, fighter.Definition.CalmEntry);
                }

                fighter.Brain.SendEvent(CalmEvent);
            }
        }

        private void PauseAll(Fighter fighter)
        {
            for (int i = 0; i < fighter.Machines.Count; i++)
            {
                PlayMakerFSM machine = fighter.Machines[i];

                // Unity's null check is also true for a destroyed object.
                if (machine != null && machine.enabled)
                {
                    fighter.Paused.Add(new PausedMachine(machine));
                }
            }
        }

        // The game that runs him: where he is, when that changes.
        private void TellPose(Fighter fighter, double now)
        {
            if (now - fighter.PoseToldAt < PoseInterval)
            {
                return;
            }

            var pose = new FighterPose
            {
                Runner = Me,
                X = fighter.Root.position.x,
                Y = fighter.Root.position.y,
                Z = fighter.Root.position.z,
                Yaw = fighter.Root.eulerAngles.y,
                Calm = IsCalm(fighter),
                Out = IsOut(fighter),
            };
            for (int i = 0; i < fighter.Animations.Count; i++)
            {
                pose.SetClip(i, ReadClip(fighter.Animations[i]));
            }

            int bit = 0;
            for (int i = 0; i < fighter.Shown.Count; i++, bit++)
            {
                // Unity's null check is also true for a destroyed object.
                if (fighter.Shown[i] != null && fighter.Shown[i].activeSelf)
                {
                    pose.Things |= (byte)(1 << bit);
                }
            }

            for (int i = 0; i < fighter.Loose.Count; i++, bit++)
            {
                if (fighter.Loose[i] != null && fighter.Loose[i].GetComponent<FixedJoint>() == null)
                {
                    pose.Things |= (byte)(1 << bit);
                }
            }

            FighterPose last = fighter.Told;
            bool same = fighter.PoseSerial > 0
                && (new Vector3(pose.X - last.X, pose.Y - last.Y, pose.Z - last.Z)).sqrMagnitude < PoseStep * PoseStep
                && Mathf.Abs(Mathf.DeltaAngle(pose.Yaw, last.Yaw)) < YawStep
                && pose.Clip0 == last.Clip0 && pose.Clip1 == last.Clip1 && pose.Clip2 == last.Clip2 && pose.Clip3 == last.Clip3
                && pose.Calm == last.Calm && pose.Out == last.Out && pose.Things == last.Things;
            if (same && now - fighter.PoseToldAt < PoseKeepAlive)
            {
                return;
            }

            if (same && !pose.Out)
            {
                // Not out and still: nothing to keep alive.
                return;
            }

            fighter.PoseSerial++;
            pose.Serial = fighter.PoseSerial;
            fighter.Told = pose;
            fighter.PoseToldAt = now;
            current.Set(Prefix + fighter.Definition.Name + "/pose", pose.Pack());
        }

        // The clip that sounds loudest, as its place plus one, with a count of how often it began (a punch again).
        private static byte ReadClip(Animated animated)
        {
            // Unity's null check is also true for a destroyed object.
            if (animated.Animation == null)
            {
                return 0;
            }

            int best = -1;
            float weight = 0f;
            for (int i = 0; i < animated.Clips.Length && i < (1 << ClipBits) - 1; i++)
            {
                AnimationState state = animated.Animation[animated.Clips[i]];
                if (state != null && state.enabled && state.weight > weight)
                {
                    best = i;
                    weight = state.weight;
                }
            }

            if (best < 0)
            {
                return 0;
            }

            float time = animated.Animation[animated.Clips[best]].time;
            if (best != animated.ReadClip || time + 0.05f < animated.ReadTime)
            {
                animated.Started = (animated.Started + 1) & 7;
            }

            animated.ReadClip = best;
            animated.ReadTime = time;
            return (byte)((best + 1) | (animated.Started << ClipBits));
        }

        // A game that does not run him: his machines paused, and he is where the runner's game has him.
        private void Follow(Fighter fighter, float dt)
        {
            if (!fighter.Bound || !fighter.Root.gameObject.activeInHierarchy)
            {
                return;
            }

            for (int i = 0; i < fighter.Machines.Count; i++)
            {
                PlayMakerFSM machine = fighter.Machines[i];

                // Unity's null check is also true for a destroyed object. A machine switched on since (by the game) is paused too.
                if (machine != null && machine.enabled && !IsPaused(fighter, machine))
                {
                    fighter.Paused.Add(new PausedMachine(machine));
                }
            }

            if (!fighter.HavePose || fighter.Pose.Runner != fighter.Runner)
            {
                return;
            }

            FighterPose pose = fighter.Pose;
            var target = new Vector3(pose.X, pose.Y, pose.Z);
            Quaternion heading = Quaternion.Euler(0f, pose.Yaw, 0f);
            if (!fighter.Placed || (fighter.Root.position - target).sqrMagnitude > SnapDistance * SnapDistance)
            {
                fighter.Root.position = target;
                fighter.Root.rotation = heading;
                fighter.Placed = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-Smoothing * dt);
                fighter.Root.position = Vector3.Lerp(fighter.Root.position, target, k);
                fighter.Root.rotation = Quaternion.Slerp(fighter.Root.rotation, heading, k);
            }

            for (int i = 0; i < fighter.Animations.Count; i++)
            {
                ShowClip(fighter.Animations[i], pose.Clip(i), i == 0);
            }

            int bit = 0;
            for (int i = 0; i < fighter.Shown.Count; i++, bit++)
            {
                bool on = (pose.Things & (1 << bit)) != 0;

                // Unity's null check is also true for a destroyed object.
                if (fighter.Shown[i] != null && fighter.Shown[i].activeSelf != on)
                {
                    fighter.Shown[i].SetActive(on);
                }
            }

            for (int i = 0; i < fighter.Loose.Count; i++, bit++)
            {
                FixedJoint joint = fighter.Loose[i] == null ? null : fighter.Loose[i].GetComponent<FixedJoint>();
                if ((pose.Things & (1 << bit)) != 0 && joint != null)
                {
                    // His own machine there lets it fall, as in the game that runs him.
                    UnityEngine.Object.Destroy(joint);
                }
            }
        }

        private static bool IsPaused(Fighter fighter, PlayMakerFSM machine)
        {
            for (int i = 0; i < fighter.Paused.Count; i++)
            {
                if (fighter.Paused[i].Machine == machine)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ShowClip(Animated animated, byte clip, bool body)
        {
            // Unity's null check is also true for a destroyed object.
            if (clip == animated.Shown || animated.Animation == null)
            {
                return;
            }

            animated.Shown = clip;
            int index = (clip & ((1 << ClipBits) - 1)) - 1;
            if (index < 0 || index >= animated.Clips.Length || animated.Animation[animated.Clips[index]] == null)
            {
                return;
            }

            string name = animated.Clips[index];
            if (body)
            {
                animated.Animation.CrossFade(name, 0.3f, PlayMode.StopAll);
            }
            else
            {
                // From its start, even if it is the clip it played last (a punch again).
                animated.Animation[name].time = 0f;
                animated.Animation.Play(name, PlayMode.StopAll);
            }
        }

        private void OnCollision(Binding binding, int which, Collision collision)
        {
            Fighter fighter = Of(binding);
            if (fighter == null)
            {
                return;
            }

            Hit hit = which < fighter.Hits.Count ? fighter.Hits[which] : null;

            // Unity's null check is also true for a destroyed object.
            if (active != this || current == null || hit == null || hit.Fist == null || collision == null || collision.collider == null
                || collision.collider.gameObject != hit.Fist || Runs(fighter))
            {
                return;
            }

            Punched(fighter, which, false);
        }

        private void Punched(Fighter fighter, int which, bool test)
        {
            Hit hit = fighter.Hits[which];
            if (!test && clock - hit.PunchedAt < PunchGap)
            {
                return;
            }

            hit.PunchedAt = clock;
            if (Runs(fighter))
            {
                Count(fighter, which, Me);
                return;
            }

            fighter.PunchSerial++;
            current.Set(Prefix + fighter.Definition.Name + "/punch/" + Me.ToString(CultureInfo.InvariantCulture), new[] { fighter.PunchSerial, which });
            log.Info(Category, "Fighter '" + fighter.Definition.Name + "': this game's player punched him (part " + which + "); told to the game that runs him.");
        }

        // The game that runs him: a punch counts as one of its own player's.
        private void Count(Fighter fighter, int which, byte by)
        {
            Hit hit = which >= 0 && which < fighter.Hits.Count ? fighter.Hits[which] : null;

            // Unity's null check is also true for a destroyed object.
            if (hit == null || hit.Machine == null || !hit.Machine.enabled || !fighter.Root.gameObject.activeInHierarchy)
            {
                return;
            }

            string name = PunchEvent + " " + hit.State;
            if (!DoorSync.HasGlobalTransition(hit.Machine, name))
            {
                hit.Machine.AddGlobalTransition(name, hit.State);
            }

            hit.Machine.SendEvent(name);
            fighter.PunchesCounted++;
            log.Info(Category, "Fighter '" + fighter.Definition.Name + "': a punch of player " + by.ToString(CultureInfo.InvariantCulture) + " counted (part " + which + ").");
        }

        private void OnSound(Binding binding, int key)
        {
            Fighter fighter = Of(binding);
            Sound sound;
            if (fighter == null || active != this || current == null || manager.Session.State != SessionState.Connected || !Runs(fighter) || !fighter.Sounds.TryGetValue(key, out sound))
            {
                return;
            }

            if (sound.Struck && !StruckBy(fighter))
            {
                return;
            }

            var values = new int[3 + SoundTextNumbers];
            string variation = sound.Action.variationName == null ? string.Empty : sound.Action.variationName.Value ?? string.Empty;
            if (!TallyText.TryPack(variation, values, 3, SoundTextNumbers))
            {
                return;
            }

            fighter.SoundSerial++;
            values[0] = Me + 1;
            values[1] = fighter.SoundSerial;
            values[2] = key;
            current.Set(Prefix + fighter.Definition.Name + "/sound", values);
        }

        // Whether the player's machine is hit by this fighter (it has one variable for both).
        private bool StruckBy(Fighter fighter)
        {
            // Unity's null check is also true for a destroyed object.
            if (fighter.Struck == null)
            {
                return false;
            }

            FsmGameObject by = fighter.Struck.FsmVariables.FindFsmGameObject(fighter.Definition.StruckBy);
            return by != null && by.Value == fighter.Root.gameObject;
        }

        private void PlaySound(Fighter fighter, int key, string variation)
        {
            Sound sound;
            if (!fighter.Sounds.TryGetValue(key, out sound))
            {
                return;
            }

            MasterAudioPlaySound play = sound.Action;
            Transform at;
            if (sound.Struck)
            {
                at = fighter.StruckAt;
            }
            else
            {
                GameObject target = sound.Machine == null || sound.Machine.Fsm == null ? null : sound.Machine.Fsm.GetOwnerDefaultTarget(play.gameObject);
                at = target == null ? null : target.transform;
            }

            string group = play.soundGroupName == null ? null : play.soundGroupName.Value;

            // Unity's null check is also true for a destroyed object.
            if (at == null || string.IsNullOrEmpty(group))
            {
                return;
            }

            try
            {
                if (sound.StopFirst)
                {
                    MasterAudio.StopAllOfSound(group);
                }

                float? pitch = play.useFixedPitch != null && play.useFixedPitch.Value && play.fixedPitch != null ? play.fixedPitch.Value : (float?)null;
                fighter.SoundsPlayed++;
                MasterAudio.PlaySound3DAndForget(
                    group,
                    at,
                    play.attachToGameObject != null && play.attachToGameObject.Value,
                    play.volume == null ? 1f : play.volume.Value,
                    pitch,
                    play.delaySound == null ? 0f : play.delaySound.Value,
                    string.IsNullOrEmpty(variation) ? null : variation);
            }
            catch (Exception exception)
            {
                log.Warning(Category, "Fighter '" + fighter.Definition.Name + "': the sound '" + group + "/" + variation + "' could not be played: " + exception.Message);
            }
        }

        private void OnDown(Binding binding)
        {
            Fighter fighter = Of(binding);
            if (fighter == null || active != this || current == null || manager.Session.State != SessionState.Connected || !Runs(fighter))
            {
                return;
            }

            fighter.DownSerial++;
            current.Set(Prefix + fighter.Definition.Name + "/down", new[] { Me + 1, fighter.DownSerial });
            log.Info(Category, "Fighter '" + fighter.Definition.Name + "': down in this game; the others knock theirs down too.");
        }

        private void KnockDown(Fighter fighter)
        {
            // Unity's null check is also true for a destroyed object.
            if (fighter.Down == null || fighter.Root == null || !fighter.Root.gameObject.activeInHierarchy)
            {
                return;
            }

            if (fighter.HavePose)
            {
                fighter.Root.position = new Vector3(fighter.Pose.X, fighter.Pose.Y, fighter.Pose.Z);
                fighter.Root.rotation = Quaternion.Euler(0f, fighter.Pose.Yaw, 0f);
            }

            for (int i = 0; i < fighter.Paused.Count; i++)
            {
                if (fighter.Paused[i].Machine == fighter.Down)
                {
                    fighter.Paused[i].Resume();
                    fighter.Paused.RemoveAt(i);
                    break;
                }
            }

            string state = fighter.Definition.Down.State;
            if (!DoorSync.HasGlobalTransition(fighter.Down, DownEvent))
            {
                fighter.Down.AddGlobalTransition(DownEvent, state);
            }

            fighter.Down.SendEvent(DownEvent);
            log.Info(Category, "Fighter '" + fighter.Definition.Name + "': knocked down here, as in the game that runs him.");
        }

        private void OnCall(Binding binding)
        {
            Fighter fighter = Of(binding);
            if (fighter == null || active != this || current == null || manager.Session.State != SessionState.Connected || host)
            {
                return;
            }

            fighter.CallSerial++;
            current.Set(Prefix + fighter.Definition.Name + "/call/" + Me.ToString(CultureInfo.InvariantCulture), new[] { fighter.CallSerial });
            log.Info(Category, "Fighter '" + fighter.Definition.Name + "': called out in this guest's game; told to the host.");
        }

        private void OnStateWanted(string name, int[] values)
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return;
            }

            heard.Add(new KeyValuePair<string, int[]>(name, values));
            heardCatchingUp.Add(current != null && current.CatchingUp);
        }

        private void Hear(string name, int[] values, bool catchingUp)
        {
            string rest = name.Substring(Prefix.Length);
            int slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                return;
            }

            string fighterName = rest.Substring(0, slash);
            string what = rest.Substring(slash + 1);
            Fighter fighter = null;
            for (int i = 0; i < fighters.Count; i++)
            {
                if (fighters[i].Definition.Name == fighterName)
                {
                    fighter = fighters[i];
                }
            }

            if (fighter == null || fighter.Root == null || values == null || values.Length == 0)
            {
                return;
            }

            if (what == "runner")
            {
                if (values[0] - 1 != fighter.Runner)
                {
                    SetRunner(fighter, values[0] - 1);
                }
            }
            else if (what == "on")
            {
                if (!Decides(fighter))
                {
                    bool on = values[0] != 0;
                    fighter.OutChanged = !fighter.OutHeard || on != fighter.OutSaid;
                    fighter.OutHeard = true;
                    fighter.OutSaid = on;
                }
            }
            else if (what == "pose")
            {
                FighterPose pose;
                if (FighterPose.TryUnpack(values, out pose) && pose.Runner != Me)
                {
                    bool wasOut = fighter.HavePose && fighter.Pose.Runner == pose.Runner && fighter.Pose.Out;
                    fighter.Pose = pose;
                    fighter.HavePose = true;
                    fighter.PosesHeard++;
                    if (wasOut && !pose.Out && pose.Runner == fighter.Runner && Decides(fighter) && IsOut(fighter) && !catchingUp)
                    {
                        // The game that runs him took him away (he robbed the watch): so is it here, which says it.
                        log.Info(Category, "Fighter '" + fighter.Definition.Name + "': the game that runs him took him away; not out here either.");
                        (fighter.SwitchObject ?? fighter.Root.gameObject).SetActive(false);
                    }
                }
            }
            else if (what == "sound")
            {
                if (!catchingUp && values.Length == 3 + SoundTextNumbers && values[0] - 1 != Me && !Runs(fighter))
                {
                    PlaySound(fighter, values[2], TallyText.Unpack(values, 3, SoundTextNumbers));
                }
            }
            else if (what == "down")
            {
                if (!catchingUp && values.Length == 2 && values[0] - 1 != Me && !Runs(fighter))
                {
                    KnockDown(fighter);
                }
            }
            else if (what.StartsWith("punch/", StringComparison.Ordinal))
            {
                byte by;
                if (!catchingUp && values.Length == 2 && byte.TryParse(what.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out by) && by != Me && Runs(fighter))
                {
                    Count(fighter, values[1], by);
                }
            }
            else if (what.StartsWith("call/", StringComparison.Ordinal))
            {
                byte by;
                if (!catchingUp && host && byte.TryParse(what.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out by) && by != Me)
                {
                    Call(fighter, by);
                }
            }
        }

        // The host: what the state that calls him out does, for a guest whose game ran it.
        private void Call(Fighter fighter, byte by)
        {
            // Unity's null check is also true for a destroyed object.
            PlayMakerFSM to = fighter.CallTo == null ? null : DoorSync.FindMachine(fighter.CallTo, fighter.Definition.CallTo.Machine);
            if (to == null)
            {
                return;
            }

            to.SendEvent(fighter.Definition.CallEvent);
            log.Info(Category, "Fighter '" + fighter.Definition.Name + "': called out by player " + by.ToString(CultureInfo.InvariantCulture) + "'s game ('" + fighter.Definition.CallEvent + "' to '"
                + fighter.Definition.CallTo.Path + " :: " + fighter.Definition.CallTo.Machine + "', in '" + to.ActiveStateName + "').");
        }

        // His objects and what the mod hooked into them.
        private sealed class Binding
        {
            public Binding(FighterDefinition definition, Transform root)
            {
                Definition = definition;
                Root = root;
            }

            public FighterDefinition Definition { get; }

            public Transform Root { get; }

            public GameObject SwitchObject;
            public Transform CallObject;
            public Transform CallTo;
            public Transform StruckObject;
            public PlayMakerFSM Struck;
            public Transform StruckAt;
            public bool Bound;
            public bool CallHooked;
            public bool StruckHooked;
            public PlayMakerFSM Brain;
            public PlayMakerFSM Down;
            public readonly List<PlayMakerFSM> Machines = new List<PlayMakerFSM>();
            public readonly List<Animated> Animations = new List<Animated>();
            public readonly List<GameObject> Shown = new List<GameObject>();
            public readonly List<GameObject> Loose = new List<GameObject>();
            public readonly List<Hit> Hits = new List<Hit>();
            public readonly Dictionary<int, Sound> Sounds = new Dictionary<int, Sound>();
            public readonly HashSet<FsmStateAction> Hooked = new HashSet<FsmStateAction>();
            public readonly List<PlayMakerFSM> Asleep = new List<PlayMakerFSM>();
            public bool DownHooked;
        }

        // How he is shared in this session.
        private sealed class Fighter
        {
            public Fighter(Binding binding)
            {
                B = binding;
                for (int i = 0; i < binding.Animations.Count; i++)
                {
                    binding.Animations[i].Shown = 0;
                }
            }

            public Binding B { get; }

            public FighterDefinition Definition
            {
                get { return B.Definition; }
            }

            public Transform Root
            {
                get { return B.Root; }
            }

            public GameObject SwitchObject
            {
                get { return B.SwitchObject; }
            }

            public Transform CallTo
            {
                get { return B.CallTo; }
            }

            public PlayMakerFSM Struck
            {
                get { return B.Struck; }
            }

            public Transform StruckAt
            {
                get { return B.StruckAt; }
            }

            public bool Bound
            {
                get { return B.Bound; }
            }

            public PlayMakerFSM Brain
            {
                get { return B.Brain; }
            }

            public PlayMakerFSM Down
            {
                get { return B.Down; }
            }

            public List<PlayMakerFSM> Machines
            {
                get { return B.Machines; }
            }

            public List<Animated> Animations
            {
                get { return B.Animations; }
            }

            public List<GameObject> Shown
            {
                get { return B.Shown; }
            }

            public List<GameObject> Loose
            {
                get { return B.Loose; }
            }

            public List<Hit> Hits
            {
                get { return B.Hits; }
            }

            public Dictionary<int, Sound> Sounds
            {
                get { return B.Sounds; }
            }

            public readonly List<PausedMachine> Paused = new List<PausedMachine>();
            public int Runner = -1;
            public FighterPose Pose;
            public bool HavePose;
            public bool Placed;
            public FighterPose Told;
            public double PoseToldAt = double.NegativeInfinity;
            public int PoseSerial;
            public long PosesHeard;
            public int SoundSerial;
            public long SoundsPlayed;
            public int PunchSerial;
            public long PunchesCounted;
            public int DownSerial;
            public int CallSerial;
            public bool OutTold;
            public bool OutHeard;
            public bool OutSaid;
            public bool OutChanged;

            public void Release()
            {
                for (int i = 0; i < Paused.Count; i++)
                {
                    Paused[i].Resume();
                }

                Paused.Clear();
            }
        }

        private sealed class Animated
        {
            public Animation Animation;
            public string[] Clips;
            public byte Shown;
            public int ReadClip = -1;
            public float ReadTime;
            public int Started;
        }

        private sealed class Hit
        {
            public PlayMakerFSM Machine;
            public string State;
            public GameObject Fist;
            public double PunchedAt = double.NegativeInfinity;
        }

        private sealed class Sound
        {
            public PlayMakerFSM Machine;
            public MasterAudioPlaySound Action;
            public bool StopFirst;
            public bool Struck;
        }
    }
}
