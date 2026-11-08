using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// A test aid: takes the local player somewhere at once, on foot.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md): the player is the root object "PLAYER", moved by a
    /// CharacterController whose capsule ends 0.25 m below the object's origin; the global string
    /// "PlayerCurrentVehicle" names the vehicle the player sits in, empty on foot. The shop is the root object
    /// "STORE"; its child "SpawnToStore" (level file, 2026-10-14) is 6.5 m in front of the till, where the player
    /// is put, on whatever is under it.
    /// </summary>
    internal static class Travel
    {
        private const string StorePath = "STORE";
        private const string StoreSpot = "SpawnToStore";
        private const string VehicleVariable = "PlayerCurrentVehicle";

        // From this high above the spot down to this far below, the first solid thing is the ground.
        private const float LookFrom = 2f;
        private const float LookDown = 12f;

        // The capsule's foot is this far below the player's origin, and a little air under it.
        private const float FeetBelowOrigin = 0.25f;
        private const float Clearance = 0.05f;

        /// <summary>Takes the local player to <paramref name="place"/>. Returns what happened, for the console.</summary>
        public static string To(string place, Transform player, VehiclePresenter vehicles, Logger log)
        {
            if (player == null)
            {
                return "There is no game world yet.";
            }

            string wanted = (place ?? string.Empty).Trim().ToLowerInvariant();
            if (wanted != "store" && wanted != "shop" && wanted != "tienda" && wanted != "teimo")
            {
                return "Only 'ss tp store' for now (also 'tienda').";
            }

            FsmString seat = FsmVariables.GlobalVariables.FindFsmString(VehicleVariable);
            ushort carrier;
            Transform carrierRoot;
            if ((seat != null && !string.IsNullOrEmpty(seat.Value)) || player.parent != null || (vehicles != null && vehicles.TryGetCarrier(player.position, out carrier, out carrierRoot)))
            {
                return "Get out of the vehicle first: on foot only.";
            }

            GameObject store = GameObject.Find(StorePath);
            Transform spot = store == null ? null : store.transform.Find(StoreSpot);
            if (spot == null)
            {
                return "The shop was not found as this build expects it ('" + StorePath + "/" + StoreSpot + "').";
            }

            Vector3 feet = spot.position;
            bool ground = false;
            float nearest = float.MaxValue;
            RaycastHit[] hits = Physics.RaycastAll(spot.position + (Vector3.up * LookFrom), Vector3.down, LookDown);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider.isTrigger || hits[i].transform.IsChildOf(player) || hits[i].distance >= nearest)
                {
                    continue;
                }

                nearest = hits[i].distance;
                feet = hits[i].point;
                ground = true;
            }

            Vector3 to = feet + (Vector3.up * (FeetBelowOrigin + Clearance));
            Vector3 from = player.position;

            // A character controller that is on keeps its own idea of where it is for a frame.
            CharacterController controller = player.GetComponent<CharacterController>();
            bool wasOn = controller != null && controller.enabled;
            if (wasOn)
            {
                controller.enabled = false;
            }

            player.position = to;
            player.rotation = Quaternion.Euler(0f, spot.eulerAngles.y, 0f);
            if (wasOn)
            {
                controller.enabled = true;
            }

            log.Info(
                LogCategory.Player,
                "Test aid: the local player was taken from " + Where(from) + " to the shop, " + Where(to) + (ground ? ", standing on '" + Name(hits, nearest) + "'." : ", with no ground found under it."));
            return ground ? "You are at the shop, in front of Teimo's." : "You are at the shop, but nothing was found under your feet: tell me if you fall.";
        }

        private static string Name(RaycastHit[] hits, float distance)
        {
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].distance == distance)
                {
                    return hits[i].collider.name;
                }
            }

            return "?";
        }

        private static string Where(Vector3 position)
        {
            return "(" + position.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " + position.y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + position.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }
    }
}
