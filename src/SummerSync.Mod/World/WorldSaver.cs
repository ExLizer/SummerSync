using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SummerSync.Core.Logging;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Saves the world without leaving it: what the game's "SAVE AND QUIT TO MENU" writes, and the game goes on.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Guardar sin salir al menú"): the toilets' button ("Button",
    /// states "Mute audio" and "Save all") broadcasts OPENBAG (the parts' boxes open into what they hold), writes
    /// "GameVersion", and 0.3 s later broadcasts SAVEGAME. Every machine that saves goes on it to a state that writes its
    /// values with Easy Save, a few through other states first (a tag built, a list walked), and then mostly to a state with
    /// no way out or to their loading again, which in a world that goes on would stop or double things (the graves made
    /// again). Then the button loads the menu, and the scene unloading saves what an <see cref="ES2Auto"/> holds (the
    /// Satsuma's panels), which their save states switch on.
    ///
    /// How: OPENBAG as the game sends it; 0.3 s later, for every machine SAVEGAME would reach, the actions of the states it
    /// would go through until it has written all it saves are run here, one after the other, without the machine changing
    /// state: what writes the save, reads into a variable or tests one, and nothing that moves, shows or sends anything. An
    /// event one of them sends (a test's) is held back and picks the next state. The machine's variables and the game's
    /// globals are put back as they were. The panels' <see cref="ES2Auto"/> save there and then.
    ///
    /// Each Easy Save write opens, reads and rewrites its whole file (<c>ES2.Save</c>, one <see cref="ES2Writer"/> a tag): about
    /// 1500 of them took the game 10 s (2026-10-18), as long as a session waits for a silent player. While the mod saves,
    /// each writer's one record stays in memory instead (the last for each tag), and at the end one writer a file writes them
    /// all, dropping the old ones in one pass.
    /// </summary>
    internal sealed class WorldSaver
    {
        private const string OpenEvent = "OPENBAG";
        private const string SaveEvent = "SAVEGAME";
        private const string FinishedEvent = "FINISHED";
        private const float OpenSeconds = 0.3f;

        // A machine that walks a list saves one thing per two states (the hay bales: 24); this is far past any.
        private const int MaxSteps = 20000;
        private const int MaxErrorsLogged = 5;

        // What a save state does that the mod runs: Easy Save's writes and its "Exists", and what only reads, works out or
        // tests a machine's own variables. Anything else (moving, showing, sending, making) is left out.
        private static readonly HashSet<string> Runs = new HashSet<string>
        {
            "Exists", "BuildString", "CInputGetAxisDeadZone",
            "SetIntValue", "SetFloatValue", "SetBoolValue", "SetStringValue", "SetVector3Value", "SetVector2Value",
            "SetColorValue", "SetObjectValue", "SetGameObject", "SetQuaternionValue",
            "FloatOperator", "FloatAdd", "FloatSubtract", "FloatMultiply", "FloatDivide", "FloatClamp", "FloatAbs",
            "FloatCompare", "FloatSwitch", "FloatChanged", "FloatSignTest",
            "IntAdd", "IntOperator", "IntClamp", "IntCompare", "IntSwitch", "IntChanged",
            "BoolTest", "BoolFlip", "BoolAllTrue", "BoolNoneTrue", "BoolAnyTrue", "BoolChanged", "BoolOperator",
            "StringCompare", "StringChanged", "StringContains",
            "ConvertIntToFloat", "ConvertFloatToInt", "ConvertIntToString", "ConvertFloatToString", "ConvertStringToInt",
            "ConvertBoolToInt", "ConvertBoolToFloat", "ConvertBoolToString",
            "RandomInt", "RandomFloat", "RandomBool",
            "GameObjectCompare", "ObjectCompare", "HasComponent", "GameObjectIsChildOf", "GameObjectIsNull",
            "ArrayListGet", "ArrayListCount", "ArrayListContains", "ArrayListIndexOf", "HashTableGet", "HashTableContains",
        };

        private static bool patched;
        private static bool patchFailed;

        // The machine whose save is being run, and the event one of its actions sent to it.
        private static Fsm sandbox;
        private static FsmEvent held;

        // While the mod saves: the records written so far, by file. Null otherwise.
        private static Dictionary<string, Batch> batches;
        private static FieldInfo tagsField;

        private readonly Logger log;
        private bool pending;
        private float dueAt;
        private PlayMakerFSM button;
        private Action<Report> done;

        public WorldSaver(Logger log)
        {
            this.log = log;
        }

        /// <summary>What a save did.</summary>
        internal sealed class Report
        {
            public int Machines;
            public int States;
            public int Actions;
            public int Panels;
            public int Files;
            public int Records;
            public int Errors;
            public int Endless;
            public long Milliseconds;
            public readonly Dictionary<string, int> Skipped = new Dictionary<string, int>();

            public override string ToString()
            {
                var text = new StringBuilder();
                text.Append(Machines).Append(" machine(s), ").Append(States).Append(" state(s), ").Append(Actions).Append(" action(s) run, ")
                    .Append(Panels).Append(" panel(s) of the Satsuma, ").Append(Records).Append(" record(s) in ").Append(Files).Append(" file(s), ")
                    .Append(Errors).Append(" error(s)");
                if (Endless > 0)
                {
                    text.Append(", ").Append(Endless).Append(" cut short");
                }

                text.Append(", in ").Append(Milliseconds.ToString(CultureInfo.InvariantCulture)).Append(" ms. Left out: ");
                var names = new List<string>(Skipped.Keys);
                names.Sort(StringComparer.Ordinal);
                for (int i = 0; i < names.Count; i++)
                {
                    text.Append(i == 0 ? string.Empty : ", ").Append(names[i]).Append(' ').Append(Skipped[names[i]]);
                }

                return text.Append('.').ToString();
            }
        }

        /// <summary>Whether the mod is running the game's save actions right now (in this call).</summary>
        public static bool Running
        {
            get { return batches != null; }
        }

        /// <summary>Whether a save has begun and not ended.</summary>
        public bool Busy
        {
            get { return pending; }
        }

        /// <summary>
        /// Begins a save: the boxes open now, and the save is written 0.3 s later, then <paramref name="saved"/> is called.
        /// <paramref name="from"/> is the toilet's button pressed (its "GameVersion"), or null for any. False if a save is
        /// already on its way, or the mod cannot hold back the events of the machines it runs (then nothing happens).
        /// </summary>
        public bool Begin(PlayMakerFSM from, Action<Report> saved)
        {
            if (pending || !Patch())
            {
                return false;
            }

            button = from;
            done = saved;
            pending = true;
            dueAt = Time.realtimeSinceStartup + OpenSeconds;
            PlayMakerFSM.BroadcastEvent(OpenEvent);
            log.Info(LogCategory.Fsm, "Save: the world is saved without leaving it; the parts' boxes open first (" + OpenEvent + ").");
            return true;
        }

        /// <summary>Once per frame: writes the save when its time has come.</summary>
        public void Update()
        {
            if (!pending || Time.realtimeSinceStartup < dueAt)
            {
                return;
            }

            pending = false;
            Report report = SaveNow(button);
            button = null;
            Action<Report> callback = done;
            done = null;
            log.Info(LogCategory.Fsm, "Save: written. " + report);
            if (callback != null)
            {
                callback(report);
            }
        }

        /// <summary>Forgets a save on its way: the world is going.</summary>
        public void Release()
        {
            pending = false;
            button = null;
            done = null;
        }

        private Report SaveNow(PlayMakerFSM from)
        {
            var report = new Report();
            Stopwatch watch = Stopwatch.StartNew();
            Snapshot globals = new Snapshot(FsmVariables.GlobalVariables);
            var panels = new List<ES2Auto>();
            batches = new Dictionary<string, Batch>();
            try
            {
                // The version first, as the button writes it before SAVEGAME: only its writes, not the mod's own action there
                // (SaveSync's), which would ask for this save again.
                PlayMakerFSM version = from ?? FindButton();
                FsmState mute = version == null ? null : FindState(version.Fsm, SaveButtons.MuteState);
                if (mute != null)
                {
                    Run(version.Fsm, mute, null, report, panels, true);
                }

                var machines = new List<PlayMakerFSM>(PlayMakerFSM.FsmList);
                for (int i = 0; i < machines.Count; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    PlayMakerFSM machine = machines[i];
                    Fsm fsm = machine == null ? null : machine.Fsm;
                    if (fsm == null || !fsm.Active || !fsm.Started)
                    {
                        continue;
                    }

                    FsmState start = Target(fsm);
                    if (start == null)
                    {
                        continue;
                    }

                    report.Machines++;
                    Run(fsm, start, SavePath(fsm), report, panels, false);
                }

                for (int i = 0; i < panels.Count; i++)
                {
                    if (panels[i] != null)
                    {
                        try
                        {
                            panels[i].Save();
                            report.Panels++;
                        }
                        catch (Exception exception)
                        {
                            Error(report, "the panel '" + panels[i].name + "'", exception);
                        }
                    }
                }
            }
            finally
            {
                sandbox = null;
                held = null;
                globals.Restore();
                Dictionary<string, Batch> written = batches;
                batches = null;
                Flush(written, report);
            }

            report.Milliseconds = watch.ElapsedMilliseconds;
            return report;
        }

        // The state SAVEGAME takes a machine to: by a global transition first, as PlayMaker looks, or else by one of the
        // state it is in.
        private static FsmState Target(Fsm fsm)
        {
            string to = To(fsm.GlobalTransitions, SaveEvent);
            if (to == null && fsm.ActiveState != null)
            {
                to = To(fsm.ActiveState.Transitions, SaveEvent);
            }

            return to == null ? null : FindState(fsm, to);
        }

        // The states of a machine that write the save or lead to one that does without going through one that loads.
        private static HashSet<string> SavePath(Fsm fsm)
        {
            FsmState[] states = fsm.States;
            var path = new HashSet<string>();
            var loading = new HashSet<string>();
            for (int i = 0; i < states.Length; i++)
            {
                bool saves = false;
                bool loads = false;
                FsmStateAction[] actions = states[i].Actions;
                for (int a = 0; a < actions.Length; a++)
                {
                    string name = actions[a].GetType().Name;
                    saves |= IsWrite(name);
                    loads |= name.StartsWith("Load", StringComparison.Ordinal) || name.EndsWith("EasyLoad", StringComparison.Ordinal);
                }

                if (loads)
                {
                    loading.Add(states[i].Name);
                }
                else if (saves)
                {
                    path.Add(states[i].Name);
                }
            }

            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = 0; i < states.Length; i++)
                {
                    string name = states[i].Name;
                    if (path.Contains(name) || loading.Contains(name))
                    {
                        continue;
                    }

                    FsmTransition[] transitions = states[i].Transitions;
                    for (int t = 0; t < transitions.Length; t++)
                    {
                        if (path.Contains(transitions[t].ToState))
                        {
                            path.Add(name);
                            grew = true;
                            break;
                        }
                    }
                }
            }

            return path;
        }

        // Runs a machine's save from a state: that state, and while the next one is on the save path, the next. With
        // <paramref name="writesOnly"/>, only its Easy Save writes.
        private void Run(Fsm fsm, FsmState start, HashSet<string> path, Report report, List<ES2Auto> panels, bool writesOnly)
        {
            Snapshot variables = new Snapshot(fsm.Variables);
            sandbox = fsm;
            try
            {
                FsmState state = start;
                int steps = 0;
                while (state != null)
                {
                    if (++steps > MaxSteps)
                    {
                        report.Endless++;
                        log.Warning(LogCategory.Fsm, "Save: '" + Describe(fsm) + "' went through " + MaxSteps + " states and was stopped in '" + state.Name + "'.");
                        break;
                    }

                    report.States++;
                    FsmEvent sent = RunState(fsm, state, report, panels, writesOnly);
                    if (path == null)
                    {
                        break;
                    }

                    string to = To(state.Transitions, sent == null ? FinishedEvent : sent.Name);
                    state = to != null && path.Contains(to) ? FindState(fsm, to) : null;
                }
            }
            finally
            {
                sandbox = null;
                held = null;
                variables.Restore();
            }
        }

        // The actions of one state the mod runs, in order. Returns the event one sent that the state has a way out for: then,
        // as in PlayMaker, the rest of the state is not run.
        private FsmEvent RunState(Fsm fsm, FsmState state, Report report, List<ES2Auto> panels, bool writesOnly)
        {
            FsmStateAction[] actions = state.Actions;
            for (int i = 0; i < actions.Length; i++)
            {
                FsmStateAction action = actions[i];
                if (action == null || !action.Enabled)
                {
                    continue;
                }

                Type type = action.GetType();
                string name = type.Name;
                bool hook = type.FullName != null && type.FullName.IndexOf("MSCL_PMHook", StringComparison.Ordinal) >= 0;
                if (writesOnly && !IsWrite(name))
                {
                    continue;
                }

                if (name == "SetProperty")
                {
                    if (!Panel(action, panels))
                    {
                        Skip(report, name);
                        continue;
                    }
                }
                else if (!hook && !IsWrite(name) && !Runs.Contains(name) && !name.StartsWith("Get", StringComparison.Ordinal))
                {
                    Skip(report, name);
                    continue;
                }

                held = null;
                try
                {
                    action.Init(state);
                    action.Finished = false;
                    action.Entered = true;
                    action.OnEnter();
                    report.Actions++;
                }
                catch (Exception exception)
                {
                    Error(report, "'" + Describe(fsm) + "' in '" + state.Name + "' (" + name + ")", exception);
                }
                finally
                {
                    action.Entered = false;
                    action.Active = false;
                }

                if (held != null && To(state.Transitions, held.Name) != null)
                {
                    FsmEvent sent = held;
                    held = null;
                    return sent;
                }
            }

            held = null;
            return null;
        }

        // A save state's "SetProperty" on an ES2Auto: its file is set as the game sets it, and switching it on (which would save
        // it when the scene unloads) becomes saving it at the end. False for any other.
        private static bool Panel(FsmStateAction action, List<ES2Auto> panels)
        {
            var set = action as SetProperty;
            FsmProperty property = set == null ? null : set.targetProperty;
            ES2Auto panel = property == null || property.TargetObject == null ? null : property.TargetObject.Value as ES2Auto;
            if (panel == null)
            {
                return false;
            }

            if (property.PropertyName == "enabled")
            {
                if (!panels.Contains(panel))
                {
                    panels.Add(panel);
                }

                return false;
            }

            return property.PropertyName == "saveFile";
        }

        private static bool IsWrite(string name)
        {
            return name.StartsWith("Save", StringComparison.Ordinal) || name.EndsWith("EasySave", StringComparison.Ordinal);
        }

        private static void Skip(Report report, string name)
        {
            int count;
            report.Skipped.TryGetValue(name, out count);
            report.Skipped[name] = count + 1;
        }

        private void Error(Report report, string where, Exception exception)
        {
            report.Errors++;
            if (report.Errors <= MaxErrorsLogged)
            {
                log.Warning(LogCategory.Fsm, "Save: " + where + " failed: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string To(FsmTransition[] transitions, string eventName)
        {
            if (transitions == null)
            {
                return null;
            }

            for (int i = 0; i < transitions.Length; i++)
            {
                if (transitions[i].EventName == eventName)
                {
                    return transitions[i].ToState;
                }
            }

            return null;
        }

        private static FsmState FindState(Fsm fsm, string name)
        {
            FsmState[] states = fsm.States;
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].Name == name)
                {
                    return states[i];
                }
            }

            return null;
        }

        private static PlayMakerFSM FindButton()
        {
            List<PlayMakerFSM> machines = PlayMakerFSM.FsmList;
            for (int i = 0; i < machines.Count; i++)
            {
                if (machines[i] != null && SaveButtons.IsButton(machines[i]))
                {
                    return machines[i];
                }
            }

            return null;
        }

        private static string Describe(Fsm fsm)
        {
            return (fsm.GameObjectName ?? "?") + " :: " + fsm.Name;
        }

        // Each file's records, by one writer: the old records of those tags dropped and the rest kept, in one read and one write.
        private void Flush(Dictionary<string, Batch> written, Report report)
        {
            foreach (KeyValuePair<string, Batch> file in written)
            {
                Batch batch = file.Value;
                try
                {
                    using (ES2Writer writer = ES2Writer.Create(batch.Settings))
                    {
                        for (int i = 0; i < batch.Order.Count; i++)
                        {
                            string tag = batch.Order[i];
                            writer.WriteRaw(batch.Records[tag]);
                            writer.Delete(tag);
                        }

                        writer.Save();
                    }

                    report.Files++;
                    report.Records += batch.Order.Count;
                }
                catch (Exception exception)
                {
                    Error(report, "writing '" + file.Key + "'", exception);
                }
                finally
                {
                    // What Easy Save remembers of where each tag is in the file is not so any more.
                    ES2Cache.DeleteCachedFile(file.Key);
                }
            }
        }

        // An Easy Save writer about to write its file while the mod saves: its one record is kept instead. A writer of several
        // records, or not to a file in memory first, writes as always.
        private static bool BeforeWriterSave(ES2Writer __instance)
        {
            Dictionary<string, Batch> into = batches;
            ES2Settings settings = __instance.settings;
            var memory = __instance.stream == null ? null : __instance.stream.stream as MemoryStream;
            if (into == null || memory == null || settings == null || settings.encrypt || settings.saveLocation != ES2Settings.SaveLocation.File
                || string.IsNullOrEmpty(settings.filenameData.filePath) || string.IsNullOrEmpty(settings.filenameData.tag))
            {
                return true;
            }

            var tags = tagsField == null ? null : tagsField.GetValue(__instance) as List<string>;
            if (tags == null || tags.Count != 1 || tags[0] != settings.filenameData.tag)
            {
                return true;
            }

            Batch batch;
            if (!into.TryGetValue(settings.filenameData.filePath, out batch))
            {
                batch = new Batch(settings);
                into[settings.filenameData.filePath] = batch;
            }

            batch.Put(tags[0], memory.ToArray());
            return false;
        }

        // Holds back the events of the machine whose save is being run: the first one to itself is what its state would go
        // on with. Every other machine's go through as always.
        private static bool BeforeEvent(Fsm __instance, FsmEventTarget eventTarget, FsmEvent fsmEvent)
        {
            if (sandbox == null || __instance != sandbox)
            {
                return true;
            }

            if (held == null && (eventTarget == null || eventTarget.target == FsmEventTarget.EventTarget.Self))
            {
                held = fsmEvent;
            }

            return false;
        }

        private bool Patch()
        {
            if (patched || patchFailed)
            {
                return patched;
            }

            try
            {
                MethodInfo method = typeof(Fsm).GetMethod("Event", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(FsmEventTarget), typeof(FsmEvent) }, null);
                if (method == null)
                {
                    throw new InvalidOperationException("no Fsm.Event(FsmEventTarget, FsmEvent)");
                }

                MethodInfo save = typeof(ES2Writer).GetMethod("Save", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                tagsField = typeof(ES2Writer).GetField("tagsToDelete", BindingFlags.NonPublic | BindingFlags.Instance);
                if (save == null || tagsField == null)
                {
                    throw new InvalidOperationException("no ES2Writer.Save(bool) or ES2Writer.tagsToDelete");
                }

                HarmonyInstance harmony = HarmonyInstance.Create("summersync.save");
                harmony.Patch(method, new HarmonyMethod(typeof(WorldSaver).GetMethod("BeforeEvent", BindingFlags.NonPublic | BindingFlags.Static)), null);
                harmony.Patch(save, new HarmonyMethod(typeof(WorldSaver).GetMethod("BeforeWriterSave", BindingFlags.NonPublic | BindingFlags.Static)), null);
                patched = true;
            }
            catch (Exception exception)
            {
                patchFailed = true;
                log.Warning(LogCategory.Fsm, "Save: PlayMaker is not as this build expects (" + exception.Message + "); saving goes to the menu as the game does.");
            }

            return patched;
        }

        // The records one file gets from a save, in the order first written, the last of each tag.
        private sealed class Batch
        {
            public readonly ES2Settings Settings;
            public readonly List<string> Order = new List<string>();
            public readonly Dictionary<string, byte[]> Records = new Dictionary<string, byte[]>();

            public Batch(ES2Settings settings)
            {
                Settings = settings;
            }

            public void Put(string tag, byte[] record)
            {
                if (!Records.ContainsKey(tag))
                {
                    Order.Add(tag);
                }

                Records[tag] = record;
            }
        }

        // The values of every variable of a machine (or the game's globals), to put back after its save ran.
        private sealed class Snapshot
        {
            private readonly FsmVariables variables;
            private readonly float[] floats;
            private readonly int[] ints;
            private readonly bool[] bools;
            private readonly string[] strings;
            private readonly Vector2[] vector2s;
            private readonly Vector3[] vector3s;
            private readonly Rect[] rects;
            private readonly Quaternion[] quaternions;
            private readonly Color[] colors;
            private readonly GameObject[] gameObjects;
            private readonly UnityEngine.Object[] objects;
            private readonly Material[] materials;
            private readonly Texture[] textures;

            public Snapshot(FsmVariables variables)
            {
                this.variables = variables;
                if (variables == null)
                {
                    return;
                }

                FsmFloat[] f = variables.FloatVariables;
                floats = new float[f.Length];
                for (int i = 0; i < f.Length; i++)
                {
                    floats[i] = f[i].Value;
                }

                FsmInt[] n = variables.IntVariables;
                ints = new int[n.Length];
                for (int i = 0; i < n.Length; i++)
                {
                    ints[i] = n[i].Value;
                }

                FsmBool[] b = variables.BoolVariables;
                bools = new bool[b.Length];
                for (int i = 0; i < b.Length; i++)
                {
                    bools[i] = b[i].Value;
                }

                FsmString[] s = variables.StringVariables;
                strings = new string[s.Length];
                for (int i = 0; i < s.Length; i++)
                {
                    strings[i] = s[i].Value;
                }

                FsmVector2[] v2 = variables.Vector2Variables;
                vector2s = new Vector2[v2.Length];
                for (int i = 0; i < v2.Length; i++)
                {
                    vector2s[i] = v2[i].Value;
                }

                FsmVector3[] v3 = variables.Vector3Variables;
                vector3s = new Vector3[v3.Length];
                for (int i = 0; i < v3.Length; i++)
                {
                    vector3s[i] = v3[i].Value;
                }

                FsmRect[] r = variables.RectVariables;
                rects = new Rect[r.Length];
                for (int i = 0; i < r.Length; i++)
                {
                    rects[i] = r[i].Value;
                }

                FsmQuaternion[] q = variables.QuaternionVariables;
                quaternions = new Quaternion[q.Length];
                for (int i = 0; i < q.Length; i++)
                {
                    quaternions[i] = q[i].Value;
                }

                FsmColor[] c = variables.ColorVariables;
                colors = new Color[c.Length];
                for (int i = 0; i < c.Length; i++)
                {
                    colors[i] = c[i].Value;
                }

                FsmGameObject[] g = variables.GameObjectVariables;
                gameObjects = new GameObject[g.Length];
                for (int i = 0; i < g.Length; i++)
                {
                    gameObjects[i] = g[i].Value;
                }

                FsmObject[] o = variables.ObjectVariables;
                objects = new UnityEngine.Object[o.Length];
                for (int i = 0; i < o.Length; i++)
                {
                    objects[i] = o[i].Value;
                }

                FsmMaterial[] m = variables.MaterialVariables;
                materials = new Material[m.Length];
                for (int i = 0; i < m.Length; i++)
                {
                    materials[i] = m[i].Value;
                }

                FsmTexture[] t = variables.TextureVariables;
                textures = new Texture[t.Length];
                for (int i = 0; i < t.Length; i++)
                {
                    textures[i] = t[i].Value;
                }
            }

            public void Restore()
            {
                if (variables == null)
                {
                    return;
                }

                FsmFloat[] f = variables.FloatVariables;
                for (int i = 0; i < f.Length && i < floats.Length; i++)
                {
                    f[i].Value = floats[i];
                }

                FsmInt[] n = variables.IntVariables;
                for (int i = 0; i < n.Length && i < ints.Length; i++)
                {
                    n[i].Value = ints[i];
                }

                FsmBool[] b = variables.BoolVariables;
                for (int i = 0; i < b.Length && i < bools.Length; i++)
                {
                    b[i].Value = bools[i];
                }

                FsmString[] s = variables.StringVariables;
                for (int i = 0; i < s.Length && i < strings.Length; i++)
                {
                    s[i].Value = strings[i];
                }

                FsmVector2[] v2 = variables.Vector2Variables;
                for (int i = 0; i < v2.Length && i < vector2s.Length; i++)
                {
                    v2[i].Value = vector2s[i];
                }

                FsmVector3[] v3 = variables.Vector3Variables;
                for (int i = 0; i < v3.Length && i < vector3s.Length; i++)
                {
                    v3[i].Value = vector3s[i];
                }

                FsmRect[] r = variables.RectVariables;
                for (int i = 0; i < r.Length && i < rects.Length; i++)
                {
                    r[i].Value = rects[i];
                }

                FsmQuaternion[] q = variables.QuaternionVariables;
                for (int i = 0; i < q.Length && i < quaternions.Length; i++)
                {
                    q[i].Value = quaternions[i];
                }

                FsmColor[] c = variables.ColorVariables;
                for (int i = 0; i < c.Length && i < colors.Length; i++)
                {
                    c[i].Value = colors[i];
                }

                FsmGameObject[] g = variables.GameObjectVariables;
                for (int i = 0; i < g.Length && i < gameObjects.Length; i++)
                {
                    g[i].Value = gameObjects[i];
                }

                FsmObject[] o = variables.ObjectVariables;
                for (int i = 0; i < o.Length && i < objects.Length; i++)
                {
                    o[i].Value = objects[i];
                }

                FsmMaterial[] m = variables.MaterialVariables;
                for (int i = 0; i < m.Length && i < materials.Length; i++)
                {
                    m[i].Value = materials[i];
                }

                FsmTexture[] t = variables.TextureVariables;
                for (int i = 0; i < t.Length && i < textures.Length; i++)
                {
                    t[i].Value = textures[i];
                }
            }
        }
    }
}
