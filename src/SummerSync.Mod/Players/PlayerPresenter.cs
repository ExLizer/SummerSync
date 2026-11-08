using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// The players, as far as Unity is concerned: it tells the network where the local player is, and puts an
    /// avatar and a floating name where the network says each of the others is.
    ///
    /// It belongs to <see cref="RuntimeDriver"/> and dies with it. The avatars are children of the driver's
    /// GameObject, so they survive the change from the menu to the game and are swept away, like everything
    /// else, when MSCLoader clears the scene.
    /// </summary>
    internal sealed class PlayerPresenter
    {
        private const float LabelMaxDistance = 300f;
        private const float EchoRight = 1.5f;
        private const float EchoAhead = 2.5f;

        // How often the log hears how a player at the wheel reaches the controls.
        private const double DriverLogEvery = 15.0;

        // First choice first. Which of these the game's build actually contains is only known inside the game.
        private static readonly string[] ShaderNames = { "Legacy Shaders/Diffuse", "Diffuse", "Standard", "Unlit/Color", "Sprites/Default" };

        private static readonly Color[] Colors =
        {
            new Color(1f, 0.55f, 0.1f), new Color(0.2f, 0.8f, 1f), new Color(1f, 0.3f, 0.8f), new Color(0.6f, 1f, 0.2f),
        };

        // How often, in frames, the pairs that must not collide are told so again while anybody pushes.
        private const int HandsEvery = 30;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly Transform parent;
        private readonly LocalPlayerProbe probe;
        private readonly VehiclePresenter vehicles;
        private readonly Dictionary<byte, PlayerAvatar> avatars = new Dictionary<byte, PlayerAvatar>();
        private readonly Dictionary<byte, PlayerPose> dead = new Dictionary<byte, PlayerPose>();
        private readonly List<byte> gone = new List<byte>();
        private readonly GUIContent label = new GUIContent();
        private Shader shader;
        private bool shaderChosen;
        private GUIStyle labelStyle;
        private Camera camera;
        private bool localInWorld;
        private PlayerPose localPose;
        private bool echoPlaced;
        private Vector3 echoOffset;
        private bool localPushed;
        private bool handsReported;
        private int frame;

        // When the log next hears how each driver's hands and feet reach the controls; absent while they do not drive.
        private readonly Dictionary<byte, double> nextDriverLog = new Dictionary<byte, double>();

        /// <summary>Who looks like whom. Set by the driver; without it everyone is a capsule.</summary>
        public LookSync Looks { get; set; }

        /// <summary>The NPCs a player can ride (the bus). Set by the driver; without it a rider is shown nowhere.</summary>
        public World.NpcSync Npcs { get; set; }

        public PlayerPresenter(Logger log, NetworkManager manager, Transform parent, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.parent = parent;
            this.vehicles = vehicles;
            probe = new LocalPlayerProbe(log);
        }

        /// <summary>The local player's root object while they are in the game world, or null.</summary>
        /// <summary>
        /// A player who died is drawn dead where they died, whatever their game says now (<see cref="World.DeathSync"/>):
        /// <paramref name="pose"/> is how, relative to a vehicle or in the world. Null when they are back.
        /// </summary>
        public void SetDead(byte id, PlayerPose? pose)
        {
            if (pose.HasValue)
            {
                dead[id] = pose.Value;
            }
            else
            {
                dead.Remove(id);
            }
        }

        public Transform LocalPlayer
        {
            get { return localInWorld ? probe.Body : null; }
        }

        /// <summary>Call when the driver starts and whenever Unity has loaded a level.</summary>
        public void OnLevelLoaded(string levelName)
        {
            probe.OnLevelLoaded(levelName);
        }

        /// <summary>
        /// Once per frame, after the game has moved everything: reads the local player for the network and
        /// places the others for the camera.
        /// </summary>
        public void LateUpdate(double now)
        {
            localInWorld = probe.TryRead(now, out localPose);
            if (localInWorld)
            {
                // At the wheel, what goes out is where the player sits in the vehicle, not where that is in the world.
                vehicles.Localise(ref localPose);
                if (localPose.Parent == 0 && Npcs != null)
                {
                    // On the bus, likewise: where the player is on it.
                    Npcs.Localise(probe.Body, ref localPose);
                }
            }

            manager.SetLocalPlayer(localInWorld, localPose);

            PlayerReplication players = manager.Players;
            if (players == null)
            {
                Clear();
                return;
            }

            players.Sample(now);
            IList<RemotePlayer> remotes = players.Remotes;
            bool echo = manager.Mode == NetworkMode.LoopbackTest;
            bool handsChanged = false;
            frame++;
            for (int i = 0; i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];

                // Without a body of our own there is no world to put the others in. And a player who rides a
                // vehicle this machine does not have cannot be put anywhere.
                PlayerPose shown;
                bool died = dead.TryGetValue(remote.Id, out shown);
                if (!died)
                {
                    shown = remote.Shown;
                }

                float viewInVehicle = shown.Yaw;
                bool placed = shown.Parent >= PlayerPose.NpcParentBase ? Npcs != null && Npcs.ToWorld(ref shown) : vehicles.ToWorld(ref shown);
                if ((!remote.Visible && !died) || !localInWorld || !placed)
                {
                    PlayerAvatar hidden;
                    if (avatars.TryGetValue(remote.Id, out hidden))
                    {
                        hidden.Hide();
                    }

                    continue;
                }

                PlayerAvatar avatar = AvatarFor(remote);
                Dress(avatar, remote, echo);
                // In a vehicle the view is told relative to it: what the world adds is where the vehicle faces.
                float seatYaw = shown.Parent != 0 ? Mathf.Repeat(shown.Yaw - viewInVehicle, 360f) : float.NaN;
                Cockpit cockpit = shown.Driving && shown.Parent != 0 && shown.Parent < PlayerPose.NpcParentBase ? vehicles.CockpitOf(shown.Parent) : null;
                avatar.Show(shown, echo ? EchoOffset() : Vector3.zero, seatYaw, cockpit);
                ReportDriver(remote, avatar, cockpit, now);

                // Not for the echo of the local player in the loopback test: nothing of the game is touched there.
                if (avatar.SetPushing(shown.Pushing && !echo))
                {
                    handsChanged = true;
                    log.Info(
                        LogCategory.Player,
                        avatar.Pushing
                            ? "Player " + remote.Id + " '" + remote.Name + "' has their hands out to push: this machine holds out hands for them that push what it simulates."
                            : "Player " + remote.Id + " '" + remote.Name + "' no longer pushes.");
                    if (avatar.Pushing && !handsReported)
                    {
                        handsReported = true;
                        log.Info(
                            LogCategory.Player,
                            "Those hands are a ball of radius " + PushHands.Radius.ToString("0.#", CultureInfo.InvariantCulture) + " m and "
                            + PushHands.Mass.ToString("0.#", CultureInfo.InvariantCulture) + " kg on a spring, on layer " + PushHands.Layer
                            + ", which touches layers: " + PushHands.Touches() + ". They pass through the local player and through other hands.");
                    }
                }
            }

            RemoveAvatarsOfPlayersWhoLeft(players);
            KeepHandsApart(handsChanged);
            camera = Camera.main;
        }

        // Once the hands and feet of a player at the wheel are on the controls, and every so often after: how near they are.
        private void ReportDriver(RemotePlayer remote, PlayerAvatar avatar, Cockpit cockpit, double now)
        {
            if (cockpit == null || !avatar.AtTheWheel)
            {
                nextDriverLog.Remove(remote.Id);
                return;
            }

            // The text is only made when it goes to the log: this runs on every frame.
            double next;
            if (nextDriverLog.TryGetValue(remote.Id, out next) && now < next)
            {
                return;
            }

            nextDriverLog[remote.Id] = now + DriverLogEvery;
            log.Info(LogCategory.Player, "Player " + remote.Id + " '" + remote.Name + "' at the wheel of '" + cockpit.Root.name + "': " + avatar.DriverReport + ".");
        }

        /// <summary>Draws the names. Only from OnGUI.</summary>
        public void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || camera == null || avatars.Count == 0)
            {
                return;
            }

            PlayerReplication players = manager.Players;
            if (players == null)
            {
                return;
            }

            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, wordWrap = false, richText = false };
            }

            bool echo = manager.Mode == NetworkMode.LoopbackTest;
            IList<RemotePlayer> remotes = players.Remotes;
            for (int i = 0; i < remotes.Count; i++)
            {
                PlayerAvatar avatar;
                if (!avatars.TryGetValue(remotes[i].Id, out avatar) || !avatar.IsShown)
                {
                    continue;
                }

                Vector3 point = avatar.LabelPoint;
                Vector3 screen = camera.WorldToScreenPoint(point);
                float distance = Vector3.Distance(camera.transform.position, point);
                if (screen.z <= 0f || distance > LabelMaxDistance)
                {
                    continue;
                }

                label.text = avatar.Label(echo ? "Echo of you" : remotes[i].Name, distance);
                Vector2 size = labelStyle.CalcSize(label);
                var area = new Rect(screen.x - (size.x * 0.5f), Screen.height - screen.y - size.y, size.x, size.y);

                // A dark copy one pixel off keeps the name readable against any background.
                labelStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), label, labelStyle);
                labelStyle.normal.textColor = Color.white;
                GUI.Label(area, label, labelStyle);
            }
        }

        // The hands held out for another player push vehicles and whatever else the game's own would push. They
        // must not push the local player, whose capsule the game would throw into the air, nor the local
        // player's own hands, nor those of a third player: two people pushing side by side stand closer than
        // their two balls are wide. Unity forgets that a pair ignores each other whenever one of the two is
        // switched off and on again, so it is said again whenever anybody's hands come out, and now and then.
        private void KeepHandsApart(bool handsChanged)
        {
            bool localPushes = probe.Pushing;
            bool again = handsChanged || localPushes != localPushed || frame % HandsEvery == 0;
            localPushed = localPushes;
            if (!again || avatars.Count == 0)
            {
                return;
            }

            foreach (PlayerAvatar avatar in avatars.Values)
            {
                if (!avatar.IsAlive || !avatar.Pushing)
                {
                    continue;
                }

                Collider ball = avatar.PushBall;
                Ignore(ball, probe.Capsule);
                Ignore(ball, probe.PushBall);
                foreach (PlayerAvatar other in avatars.Values)
                {
                    if (other != avatar && other.IsAlive && other.Pushing)
                    {
                        Ignore(ball, other.PushBall);
                    }
                }
            }
        }

        // Unity refuses a pair in which either collider is not there to collide at the moment.
        private static void Ignore(Collider one, Collider other)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (one == null || other == null || !one.enabled || !other.enabled || !one.gameObject.activeInHierarchy || !other.gameObject.activeInHierarchy)
            {
                return;
            }

            Physics.IgnoreCollision(one, other, true);
        }

        /// <summary>Removes every avatar, e.g. when the session ends.</summary>
        public void Clear()
        {
            if (avatars.Count == 0)
            {
                return;
            }

            foreach (PlayerAvatar avatar in avatars.Values)
            {
                avatar.Destroy();
            }

            avatars.Clear();
            echoPlaced = false;
        }

        private PlayerAvatar AvatarFor(RemotePlayer remote)
        {
            PlayerAvatar avatar;
            if (avatars.TryGetValue(remote.Id, out avatar) && avatar.IsAlive)
            {
                return avatar;
            }

            if (!shaderChosen)
            {
                shaderChosen = true;
                shader = ChooseShader();
            }

            avatar = new PlayerAvatar(parent, "Player " + remote.Id + " (" + remote.Name + ")", shader, Colors[remote.Id % Colors.Length]);
            avatars[remote.Id] = avatar;
            log.Info(LogCategory.Player, "Created the avatar of player " + remote.Id + " '" + remote.Name + "'.");
            return avatar;
        }

        // The person a player chose, made once, when it changes.
        private void Dress(PlayerAvatar avatar, RemotePlayer remote, bool echo)
        {
            int wanted = Looks == null ? -1 : echo ? Looks.Mine : Looks.LookOf(remote.Id);
            if (wanted == avatar.Look)
            {
                return;
            }

            Characters.Look look = Characters.At(wanted);
            CharacterModel person = look == null ? null : CharacterModel.Build(look, parent, "Player " + remote.Id + " as " + look.Name, -1, CharacterTuning.Settings);
            avatar.SetModel(person, wanted);
            log.Info(
                LogCategory.Player,
                look == null
                    ? "Player " + remote.Id + " '" + remote.Name + "' looks like a capsule."
                    : person == null
                        ? "Player " + remote.Id + " '" + remote.Name + "' chose " + look.Name + ", who is not in this world (" + look.Where + "): a capsule."
                        : "Player " + remote.Id + " '" + remote.Name + "' looks like " + look.Name + ": " + person.Describe() + ".");
        }

        private void RemoveAvatarsOfPlayersWhoLeft(PlayerReplication players)
        {
            if (avatars.Count == 0)
            {
                return;
            }

            gone.Clear();
            foreach (byte id in avatars.Keys)
            {
                if (players.Find(id) == null)
                {
                    gone.Add(id);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                avatars[gone[i]].Destroy();
                avatars.Remove(gone[i]);
                log.Info(LogCategory.Player, "Removed the avatar of player " + gone[i] + ".");
            }

            if (avatars.Count == 0)
            {
                echoPlaced = false;
            }
        }

        // In the loopback test the only other player is the local one, a moment ago. Drawn in place it would
        // be inside the camera, so it is drawn a couple of steps ahead and to the right of where the player
        // stood when it first appeared, and from then on it mirrors every movement from there.
        private Vector3 EchoOffset()
        {
            if (!echoPlaced)
            {
                echoPlaced = true;
                Quaternion heading = Quaternion.Euler(0f, localPose.Yaw, 0f);
                echoOffset = heading * new Vector3(EchoRight, 0f, EchoAhead);
            }

            return echoOffset;
        }

        private Shader ChooseShader()
        {
            for (int i = 0; i < ShaderNames.Length; i++)
            {
                Shader found = Shader.Find(ShaderNames[i]);
                if (found != null && found.isSupported)
                {
                    log.Info(LogCategory.Player, "Avatars are drawn with the shader '" + found.name + "'.");
                    return found;
                }
            }

            log.Warning(
                LogCategory.Player,
                "None of the shaders " + string.Join(", ", ShaderNames) + " is in this build of the game. Avatars keep Unity's default material, which may show up pink.");
            return null;
        }
    }
}
