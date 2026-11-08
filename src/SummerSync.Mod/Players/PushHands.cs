using System.Globalization;
using System.Text;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// What the game holds out in front of its player when they push something heavy (the "Push" action), and
    /// the same thing built in front of another player's avatar.
    ///
    /// From the game's level file, read on 2026-10-13 (docs/GAME_NOTES.md): under the player's eyes there is an
    /// object "Hand Push", switched on while pushing. Its child "Pivot" slides forward when it comes on, and
    /// carries two bodies: "RigidBody", kinematic, and "Collider", a ball that weighs something and hangs from
    /// the first by a spring with no length. The ball is on a layer of its own, which the hull of every vehicle
    /// is on as well. Nothing else is involved: what pushes a vehicle is that ball, by touching it, as hard as
    /// the spring is stretched.
    ///
    /// A player's own ball only pushes what their own machine simulates. So every machine builds one in front
    /// of each other player who has their hands out: on the machine that simulates the vehicle, everybody's
    /// push arrives and adds up.
    /// </summary>
    internal static class PushHands
    {
        /// <summary>Path of the game's object, below the player's eyes.</summary>
        public const string ObjectName = "Hand Push";

        /// <summary>Path, below that object, of the ball that does the pushing.</summary>
        public const string BallPath = "Pivot/Collider";

        private const string AnchorPath = "Pivot/RigidBody";

        /// <summary>The layer of the ball, and of the hulls it pushes.</summary>
        public const int Layer = 17;

        public const float Radius = 0.5f;
        public const float Mass = 5f;
        public const float Drag = 2f;
        public const float AngularDrag = 2f;
        public const float Spring = 5000f;
        public const float Damper = 1f;

        /// <summary>
        /// Where the ball rests, measured from the eyes: where "Hand Push" is, plus where its "Pivot" ends up
        /// once the animation that brings the hands out has finished.
        /// </summary>
        public static readonly Vector3 Rest = new Vector3(0.035f, -0.036f, 0.114f) + new Vector3(0f, -0.178f, 0.193f);

        /// <summary>Where the kinematic body is, measured from where the ball rests.</summary>
        public static readonly Vector3 Anchor = new Vector3(0f, 0.057f, 0.416f);

        /// <summary>
        /// Builds, switched off, the hands of another player under <paramref name="eyes"/>: a transform at
        /// their eyes that looks where they look.
        /// </summary>
        public static GameObject Build(Transform eyes, out Rigidbody ball, out SphereCollider touch)
        {
            var hands = new GameObject("Push");
            hands.SetActive(false);
            hands.transform.parent = eyes;
            hands.transform.localPosition = Rest;
            hands.transform.localRotation = Quaternion.identity;

            var held = new GameObject("Anchor");
            held.transform.parent = hands.transform;
            held.transform.localPosition = Anchor;
            held.transform.localRotation = Quaternion.identity;
            Rigidbody anchor = held.AddComponent<Rigidbody>();
            anchor.isKinematic = true;
            anchor.useGravity = false;
            anchor.mass = 1f;

            var round = new GameObject("Ball");
            round.layer = Layer;
            round.transform.parent = hands.transform;
            round.transform.localPosition = Vector3.zero;
            round.transform.localRotation = Quaternion.identity;
            touch = round.AddComponent<SphereCollider>();
            touch.radius = Radius;
            ball = round.AddComponent<Rigidbody>();
            ball.mass = Mass;
            ball.drag = Drag;
            ball.angularDrag = AngularDrag;
            ball.useGravity = true;
            ball.collisionDetectionMode = CollisionDetectionMode.Continuous;

            SpringJoint spring = round.AddComponent<SpringJoint>();
            spring.autoConfigureConnectedAnchor = false;
            spring.connectedBody = anchor;
            spring.anchor = Vector3.zero;
            spring.connectedAnchor = -Anchor;
            spring.spring = Spring;
            spring.damper = Damper;
            spring.minDistance = 0f;
            spring.maxDistance = 0f;
            spring.enableCollision = false;
            return hands;
        }

        /// <summary>
        /// Describes the game's own object as found under the local player's eyes, for the log, and says
        /// whether it is what this class builds. A game update that changes it shows up here.
        /// </summary>
        public static string Describe(Transform hands, out bool asExpected)
        {
            asExpected = false;
            var text = new StringBuilder(260);
            Vector3 at = hands.localPosition;
            text.Append("'").Append(ObjectName).Append("' at (").Append(Format(at.x)).Append(", ").Append(Format(at.y)).Append(", ").Append(Format(at.z))
                .Append(") from the eyes");
            Transform round = hands.Find(BallPath);
            Transform held = hands.Find(AnchorPath);
            SphereCollider touch = round == null ? null : round.GetComponent<SphereCollider>();
            Rigidbody ball = round == null ? null : round.GetComponent<Rigidbody>();
            SpringJoint spring = round == null ? null : round.GetComponent<SpringJoint>();
            if (touch == null || ball == null || spring == null || held == null)
            {
                text.Append("; NOT built as this build expects ('").Append(BallPath).Append("' with a SphereCollider, a Rigidbody and a SpringJoint, and '")
                    .Append(AnchorPath).Append("'): the hands shown for other players may push differently from the game's");
                return text.ToString();
            }

            Vector3 anchor = held.localPosition - round.localPosition;
            text.Append("; ball of radius ").Append(Format(touch.radius)).Append(" on layer ").Append(round.gameObject.layer).Append(", ")
                .Append(Format(ball.mass)).Append(" kg, drag ").Append(Format(ball.drag)).Append(", spring ").Append(Format(spring.spring))
                .Append(", damper ").Append(Format(spring.damper)).Append(", anchor (").Append(Format(anchor.x)).Append(", ").Append(Format(anchor.y))
                .Append(", ").Append(Format(anchor.z)).Append(") from it");
            asExpected = Near(touch.radius, Radius) && round.gameObject.layer == Layer && Near(ball.mass, Mass) && Near(ball.drag, Drag)
                && Near(spring.spring, Spring) && Near(spring.damper, Damper) && (anchor - Anchor).sqrMagnitude < 0.0001f
                && (at - new Vector3(0.035f, -0.036f, 0.114f)).sqrMagnitude < 0.0001f;
            text.Append(asExpected ? ": as this build expects" : ": NOT what this build builds for other players, whose hands may push differently");
            return text.ToString();
        }

        /// <summary>The layers that the ball's layer does not pass through, for the log.</summary>
        public static string Touches()
        {
            var text = new StringBuilder(80);
            for (int layer = 0; layer < 32; layer++)
            {
                if (!Physics.GetIgnoreLayerCollision(Layer, layer))
                {
                    text.Append(text.Length == 0 ? string.Empty : ", ").Append(layer);
                }
            }

            return text.Length == 0 ? "none" : text.ToString();
        }

        private static bool Near(float value, float expected)
        {
            return Mathf.Abs(value - expected) <= 0.001f + (Mathf.Abs(expected) * 0.001f);
        }

        private static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
