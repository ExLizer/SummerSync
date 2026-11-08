using System;
using System.IO;
using System.Text;
using MSCLoader;
using SummerSync.Core;
using SummerSync.Core.Logging;
using SummerSync.Unity.Net;
using UnityEngine;

namespace SummerSync.Unity
{
    /// <summary>
    /// State that must outlive a single <see cref="Mod"/> instance.
    ///
    /// MSCLoader re-creates every mod each time the main menu loads again after a game session, and destroys
    /// all GameObjects while doing so. The assembly stays loaded, so static state is the only thing that
    /// survives. Whatever has to last for the whole process (the log file handle, the network) belongs here
    /// and not in instance fields of the mod.
    /// </summary>
    internal static class ModRuntime
    {
        private const string LogFileName = "SummerSync.log";

        // Two copies of the game on one machine share this folder; each gets its own log file.
        private const int MaxGameInstances = 4;

        public static readonly Logger Log = new Logger();

        public static readonly NetworkController Network = new NetworkController(Log);

        private static NetworkSettings networkSettings;
        private static FileLogSink logFile;
        private static string logFileProblem = "not opened yet";

        /// <summary>How many mod instances have attached in this process: 1 after a fresh start.</summary>
        public static int Generation { get; private set; }

        /// <summary>The folder MSCLoader gives the mod for its own files: the log is there. Null until the log was opened.</summary>
        public static string SettingsFolder { get; private set; }

        /// <summary>Called once by every new mod instance before it does anything else.</summary>
        public static void Attach(Mod mod)
        {
            Generation++;
            if (Generation == 1)
            {
                OpenLog(mod);
                Log.Info(
                    LogCategory.General,
                    BuildInfo.ProductName + " " + BuildInfo.InformationalVersion + Edition.Mark + " loaded (MSCLoader " + ModLoader.MSCLoader_Ver
                    + ", Unity " + Application.unityVersion + ", process " + ProcessId() + ").");
            }
            else
            {
                Log.Info(LogCategory.General, "MSCLoader reloaded its mods: mod instance #" + Generation + " attached.");
            }

            // Logged on every reload so incompatible mods can be identified by their real IDs from a user's log.
            Log.Info(LogCategory.General, "Loaded mods: " + DescribeLoadedMods());

            Network.OnAttach();
            RuntimeDriver.Ensure();
            RegisterConsoleCommand();
        }

        private static SettingsCheckBox importMusic;

        /// <summary>Whether the songs of the Radio folder are imported without the menu's button (on unless switched off).</summary>
        public static bool ImportMusic
        {
            get { return importMusic == null || importMusic.GetValue(); }
        }

        public static void UseMusicSetting(SettingsCheckBox setting)
        {
            importMusic = setting;
        }

        /// <summary>The key that opens the chat, as the player set it in the mod's keys; null before the settings exist.</summary>
        public static SettingsKeybind ChatKey { get; set; }

        /// <summary>Whether the player asked for the owner labels over nearby objects.</summary>
        public static bool ShowObjectOwners
        {
            get { return networkSettings != null && networkSettings.ShowObjectOwners; }
        }

        /// <summary>The settings of the current mod instance, so the console can keep its sliders in step.</summary>
        public static void UseSettings(NetworkSettings settings)
        {
            networkSettings = settings;
        }

        /// <summary>
        /// One line on the state of the log file, for the overlay. The log is the only thing that travels from
        /// another player's machine, so whether it is being written has to be visible on their screen.
        /// </summary>
        public static void AppendLogStatus(StringBuilder text)
        {
            text.Append("Log: ");
            if (logFile == null)
            {
                text.Append("NO FILE (").Append(logFileProblem).Append(')');
            }
            else
            {
                text.Append(Path.GetFileName(logFile.Path)).Append(", ").Append(logFile.LinesWritten).Append(" lines");
            }

            if (Log.MinimumLevel != LogLevel.Info)
            {
                text.Append(", level ").Append(Log.MinimumLevel);
            }

            if ((Log.EnabledCategories & LogCategory.All) != LogCategory.All)
            {
                text.Append(", some categories off");
            }

            if (Log.SinkFailures > 0)
            {
                text.Append(", ").Append(Log.SinkFailures).Append(" entries NOT written (").Append(Log.LastSinkError).Append(')');
            }
        }

        public static void SetSimulator(float latencyMs, float jitterMs, float lossPercent)
        {
            if (networkSettings != null)
            {
                networkSettings.SetSimulator(latencyMs, jitterMs, lossPercent);
            }
            else
            {
                Network.SetConditions(latencyMs, jitterMs, lossPercent);
            }
        }

        private static void OpenLog(Mod mod)
        {
            Log.AddSink(new ModConsoleLogSink());

            try
            {
                SettingsFolder = ModLoader.GetModSettingsFolder(mod);
                string path = Path.Combine(SettingsFolder, LogFileName);
                FileLogSink sink = FileLogSink.OpenFirstFree(path, MaxGameInstances);
                logFile = sink;
                Log.AddSink(sink);
                Log.Info(LogCategory.General, "Log file: " + sink.Path);
                if (sink.Path != path)
                {
                    Log.Info(LogCategory.General, "Another copy of the game is already running and owns " + LogFileName + ".");
                }
            }
            catch (Exception exception)
            {
                // Whatever the cause (read-only folder, locked file, odd path), the mod must still load.
                logFileProblem = exception.Message;
                Log.Warning(LogCategory.General, "File logging is disabled: " + exception.Message);
            }
        }

        // Tells two runs of the game apart in a log, and a log apart from that of a second copy of the game.
        private static string ProcessId()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static void RegisterConsoleCommand()
        {
            try
            {
                ConsoleCommand.Add(new NetworkCommand());
            }
            catch (ArgumentException)
            {
                // Already registered by an earlier mod instance, and the console survived the reload.
            }
            catch (Exception exception)
            {
                Log.Warning(LogCategory.General, "Could not register the 'ss' console command: " + exception.Message);
            }
        }

        private static string DescribeLoadedMods()
        {
            var text = new StringBuilder();
            foreach (Mod mod in ModLoader.LoadedMods)
            {
                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                text.Append(mod.ID).Append(' ').Append(mod.Version);
            }

            return text.ToString();
        }
    }
}
