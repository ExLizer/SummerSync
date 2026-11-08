using System.Text;
using MSCLoader;
using SummerSync.Core;
using SummerSync.Core.Diagnostics;
using SummerSync.Core.Logging;
using SummerSync.Unity.Net;
using UnityEngine;

namespace SummerSync.Unity
{
    /// <summary>
    /// MSCLoader entry point: settings, key bindings and the debug overlay.
    ///
    /// MSCLoader creates a new instance of this class whenever the main menu loads again, so instance fields
    /// only hold what is cheap to rebuild. Process-wide state lives in <see cref="ModRuntime"/>.
    /// </summary>
    public sealed class SummerSyncMod : Mod
    {
        private readonly LogSettings logSettings = new LogSettings();
        private readonly NetworkSettings networkSettings = new NetworkSettings();
        private readonly StringBuilder overlayText = new StringBuilder();
        private readonly FrameRateCounter frameRate = new FrameRateCounter();
        private readonly DebugOverlay overlay = new DebugOverlay();
        private SettingsDropDownList language;
        private SettingsCheckBox showOverlay;
        private SettingsCheckBox importMusic;
        private SettingsCheckBox openConsole;
        private SettingsKeybind toggleOverlay;
        private bool started;

        public override string ID => "SummerSync";

        public override string Name => BuildInfo.ProductName;

        public override string Author => "Julian";

        public override string Version => BuildInfo.Version;

        public override string Description => "Multiplayer for My Summer Car (beta).";

        public override Game SupportedGames => Game.MySummerCar;

        private static Logger Log => ModRuntime.Log;

        public override void ModSetup()
        {
            // MSCLoader only allows SetupFunction calls here; real initialisation happens in EnsureStarted.
            SetupFunction(Setup.ModSettings, CreateSettings);
            SetupFunction(Setup.ModSettingsLoaded, ApplySettings);

            // Registering OnMenuLoad is also what makes MSCLoader run Update and OnGUI in the main menu,
            // which the lobby screens of later phases depend on.
            SetupFunction(Setup.OnMenuLoad, OnMenuLoad);
            SetupFunction(Setup.OnLoad, OnGameLoaded);
            SetupFunction(Setup.Update, OnUpdate);
            SetupFunction(Setup.OnGUI, OnGui);
        }

        // The settings MSCLoader shows. The public edition shows the players' own; the developers' one also the network
        // test, the simulator and the log (Edition).
        private void CreateSettings()
        {
            language = Settings.AddDropDownList("language", "Language / Idioma", Texts.Languages, 0, ApplySettings, true);
            showOverlay = Settings.AddCheckBox("showOverlay", Edition.Developer ? "Show debug overlay" : "Show connection info (F8)", Edition.Developer, null, true);
            importMusic = Settings.AddCheckBox("importMusic", "Import the songs of the game's Radio folder when the game opens", true, null, true);
            ModRuntime.UseMusicSetting(importMusic);
            openConsole = Settings.AddCheckBox("openConsole", "Open MSCLoader's console on the mod's warnings and errors", false, ApplySettings, true);
            toggleOverlay = Keybind.Add("toggleOverlay", Edition.Developer ? "Toggle debug overlay" : "Toggle connection info", KeyCode.F8);
            ModRuntime.ChatKey = Keybind.Add("chat", "Chat: write a line (Enter sends, Esc closes)", Players.ChatBox.DefaultKey);
            networkSettings.Create(Edition.Developer);
            if (Edition.Developer)
            {
                logSettings.Create(ApplySettings);
            }

            ModRuntime.UseSettings(networkSettings);
        }

        private void ApplySettings()
        {
            if (language != null)
            {
                Texts.Spanish = language.GetSelectedItemIndex() == 1;
            }

            ModConsoleLogSink.OpensConsole = openConsole != null && openConsole.GetValue();

            logSettings.ApplyTo(Log);
            networkSettings.Apply();
        }

        private void OnMenuLoad()
        {
            EnsureStarted();
            Log.Info(LogCategory.General, "Main menu loaded (Unity level '" + Application.loadedLevelName + "').");
        }

        private void OnGameLoaded()
        {
            EnsureStarted();
            Log.Info(LogCategory.General, "Game scene loaded (Unity level '" + Application.loadedLevelName + "').");
        }

        private void OnUpdate()
        {
            EnsureStarted();

            if (toggleOverlay != null && showOverlay != null && toggleOverlay.GetKeybindDown())
            {
                showOverlay.SetValue(!showOverlay.GetValue());
            }

            if (frameRate.Tick(Time.unscaledDeltaTime))
            {
                overlay.SetText(BuildOverlayText());
            }
        }

        private void OnGui()
        {
            EnsureStarted();

            if (showOverlay == null || showOverlay.GetValue())
            {
                overlay.Draw();
            }
        }

        /// <summary>
        /// One-time initialisation of this instance. MSCLoader does not document the order of its callbacks,
        /// so every callback calls this first instead of relying on one of them running before the others.
        /// </summary>
        private void EnsureStarted()
        {
            if (started)
            {
                return;
            }

            started = true;
            ModRuntime.Attach(this);
            ApplySettings();
            overlay.SetText(BuildOverlayText());
        }

        private string BuildOverlayText()
        {
            overlayText.Length = 0;
            overlayText.Append(BuildInfo.ProductName).Append(' ').Append(BuildInfo.InformationalVersion).Append(Edition.Mark)
                .Append(" | MSCLoader ").Append(ModLoader.MSCLoader_Ver).Append(" | Unity ").Append(Application.unityVersion).Append('\n')
                .Append("Scene: ").Append(Application.loadedLevelName).Append(" | ").Append(Mathf.RoundToInt(frameRate.FramesPerSecond)).Append(" fps")
                .Append(" | game ").Append(ModRuntime.Network.Manager.GameVersion).Append('\n');
            ModRuntime.Network.AppendStatus(overlayText);
            overlayText.Append('\n');
            World.HostWorld.AppendStatus(overlayText);
            overlayText.Append('\n');
            ModRuntime.AppendLogStatus(overlayText);
            return overlayText.ToString();
        }
    }
}
