using System;
using System.Collections.Generic;
using SummerSync.Unity.World;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// A car of the game that drives by itself (the traffic, the bus, the police…) shown on a machine where another
    /// game drives it: kinematic, its controller, engine and driving state machines off, moved to where the other game
    /// has it; its wheels turn and its engine sounds as there. What it changed is undone by <see cref="Restore"/>.
    ///
    /// The joints that the game breaks to kill someone in a crash (the NPC driver's "CrashEvent/DeathForce", a rider's
    /// "DriverHeadPivot") do not break while it is followed: the car is moved here by the mod, not by a crash, and a crash
    /// is the other game's to have. Only how hard they hold is written, as the game itself does, never their anchors.
    /// </summary>
    internal sealed class FollowedCar
    {
        private readonly List<PausedMachine> switchedOff = new List<PausedMachine>();
        private readonly List<Joint> held = new List<Joint>();
        private readonly List<float> heldForce = new List<float>();
        private readonly List<float> heldTorque = new List<float>();
        private readonly string[] driving;
        private readonly Wheel[] wheels;
        private bool madeKinematic;
        private bool controllerOff;
        private bool drivetrainOff;
        private Vector3 shellCentre;
        private Vector3 shellHalf;
        private bool shellKnown;

        /// <param name="driving">The names of the state machines that drive it, switched off while it is followed.</param>
        public FollowedCar(Transform root, Rigidbody body, string[] driving)
        {
            this.driving = driving;
            Root = root;
            Body = body;
            Controller = root.GetComponent<CarController>();
            Drivetrain = root.GetComponent<Drivetrain>();
            Transform brake = root.Find("BrakeLights");
            BrakeLights = brake == null ? null : brake.gameObject;
            wheels = root.GetComponentsInChildren<Wheel>(true);
            InterpolationBefore = body.interpolation;
        }

        public Transform Root { get; }

        /// <summary>"VICTRO#2": its name and how many of that name come before it in the scene.</summary>
        public string Path { get; set; }

        public Rigidbody Body { get; }

        public CarController Controller { get; }

        public Drivetrain Drivetrain { get; }

        public GameObject BrakeLights { get; }

        public RigidbodyInterpolation InterpolationBefore { get; private set; }

        public bool Following { get; private set; }

        public bool Placed { get; set; }

        public void StartFollowing()
        {
            Following = true;
            Placed = false;
            InterpolationBefore = Body.interpolation;
            PlayMakerFSM[] machines = Root.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].enabled && Array.IndexOf(driving, machines[i].FsmName) >= 0)
                {
                    switchedOff.Add(new PausedMachine(machines[i]));
                }
            }

            held.Clear();
            heldForce.Clear();
            heldTorque.Clear();
            Joint[] joints = Root.GetComponentsInChildren<Joint>(true);
            for (int i = 0; i < joints.Length; i++)
            {
                string name = joints[i].gameObject.name;
                if (name == "DeathForce" || name == "DriverHeadPivot")
                {
                    held.Add(joints[i]);
                    heldForce.Add(joints[i].breakForce);
                    heldTorque.Add(joints[i].breakTorque);
                }
            }

            KeepOff();
            MeasureShell();
        }

        /// <summary>What the game may switch back on by itself (its physics, its engine) is switched off again.</summary>
        public void KeepOff()
        {
            if (!Body.isKinematic)
            {
                madeKinematic = true;
                Body.isKinematic = true;
            }

            if (Controller != null && Controller.enabled)
            {
                controllerOff = true;
                Controller.enabled = false;
            }

            if (Drivetrain != null && Drivetrain.enabled)
            {
                drivetrainOff = true;
                Drivetrain.enabled = false;
            }

            // The game sets how hard they hold now and then (with the player's distance, when someone gets in).
            for (int i = 0; i < held.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (held[i] != null && (held[i].breakForce < float.MaxValue || held[i].breakTorque < float.MaxValue))
                {
                    held[i].breakForce = float.MaxValue;
                    held[i].breakTorque = float.MaxValue;
                }
            }
        }

        public void ShowControls(float speed, float rpm, bool braking)
        {
            if (Drivetrain != null)
            {
                Drivetrain.rpm = rpm;
            }

            for (int i = 0; i < wheels.Length; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (wheels[i] != null && wheels[i].radius > 0.01f)
                {
                    wheels[i].angularVelocity = speed / wheels[i].radius;
                }
            }

            if (BrakeLights != null && BrakeLights.activeSelf != braking)
            {
                BrakeLights.SetActive(braking);
            }
        }

        /// <summary>Whether that point is within <paramref name="margin"/> of the car's colliders' box.</summary>
        public bool StandsBeside(Vector3 point, float margin)
        {
            if (!shellKnown)
            {
                return false;
            }

            Vector3 local = Root.InverseTransformPoint(point) - shellCentre;
            return Mathf.Abs(local.x) <= shellHalf.x + margin && Mathf.Abs(local.y) <= shellHalf.y + margin && Mathf.Abs(local.z) <= shellHalf.z + margin;
        }

        public void Restore()
        {
            // Unity's null check is also true for a destroyed object.
            for (int i = 0; i < switchedOff.Count; i++)
            {
                switchedOff[i].Resume();
            }

            for (int i = 0; i < held.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (held[i] != null)
                {
                    held[i].breakForce = heldForce[i];
                    held[i].breakTorque = heldTorque[i];
                }
            }

            held.Clear();

            switchedOff.Clear();
            if (Body != null)
            {
                Body.interpolation = InterpolationBefore;
                if (madeKinematic)
                {
                    Body.isKinematic = false;
                }
            }

            if (controllerOff && Controller != null)
            {
                Controller.enabled = true;
            }

            if (drivetrainOff && Drivetrain != null)
            {
                Drivetrain.enabled = true;
            }

            madeKinematic = false;
            controllerOff = false;
            drivetrainOff = false;
            Following = false;
        }

        // The box of the car's solid colliders, in its own coordinates.
        private void MeasureShell()
        {
            Collider[] colliders = Root.GetComponentsInChildren<Collider>();
            bool any = false;
            var box = new Bounds();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i].isTrigger)
                {
                    continue;
                }

                Bounds world = colliders[i].bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = new Vector3(
                        (corner & 1) == 0 ? world.min.x : world.max.x,
                        (corner & 2) == 0 ? world.min.y : world.max.y,
                        (corner & 4) == 0 ? world.min.z : world.max.z);
                    Vector3 local = Root.InverseTransformPoint(point);
                    if (!any)
                    {
                        box = new Bounds(local, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        box.Encapsulate(local);
                    }
                }
            }

            shellKnown = any;
            shellCentre = box.center;
            shellHalf = box.extents;
        }
    }
}
