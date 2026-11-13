using System.Globalization;
using System.Text;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// Finds the local player in the game scene and reads where they stand and where they look.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md): the player is the root
    /// object "PLAYER", moved by a CharacterController, and the camera they see through is the object the
    /// game's own "POV" variable points to. Everything else is read from the objects found, and what was found
    /// is written to the log, so that a wrong assumption shows up there instead of as a player in the wrong
    /// place.
    /// </summary>
    internal sealed class LocalPlayerProbe
    {
        private const string GameLevel = "GAME";
        private const string PlayerPath = "/PLAYER";
        private const string EyesPath = "Pivot/AnimPivot/Camera/FPSCamera/FPSCamera";
        private const double SearchInterval = 1.0;
        private const double MissingAfter = 15.0;
        private const float EyeHeightWithoutCamera = 1.6f;

        private readonly Logger log;
        private Transform body;
        private Transform eyes;
        private Transform pushHands;
        private Collider pushBall;
        private CharacterController controller;
        private PlayerActions actions;

        // The game's "PlayerHelmet": the player wears the helmet. Looked up once per world.
        private FsmBool helmet;
        private double nextSearch;
        private double searchingSince = double.NaN;
        private bool missingReported;
        private bool inGameLevel;

        public LocalPlayerProbe(Logger log)
        {
            this.log = log;
        }

        /// <summary>The player's root object, once found. Null in the menu and after the object is destroyed.</summary>
        public Transform Body
        {
            get { return body; }
        }

        /// <summary>The capsule the game moves the player with, or null.</summary>
        public CharacterController Capsule
        {
            get { return controller; }
        }

        /// <summary>The ball the game pushes with, whether the player is pushing or not. Null if the game has none.</summary>
        public Collider PushBall
        {
            get { return pushBall; }
        }

        /// <summary>Whether the player has their hands out to push.</summary>
        public bool Pushing
        {
            // Unity's null check is also true for an object that has been destroyed.
            get { return pushHands != null && pushHands.gameObject.activeInHierarchy; }
        }

        /// <summary>Tells the probe which Unity level is loaded. Only the game scene has a player to find.</summary>
        public void OnLevelLoaded(string levelName)
        {
            inGameLevel = levelName == GameLevel;
            body = null;
            helmet = null;
            searchingSince = double.NaN;
            nextSearch = 0;
        }

        /// <summary>
        /// Reads the local player's pose. False when there is no player to read: in the main menu, while the
        /// game scene is still being set up, or when the player object is switched off.
        /// </summary>
        public bool TryRead(double now, out PlayerPose pose)
        {
            pose = default(PlayerPose);
            if (!inGameLevel)
            {
                return false;
            }

            // Unity's null check is also true for an object that has been destroyed.
            if (body == null)
            {
                if (now < nextSearch)
                {
                    return false;
                }

                nextSearch = now + SearchInterval;
                if (!Find(now))
                {
                    return false;
                }
            }

            if (!body.gameObject.activeInHierarchy)
            {
                return false;
            }

            Vector3 feet = Feet();
            Vector3 forward = body.forward;
            float eyeHeight = EyeHeightWithoutCamera;
            if (eyes != null)
            {
                forward = eyes.forward;
                eyeHeight = eyes.position.y - feet.y;
            }

            pose.X = feet.x;
            pose.Y = feet.y;
            pose.Z = feet.z;
            pose.Yaw = Mathf.Repeat(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 360f);
            pose.Pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            pose.EyeHeight = Mathf.Clamp(eyeHeight, 0.1f, PlayerWire.MaxEyeHeight);
            pose.Pushing = Pushing;
            if (helmet == null)
            {
                helmet = FsmVariables.GlobalVariables.FindFsmBool("PlayerHelmet");
            }

            pose.Helmet = helmet != null && helmet.Value;
            if (actions != null)
            {
                actions.Read(ref pose);
            }

            return true;
        }

        // The point on the ground under the player: the bottom of the capsule the game moves them with.
        private Vector3 Feet()
        {
            if (controller == null)
            {
                return body.position;
            }

            Vector3 centre = body.TransformPoint(controller.center);
            return centre - (Vector3.up * (controller.height * 0.5f * body.lossyScale.y));
        }

        private bool Find(double now)
        {
            GameObject player = GameObject.Find(PlayerPath);
            if (player == null)
            {
                if (double.IsNaN(searchingSince))
                {
                    searchingSince = now;
                }
                else if (!missingReported && now - searchingSince > MissingAfter)
                {
                    missingReported = true;
                    log.Warning(
                        LogCategory.Player,
                        "No active root object 'PLAYER' in the game scene after " + MissingAfter.ToString("0", CultureInfo.InvariantCulture)
                        + " s. The other players will not see this one. Has the game changed, or does another mod rename the player?");
                }

                return false;
            }

            body = player.transform;
            controller = player.GetComponent<CharacterController>();
            eyes = body.Find(EyesPath);
            pushHands = eyes == null ? null : eyes.Find(PushHands.ObjectName);
            Transform ball = pushHands == null ? null : pushHands.Find(PushHands.BallPath);
            pushBall = ball == null ? null : ball.GetComponent<Collider>();
            searchingSince = double.NaN;
            Report(player);
            actions = PlayerActions.Find(body, log);
            return true;
        }

        private void Report(GameObject player)
        {
            log.Info(
                LogCategory.Player,
                "Local player found: 'PLAYER' at " + Format(body.position) + ", scale " + Format(body.lossyScale) + ", layer " + player.layer
                + ", parent " + (body.parent == null ? "none" : "'" + body.parent.name + "'") + ", components: " + ComponentNames(player) + ".");

            if (controller != null)
            {
                log.Info(
                    LogCategory.Player,
                    "Its CharacterController: centre " + Format(controller.center) + ", height " + Format(controller.height) + ", radius "
                    + Format(controller.radius) + ", enabled " + controller.enabled + ". Feet at " + Format(Feet()) + ", "
                    + Format(body.position.y - Feet().y) + " m below the object's origin.");
            }
            else
            {
                log.Warning(LogCategory.Player, "'PLAYER' has no CharacterController: its origin is taken as the feet.");
            }

            if (eyes != null)
            {
                log.Info(
                    LogCategory.Player,
                    "Its eyes: '" + EyesPath + "', " + Format(eyes.position.y - Feet().y) + " m above the feet, components: " + ComponentNames(eyes.gameObject) + ".");
            }
            else
            {
                log.Warning(
                    LogCategory.Player,
                    "'PLAYER' has no child '" + EyesPath + "': the look direction is taken from the body and the eye height is a guess.");
            }

            if (pushHands != null)
            {
                bool asExpected;
                string hands = PushHands.Describe(pushHands, out asExpected);
                if (asExpected)
                {
                    log.Info(LogCategory.Player, "Its hands for pushing: " + hands + ". That layer touches layers: " + PushHands.Touches() + ".");
                }
                else
                {
                    log.Warning(LogCategory.Player, "Its hands for pushing: " + hands + ".");
                }
            }
            else
            {
                log.Warning(
                    LogCategory.Player,
                    "The player's eyes have no child '" + PushHands.ObjectName + "': the others will not be told when this player pushes.");
            }

            Camera main = Camera.main;
            log.Info(
                LogCategory.Player,
                main == null
                    ? "Camera.main is not set."
                    : "Camera.main is '" + main.name + "' (same object as the eyes: " + (eyes != null && main.transform == eyes) + "), culling mask 0x"
                      + main.cullingMask.ToString("X8", CultureInfo.InvariantCulture) + ", near " + Format(main.nearClipPlane) + ", far " + Format(main.farClipPlane) + ".");
        }

        private static string ComponentNames(GameObject target)
        {
            var text = new StringBuilder();
            Component[] components = target.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                // A component whose script is missing comes back as null.
                text.Append(components[i] == null ? "(missing script)" : components[i].GetType().Name);
            }

            return text.ToString();
        }

        private static string Format(Vector3 value)
        {
            return "(" + Format(value.x) + ", " + Format(value.y) + ", " + Format(value.z) + ")";
        }

        private static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
