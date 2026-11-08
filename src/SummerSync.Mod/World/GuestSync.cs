using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using Steamworks;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// What is a guest's own in the host's world, kept by the host with its save (Fase 8, docs/DECISIONS.md): its needs,
    /// what it carries of its own (cigarettes) and where it stands. A guest says it every few seconds; the host
    /// writes everyone's last word to "guests.txt" in the mod's folder of the world it plays (<see cref="SaveWorlds"/>)
    /// when its game saves (the bills' "Save game", which every SAVEGAME reaches), and reads it when a session starts. A guest that comes into a copy of the host's world
    /// starts as the save's player (the host's), and takes up what the host's save has of it. The keys are everyone's
    /// (data/jobs/keys.json, <see cref="JobSync"/>): a guest's saved ones are not taken up.
    ///
    /// What is assumed about the game (dump of 2026-10-15): the player's needs are global floats ("PlayerHunger",
    /// "PlayerThirst", "PlayerFatigue", "PlayerUrine", "PlayerStress", "PlayerDirtiness", "PlayerDrunk", "PlayerBurns",
    /// "PlayerAllergy"), its cigarettes a global number too ("PlayerCigarettes").
    /// </summary>
    internal sealed class GuestSync
    {
        private const string SavePath = "/Systems/ElectricityBills";
        private const string SaveMachine = "Data";
        private const string SaveState = "Save game";
        private const string VehicleGlobal = "PlayerCurrentVehicle";
        private const double FirstLookSeconds = 4.0;
        private const double ReportSeconds = 10.0;
        private const LogCategory Category = LogCategory.Player;

        private static readonly string[] Own =
        {
            "PlayerHunger", "PlayerThirst", "PlayerFatigue", "PlayerUrine", "PlayerStress", "PlayerDirtiness", "PlayerDrunk", "PlayerBurns",
            "PlayerAllergy", "PlayerCigarettes",
        };

        private static readonly HashSet<int> Hooked = new HashSet<int>();
        private static GuestSync active;

        /// <summary>
        /// Whether this player is what it is in this world: always on the host and in a world of its own; a guest in the
        /// host's world once it has taken up what the host's save has of it (until then it is the save's player, the host).
        /// </summary>
        public static bool Settled { get; private set; }

        // A guest that died without permanent death comes back at home when it is next in the host's world, whatever
        // the host's save says of it. Lives with the process, as the main menu makes the mod anew.
        private static bool comesBack;

        // The needs the game sets a player who comes back from a death to (Systems/Death, "Mute audio"), with urine and
        // dirtiness at nothing as at the start. The place is home (DeathSync.PutAtHome).
        private const string DiedValue = "Died";
        private static readonly string[] ComeBackNeeds = { "PlayerFatigue", "PlayerHunger", "PlayerThirst", "PlayerStress", "PlayerDrunk", "PlayerUrine", "PlayerDirtiness" };
        private static readonly float[] ComeBackValues = { 10f, 12f, 8f, 0f, 0f, 0f, 0f };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private GuestReplication current;
        private Transform player;
        private bool host;
        private bool started;
        private bool asked;
        private bool restored;
        private double startAt;
        private double nextReport;
        private GuestRecord lastOnFoot;
        private GuestRecord wanted;
        private bool restoreHeard;

        public GuestSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null without a world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            GuestReplication guests = localPlayer == null ? null : manager.Guests;
            if (guests != current)
            {
                if (current != null)
                {
                    current.RestoreWanted -= OnRestoreWanted;
                }

                current = guests;
                player = localPlayer;
                host = manager.Mode == NetworkMode.Host;
                Settled = false;
                started = false;
                asked = false;
                restored = false;
                restoreHeard = false;
                wanted = null;
                lastOnFoot = null;
                startAt = now + FirstLookSeconds;
                active = current == null ? null : this;
                if (current != null)
                {
                    current.RestoreWanted += OnRestoreWanted;
                }
            }

            if (current == null || now < startAt || manager.Session == null || manager.Session.State != SessionState.Connected)
            {
                return;
            }

            if (!started)
            {
                started = true;
                if (host)
                {
                    LoadBook();
                    HookSave();
                }
            }

            if (host)
            {
                Settled = true;
                return;
            }

            if (!HostWorld.PlaysHostWorld)
            {
                // Its own world: what it is there is its own save's.
                Settled = true;
                return;
            }

            if (!asked)
            {
                asked = true;
                current.Report(Read(), true);
                return;
            }

            if (restoreHeard && !restored)
            {
                restored = true;
                Apply(wanted);
                Settled = true;
            }

            if (restored && now >= nextReport)
            {
                nextReport = now + ReportSeconds;
                current.Report(Read(), false);
            }
        }

        /// <summary>
        /// The local player died. A guest in the host's world tells the host at once: to come back at home
        /// (<paramref name="comingBack"/>), or, with permanent death, that what the host keeps of it is over; it then
        /// starts as a new player, as the save's.
        /// </summary>
        public static void LocalPlayerDied(bool comingBack)
        {
            GuestSync sync = active;
            if (sync == null || sync.host || !HostWorld.PlaysHostWorld || sync.current == null)
            {
                return;
            }

            comesBack = comingBack;
            GuestRecord record = sync.Read();
            record.Values[DiedValue] = comingBack ? 1f : 2f;
            sync.current.Report(record, false);
            sync.log.Info(Category, comingBack
                ? "Guests: told the host this player died, and comes back at home."
                : "Guests: told the host this player died for good; next time it starts as a new player.");
        }

        private void OnRestoreWanted(GuestRecord record)
        {
            wanted = record;
            restoreHeard = true;
        }

        // What this player is now. In a vehicle, the place is the last one on foot: put back there it is not inside a car.
        private GuestRecord Read()
        {
            var record = new GuestRecord { Key = KeyOfThisPlayer(), Name = LocalName() };
            FsmVariables globals = FsmVariables.GlobalVariables;
            for (int i = 0; i < Own.Length; i++)
            {
                FsmFloat number = globals.FindFsmFloat(Own[i]);
                if (number != null)
                {
                    record.Values[Own[i]] = number.Value;
                    continue;
                }

                FsmInt whole = globals.FindFsmInt(Own[i]);
                if (whole != null)
                {
                    record.Values[Own[i]] = whole.Value;
                }
            }

            // What it is wanted for is its own too (WantedSync).
            WantedSync.ReadInto(record.Values);
            FsmString vehicle = globals.FindFsmString(VehicleGlobal);
            bool onFoot = vehicle == null || string.IsNullOrEmpty(vehicle.Value);
            if (onFoot && player != null)
            {
                record.HasPlace = true;
                record.X = player.position.x;
                record.Y = player.position.y;
                record.Z = player.position.z;
                record.Yaw = player.eulerAngles.y;
                lastOnFoot = record;
            }
            else if (lastOnFoot != null)
            {
                record.HasPlace = true;
                record.X = lastOnFoot.X;
                record.Y = lastOnFoot.Y;
                record.Z = lastOnFoot.Z;
                record.Yaw = lastOnFoot.Yaw;
            }

            return record;
        }

        // A guest: what the host's save has of it, taken up; and back at home if it died since.
        private void Apply(GuestRecord record)
        {
            float died;
            if (record != null && record.Values.TryGetValue(DiedValue, out died) && died >= 2f)
            {
                log.Info(Category, "Guests: this player died for good last time; it starts as a new player, the save's.");
                comesBack = false;
                WantedSync.ApplyGuest(null, log);
                return;
            }

            bool backFromDeath = comesBack || (record != null && record.Values.TryGetValue(DiedValue, out died) && died >= 1f);
            comesBack = false;
            if (record == null)
            {
                log.Info(Category, "Guests: the host's save has nothing of this player yet; it starts as the save's player.");
                WantedSync.ApplyGuest(null, log);
                if (backFromDeath)
                {
                    ComeBack();
                }

                return;
            }

            ApplyRecord(record);
            WantedSync.ApplyGuest(record.Values, log);
            if (backFromDeath)
            {
                ComeBack();
            }
        }

        // As the mod puts back a player who died without permanent death: at home, with the needs the game sets.
        private void ComeBack()
        {
            FsmVariables globals = FsmVariables.GlobalVariables;
            for (int i = 0; i < ComeBackNeeds.Length; i++)
            {
                FsmFloat need = globals.FindFsmFloat(ComeBackNeeds[i]);
                if (need != null)
                {
                    need.Value = ComeBackValues[i];
                }
            }

            if (player != null)
            {
                DeathSync.PutAtHome(player);
            }

            log.Info(Category, "Guests: this player died last time, and comes back at home, where a new game starts, with the needs the game gives a dead player.");
        }

        private void ApplyRecord(GuestRecord record)
        {
            FsmVariables globals = FsmVariables.GlobalVariables;
            int set = 0;
            foreach (KeyValuePair<string, float> value in record.Values)
            {
                if (Array.IndexOf(Own, value.Key) < 0)
                {
                    continue;
                }

                FsmFloat number = globals.FindFsmFloat(value.Key);
                if (number != null)
                {
                    number.Value = value.Value;
                    set++;
                    continue;
                }

                FsmInt whole = globals.FindFsmInt(value.Key);
                if (whole != null)
                {
                    whole.Value = Mathf.RoundToInt(value.Value);
                    set++;
                }
            }

            bool moved = false;
            FsmString vehicle = globals.FindFsmString(VehicleGlobal);
            if (record.HasPlace && player != null && (vehicle == null || string.IsNullOrEmpty(vehicle.Value)))
            {
                // A character controller that is on keeps its own idea of where it is for a frame.
                CharacterController controller = player.GetComponent<CharacterController>();
                bool wasOn = controller != null && controller.enabled;
                if (wasOn)
                {
                    controller.enabled = false;
                }

                player.position = new Vector3(record.X, record.Y, record.Z);
                player.rotation = Quaternion.Euler(0f, record.Yaw, 0f);
                if (wasOn)
                {
                    controller.enabled = true;
                }

                moved = true;
            }

            log.Info(
                Category,
                "Guests: this player takes up what the host's save has of it: " + set + " value(s) (needs, cigarettes)"
                + (moved ? ", at (" + record.X.ToString("0.0") + ", " + record.Y.ToString("0.0") + ", " + record.Z.ToString("0.0") + ")" : string.Empty) + ".");
        }

        private static string KeyOfThisPlayer()
        {
            try
            {
                return SteamUser.GetSteamID().m_SteamID.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // Without Steam: the player's name.
                return "name:" + LocalName();
            }
        }

        private static string LocalName()
        {
            NetSession session = ModRuntime.Network.Manager.Session;
            IList<PlayerInfo> players = session == null ? null : session.Players;
            for (int i = 0; players != null && i < players.Count; i++)
            {
                if (players[i].IsLocal)
                {
                    return players[i].Name ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static string BookPath
        {
            get
            {
                string info = SaveWorlds.CurrentInfo;
                return info == null ? null : Path.Combine(info, SaveWorlds.GuestsFile);
            }
        }

        // The host: the guests its save has.
        private void LoadBook()
        {
            string path = BookPath;
            if (path == null || !File.Exists(path))
            {
                current.LoadSaved(new GuestRecord[0]);
                log.Info(Category, "Guests: this world's save has no guests yet.");
                return;
            }

            try
            {
                List<GuestRecord> records = GuestBook.Read(File.ReadAllText(path));
                current.LoadSaved(records);
                log.Info(Category, "Guests: this world's save has " + records.Count + " guest(s).");
            }
            catch (Exception exception)
            {
                log.Warning(Category, "Guests: '" + path + "' could not be read; the guests start as the save's player. " + exception.Message);
            }
        }

        // The host: the guests are saved with the game, when it saves.
        private void HookSave()
        {
            GameObject bills = GameObject.Find(SavePath);
            PlayMakerFSM machine = bills == null ? null : DoorSync.FindMachine(bills.transform, SaveMachine);
            if (machine == null || !DoorSync.HasState(machine, SaveState))
            {
                log.Warning(Category, "Guests: no '" + SavePath + " :: " + SaveMachine + "' with '" + SaveState + "' here; the guests are not saved with the game.");
                return;
            }

            if (Hooked.Add(machine.GetInstanceID()))
            {
                machine.FsmInject(SaveState, Saved, false, 0, false);
            }
        }

        private static void Saved()
        {
            GuestSync sync = active;
            if (sync != null)
            {
                sync.OnSaved();
            }
        }

        private void OnSaved()
        {
            if (!host || current == null)
            {
                return;
            }

            string path = BookPath;
            if (path == null)
            {
                return;
            }

            try
            {
                List<GuestRecord> records = current.Snapshot();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, GuestBook.Write(records));
                log.Info(Category, "Guests: saved with the game: " + records.Count + " guest(s), " + current.LiveCount + " of them here this session.");
            }
            catch (Exception exception)
            {
                log.Warning(Category, "Guests: '" + path + "' could not be written; the guests' needs and places are not saved this time. " + exception.Message);
            }
        }
    }
}
