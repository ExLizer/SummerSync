using System.Globalization;
using System.Text;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// What a driver works with in one vehicle, and where on each thing a hand or a foot goes: the steering wheel (or
    /// the two grips of a handlebar), the pedals and the gear lever.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "El conductor"): a steering wheel is turned by the game's
    /// component <see cref="SteeringWheel"/>, which sets one angle of its object (z, or y if "rotateAroundY") to where it
    /// stood plus the controller's steering times "maxSteeringAngle", the other way round with "invertRotation". A
    /// pedal is turned by a state machine of its own that sets one of its angles to the controller's input times a
    /// number of degrees; those, and the levers, come from the vehicle's file. Where on a mesh the hand or the foot goes
    /// is measured from the meshes below it: the rim is the widest mesh below the wheel, a pedal's pad and a lever's
    /// knob are the far end of their meshes from where they turn.
    /// </summary>
    internal sealed class Cockpit
    {
        // Below this the "wheel" is the bare column of a car whose steering wheel is not installed.
        private const float SmallestWheel = 0.08f;

        // How far in from the rim's outer edge the hand closes round it, and how far in from the end of a pedal the
        // foot presses and from the top of a lever the hand holds it.
        private const float RimInset = 0.015f;
        private const float PadAlong = 0.85f;
        private const float KnobBelowTop = 0.03f;

        private readonly Transform root;
        private readonly VehicleDefinition definition;
        private readonly Pedal[] pedals;
        private readonly Transform[] levers;
        private readonly Vector3[] knobs;
        private readonly Transform[] grips;
        private SteeringWheel steering;
        private Transform wheel;
        private Vector3 wheelCentre;
        private Vector3 wheelAxis;
        private float wheelRadius;

        private Cockpit(Transform root, VehicleDefinition definition)
        {
            this.root = root;
            this.definition = definition;
            pedals = new Pedal[definition.Pedals.Length];
            for (int i = 0; i < pedals.Length; i++)
            {
                pedals[i] = new Pedal { Data = definition.Pedals[i] };
            }

            levers = new Transform[definition.Levers.Length];
            knobs = new Vector3[levers.Length];
            grips = definition.Grips == null ? null : new Transform[2];
        }

        /// <summary>The vehicle's root object: its right, up and forward are the driver's.</summary>
        public Transform Root
        {
            get { return root; }
        }

        /// <summary>Whether the hands have something to hold: a steering wheel that is there, or the grips of a handlebar.</summary>
        public bool HasHold
        {
            get
            {
                // Unity's null check is also true for a destroyed object.
                if (grips != null)
                {
                    return grips[0] != null && grips[1] != null && grips[0].gameObject.activeInHierarchy && grips[1].gameObject.activeInHierarchy;
                }

                return wheel != null && wheelRadius >= SmallestWheel && wheel.gameObject.activeInHierarchy;
            }
        }

        /// <summary>Whether something to hold was found, switched on or not: a steering wheel with a rim, or both grips.</summary>
        public bool Found
        {
            get { return grips != null ? grips[0] != null && grips[1] != null : wheel != null && wheelRadius >= SmallestWheel; }
        }

        /// <summary>Whether the hands hold a handlebar rather than a wheel.</summary>
        public bool Handlebar
        {
            get { return grips != null; }
        }

        public static Cockpit Find(Transform root, VehicleDefinition definition)
        {
            var cockpit = new Cockpit(root, definition);
            cockpit.Measure();
            return cockpit;
        }

        /// <summary>Looks for everything again and measures it: a part was installed or taken off (the Satsuma's wheel, its gear stick).</summary>
        public void Measure()
        {
            FindWheel();
            for (int i = 0; i < pedals.Length; i++)
            {
                Pedal pedal = pedals[i];
                pedal.At = root.Find(pedal.Data.Path);
                pedal.Pad = pedal.At == null ? Vector3.zero : FarEnd(pedal.At, PadAlong, 0f);
            }

            for (int i = 0; i < levers.Length; i++)
            {
                levers[i] = root.Find(definition.Levers[i]);
                knobs[i] = levers[i] == null ? Vector3.zero : FarEnd(levers[i], 1f, KnobBelowTop);
            }

            if (grips != null)
            {
                grips[0] = root.Find(definition.Grips[0]);
                grips[1] = root.Find(definition.Grips[1]);
            }
        }

        /// <summary>How many degrees the rim is turned from where it stands with the wheels straight, for that steering.</summary>
        public float Turn(float steeringInput)
        {
            if (steering == null)
            {
                return 0f;
            }

            return (steering.invertRotation ? -1f : 1f) * steeringInput * steering.maxSteeringAngle;
        }

        /// <summary>
        /// Where the left (0) or the right (1) hand holds: on the rim, <paramref name="fromTop"/> degrees from the top
        /// towards that hand's side and turned on with the rim by <paramref name="held"/> degrees; or on its grip of a
        /// handlebar. False if there is nothing to hold.
        /// </summary>
        public bool Grip(int side, float fromTop, float held, out Vector3 point)
        {
            point = Vector3.zero;
            if (!HasHold)
            {
                return false;
            }

            if (grips != null)
            {
                point = grips[side].position;
                return true;
            }

            Vector3 centre = wheel.TransformPoint(wheelCentre);
            Vector3 axis = wheel.TransformDirection(wheelAxis).normalized;

            // The top of the rim and its side, as they are with the wheels straight: the vehicle's up and right laid on
            // the wheel's plane.
            Vector3 up = Vector3.ProjectOnPlane(root.up, axis);
            if (up.sqrMagnitude < 1e-6f)
            {
                up = Vector3.ProjectOnPlane(root.forward, axis);
            }

            up.Normalize();
            Vector3 right = Vector3.Cross(axis, up);
            if (Vector3.Dot(right, root.right) < 0f)
            {
                right = -right;
            }

            float angle = (side == 0 ? -fromTop : fromTop) * Mathf.Deg2Rad;
            Vector3 onRim = (up * Mathf.Cos(angle)) + (right * Mathf.Sin(angle));
            point = centre + (Quaternion.AngleAxis(held, axis) * onRim * wheelRadius);
            return true;
        }

        /// <summary>Whether the vehicle has that pedal, there and switched on.</summary>
        public bool HasPedal(DriverInput input)
        {
            Pedal pedal = PedalFor(input);
            return pedal != null && pedal.At != null && pedal.At.gameObject.activeInHierarchy;
        }

        /// <summary>Where a foot presses that pedal now, as it is turned. False if the vehicle has none there.</summary>
        public bool Pad(DriverInput input, out Vector3 point)
        {
            Pedal pedal = PedalFor(input);
            if (pedal == null || pedal.At == null || !pedal.At.gameObject.activeInHierarchy)
            {
                point = Vector3.zero;
                return false;
            }

            point = pedal.At.TransformPoint(pedal.Pad);
            return true;
        }

        /// <summary>Where a hand holds the gear lever now: the first of the vehicle's levers that is there. False if none is.</summary>
        public bool Knob(out Vector3 point)
        {
            for (int i = 0; i < levers.Length; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (levers[i] != null && levers[i].gameObject.activeInHierarchy)
                {
                    point = levers[i].TransformPoint(knobs[i]);
                    return true;
                }
            }

            point = Vector3.zero;
            return false;
        }

        /// <summary>
        /// On a machine that follows the vehicle: turns its pedals as far as the driver presses them, as their own state
        /// machines do on the driver's. Those read the controller, which is switched off here, or the local player's keys.
        /// Called after them, in LateUpdate.
        /// </summary>
        public void ShowPedals(VehicleControls controls)
        {
            for (int i = 0; i < pedals.Length; i++)
            {
                Pedal pedal = pedals[i];

                // Unity's null check is also true for a destroyed object.
                if (pedal.At == null)
                {
                    continue;
                }

                float input = pedal.Data.Input == DriverInput.Clutch ? controls.Clutch : pedal.Data.Input == DriverInput.Brake ? controls.Brake : controls.Throttle;
                Vector3 angles = pedal.At.localEulerAngles;
                angles[pedal.Data.Axis] = input * pedal.Data.Degrees;
                pedal.At.localEulerAngles = angles;
            }
        }

        /// <summary>What was found and measured, for the log.</summary>
        public string Describe()
        {
            var text = new StringBuilder("Driver's place in '").Append(root.name).Append("': ");
            if (grips != null)
            {
                text.Append("handlebar grips ").Append(Named(grips[0], definition.Grips[0])).Append(" and ").Append(Named(grips[1], definition.Grips[1]));
            }
            else if (wheel == null)
            {
                text.Append("NO steering wheel found");
            }
            else
            {
                text.Append("steering wheel '").Append(PathOf(wheel)).Append("' (rim ").Append(Format(wheelRadius)).Append(" m")
                    .Append(wheelRadius < SmallestWheel ? ", too small to hold: none installed?" : string.Empty)
                    .Append(", centre ").Append(Format(wheelCentre)).Append(", turns about its ").Append(steering.rotateAroundY ? "y" : "z")
                    .Append(", ").Append(Format(steering.maxSteeringAngle)).Append(" degrees at full lock").Append(steering.invertRotation ? ", inverted" : string.Empty)
                    .Append(wheel.gameObject.activeInHierarchy ? string.Empty : ", switched off").Append(')');
            }

            for (int i = 0; i < pedals.Length; i++)
            {
                Pedal pedal = pedals[i];
                text.Append("; ").Append(pedal.Data.Input.ToString().ToLowerInvariant()).Append(' ');
                if (pedal.At == null)
                {
                    text.Append("NOT FOUND at '").Append(pedal.Data.Path).Append('\'');
                }
                else
                {
                    text.Append("pad at ").Append(Format(pedal.Pad)).Append(", now at ")
                        .Append(Format(Mathf.DeltaAngle(0f, pedal.At.localEulerAngles[pedal.Data.Axis]))).Append(" degrees of ")
                        .Append(Format(pedal.Data.Degrees)).Append(pedal.At.gameObject.activeInHierarchy ? string.Empty : " (switched off)");
                }
            }

            text.Append(levers.Length == 0 ? "; no gear lever" : "; gear lever");
            for (int i = 0; i < levers.Length; i++)
            {
                text.Append(i == 0 ? " " : ", or ");
                if (levers[i] == null)
                {
                    text.Append("NOT FOUND at '").Append(definition.Levers[i]).Append('\'');
                }
                else
                {
                    text.Append("'").Append(levers[i].name).Append("' knob at ").Append(Format(knobs[i])).Append(levers[i].gameObject.activeInHierarchy ? string.Empty : " (switched off)");
                }
            }

            return text.Append('.').ToString();
        }

        private Pedal PedalFor(DriverInput input)
        {
            for (int i = 0; i < pedals.Length; i++)
            {
                if (pedals[i].Data.Input == input)
                {
                    return pedals[i];
                }
            }

            return null;
        }

        // The game's steering wheel below the root, preferring one that is switched on; its rim is the widest mesh below it.
        private void FindWheel()
        {
            steering = null;
            wheel = null;
            wheelRadius = 0f;
            if (grips != null)
            {
                return;
            }

            SteeringWheel[] found = root.GetComponentsInChildren<SteeringWheel>(true);
            for (int i = 0; i < found.Length; i++)
            {
                if (steering == null || (found[i].gameObject.activeInHierarchy && !steering.gameObject.activeInHierarchy))
                {
                    steering = found[i];
                }
            }

            if (steering == null)
            {
                return;
            }

            wheel = steering.transform;
            wheelAxis = steering.rotateAroundY ? Vector3.up : Vector3.forward;

            // The wheel's plane is its x and the other of y and z; its axis the third.
            int second = steering.rotateAroundY ? 2 : 1;
            int along = steering.rotateAroundY ? 1 : 2;
            MeshFilter[] meshes = wheel.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshes.Length; i++)
            {
                Bounds box;
                if (!SwitchedOnBelow(meshes[i].transform, wheel) || !LocalBox(wheel, meshes[i], out box))
                {
                    continue;
                }

                // A rim is round: as wide one way as the other on the wheel's plane. The narrower way is its size.
                float radius = Mathf.Min(box.extents[0], box.extents[second]) - RimInset;
                if (radius > wheelRadius)
                {
                    wheelRadius = radius;

                    // The rim is at the end of the mesh that faces the driver, towards the back of the vehicle: the mesh may
                    // go on along the column into the dashboard (the van's goes 0.95 m).
                    Vector3 centre = box.center;
                    float back = Vector3.Dot(root.InverseTransformDirection(wheel.TransformDirection(wheelAxis)), Vector3.back) >= 0f ? 1f : -1f;
                    centre[along] = centre[along] + (back * Mathf.Max(0f, box.extents[along] - RimInset));
                    wheelCentre = centre;
                }
            }
        }

        // The far end of the meshes below an object from the point it turns about, that far along (1 the very end),
        // less a little; in that object's own coordinates. Where it turns about, if it is in the middle of its meshes.
        private static Vector3 FarEnd(Transform at, float along, float less)
        {
            MeshFilter[] meshes = at.GetComponentsInChildren<MeshFilter>(true);
            bool any = false;
            var all = new Bounds();
            for (int i = 0; i < meshes.Length; i++)
            {
                Bounds box;
                if (!SwitchedOnBelow(meshes[i].transform, at) || !LocalBox(at, meshes[i], out box))
                {
                    continue;
                }

                if (any)
                {
                    all.Encapsulate(box);
                }
                else
                {
                    all = box;
                    any = true;
                }
            }

            if (!any || all.center.magnitude < 0.02f)
            {
                return any ? all.center : Vector3.zero;
            }

            Vector3 direction = all.center.normalized;
            float far = 0f;
            Vector3 min = all.min;
            Vector3 max = all.max;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? min.x : max.x, (c & 2) == 0 ? min.y : max.y, (c & 4) == 0 ? min.z : max.z);
                far = Mathf.Max(far, Vector3.Dot(corner, direction));
            }

            return direction * Mathf.Max(0f, (far * along) - less);
        }

        // Whether nothing from that object up to the one above it is switched off by itself. What is switched off only
        // because something above (the detail far from the player, "LOD") is, counts: it is there when that comes on. A
        // part that is not installed is a copy switched off by itself (the Satsuma's steering wheels).
        private static bool SwitchedOnBelow(Transform at, Transform top)
        {
            for (Transform up = at; up != null && up != top; up = up.parent)
            {
                if (!up.gameObject.activeSelf)
                {
                    return false;
                }
            }

            return true;
        }

        // The box of one mesh in the coordinates of an object above it.
        private static bool LocalBox(Transform at, MeshFilter mesh, out Bounds box)
        {
            box = new Bounds();
            if (mesh == null || mesh.sharedMesh == null)
            {
                return false;
            }

            Bounds own = mesh.sharedMesh.bounds;
            Vector3 min = own.min;
            Vector3 max = own.max;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? min.x : max.x, (c & 2) == 0 ? min.y : max.y, (c & 4) == 0 ? min.z : max.z);
                Vector3 local = at.InverseTransformPoint(mesh.transform.TransformPoint(corner));
                if (c == 0)
                {
                    box = new Bounds(local, Vector3.zero);
                }
                else
                {
                    box.Encapsulate(local);
                }
            }

            return true;
        }

        private string PathOf(Transform at)
        {
            string path = at.name;
            for (Transform up = at.parent; up != null && up != root; up = up.parent)
            {
                path = up.name + "/" + path;
            }

            return path;
        }

        private static string Named(Transform at, string path)
        {
            return at == null ? "NOT FOUND at '" + path + "'" : "'" + at.name + "'";
        }

        private static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Format(Vector3 value)
        {
            return "(" + Format(value.x) + ", " + Format(value.y) + ", " + Format(value.z) + ")";
        }

        private sealed class Pedal
        {
            public VehiclePedal Data;
            public Transform At;
            public Vector3 Pad;
        }
    }
}
