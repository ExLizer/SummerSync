using System;
using System.Globalization;
using System.IO;
using MSCLoader;
using SummerSync.Core;
using SummerSync.Core.Dump;
using UnityEngine;

namespace SummerSync.Dumper
{
    /// <summary>
    /// MSCLoader entry point of the state dumper. Dumps are taken with a key, with the "ssdump" console command
    /// or automatically on a timer, and are written under the mod's settings folder.
    /// </summary>
    public sealed class DumperMod : Mod
    {
        private const string DumpFolderName = "dumps";
        private const float MessageSeconds = 8f;
        private const int AutoDumpsToKeep = 30;

        private static readonly int[] AutoIntervals = { 0, 5, 10, 30, 60 };

        private static readonly string[] AutoIntervalNames =
        {
            "Off", "Every 5 seconds", "Every 10 seconds", "Every 30 seconds", "Every 60 seconds",
        };

        private readonly DumpRunner runner = new DumpRunner();
        private SettingsKeybind stateDumpKey;
        private SettingsKeybind fullDumpKey;
        private SettingsDropDownList autoInterval;
        private SettingsCheckBox includeInactive;
        private float nextAutoDump;
        private string message;
        private float messageUntil;
        private GUIStyle messageStyle;

        public override string ID => "SummerSyncDumper";

        public override string Name => "SummerSync Dumper";

        public override string Author => "Julian";

        public override string Version => BuildInfo.Version;

        public override string Description => "Dumps game state to JSON for SummerSync development.";

        public override Game SupportedGames => Game.MySummerCar;

        private string DumpFolder
        {
            get { return Path.Combine(ModLoader.GetModSettingsFolder(this), DumpFolderName); }
        }

        public override void ModSetup()
        {
            // No OnMenuLoad on purpose: MSCLoader then only runs Update and OnGUI inside a loaded game.
            SetupFunction(Setup.ModSettings, CreateSettings);
            SetupFunction(Setup.OnLoad, OnGameLoaded);
            SetupFunction(Setup.Update, OnUpdate);
            SetupFunction(Setup.OnGUI, OnGui);
        }

        /// <summary>Takes a dump now. Also the entry point of the console command.</summary>
        internal void Dump(bool full, string trigger)
        {
            try
            {
                bool inactive = includeInactive == null || includeInactive.GetValue();
                DumpResult result = runner.Run(DumpFolder, full, trigger, inactive);
                string summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} dump: {1} FSMs, {2} collections, {3} rigidbodies, {4} vehicles, {5:0.0} MB in {6} ms{7}",
                    result.Kind,
                    result.Fsms,
                    result.Collections,
                    result.Rigidbodies,
                    result.Vehicles,
                    result.Bytes / (1024f * 1024f),
                    result.Milliseconds,
                    result.Errors > 0 ? ", " + result.Errors + " item(s) skipped (see \"errors\" in the file)" : string.Empty);

                ModConsole.Print("[SummerSync Dumper] " + summary);
                ModConsole.Print("[SummerSync Dumper] " + result.FilePath);
                ShowMessage(summary + "\n" + Path.GetFileName(result.FilePath));

                if (trigger == DumpRunner.TriggerAuto)
                {
                    DeleteOldAutoDumps();
                }
            }
            catch (Exception exception)
            {
                // A diagnostic tool must never break the game it observes: report and carry on.
                ModConsole.Error("[SummerSync Dumper] Dump failed: " + exception);
                ShowMessage("Dump failed: " + exception.Message);
            }
        }

        private void CreateSettings()
        {
            Settings.AddText("State dump: variables, active states, collections and physics. Full dump: also every state, transition and action.", true);
            stateDumpKey = Keybind.Add("stateDump", "State dump", KeyCode.F9, KeyCode.LeftControl);
            fullDumpKey = Keybind.Add("fullDump", "Full dump", KeyCode.F10, KeyCode.LeftControl);
            autoInterval = Settings.AddDropDownList("autoInterval", "Automatic state dump", AutoIntervalNames, 0, ResetAutoTimer, true);
            includeInactive = Settings.AddCheckBox("includeInactive", "Include inactive objects and loaded prefabs", true, null, true);
        }

        private void OnGameLoaded()
        {
            try
            {
                ConsoleCommand.Add(new DumpCommand(this));
            }
            catch (Exception exception)
            {
                ModConsole.Warning("[SummerSync Dumper] Could not register the 'ssdump' console command: " + exception.Message);
            }

            ResetAutoTimer();
            ModConsole.Print("[SummerSync Dumper] Ready. Ctrl+F9: state dump. Ctrl+F10: full dump. Console: ssdump [full].");
            ModConsole.Print("[SummerSync Dumper] Dumps go to " + DumpFolder);
        }

        private void OnUpdate()
        {
            if (fullDumpKey != null && fullDumpKey.GetKeybindDown())
            {
                Dump(true, DumpRunner.TriggerKey);
            }
            else if (stateDumpKey != null && stateDumpKey.GetKeybindDown())
            {
                Dump(false, DumpRunner.TriggerKey);
            }
            else if (AutoIntervalSeconds() > 0 && Time.realtimeSinceStartup >= nextAutoDump)
            {
                ResetAutoTimer();
                Dump(false, DumpRunner.TriggerAuto);
            }
        }

        private void OnGui()
        {
            if (message == null || Time.realtimeSinceStartup > messageUntil)
            {
                return;
            }

            if (messageStyle == null)
            {
                messageStyle = new GUIStyle(GUI.skin.box) { fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                messageStyle.normal.textColor = Color.white;
            }

            const float width = 760f;
            const float height = 56f;
            GUI.Box(new Rect((Screen.width - width) / 2f, Screen.height - height - 40f, width, height), message, messageStyle);
        }

        private int AutoIntervalSeconds()
        {
            if (autoInterval == null)
            {
                return 0;
            }

            int index = autoInterval.GetSelectedItemIndex();
            return index >= 0 && index < AutoIntervals.Length ? AutoIntervals[index] : 0;
        }

        private void ResetAutoTimer()
        {
            nextAutoDump = Time.realtimeSinceStartup + AutoIntervalSeconds();
        }

        private void ShowMessage(string text)
        {
            message = text;
            messageUntil = Time.realtimeSinceStartup + MessageSeconds;
        }

        // Automatic dumps would otherwise fill the disk. Only files this mod created on a timer are removed.
        private void DeleteOldAutoDumps()
        {
            string[] files = Directory.GetFiles(DumpFolder, "dump-*-" + DumpFormat.KindState + "-auto.json");
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length - AutoDumpsToKeep; i++)
            {
                File.Delete(files[i]);
            }
        }

        private sealed class DumpCommand : ConsoleCommand
        {
            private readonly DumperMod mod;

            public DumpCommand(DumperMod mod)
            {
                this.mod = mod;
            }

            public override string Name => "ssdump";

            public override string Help => "SummerSync Dumper: 'ssdump' writes a state dump, 'ssdump full' a full dump.";

            public override void Run(string[] args)
            {
                bool full = args != null && args.Length > 0 && string.Equals(args[0], "full", StringComparison.OrdinalIgnoreCase);
                mod.Dump(full, DumpRunner.TriggerCommand);
            }
        }
    }
}
