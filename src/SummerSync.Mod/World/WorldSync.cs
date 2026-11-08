using System;
using System.Globalization;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The time of day and the weather, as far as the game is concerned: on the host it reads them, and on a
    /// client it makes the game show what the host has.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md):
    ///
    /// The time is kept by the state machine "Color" on the sun. A day is twelve blocks of two hours; each is a
    /// state that lasts as long as its variable "Timescale" says, 600 seconds, measured in real time by an
    /// action that fades the colour of the light, and that names the block in the integer "Time" (2 to 22, and
    /// 24 for the one that starts at midnight). The day of the week is the global "GlobalDay". The game itself
    /// moves the time by writing "Time" and sending "WAKEUP", which is what sleeping does; that event also puts
    /// the hour count (state machine "Clock" on the sun) and the kitchen clock right. The sun is turned to the
    /// float "Rotation" of the state machine of that name on its pivot. Every other clock in the game copies
    /// the two hands of the kitchen clock.
    ///
    /// The weather is one front of clouds, "MAP/CloudSystem/Clouds", that its state machine "Weather" moves
    /// along X at the speed in its variable "Speed"; "WeatherType" says what falls from it and
    /// "WeatherCloudID" which of its six clouds is the dark one. Whether it rains on a player is worked out
    /// from where that player stands, by the game on each machine.
    ///
    /// On a client the game's clock keeps running by itself, and is only steered: while it is in the same
    /// block as the host's, the length of the block is stretched or shrunk, frame by frame, so that the fade
    /// is as far along as the host's is; a block that the host has already left is ended; and only when the
    /// two are further apart than that can mend is the time moved, the way sleeping moves it.
    ///
    /// Offline and in the loopback test nothing here touches the game.
    /// </summary>
    internal sealed class WorldSync
    {
        private const string SunPath = "MAP/SUN/Pivot/SUN";
        private const string SunPivotPath = "MAP/SUN/Pivot";
        private const string BlockMachine = "Color";
        private const string HourMachine = "Clock";
        private const string SunMachine = "Rotation";
        private const string WakeEvent = "WAKEUP";
        private const string KitchenClockPath = "YARD/Building/Dynamics/SuomiClock/Clock";
        private const string HourHandPath = "hour/NeedleHour";
        private const string MinuteHandPath = "minute/NeedleMinute";
        private const string CloudsPath = "MAP/CloudSystem/Clouds";
        private const string WeatherMachine = "Weather";
        private const string CloudObjects = "CloudObjects";
        private const string DarkCloudMaterial = "cloud_rain";
        private const string WeatherEvent = "SUMMERSYNC WEATHER";
        private const string WeatherEntryState = "Set cloud";

        private const double SearchInterval = 2.0;

        // Further apart than this, in seconds, and in another block: the time is moved instead of steered.
        private const double JumpSeconds = 3.0;
        private const double JumpAgainAfter = 2.0;

        // A block is not steered in its first moments, and never made so short that it would end by itself:
        // it ends when the host's does.
        private const float SteerAfter = 2f;
        private const float SteerMargin = 1f;
        private const float SlowestSteer = 4f;

        private const float SunTolerance = 0.6f;
        private const float HourHandTolerance = 0.3f;
        private const float MinuteHandTolerance = 1.5f;
        private const float MinutesTolerance = 0.3f;

        // Further than this, in metres, from where the host has the front, it is put there.
        private const float FrontTolerance = 10f;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private WorldReplication current;
        private double nextSearch;
        private bool searchReported;

        private PlayMakerFSM blocks;
        private PlayMakerFSM hours;
        private FsmInt blockHour;
        private FsmFloat blockMinutes;
        private FsmFloat blockMinutesAdd;
        private FsmFloat blockLength;
        private float usualLength;
        private FsmFloat sunAngle;
        private FsmInt day;
        private FsmFloat clockMinutes;
        private FsmFloat hourHandShown;
        private FsmFloat minuteHandShown;
        private Transform hourHand;
        private Transform minuteHand;
        private bool clockFound;

        private bool blockKnown;
        private int lastHour;
        private float lastMinutes;
        private float blockStart;
        private double nextJump;
        private bool steered;

        private Transform clouds;
        private PlayMakerFSM weather;
        private FsmFloat frontX;
        private FsmFloat frontZ;
        private FsmFloat frontYaw;
        private FsmFloat frontSpeed;
        private FsmInt frontKind;
        private FsmInt frontCloud;
        private FsmFloat thunderForce;
        private bool weatherFound;
        private bool weatherHooked;
        private int appliedSerial = -1;
        private bool frontApplied;
        private WeatherState appliedFront;
        private int forceAgain;

        public WorldSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            WorldReplication world = localPlayer == null ? null : manager.World;
            if (world != current)
            {
                Release();
                if (current != null)
                {
                    current.ClearWorld();
                }

                current = world;
                clockFound = false;
                weatherFound = false;
                searchReported = false;
                nextSearch = now;
                blockKnown = false;
                appliedSerial = -1;
                frontApplied = false;
            }

            if (current == null)
            {
                return;
            }

            if ((!clockFound || !weatherFound) && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            bool host = manager.Mode == NetworkMode.Host;
            WorldClock shown = default(WorldClock);
            WeatherState front = default(WeatherState);
            bool hasClock = clockFound && Clock(host, now, out shown);
            bool hasFront = weatherFound && Front(host, out front);
            if (host)
            {
                if (hasClock)
                {
                    current.SetClock(shown);
                }

                if (hasFront)
                {
                    current.SetWeather(front, frontSpeed.Value);
                }
            }

            current.Show(hasClock, shown, hasFront, front);
        }

        /// <summary>
        /// A test aid, for the host: moves the time to the two-hour block that <paramref name="hour"/> is in,
        /// the way the game moves it when its player sleeps. The other players follow. Returns what it did, for
        /// the console.
        /// </summary>
        public string TestHour(int hour)
        {
            if (current == null || !clockFound || blocks == null)
            {
                return "There is no session with a game world, or the game's clock was not found.";
            }

            if (manager.Mode != NetworkMode.Host)
            {
                return "The time is the host's: run this on the host.";
            }

            if (hour < 0 || hour > 24)
            {
                return "An hour from 0 to 24, please.";
            }

            int block = hour - (hour % 2);
            block = block == 0 ? 24 : block;
            blockHour.Value = block;
            blockKnown = false;
            blocks.SendEvent(WakeEvent);
            if (hours != null)
            {
                hours.SendEvent(WakeEvent);
            }

            log.Info(LogCategory.Fsm, "Test aid: moved the game's time to the block that starts at " + (block % 24) + ":00. State now '" + blocks.ActiveStateName + "'.");
            return "It is " + (block % 24).ToString("00", CultureInfo.InvariantCulture) + ":00 now (the game keeps time in blocks of two hours). The other players follow.";
        }

        /// <summary>
        /// A test aid, for the host: makes the front dry, rainy or stormy and, unless dry, puts one of its
        /// clouds over the local player. Returns what it did, for the console.
        /// </summary>
        public string TestWeather(string kind, Transform localPlayer)
        {
            if (current == null || !weatherFound || weather == null || clouds == null || localPlayer == null)
            {
                return "There is no session with a game world, or the game's weather was not found.";
            }

            if (manager.Mode != NetworkMode.Host)
            {
                return "The weather is the host's: run this on the host.";
            }

            int wanted = string.Equals(kind, "dry", StringComparison.OrdinalIgnoreCase) ? WeatherState.Dry
                : string.Equals(kind, "rain", StringComparison.OrdinalIgnoreCase) ? WeatherState.Rain
                : string.Equals(kind, "thunder", StringComparison.OrdinalIgnoreCase) ? WeatherState.Thunder : -1;
            if (wanted < 0)
            {
                return "dry, rain or thunder, please.";
            }

            if (wanted != WeatherState.Dry)
            {
                if (frontCloud.Value < 1 || frontCloud.Value > 6)
                {
                    frontCloud.Value = 1;
                }

                // It rains under the clouds, not under the middle of the front.
                Transform cloud = clouds.Find(CloudObjects + "/" + frontCloud.Value.ToString(CultureInfo.InvariantCulture));
                Vector3 over = cloud == null ? clouds.position : cloud.position;
                Vector3 place = clouds.position + (localPlayer.position - over);
                clouds.position = new Vector3(place.x, clouds.position.y, place.z);
                frontX.Value = place.x;
                frontZ.Value = place.z;
            }

            frontKind.Value = wanted;
            LightenOtherClouds(frontCloud.Value);
            if (!weatherHooked)
            {
                weatherHooked = true;
                weather.AddGlobalTransition(WeatherEvent, WeatherEntryState);
            }

            weather.SendEvent(WeatherEvent);
            log.Info(
                LogCategory.Fsm,
                "Test aid: weather set to kind " + wanted + " (0 dry, 1 rain, 2 thunder), front at (" + Format(frontX.Value) + ", " + Format(frontZ.Value)
                + "), dark cloud " + frontCloud.Value + ". State now '" + weather.ActiveStateName + "'.");
            return wanted == WeatherState.Dry
                ? "The front is dry now. The other players follow."
                : "The front brings " + kind.ToLowerInvariant() + " now, and one of its clouds is over you: it starts in a few seconds, outdoors. The other players follow.";
        }

        /// <summary>Gives the game's clock back to the game: a block lasts what it always lasted.</summary>
        public void Release()
        {
            // Unity's null check is also true for a destroyed object.
            if (steered && blocks != null && blockLength != null)
            {
                blockLength.Value = usualLength;
            }

            steered = false;
        }

        private void Search()
        {
            if (!clockFound)
            {
                GameObject sun = GameObject.Find(SunPath);
                GameObject pivot = GameObject.Find(SunPivotPath);
                blocks = sun == null ? null : Machine(sun, BlockMachine);
                hours = sun == null ? null : Machine(sun, HourMachine);
                PlayMakerFSM turn = pivot == null ? null : Machine(pivot, SunMachine);
                blockHour = blocks == null ? null : blocks.FsmVariables.FindFsmInt("Time");
                blockMinutes = blocks == null ? null : blocks.FsmVariables.FindFsmFloat("Minutes");
                blockMinutesAdd = blocks == null ? null : blocks.FsmVariables.FindFsmFloat("MinutesAdd");
                blockLength = blocks == null ? null : blocks.FsmVariables.FindFsmFloat("Timescale");
                sunAngle = turn == null ? null : turn.FsmVariables.FindFsmFloat("Rotation");
                day = FsmVariables.GlobalVariables.FindFsmInt("GlobalDay");
                clockMinutes = FsmVariables.GlobalVariables.FindFsmFloat("ClockMinutes");
                hourHandShown = FsmVariables.GlobalVariables.FindFsmFloat("TimeRotationHour");
                minuteHandShown = FsmVariables.GlobalVariables.FindFsmFloat("TimeRotationMinute");
                GameObject kitchen = GameObject.Find(KitchenClockPath);
                hourHand = kitchen == null ? null : kitchen.transform.Find(HourHandPath);
                minuteHand = kitchen == null ? null : kitchen.transform.Find(MinuteHandPath);
                clockFound = blockHour != null && blockMinutes != null && blockLength != null && day != null;
                if (clockFound)
                {
                    usualLength = blockLength.Value > 30f ? blockLength.Value : WorldClock.BlockSeconds;
                    log.Info(
                        LogCategory.Fsm,
                        "The game's clock: day " + day.Value + ", block " + blockHour.Value + ", " + Format(blockMinutes.Value) + " of its minutes gone, a block lasts "
                        + Format(usualLength) + " s, state '" + blocks.ActiveStateName + "'. Sun " + (sunAngle == null ? "NOT FOUND" : "at " + Format(sunAngle.Value) + " degrees")
                        + ", hour count " + (hours == null ? "NOT FOUND" : "found") + ", kitchen clock hands " + (hourHand == null || minuteHand == null ? "NOT FOUND" : "found")
                        + ", its minutes " + (clockMinutes == null ? "NOT FOUND" : Format(clockMinutes.Value)) + ".");
                }
            }

            if (!weatherFound)
            {
                GameObject front = GameObject.Find(CloudsPath);
                clouds = front == null ? null : front.transform;
                weather = front == null ? null : Machine(front, WeatherMachine);
                frontX = weather == null ? null : weather.FsmVariables.FindFsmFloat("PosX");
                frontZ = weather == null ? null : weather.FsmVariables.FindFsmFloat("PosZ");
                frontYaw = weather == null ? null : weather.FsmVariables.FindFsmFloat("Rotation");
                frontSpeed = weather == null ? null : weather.FsmVariables.FindFsmFloat("Speed");
                frontKind = weather == null ? null : weather.FsmVariables.FindFsmInt("WeatherType");
                frontCloud = weather == null ? null : weather.FsmVariables.FindFsmInt("WeatherCloudID");
                thunderForce = weather == null ? null : weather.FsmVariables.FindFsmFloat("ThunderForce");
                weatherFound = frontX != null && frontZ != null && frontSpeed != null && frontKind != null && frontCloud != null;
                weatherHooked = false;
                if (weatherFound)
                {
                    log.Info(
                        LogCategory.Fsm,
                        "The game's weather: front at (" + Format(frontX.Value) + ", " + Format(frontZ.Value) + "), moving at " + Format(frontSpeed.Value)
                        + " m/s, kind " + frontKind.Value + " (0 dry, 1 rain, 2 thunder), dark cloud " + frontCloud.Value + ", state '" + weather.ActiveStateName + "'.");
                }
            }

            if (!searchReported && (!clockFound || !weatherFound))
            {
                searchReported = true;
                log.Warning(
                    LogCategory.Fsm,
                    (clockFound ? string.Empty : "The game's clock was not found as this build expects it ('" + SunPath + "', state machine '" + BlockMachine
                        + "' with Time, Minutes and Timescale, and the global GlobalDay): the time of day is not shared for now. ")
                    + (weatherFound ? string.Empty : "The game's weather was not found as this build expects it ('" + CloudsPath + "', state machine '"
                        + WeatherMachine + "' with PosX, PosZ, Speed, WeatherType and WeatherCloudID): the weather is not shared for now."));
            }
        }

        private static PlayMakerFSM Machine(GameObject owner, string name)
        {
            PlayMakerFSM[] machines = owner.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == name)
                {
                    return machines[i];
                }
            }

            return null;
        }

        // Reads the game's clock and, on a client, brings it to the host's. False if the game is not showing a
        // time just now.
        private bool Clock(bool host, double now, out WorldClock shown)
        {
            shown = default(WorldClock);

            // Unity's null check is also true for a destroyed object.
            if (blocks == null)
            {
                clockFound = false;
                return false;
            }

            float real = Time.realtimeSinceStartup;
            int hour = blockHour.Value;
            float minutes = blockMinutes.Value;
            float perSecond = blockMinutesAdd != null && blockMinutesAdd.Value > 0.0001f ? blockMinutesAdd.Value : 0.2f;
            bool entered = !blockKnown || hour != lastHour || minutes < lastMinutes - 0.5f;
            if (entered)
            {
                // The block has just begun, or has just been found: it began as long ago as its own count says.
                blockStart = real - (minutes / perSecond);
                blockKnown = true;
                if (steered)
                {
                    blockLength.Value = usualLength;
                    steered = false;
                }
            }

            lastHour = hour;
            lastMinutes = minutes;
            float run = Mathf.Max(0f, real - blockStart);
            float length = blockLength.Value > 0.01f ? blockLength.Value : usualLength;
            shown = new WorldClock(day.Value, hour, Mathf.Clamp(run / length, 0f, 1f) * WorldClock.BlockSeconds);
            shown.SunAngle = sunAngle == null ? 0f : sunAngle.Value;
            shown.HourHand = hourHandShown == null ? 0f : hourHandShown.Value;
            shown.MinuteHand = minuteHandShown == null ? 0f : minuteHandShown.Value;
            shown.Minutes = clockMinutes == null ? 0f : Mathf.Clamp(clockMinutes.Value, 0f, 60f);
            if (!shown.IsValid)
            {
                return false;
            }

            WorldClock wanted;
            if (host || !current.TryGetClock(out wanted))
            {
                return true;
            }

            if (!shown.SameBlock(wanted))
            {
                double ahead = wanted.Ahead(shown);
                if (Math.Abs(ahead) > JumpSeconds)
                {
                    if (now >= nextJump)
                    {
                        nextJump = now + JumpAgainAfter;
                        Move(shown, wanted);
                    }

                    return true;
                }

                if (ahead > 0.0 && run > SteerAfter)
                {
                    // The host has just left this block: it ends here too.
                    blockLength.Value = Mathf.Max(0.05f, run);
                    steered = true;
                }

                return true;
            }

            // The same block: as far along here as the host has it.
            if (run < SteerAfter || wanted.Elapsed < 1f)
            {
                blockLength.Value = usualLength;
            }
            else
            {
                float along = wanted.Elapsed / WorldClock.BlockSeconds;
                blockLength.Value = Mathf.Clamp(run / along, run + SteerMargin, usualLength * SlowestSteer);
                steered = true;
            }

            if (sunAngle != null && Mathf.Abs(WorldClock.AngleBetween(sunAngle.Value, wanted.SunAngle)) > SunTolerance)
            {
                sunAngle.Value = wanted.SunAngle;
            }

            Turn(hourHand, wanted.HourHand, HourHandTolerance);
            Turn(minuteHand, wanted.MinuteHand, MinuteHandTolerance);
            if (clockMinutes != null)
            {
                float apart = Mathf.Repeat(clockMinutes.Value - wanted.Minutes + 30f, 60f) - 30f;
                if (Mathf.Abs(apart) > MinutesTolerance)
                {
                    clockMinutes.Value = wanted.Minutes;
                }
            }

            return true;
        }

        // Moves the time the way the game moves it when its player sleeps.
        private void Move(WorldClock from, WorldClock to)
        {
            day.Value = to.Day;
            blockHour.Value = to.Hour;
            blockLength.Value = usualLength;
            steered = false;
            blockKnown = false;
            blocks.SendEvent(WakeEvent);
            if (hours != null)
            {
                hours.SendEvent(WakeEvent);
            }

            log.Info(
                LogCategory.Fsm,
                "The host has day " + to.Day + ", " + Describe(to) + "; this game had day " + from.Day + ", " + Describe(from)
                + ". Moved this game's time to the host's, the way the game moves it when its player sleeps.");
        }

        private static void Turn(Transform hand, float degrees, float tolerance)
        {
            // Unity's null check is also true for a destroyed object.
            if (hand == null)
            {
                return;
            }

            Vector3 angles = hand.localEulerAngles;
            if (Mathf.Abs(WorldClock.AngleBetween(angles.y, degrees)) > tolerance)
            {
                angles.y = degrees;
                hand.localEulerAngles = angles;
            }
        }

        // Reads the game's weather and, on a client, brings it to the host's.
        private bool Front(bool host, out WeatherState front)
        {
            front = default(WeatherState);

            // Unity's null check is also true for a destroyed object.
            if (weather == null || clouds == null)
            {
                weatherFound = false;
                return false;
            }

            front.X = frontX.Value;
            front.Z = frontZ.Value;
            front.Yaw = Mathf.Repeat(clouds.eulerAngles.y, 360f);
            front.Kind = (byte)Mathf.Clamp(frontKind.Value, WeatherState.Dry, WeatherState.Thunder);
            front.Cloud = (byte)Mathf.Clamp(frontCloud.Value, 0, 6);
            front.ThunderForce = thunderForce == null ? 0f : Mathf.Max(0f, thunderForce.Value);
            if (host)
            {
                return true;
            }

            WeatherState wanted;
            double age;
            if (!current.TryGetWeather(out wanted, out age))
            {
                return true;
            }

            float there = wanted.X + (frontSpeed.Value * (float)age);
            if (current.WeatherSerial != appliedSerial && (!frontApplied || !wanted.SameFront(appliedFront)))
            {
                Bring(front, wanted, there);
            }
            else if (Mathf.Abs(frontX.Value - there) > FrontTolerance)
            {
                frontX.Value = there;
            }

            appliedSerial = current.WeatherSerial;
            if (forceAgain > 0 && thunderForce != null)
            {
                // The game draws a new one at random on its way to "thunder"; the host's is the one that counts.
                forceAgain--;
                thunderForce.Value = appliedFront.ThunderForce;
            }

            return true;
        }

        // Makes this game's front the host's: where it is, how it is turned, which cloud is dark and what
        // falls from it. The last two the game does itself, by the states it goes through when it loads a save.
        private void Bring(WeatherState from, WeatherState to, float there)
        {
            Vector3 place = clouds.position;
            clouds.position = new Vector3(there, place.y, to.Z);
            Vector3 angles = clouds.eulerAngles;
            angles.y = to.Yaw;
            clouds.eulerAngles = angles;
            frontX.Value = there;
            frontZ.Value = to.Z;
            if (frontYaw != null)
            {
                frontYaw.Value = to.Yaw;
            }

            frontKind.Value = to.Kind;
            frontCloud.Value = to.Cloud;
            if (thunderForce != null)
            {
                thunderForce.Value = to.ThunderForce;
            }

            LightenOtherClouds(to.Cloud);
            if (!weatherHooked)
            {
                weatherHooked = true;
                weather.AddGlobalTransition(WeatherEvent, WeatherEntryState);
            }

            weather.SendEvent(WeatherEvent);
            appliedFront = to;
            frontApplied = true;
            forceAgain = 3;
            log.Info(
                LogCategory.Fsm,
                "The host's weather: kind " + to.Kind + " (0 dry, 1 rain, 2 thunder), dark cloud " + to.Cloud + ", front at (" + Format(there) + ", " + Format(to.Z)
                + "). This game had kind " + from.Kind + ", dark cloud " + from.Cloud + ", front at (" + Format(from.X) + ", " + Format(from.Z) + "). Set to the host's; state now '"
                + weather.ActiveStateName + "'.");
        }

        // The game makes one cloud dark when it draws a new front, and all of them light again just before.
        // Brought to the host's front, the cloud that was dark here has to be made light by hand: it gets the
        // material of one of the others.
        private void LightenOtherClouds(int dark)
        {
            Transform all = clouds.Find(CloudObjects);
            if (all == null)
            {
                return;
            }

            Material light = null;
            for (int i = 0; i < all.childCount && light == null; i++)
            {
                Renderer renderer = all.GetChild(i).GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null && !renderer.sharedMaterial.name.StartsWith(DarkCloudMaterial, StringComparison.Ordinal))
                {
                    light = renderer.sharedMaterial;
                }
            }

            if (light == null)
            {
                return;
            }

            string keep = dark.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < all.childCount; i++)
            {
                Transform cloud = all.GetChild(i);
                Renderer renderer = cloud.GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null && cloud.name != keep
                    && renderer.sharedMaterial.name.StartsWith(DarkCloudMaterial, StringComparison.Ordinal))
                {
                    renderer.sharedMaterial = light;
                }
            }
        }

        private static string Describe(WorldClock clock)
        {
            float into = Mathf.Clamp(clock.Elapsed, 0f, WorldClock.BlockSeconds);
            int hour = ((clock.Hour % 24) + (into >= WorldClock.BlockSeconds / 2f ? 1 : 0)) % 24;
            int minute = Mathf.Min(59, (int)((into % (WorldClock.BlockSeconds / 2f)) / 5f));
            return hour.ToString("00", CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Format(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
