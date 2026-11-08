using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Steamworks;
using SummerSync.Core;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using UnityEngine;

namespace SummerSync.Unity.Net
{
    /// <summary>
    /// The game-side half of networking: it gives the <see cref="NetworkManager"/> a clock, a player name and
    /// transports (Steam, UDP), and deals with what only exists inside the game: Steam's lobby and callbacks,
    /// and MSCLoader tearing the world down when the player goes back to the main menu.
    ///
    /// One instance lives for the whole process, in <see cref="ModRuntime"/>.
    /// </summary>
    internal sealed class NetworkController
    {
        public const int DefaultUdpPort = 27016;
        public const string UdpTransportName = "udp";

        private const double SteamRetrySeconds = 2.0;
        private const double NoticeSeconds = 20.0;
        private const double LobbyWarningSeconds = 10.0;
        private const double ConditionsSettleSeconds = 1.0;
        private const string GameAssemblyName = "Assembly-CSharp";
        private const string SteamManagerTypeName = "SteamManager";

        private readonly Logger log;
        private readonly SteadyClock clock = new SteadyClock();
        private readonly NetworkManager manager;
        private SteamLobby lobby;
        private SteamTransport steamTransport;
        private double nextSteamCheck;
        private bool suspended;

        // Why Steam cannot be used, in words for the player; null while it works or it is too early to say.
        private string steamProblem;
        private int steamFailures;
        private bool launchArgumentsChecked;
        private bool keepsRunningInBackground;
        private bool runInBackgroundBefore;
        private string notice;
        private double noticeUntil;
        private double lobbyRequestedAt;
        private bool lobbyDelayReported;
        private double conditionsLogDue = double.PositiveInfinity;
        private int clockJumpsLogged;

        public NetworkController(Logger log)
        {
            this.log = log;
            // The build, not only the version: two builds of 0.2.0 that share different things joined, and each warned on
            // every round of the checksums that its lists were not the other's.
            manager = new NetworkManager(log) { ModVersion = UpdateCheck.TagOf(BuildInfo.InformationalVersion) };
            manager.Stopped += OnStopped;
        }

        public NetworkManager Manager
        {
            get { return manager; }
        }

        /// <summary>The clock the network runs on, in seconds. Anything timed against the network uses it too.</summary>
        public double Now
        {
            get { return clock.Seconds; }
        }

        /// <summary>
        /// Called by every new mod instance. After the first one this means MSCLoader rebuilt the world, which
        /// restarted Steam: whatever was registered with the old Steam session is dead and must be redone.
        /// </summary>
        public void OnAttach()
        {
            Shutdown("a new mod instance attached");
            suspended = false;
            nextSteamCheck = 0;
            steamProblem = null;
            steamFailures = 0;
            if (manager.GameVersion.Length == 0)
            {
                manager.GameVersion = ReadGameVersion();
                log.Info(
                    LogCategory.Net,
                    "Session identity: mod " + manager.ModVersion + ", game " + manager.GameVersion + ", protocol " + Protocol.Version
                    + ". The game runs in the background: " + Application.runInBackground + ".");
            }
        }

        /// <summary>Call once per frame, in every scene.</summary>
        public void Update()
        {
            double now = clock.Seconds;
            if (clock.Jumps != clockJumpsLogged)
            {
                clockJumpsLogged = clock.Jumps;
                log.Warning(LogCategory.Net, "The game's stopwatch jumped (it does every 51 hours a machine is up); the network's clock goes on from the system's tick count, at " + now.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s.");
            }

            if (lobby == null && !suspended && now >= nextSteamCheck)
            {
                nextSteamCheck = now + SteamRetrySeconds;
                TryAttachSteam();
            }

            manager.Update(now);
            WarnIfLobbyIsLate(now);
            if (now >= conditionsLogDue)
            {
                conditionsLogDue = double.PositiveInfinity;
                log.Info(LogCategory.Net, "Network simulator: " + NetworkStatus.DescribeConditions(manager.Conditions) + ".");
            }
        }

        public void StartLoopbackTest()
        {
            if (RequireOffline("start the loopback test"))
            {
                manager.StartLoopbackTest(PlayerName());
                OnStarted("Loopback self-test running: a host and a client inside this game.");
            }
        }

        public void HostSteam()
        {
            if (!RequireOffline("host on Steam") || !RequireSteam("host on Steam"))
            {
                return;
            }

            try
            {
                lobby.Leave();
                steamTransport = new SteamTransport(log, IsLobbyMember);
                manager.StartHost(steamTransport, SteamTransport.Name, PlayerName());
                OnStarted("Hosting on Steam. Creating the lobby...");
                lobby.Create(manager.MaxPlayers);
                lobbyRequestedAt = clock.Seconds;
                lobbyDelayReported = false;
                log.Info(LogCategory.Net, "Steam callbacks are delivered by " + DescribeSteamPump() + ".");
            }
            catch (Exception exception)
            {
                Fail("Could not host on Steam", exception, true);
            }
        }

        /// <summary>Whether this game is in a Steam lobby (one it hosts, once Steam made it): then friends can be invited.</summary>
        public bool SteamLobbyReady
        {
            get { return lobby != null && lobby.InLobby; }
        }

        public void InviteFriend()
        {
            if (lobby == null || !lobby.InLobby)
            {
                Report(LogLevel.Warning, "No Steam lobby to invite to. Host on Steam first; the lobby takes a moment to appear.");
                return;
            }

            try
            {
                lobby.OpenInviteDialog();
                log.Info(LogCategory.Net, "Opened the Steam invite dialog for lobby " + lobby.LobbyId + ".");
            }
            catch (Exception exception)
            {
                Fail("Could not open the Steam invite dialog", exception, false);
            }
        }

        /// <param name="lan">Listen on every network interface instead of only this machine.</param>
        public void HostUdp(int port, bool lan)
        {
            if (!RequireOffline("host on UDP"))
            {
                return;
            }

            try
            {
                var transport = new UdpTransport(lan ? IPAddress.Any : IPAddress.Loopback, port);
                manager.StartHost(transport, UdpTransportName, PlayerName());
                OnStarted("Hosting on UDP port " + transport.LocalPort + (lan ? " (LAN)." : " (this machine only)."));
            }
            catch (SocketException exception)
            {
                Report(LogLevel.Error, "Could not listen on UDP port " + port + ": " + exception.Message + " Is another copy of the game already hosting?");
            }
            catch (Exception exception)
            {
                Fail("Could not host on UDP", exception, true);
            }
        }

        public void JoinUdp(string address, int port)
        {
            if (!RequireOffline("join over UDP"))
            {
                return;
            }

            IPAddress host;
            if (!IPAddress.TryParse(address, out host) || host.AddressFamily != AddressFamily.InterNetwork)
            {
                Report(LogLevel.Error, "'" + address + "' is not an IPv4 address.");
                return;
            }

            try
            {
                // A socket bound to the loopback address cannot reach other machines, and one bound to every
                // interface makes Windows ask about the firewall: use the narrowest one that works.
                var transport = new UdpTransport(IPAddress.IsLoopback(host) ? IPAddress.Loopback : IPAddress.Any, 0);
                manager.StartClient(transport, UdpTransportName, UdpTransport.PeerId(host, port), PlayerName());
                OnStarted("Joining " + host + ":" + port + " over UDP...");
            }
            catch (Exception exception)
            {
                Fail("Could not join over UDP", exception, true);
            }
        }

        public void Leave()
        {
            if (manager.IsActive)
            {
                manager.Stop();
            }
            else
            {
                Report(LogLevel.Info, "Not in a session.");
            }
        }

        public void SetConditions(float latencyMs, float jitterMs, float lossPercent)
        {
            NetworkConditions conditions = manager.Conditions;
            if (conditions.LatencyMs == latencyMs && conditions.JitterMs == jitterMs && conditions.LossPercent == lossPercent)
            {
                return;
            }

            conditions.LatencyMs = latencyMs;
            conditions.JitterMs = jitterMs;
            conditions.LossPercent = lossPercent;

            // Dragging a slider calls this for every step on the way. The new values apply at once; the log
            // gets one line when they stop changing.
            conditionsLogDue = clock.Seconds + ConditionsSettleSeconds;
        }

        /// <summary>
        /// Ends the session and lets go of everything registered with Steam. Called while Steam is still up when
        /// the game leaves for the main menu or quits, so the other players are told instead of timing out.
        /// Nothing is attached again until the next <see cref="OnAttach"/>.
        /// </summary>
        public void Shutdown(string why)
        {
            suspended = true;
            if (manager.IsActive)
            {
                log.Info(LogCategory.Net, "Closing the session: " + why + ".");
                manager.Stop();
            }

            if (lobby != null)
            {
                lobby.Leave();
                lobby.Dispose();
                lobby = null;
                log.Info(LogCategory.Net, "Released the Steam callbacks: " + why + ".");
            }
        }

        /// <summary>Drops the session after an unexpected failure, without letting a second failure escape.</summary>
        public void AbortSession()
        {
            try
            {
                if (manager.IsActive)
                {
                    manager.Stop();
                }
            }
            catch (Exception exception)
            {
                log.Error(LogCategory.Net, "Closing the session failed as well: " + exception.Message);
            }
        }

        /// <summary>Lines for the overlay and for "ss status", separated by '\n'.</summary>
        public void AppendStatus(StringBuilder text)
        {
            NetworkStatus.Describe(manager, text);
            if (manager.Mode == NetworkMode.Host && manager.TransportName == SteamTransport.Name)
            {
                text.Append("\n Steam lobby: ").Append(DescribeLobby());
            }

            if (steamTransport != null && steamTransport.SendFailures > 0)
            {
                text.Append("\n Steam refused ").Append(steamTransport.SendFailures).Append(" send(s)");
            }

            // Always on screen while it lasts: a player who started the game the wrong way would otherwise only
            // find out when an invite silently does nothing.
            bool noticeShown = notice != null && clock.Seconds < noticeUntil;
            if (steamProblem != null && !(noticeShown && notice.IndexOf(steamProblem, StringComparison.Ordinal) >= 0))
            {
                text.Append('\n').Append(steamProblem);
            }

            if (noticeShown)
            {
                text.Append('\n').Append(notice);
            }
        }

        private static string Describe(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.ClosedByUser:
                    return "session closed";
                case DisconnectReason.HostClosed:
                    return "the host closed the session";
                case DisconnectReason.Timeout:
                    return "connection lost (timeout)";
                case DisconnectReason.ConnectFailed:
                    return "could not reach the host";
                case DisconnectReason.ServerFull:
                    return "the session is full";
                case DisconnectReason.ProtocolMismatch:
                case DisconnectReason.ModVersionMismatch:
                    return "different SummerSync versions";
                case DisconnectReason.GameVersionMismatch:
                    return "different game versions";
                default:
                    return reason.ToString();
            }
        }

        private void TryAttachSteam()
        {
            try
            {
                // Throws while Steam is not initialized, which is how its state is found out.
                ulong self = SteamUser.GetSteamID().m_SteamID;
                var created = new SteamLobby(log);
                created.JoinRequested += OnJoinRequested;
                created.JoinedAsGuest += OnJoinedAsGuest;
                created.CreateFailed += OnLobbyCreateFailed;
                created.MembersChanged += OnLobbyMembersChanged;
                lobby = created;
                steamProblem = null;
                steamFailures = 0;

                // The id of that object changes whenever the game re-creates it, which is when Steam restarts.
                log.Info(
                    LogCategory.Net,
                    "Steam is ready: '" + SteamFriends.GetPersonaName() + "' (" + self + "), app build " + SteamApps.GetAppBuildId()
                    + ", scene '" + Application.loadedLevelName + "', callbacks delivered by " + DescribeSteamPump() + ".");
                CheckLaunchArguments();
            }
            catch (InvalidOperationException)
            {
                ReportSteamUnavailable(DescribeMissingSteam());
            }
            catch (Exception exception)
            {
                ReportSteamUnavailable("Steam cannot be used in this copy of the game (" + exception.GetType().Name + ": " + exception.Message + ").");
            }
        }

        // Seen in the game: started from its .exe instead of from Steam, the game runs with Steam switched off
        // (it has no application id to give Steam), and so do MSCLoader and every mod.
        private static string DescribeMissingSteam()
        {
            bool clientRunning;
            try
            {
                clientRunning = SteamAPI.IsSteamRunning();
            }
            catch (Exception)
            {
                clientRunning = false;
            }

            return clientRunning
                ? "Steam did not start in this copy of the game. Close it and start it from the Steam library, not from the .exe or a shortcut."
                : "The Steam client is not running. Start Steam and launch the game from its library.";
        }

        /// <summary>
        /// Names the object carrying the game's SteamManager component, which is what calls Steam every frame to
        /// deliver callbacks. Only for the log: it is looked up by component type (through reflection, the class
        /// is internal to the game) because the object is not called "SteamManager" in the real game.
        /// </summary>
        private static string DescribeSteamPump()
        {
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != GameAssemblyName)
                    {
                        continue;
                    }

                    Type type = assembly.GetType(SteamManagerTypeName);
                    if (type == null)
                    {
                        return "nothing known (the game has no " + SteamManagerTypeName + " type)";
                    }

                    var active = UnityEngine.Object.FindObjectOfType(type) as Component;
                    if (active != null)
                    {
                        return "'" + active.gameObject.name + "' #" + active.gameObject.GetInstanceID();
                    }

                    UnityEngine.Object[] any = Resources.FindObjectsOfTypeAll(type);
                    return any.Length == 0 ? "nothing yet (no " + SteamManagerTypeName + " exists)" : "nothing (the " + SteamManagerTypeName + " is inactive)";
                }

                return "nothing known (" + GameAssemblyName + " is not loaded)";
            }
            catch (Exception exception)
            {
                return "nothing known (" + exception.GetType().Name + ")";
            }
        }

        // Judged by the symptom, not by guessing who delivers the callbacks: a lobby normally appears in well
        // under a second.
        private void WarnIfLobbyIsLate(double now)
        {
            if (lobbyDelayReported || lobby == null || lobby.InLobby || now - lobbyRequestedAt < LobbyWarningSeconds
                || manager.Mode != NetworkMode.Host || manager.TransportName != SteamTransport.Name)
            {
                return;
            }

            lobbyDelayReported = true;
            Report(
                LogLevel.Warning,
                "Steam has not created the lobby after " + LobbyWarningSeconds + " seconds. Steam callbacks are delivered by " + DescribeSteamPump() + ".");
        }

        private void ReportSteamUnavailable(string problem)
        {
            // The first failed look can be a matter of timing (Steam restarts when the main menu reloads), so
            // nothing is said until the next one, a couple of seconds later, fails too.
            steamFailures++;
            if (steamFailures < 2 || steamProblem == problem)
            {
                return;
            }

            steamProblem = problem;
            log.Warning(LogCategory.Net, problem + " Until then only the loopback test and local UDP work.");
        }

        // Accepting an invite while the game is closed makes Steam start it with the lobby on the command line.
        private void CheckLaunchArguments()
        {
            if (launchArgumentsChecked)
            {
                return;
            }

            launchArgumentsChecked = true;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                ulong lobbyId;
                if (arguments[i] == "+connect_lobby" && ulong.TryParse(arguments[i + 1], out lobbyId))
                {
                    log.Info(LogCategory.Net, "The game was started from a Steam invite to lobby " + lobbyId + ".");
                    OnJoinRequested(lobbyId);
                    return;
                }
            }
        }

        private void OnJoinRequested(ulong lobbyId)
        {
            if (manager.Mode == NetworkMode.LoopbackTest)
            {
                manager.Stop();
            }

            if (manager.IsActive)
            {
                Report(LogLevel.Warning, "Ignored a Steam invite: already in a session. Disconnect first, then accept it again.");
                return;
            }

            try
            {
                lobby.Leave();
                lobby.Join(lobbyId);
                Notify("Joining a friend's Steam lobby...");
            }
            catch (Exception exception)
            {
                Fail("Could not join the Steam lobby", exception, false);
            }
        }

        private void OnJoinedAsGuest(ulong host)
        {
            if (manager.IsActive)
            {
                log.Warning(LogCategory.Net, "Entered a Steam lobby while a session is running; staying in the current session.");
                return;
            }

            try
            {
                steamTransport = new SteamTransport(log, peer => peer == host || IsLobbyMember(peer));
                manager.StartClient(steamTransport, SteamTransport.Name, host, PlayerName());
                OnStarted("Connecting to " + SteamFriends.GetFriendPersonaName(new CSteamID(host)) + " over Steam...");
            }
            catch (Exception exception)
            {
                Fail("Could not connect to the lobby's host", exception, true);
            }
        }

        private void OnLobbyCreateFailed(string reason)
        {
            // Stopping reports "disconnected"; the reason goes last so that it is the one left on screen.
            manager.Stop();
            Report(LogLevel.Error, "Steam could not create the lobby (" + reason + "). The session was closed.");
        }

        private void OnLobbyMembersChanged()
        {
            if (steamTransport != null)
            {
                steamTransport.RetryDeferredRequests();
            }
        }

        private string DescribeLobby()
        {
            if (lobby == null || !lobby.InLobby)
            {
                return "being created...";
            }

            try
            {
                return lobby.LobbyId + ", " + lobby.MemberCount + " member(s). Invite a friend now.";
            }
            catch (InvalidOperationException)
            {
                // Asked for the overlay while Steam is going down.
                return lobby.LobbyId.ToString();
            }
        }

        private bool IsLobbyMember(ulong steamId)
        {
            return lobby != null && lobby.IsMember(steamId);
        }

        private void OnStarted(string message)
        {
            // Unity stops the game loop when its window loses focus unless told otherwise. A paused player
            // stops answering and drops out, and a paused host freezes the world for everybody.
            if (!keepsRunningInBackground)
            {
                keepsRunningInBackground = true;
                runInBackgroundBefore = Application.runInBackground;
                Application.runInBackground = true;
            }

            Notify(message);
        }

        private void OnStopped(NetworkMode mode, DisconnectReason reason, string text)
        {
            steamTransport = null;
            if (lobby != null)
            {
                lobby.Leave();
            }

            if (keepsRunningInBackground)
            {
                keepsRunningInBackground = false;
                Application.runInBackground = runInBackgroundBefore;
            }

            if (reason == DisconnectReason.ClosedByUser)
            {
                Notify("Disconnected.");
            }
            else
            {
                Report(LogLevel.Warning, "Disconnected: " + Describe(reason) + ". " + text);
            }
        }

        private bool RequireOffline(string action)
        {
            if (!manager.IsActive)
            {
                return true;
            }

            Report(LogLevel.Warning, "Cannot " + action + ": a session is already running (" + manager.Mode + " over " + manager.TransportName + "). Disconnect first.");
            return false;
        }

        private bool RequireSteam(string action)
        {
            if (lobby != null)
            {
                return true;
            }

            Report(LogLevel.Error, "Cannot " + action + ". " + (steamProblem ?? "Steam is not ready yet: try again in a moment."));
            return false;
        }

        private string PlayerName()
        {
            if (lobby != null)
            {
                try
                {
                    string name = SteamFriends.GetPersonaName();
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Steam went away since the last check; fall through to the generic name.
                }
            }

            return "Player";
        }

        /// <summary>
        /// Identifies the game build from its main assembly rather than from Steam's build id, so it also works
        /// in a copy of the game that runs without Steam.
        /// </summary>
        private string ReadGameVersion()
        {
            try
            {
                string path = Path.Combine(Path.Combine(Application.dataPath, "Managed"), GameBuild.MainAssemblyFileName);
                return GameBuild.Identify(Application.unityVersion, File.ReadAllBytes(path));
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "Could not identify the game build: " + exception.Message);
                return Application.unityVersion + "/unknown";
            }
        }

        private void Fail(string what, Exception exception, bool stopSession)
        {
            if (stopSession && manager.IsActive)
            {
                manager.Stop();
            }

            // After stopping, so that this message and not "disconnected" is the one left on screen.
            Report(LogLevel.Error, what + ": " + exception.Message);
            log.Debug(LogCategory.Net, exception.ToString());
        }

        private void Report(LogLevel level, string message)
        {
            log.Log(level, LogCategory.Net, message);
            Notify(message);
        }

        private void Notify(string message)
        {
            notice = message;
            noticeUntil = clock.Seconds + NoticeSeconds;
        }
    }
}
