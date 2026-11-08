using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using SummerSync.Core;
using SummerSync.Core.Dump;
using SummerSync.Core.Json;
using UnityEngine;

namespace SummerSync.Dumper
{
    /// <summary>What one dump produced, for the on-screen message and the console.</summary>
    internal sealed class DumpResult
    {
        public string FilePath;
        public string Kind;
        public int Fsms;
        public int States;
        public int Actions;
        public int Variables;
        public int Collections;
        public int Rigidbodies;
        public int Vehicles;
        public int Errors;
        public long Bytes;
        public long Milliseconds;
    }

    /// <summary>
    /// Takes a snapshot of the game and writes it as one JSON file.
    ///
    /// Every FSM, collection, rigidbody and vehicle is rendered on the side first and only appended when it
    /// rendered completely, so one broken object costs one entry (listed under "errors") and never the file.
    /// </summary>
    internal sealed class DumpRunner
    {
        public const string TriggerKey = "key";
        public const string TriggerAuto = "auto";
        public const string TriggerCommand = "command";

        private const int MaxErrors = 200;

        private readonly StringBuilder itemBuffer = new StringBuilder(4096);
        private readonly List<string> errors = new List<string>();

        public DumpResult Run(string directory, bool full, string trigger, bool includeInactive)
        {
            var watch = Stopwatch.StartNew();
            errors.Clear();

            var paths = new PathCache();
            var values = new GameValueWriter(paths);
            var fsmWriter = new FsmDumpWriter(paths, values);
            var worldWriter = new WorldDumpWriter(paths, values);
            var result = new DumpResult { Kind = full ? DumpFormat.KindFull : DumpFormat.KindState };

            Directory.CreateDirectory(directory);
            string fileName = "dump-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + result.Kind
                + (trigger == TriggerAuto ? "-auto" : string.Empty) + ".json";
            result.FilePath = Path.Combine(directory, fileName);

            // Written under a temporary name so an interrupted dump is never mistaken for a complete one.
            string temporaryPath = result.FilePath + ".tmp";
            using (var file = new StreamWriter(temporaryPath, false, new UTF8Encoding(false)))
            {
                var writer = new JsonWriter(file);
                writer.BeginObject();
                writer.Property(DumpFormat.Format, DumpFormat.Version);
                writer.Property(DumpFormat.Kind, result.Kind);
                writer.Property(DumpFormat.Tool, "SummerSyncDumper " + BuildInfo.InformationalVersion);
                writer.Property(DumpFormat.Created, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                writer.Property(DumpFormat.Trigger, trigger);

                writer.Name(DumpFormat.Game).BeginObject();
                writer.Property(DumpFormat.Unity, Application.unityVersion);
                writer.Property(DumpFormat.Scene, Application.loadedLevelName);
                writer.Property(DumpFormat.Time, Time.time);
                writer.Property(DumpFormat.Realtime, Time.realtimeSinceStartup);
                writer.Property(DumpFormat.Frame, Time.frameCount);
                writer.EndObject();

                writer.Break();
                writer.Name(DumpFormat.Globals);
                string globals = Render("global variables", item => fsmWriter.WriteVariables(item, FsmVariables.GlobalVariables));
                writer.Raw(globals ?? "{}");

                writer.Break();
                writer.Name(DumpFormat.Fsms).BeginArray();
                foreach (PlayMakerFSM component in FindAll<PlayMakerFSM>(includeInactive))
                {
                    PlayMakerFSM current = component;
                    if (Append(writer, "FSM", current, item => fsmWriter.Write(item, current, full)))
                    {
                        result.Fsms++;
                    }
                }

                writer.Break();
                writer.EndArray();

                writer.Break();
                writer.Name(DumpFormat.Collections).BeginArray();
                foreach (PlayMakerCollectionProxy proxy in FindAll<PlayMakerCollectionProxy>(includeInactive))
                {
                    PlayMakerCollectionProxy current = proxy;
                    if (Append(writer, "collection", current, item => worldWriter.WriteCollection(item, current)))
                    {
                        result.Collections++;
                    }
                }

                writer.Break();
                writer.EndArray();

                writer.Break();
                writer.Name(DumpFormat.Rigidbodies).BeginArray();
                foreach (Rigidbody body in FindAll<Rigidbody>(includeInactive))
                {
                    Rigidbody current = body;
                    if (Append(writer, "rigidbody", current, item => worldWriter.WriteRigidbody(item, current)))
                    {
                        result.Rigidbodies++;
                    }
                }

                writer.Break();
                writer.EndArray();

                writer.Break();
                writer.Name(DumpFormat.Vehicles).BeginArray();
                foreach (CarDynamics car in FindAll<CarDynamics>(includeInactive))
                {
                    CarDynamics current = car;
                    if (Append(writer, "vehicle", current, item => worldWriter.WriteVehicle(item, current, full)))
                    {
                        result.Vehicles++;
                    }
                }

                writer.Break();
                writer.EndArray();

                result.States = fsmWriter.StateCount;
                result.Actions = fsmWriter.ActionCount;
                result.Variables = fsmWriter.VariableCount;
                result.Errors = errors.Count;
                result.Milliseconds = watch.ElapsedMilliseconds;

                writer.Break();
                writer.Name(DumpFormat.Stats).BeginObject();
                writer.Property(DumpFormat.StatFsms, result.Fsms);
                writer.Property(DumpFormat.StatStates, result.States);
                writer.Property(DumpFormat.StatActions, result.Actions);
                writer.Property(DumpFormat.StatVariables, result.Variables);
                writer.Property(DumpFormat.StatCollections, result.Collections);
                writer.Property(DumpFormat.StatRigidbodies, result.Rigidbodies);
                writer.Property(DumpFormat.StatVehicles, result.Vehicles);
                writer.Property(DumpFormat.StatMilliseconds, result.Milliseconds);
                writer.EndObject();

                writer.Break();
                writer.Name(DumpFormat.Errors).BeginArray();
                for (int i = 0; i < errors.Count; i++)
                {
                    writer.Value(errors[i]);
                }

                writer.EndArray();
                writer.Break();
                writer.EndObject();
            }

            if (File.Exists(result.FilePath))
            {
                File.Delete(result.FilePath);
            }

            File.Move(temporaryPath, result.FilePath);
            result.Bytes = new FileInfo(result.FilePath).Length;
            result.Milliseconds = watch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// With <paramref name="includeInactive"/>, also returns components on inactive GameObjects and on
        /// prefabs that are only loaded in memory; otherwise just the active ones in the scene.
        /// </summary>
        private static T[] FindAll<T>(bool includeInactive)
            where T : UnityEngine.Object
        {
            return includeInactive ? Resources.FindObjectsOfTypeAll<T>() : UnityEngine.Object.FindObjectsOfType<T>();
        }

        /// <summary>Renders one item on the side and appends it only if that worked. Returns whether it was written.</summary>
        private bool Append(JsonWriter writer, string what, UnityEngine.Object source, Action<JsonWriter> write)
        {
            // A destroyed object still sits in the array but cannot be read.
            if (source == null)
            {
                return false;
            }

            string json = Render(what + " '" + source.name + "'", write);
            if (json == null)
            {
                return false;
            }

            writer.Break();
            writer.Raw(json);
            return true;
        }

        private string Render(string what, Action<JsonWriter> write)
        {
            itemBuffer.Length = 0;
            try
            {
                using (var text = new StringWriter(itemBuffer, CultureInfo.InvariantCulture))
                {
                    var item = new JsonWriter(text);
                    write(item);
                    if (!item.IsComplete)
                    {
                        throw new InvalidOperationException("The item was not written completely.");
                    }
                }

                return itemBuffer.ToString();
            }
            catch (Exception exception)
            {
                // Game objects fail in ways that cannot be predicted from outside (destroyed references, half
                // initialised FSMs); the dump must still finish and say what it skipped.
                if (errors.Count < MaxErrors)
                {
                    errors.Add(what + ": " + exception.GetType().Name + ": " + exception.Message);
                }
                else if (errors.Count == MaxErrors)
                {
                    errors.Add("More errors were omitted.");
                }

                return null;
            }
        }
    }
}
