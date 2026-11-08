using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// A photo of the Satsuma whenever this player's world is saved, for the cards of the main menu's "LOAD SAVE" and "HOST".
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Las partidas del menú"): every SAVEGAME reaches the bills'
    /// "Save game" (<see cref="GuestSync"/> saves the guests there too), while the world is still whole; the Satsuma is
    /// "SATSUMA(557kg, 248)", with what is fitted to it under it. The photo is taken by a camera of the mod's that sees as the
    /// player's does, from the car's front corner, with whatever stands between it and the car cut away (its near plane
    /// just in front of the car) and a light of its own for that one picture. It goes to the world's folder of the mod's
    /// (<see cref="SaveWorlds.CurrentInfo"/>); never while playing in a copy of a host's world.
    /// </summary>
    internal sealed class WorldPhoto
    {
        private const string SavePath = "/Systems/ElectricityBills";
        private const string SaveMachine = "Data";
        private const string SaveState = "Save game";
        private const string SatsumaRoot = "SATSUMA(557kg, 248)";
        private const int Width = 960;
        private const int Height = 540;
        private const float ViewDegrees = 30f;
        private const float NightAmbient = 0.32f;

        private static readonly HashSet<int> Hooked = new HashSet<int>();
        private static WorldPhoto active;

        private readonly Logger log;
        private Transform player;
        private bool hooked;

        public WorldPhoto(Logger log)
        {
            this.log = log;
        }

        /// <summary>Where the camera stands, around the car: degrees from its front, and up from level. "ss photo" changes them.</summary>
        public static float Around = -40f;

        public static float Up = 14f;

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null without a world.</summary>
        public void LateUpdate(Transform localPlayer)
        {
            active = this;
            if (localPlayer != player)
            {
                player = localPlayer;
                hooked = false;
            }

            if (localPlayer == null || hooked)
            {
                return;
            }

            hooked = true;
            GameObject bills = GameObject.Find(SavePath);
            PlayMakerFSM machine = bills == null ? null : DoorSync.FindMachine(bills.transform, SaveMachine);
            if (machine == null || !DoorSync.HasState(machine, SaveState))
            {
                log.Warning(LogCategory.General, "World photo: no '" + SavePath + " :: " + SaveMachine + "' with '" + SaveState + "' here; the menu's cards keep their last photo.");
                return;
            }

            if (Hooked.Add(machine.GetInstanceID()))
            {
                machine.FsmInject(SaveState, Saved, false, 0, false);
            }
        }

        private static void Saved()
        {
            WorldPhoto photo = active;
            if (photo != null)
            {
                photo.Take();
            }
        }

        /// <summary>Takes the photo now, into the world's folder of the mod's. What happened, for the log and the console.</summary>
        public string Take()
        {
            string info = SaveWorlds.CurrentInfo;
            if (info == null)
            {
                return "World photo: not in this player's own world (a copy of a host's, or no mod folder).";
            }

            try
            {
                Transform car = VehiclePresenter.FindRoot(SatsumaRoot);
                if (car == null)
                {
                    log.Warning(LogCategory.General, "World photo: no '" + SatsumaRoot + "' in the world; no photo this time.");
                    return "World photo: the Satsuma was not found.";
                }

                byte[] png = Shoot(car);
                if (png == null)
                {
                    return "World photo: the Satsuma has nothing to show.";
                }

                Directory.CreateDirectory(info);
                string path = Path.Combine(info, SaveWorlds.PhotoFile);
                string part = path + ".part";
                File.WriteAllBytes(part, png);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(part, path);
                string said = "World photo: the Satsuma, " + (png.Length / 1024).ToString(CultureInfo.InvariantCulture) + " kB in '" + path + "'.";
                log.Info(LogCategory.General, said);
                return said;
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.General, "World photo: it failed; the menu's card keeps its last photo. " + exception);
                return "World photo: it failed: " + exception.Message;
            }
        }

        private static byte[] Shoot(Transform car)
        {
            Bounds bounds;
            if (!Measure(car, out bounds))
            {
                return null;
            }

            var holder = new GameObject("SummerSync world photo");
            RenderTexture target = null;
            Texture2D image = null;
            try
            {
                Camera main = Camera.main;
                Camera camera = holder.AddComponent<Camera>();
                if (main != null)
                {
                    camera.CopyFrom(main);
                }

                camera.enabled = false;
                camera.renderingPath = RenderingPath.Forward;
                camera.fieldOfView = ViewDegrees;
                camera.aspect = (float)Width / Height;
                if (camera.clearFlags == CameraClearFlags.Depth || camera.clearFlags == CameraClearFlags.Nothing)
                {
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = RenderSettings.fogColor;
                }

                // From the car's front corner, a little above it, far enough for the whole car.
                float radius = bounds.extents.magnitude;
                float distance = (radius / Mathf.Sin(ViewDegrees * 0.5f * Mathf.Deg2Rad)) * 0.72f;
                Vector3 direction = car.rotation * (Quaternion.Euler(-Up, Around, 0f) * Vector3.forward);
                camera.transform.position = bounds.center + (direction * distance);
                camera.transform.LookAt(bounds.center);
                camera.nearClipPlane = Mathf.Max(0.1f, distance - (radius * 1.15f));
                camera.farClipPlane = Mathf.Max(camera.nearClipPlane + 50f, main != null ? main.farClipPlane : 1000f);

                // A light for this one picture: night or a dark garage still shows the car.
                Light lamp = new GameObject("Light").AddComponent<Light>();
                lamp.transform.parent = holder.transform;
                lamp.type = LightType.Directional;
                lamp.transform.rotation = Quaternion.LookRotation(Quaternion.Euler(0f, 25f, 0f) * (bounds.center - camera.transform.position));
                lamp.intensity = 0.7f;
                lamp.color = new Color(1f, 0.96f, 0.9f);
                lamp.shadows = LightShadows.None;

                // At night the world is near black: for this one picture, at least a dim ambient light.
                Color ambient = RenderSettings.ambientLight;
                RenderSettings.ambientLight = new Color(Mathf.Max(ambient.r, NightAmbient), Mathf.Max(ambient.g, NightAmbient), Mathf.Max(ambient.b, NightAmbient * 1.08f), ambient.a);
                target = RenderTexture.GetTemporary(Width, Height, 24);
                camera.targetTexture = target;
                try
                {
                    camera.Render();
                }
                finally
                {
                    RenderSettings.ambientLight = ambient;
                }

                RenderTexture before = RenderTexture.active;
                RenderTexture.active = target;
                image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                image.Apply();
                RenderTexture.active = before;
                camera.targetTexture = null;

                // Out of the scene before the frame is drawn: a destroyed object goes only at its end.
                holder.SetActive(false);
                return image.EncodeToPNG();
            }
            finally
            {
                if (target != null)
                {
                    RenderTexture.ReleaseTemporary(target);
                }

                if (image != null)
                {
                    UnityEngine.Object.Destroy(image);
                }

                holder.SetActive(false);
                UnityEngine.Object.Destroy(holder);
            }
        }

        // The car and what is fitted to it: its meshes, as they are drawn.
        private static bool Measure(Transform car, out Bounds bounds)
        {
            bounds = new Bounds(car.position, Vector3.zero);
            bool any = false;
            Renderer[] renderers = car.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!renderer.enabled || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
                {
                    continue;
                }

                // Something far bigger than a car is not a part of it (a light's cone, a trigger's mesh).
                Bounds part = renderer.bounds;
                if (part.size.magnitude > 12f)
                {
                    continue;
                }

                if (any)
                {
                    bounds.Encapsulate(part);
                }
                else
                {
                    bounds = part;
                    any = true;
                }
            }

            return any;
        }

        /// <summary>For the console: where the camera stands, and a photo now.</summary>
        public static string Tune(string around, string up)
        {
            float value;
            if (around != null && float.TryParse(around, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                Around = value;
            }

            if (up != null && float.TryParse(up, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                Up = value;
            }

            WorldPhoto photo = active;
            string taken = photo == null ? "World photo: not in a world." : photo.Take();
            return taken + " (camera " + Around.ToString("0", CultureInfo.InvariantCulture) + " degrees around, " + Up.ToString("0", CultureInfo.InvariantCulture) + " up)";
        }
    }
}
