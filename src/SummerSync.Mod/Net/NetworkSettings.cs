using System;
using MSCLoader;
using SummerSync.Core.Net;

namespace SummerSync.Unity.Net
{
    /// <summary>
    /// The network test controls in the MSCLoader settings menu: buttons to start and stop a session, and the
    /// sliders of the network simulator. The "ss" console command does the same things.
    /// </summary>
    internal sealed class NetworkSettings
    {
        private const int MaxLatencyMs = 500;
        private const int MaxJitterMs = 100;
        private const int MaxLossPercent = 30;

        private SettingsSliderInt latency;
        private SettingsSliderInt jitter;
        private SettingsSliderInt loss;
        private SettingsCheckBox showOwners;
        private bool updatingSliders;

        /// <summary>Whether to label each nearby shared object with who is simulating it.</summary>
        public bool ShowObjectOwners
        {
            get { return showOwners != null && showOwners.GetValue(); }
        }

        private static NetworkController Network
        {
            get { return ModRuntime.Network; }
        }

        /// <summary>
        /// Creates the settings. Must be called from the mod's ModSettings function. The public edition has only inviting
        /// and leaving; the developers' one (<paramref name="developer"/>) the network test, the simulator and the labels.
        /// </summary>
        public void Create(bool developer)
        {
            if (!developer)
            {
                Settings.AddHeader("Session", false, true);
                Settings.AddButton("Invite a friend (Steam overlay)", () => Network.InviteFriend(), true);
                Settings.AddButton("Disconnect", () => Network.Leave(), true);
                return;
            }

            Settings.AddHeader("Network test", false, true);
            Settings.AddText(
                "Loopback runs a host and a client inside this game. On Steam: host, then invite; your friend only accepts the invite. "
                + "Local UDP connects two copies of the game on this PC. The result shows in the debug overlay. Console: 'ss help'.",
                true);
            Settings.AddButton("Start loopback self-test", () => Network.StartLoopbackTest(), true);
            Settings.AddButton("Host on Steam (friends only)", () => Network.HostSteam(), true);
            Settings.AddButton("Invite a friend (Steam overlay)", () => Network.InviteFriend(), true);
            Settings.AddButton("Host on local UDP (port " + NetworkController.DefaultUdpPort + ")", () => Network.HostUdp(NetworkController.DefaultUdpPort, false), true);
            Settings.AddButton("Join local UDP (port " + NetworkController.DefaultUdpPort + ")", () => Network.JoinUdp(NetCommand.DefaultAddress, NetworkController.DefaultUdpPort), true);
            Settings.AddButton("Disconnect", () => Network.Leave(), true);

            Settings.AddHeader("Network simulator", false, true);
            Settings.AddText("Degrades everything this side sends. These values are remembered: put them back to 0 when you are done.", true);
            latency = Settings.AddSlider("simLatency", "Added latency (ms)", 0, MaxLatencyMs, 0, Apply, null, true);
            jitter = Settings.AddSlider("simJitter", "Jitter (+- ms)", 0, MaxJitterMs, 0, Apply, null, true);
            loss = Settings.AddSlider("simLoss", "Packet loss (%)", 0, MaxLossPercent, 0, Apply, null, true);

            Settings.AddHeader("Objects", false, true);
            showOwners = Settings.AddCheckBox("showObjectOwners", "Label nearby objects with who simulates them (debug)", false, null, true);
        }

        /// <summary>Copies the sliders to the simulator. Does nothing before <see cref="Create"/>.</summary>
        public void Apply()
        {
            if (latency == null || updatingSliders)
            {
                return;
            }

            Network.SetConditions(latency.GetValue(), jitter.GetValue(), loss.GetValue());
        }

        /// <summary>
        /// Sets the simulator from the console. The exact values apply, even beyond the slider ranges; the
        /// sliders follow as closely as they can.
        /// </summary>
        public void SetSimulator(float latencyMs, float jitterMs, float lossPercent)
        {
            if (latency != null)
            {
                // Moving a slider can fire its change handler, which would copy the rounded values back.
                updatingSliders = true;
                try
                {
                    latency.SetValue(ToSlider(latencyMs, MaxLatencyMs));
                    jitter.SetValue(ToSlider(jitterMs, MaxJitterMs));
                    loss.SetValue(ToSlider(lossPercent, MaxLossPercent));
                }
                finally
                {
                    updatingSliders = false;
                }
            }

            Network.SetConditions(latencyMs, jitterMs, lossPercent);
        }

        private static int ToSlider(float value, int max)
        {
            return Math.Max(0, Math.Min(max, (int)Math.Round(value)));
        }
    }
}
