using System;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// Put by the mod on the copy of a vehicle that another machine simulates, the first time a vehicle simulated here comes
    /// near it: tells <see cref="VehiclePresenter"/> what the copy runs into. Unity tells a body's collisions to the
    /// components of its own object, its colliders' below it included.
    /// </summary>
    internal sealed class VehicleContactProbe : MonoBehaviour
    {
        /// <summary>Called with every collision the copy starts.</summary>
        public Action<Collision> Hit;

        private void OnCollisionEnter(Collision collision)
        {
            Action<Collision> hit = Hit;
            if (hit != null)
            {
                hit(collision);
            }
        }
    }
}
