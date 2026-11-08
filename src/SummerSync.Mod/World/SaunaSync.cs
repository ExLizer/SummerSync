using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// One heat in each sauna, the host's; and its knobs and its steam, for anyone to use.
    ///
    /// What is assumed about the game, from the dump of 2026-10-14 (docs/GAME_NOTES.md): the state machine "Time" of
    /// a sauna's "Simulation" works out, about once a second, the heat of the stove ("StoveHeat") and of the room
    /// ("SaunaHeat"), with the timer ("Time") and the power ("MaxHeat"). The house's is electric: the knobs
    /// "Kiuas/ButtonPower" and "Kiuas/ButtonTime" (state machine "Screw") turn with the scroll wheel; the power's
    /// "Rot" (1 to 150) gives "MaxHeat" = Rot / 300, the timer's "Timer" (1 to 120) gives "Time" = Timer × 6, and each
    /// turns its "mesh" to that many degrees. Its "Simulation" also has "Burn", which, overheated long enough, sets
    /// the house on fire. The cottage's is heated by its wood stove (<see cref="FireSync"/>). In both, water on the
    /// stove makes "StoveTrigger" (state machine "Steam") go through "Steam" for four seconds: steam, sound, a blur
    /// for the local player's eyes and less stress, and ten degrees off the stove.
    ///
    /// The host's simulation is the heat: it says the four numbers every second, and a client writes them into its
    /// own, which goes on running between two; a client's "Burn" is switched off, so that only the host's house can
    /// catch fire. A knob turned anywhere is turned everywhere. Steam made anywhere is steam everywhere: in full for a
    /// player standing by the stove, only the cloud for one who is not.
    /// </summary>
    internal sealed class SaunaSync
    {
        private const string HeatKey = "sauna/heat/";
        private const string KnobKey = "sauna/knobs/";
        private const string SteamKey = "sauna/steam/";
        private const string SteamState = "Steam";
        private const string SteamEvent = "GLOBALEVENT";
        private const double SearchInterval = 2.0;
        private const double HeatInterval = 1.0;
        private const float NearStove = 3f;
        private const double SteamSeconds = 4.0;

        private static readonly string[][] Saunas =
        {
            // name, simulation, stove trigger, power knob, timer knob
            new[] { "house", "YARD/Building/SAUNA/Sauna/Simulation", "YARD/Building/SAUNA/Sauna/Kiuas/StoveTrigger", "YARD/Building/SAUNA/Sauna/Kiuas/ButtonPower", "YARD/Building/SAUNA/Sauna/Kiuas/ButtonTime" },
            new[] { "cottage", "COTTAGE/Sauna/Stove/Simulation", "COTTAGE/Sauna/Stove/StoveTrigger", null, null },
        };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Sauna> saunas = new List<Sauna>();
        private readonly List<Heard> heard = new List<Heard>();
        private TallyReplication current;
        private bool host;
        private Transform player;
        private double nextSearch;
        private bool found;

        public SaunaSync(Logger log, NetworkManager manager)
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
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            player = localPlayer;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                    current.ChangeWanted -= OnChangeWanted;
                }

                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                    current.ChangeWanted += OnChangeWanted;
                }

                saunas.Clear();
                heard.Clear();
                found = false;
                nextSearch = now;
            }

            if (current == null)
            {
                return;
            }

            if (!found && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            if (!found)
            {
                return;
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i], now);
            }

            heard.Clear();
            for (int i = 0; i < saunas.Count; i++)
            {
                Watch(saunas[i], now);
            }
        }

        /// <summary>Every sauna here goes back to how the game had it.</summary>
        public void Release()
        {
            for (int i = 0; i < saunas.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (saunas[i].Burn != null && saunas[i].BurnWasOn)
                {
                    saunas[i].Burn.enabled = true;
                }

                SetCloud(saunas[i], false);
            }
        }

        private void Search()
        {
            for (int i = 0; i < Saunas.Length; i++)
            {
                string[] parts = Saunas[i];
                Transform simulation = FindPath(parts[1]);
                Transform stove = FindPath(parts[2]);
                PlayMakerFSM time = simulation == null ? null : DoorSync.FindMachine(simulation, "Time");
                PlayMakerFSM steam = stove == null ? null : DoorSync.FindMachine(stove, "Steam");
                if (time == null || steam == null)
                {
                    continue;
                }

                FsmVariables variables = time.FsmVariables;
                var sauna = new Sauna
                {
                    Name = parts[0],
                    Time = time,
                    Burn = DoorSync.FindMachine(simulation, "Burn"),
                    Steam = steam,
                    Stove = stove,
                    StoveHeat = variables.FindFsmFloat("StoveHeat"),
                    RoomHeat = variables.FindFsmFloat("SaunaHeat"),
                    Timer = variables.FindFsmFloat("Time"),
                    MaxHeat = variables.FindFsmFloat("MaxHeat"),
                    Cloud = FindCloud(stove),
                    Power = FindKnob(parts[3], "Rot"),
                    TimerKnob = FindKnob(parts[4], "Timer"),
                };
                if (sauna.StoveHeat == null || sauna.RoomHeat == null)
                {
                    continue;
                }

                sauna.BurnWasOn = sauna.Burn != null && sauna.Burn.enabled;
                if (!host && sauna.Burn != null)
                {
                    // Only the host's house catches fire.
                    sauna.Burn.enabled = false;
                }

                sauna.KnownPower = sauna.Power == null ? 0 : Centi(sauna.Power.Value.Value);
                sauna.KnownTimer = sauna.TimerKnob == null ? 0 : Centi(sauna.TimerKnob.Value.Value);
                sauna.WasSteaming = steam.ActiveStateName == SteamState;
                saunas.Add(sauna);
            }

            found = saunas.Count > 0;
            log.Info(LogCategory.Fsm, "Saunas: " + saunas.Count + " of " + Saunas.Length + " are shared" + (host ? "; this game heats them for everyone." : "; the host heats them."));
            if (host)
            {
                TellKnobs();
            }
        }

        // The host: its knobs as they are now are the session's from the start (turned since the save the guests load, or
        // before hosting); only a turn from here on would be told otherwise. The heat is told every second anyway.
        private void TellKnobs()
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < saunas.Count; i++)
            {
                Sauna sauna = saunas[i];
                if (sauna.Power == null && sauna.TimerKnob == null)
                {
                    continue;
                }

                int[] now = { sauna.KnownPower, sauna.KnownTimer };
                current.Tell(KnobKey + sauna.Name, now, now);
                text.Append(text.Length == 0 ? string.Empty : ", ").Append(sauna.Name).Append("'s power ").Append(sauna.KnownPower / 100).Append(" and timer ").Append(sauna.KnownTimer / 100);
            }

            if (text.Length > 0)
            {
                log.Info(LogCategory.Fsm, "Saunas: the knobs told as they are now, for whoever joins: " + text + ".");
            }
        }

        private static Knob FindKnob(string path, string variable)
        {
            Transform at = path == null ? null : FindPath(path);
            PlayMakerFSM screw = at == null ? null : DoorSync.FindMachine(at, "Screw");
            FsmFloat value = screw == null ? null : screw.FsmVariables.FindFsmFloat(variable);
            Transform mesh = at == null ? null : at.Find("mesh");
            return value == null ? null : new Knob { Value = value, Mesh = mesh };
        }

        // The steam cloud over the stove: an old-style particle emitter called "Steam".
        private static ParticleEmitter FindCloud(Transform stove)
        {
            Transform search = stove.parent == null ? stove : stove.parent;
            ParticleEmitter[] emitters = search.GetComponentsInChildren<ParticleEmitter>(true);
            for (int i = 0; i < emitters.Length; i++)
            {
                if (emitters[i].gameObject.name == "Steam")
                {
                    return emitters[i];
                }
            }

            return null;
        }

        private static Transform FindPath(string path)
        {
            int slash = path.IndexOf('/');
            GameObject root = GameObject.Find("/" + path.Substring(0, slash));
            return root == null ? null : root.transform.Find(path.Substring(slash + 1));
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith("sauna/", StringComparison.Ordinal))
            {
                heard.Add(new Heard { Key = key, Values = values, CatchUp = current.CatchingUp });
            }
        }

        private void OnChangeWanted(string key, int[] change, byte player)
        {
            if (key.StartsWith("sauna/", StringComparison.Ordinal))
            {
                heard.Add(new Heard { Key = key, Values = change, IsChange = true, Player = player });
            }
        }

        private void Hear(Heard news, double now)
        {
            for (int i = 0; i < saunas.Count; i++)
            {
                Sauna sauna = saunas[i];
                if (news.Key == HeatKey + sauna.Name && !news.IsChange && news.Values.Length >= 4)
                {
                    // Client: the host's heat. The simulation here goes on from it.
                    sauna.StoveHeat.Value = news.Values[0] / 10f;
                    sauna.RoomHeat.Value = news.Values[1] / 10f;
                    if (sauna.Timer != null)
                    {
                        sauna.Timer.Value = news.Values[2] / 100f;
                    }

                    if (sauna.MaxHeat != null)
                    {
                        sauna.MaxHeat.Value = news.Values[3] / 1000000f;
                    }
                }
                else if (news.Key == KnobKey + sauna.Name && news.Values.Length >= 2)
                {
                    int power = news.IsChange ? sauna.KnownPower + news.Values[0] : news.Values[0];
                    int timer = news.IsChange ? sauna.KnownTimer + news.Values[1] : news.Values[1];
                    if (power != sauna.KnownPower || timer != sauna.KnownTimer)
                    {
                        Turn(sauna, power, timer, news.IsChange && timer != sauna.KnownTimer, news.CatchUp);
                    }

                    if (news.IsChange)
                    {
                        current.Tell(news.Key, new[] { sauna.KnownPower, sauna.KnownTimer }, new[] { power, timer });
                    }

                    sauna.KnownPower = power;
                    sauna.KnownTimer = timer;
                }
                else if (news.Key == SteamKey + sauna.Name && news.Values.Length >= 1)
                {
                    int count = news.IsChange ? sauna.KnownSteam + news.Values[0] : news.Values[0];
                    if (news.CatchUp)
                    {
                        // How many times water went on that stove this session, long ago maybe: a count from here on,
                        // not steam now (a joiner got one puff of steam out of nothing).
                        if (count != sauna.KnownSteam)
                        {
                            log.Info(LogCategory.Fsm, "Saunas: water went on the " + sauna.Name + "'s stove " + count + " time(s) in the host's game; taken as a count, no steam here (a catch-up on joining).");
                        }
                    }
                    else if (count > sauna.KnownSteam)
                    {
                        Steam(sauna, now);
                    }

                    if (news.IsChange)
                    {
                        current.Tell(news.Key, new[] { sauna.KnownSteam }, new[] { count });
                    }

                    sauna.KnownSteam = count;
                }
            }
        }

        // A knob turned elsewhere: turned here, as the game turns it. On the host, a timer set elsewhere starts here. Where a
        // knob is, is a state: a catch-up turns it too.
        private void Turn(Sauna sauna, int power, int timer, bool timerSet, bool catchUp)
        {
            if (sauna.Power != null)
            {
                sauna.Power.Value.Value = power / 100f;
                Rotate(sauna.Power);
                if (host && sauna.MaxHeat != null)
                {
                    sauna.MaxHeat.Value = sauna.Power.Value.Value / 300f;
                }
            }

            if (sauna.TimerKnob != null)
            {
                sauna.TimerKnob.Value.Value = timer / 100f;
                Rotate(sauna.TimerKnob);
                if (host && timerSet && sauna.Timer != null)
                {
                    sauna.Timer.Value = sauna.TimerKnob.Value.Value * 6f;
                }
            }

            log.Info(LogCategory.Fsm, "Saunas: the knobs of the " + sauna.Name + "'s " + (catchUp ? "are, in the host's game" : "were turned in another game") + ": power " + (power / 100) + ", timer " + (timer / 100)
                + (catchUp ? " (a catch-up on joining)." : "."));
        }

        private static void Rotate(Knob knob)
        {
            // Unity's null check is also true for a destroyed object.
            if (knob.Mesh != null)
            {
                Vector3 euler = knob.Mesh.localEulerAngles;
                knob.Mesh.localRotation = Quaternion.Euler(euler.x, knob.Value.Value, euler.z);
            }
        }

        // Steam made elsewhere: the whole of it for whoever is by the stove, the cloud for the others.
        private void Steam(Sauna sauna, double now)
        {
            bool near = player != null && (player.position - sauna.Stove.position).sqrMagnitude <= NearStove * NearStove;
            if (near)
            {
                sauna.Steam.SendEvent(SteamEvent);
                sauna.WasSteaming = true;
            }
            else
            {
                SetCloud(sauna, true);
                sauna.CloudUntil = now + SteamSeconds;
                if (host)
                {
                    sauna.StoveHeat.Value -= 10f;
                }
            }

            log.Info(LogCategory.Fsm, "Saunas: water on the " + sauna.Name + "'s stove in another game; " + (near ? "steam here, all of it." : "the cloud here."));
        }

        private static void SetCloud(Sauna sauna, bool on)
        {
            // Unity's null check is also true for a destroyed object.
            if (sauna.Cloud != null)
            {
                sauna.Cloud.emit = on;
            }
        }

        private void Watch(Sauna sauna, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (sauna.Time == null || sauna.Steam == null)
            {
                return;
            }

            if (sauna.CloudUntil > 0 && now >= sauna.CloudUntil)
            {
                sauna.CloudUntil = 0;
                SetCloud(sauna, false);
            }

            if (host && now >= sauna.NextHeat)
            {
                sauna.NextHeat = now + HeatInterval;
                int[] heat =
                {
                    Mathf.RoundToInt(sauna.StoveHeat.Value * 10f), Mathf.RoundToInt(sauna.RoomHeat.Value * 10f),
                    sauna.Timer == null ? 0 : Centi(sauna.Timer.Value), sauna.MaxHeat == null ? 0 : Mathf.RoundToInt(sauna.MaxHeat.Value * 1000000f),
                };
                if (heat[0] != sauna.ToldHeat[0] || heat[1] != sauna.ToldHeat[1] || heat[2] != sauna.ToldHeat[2] || heat[3] != sauna.ToldHeat[3])
                {
                    current.Tell(HeatKey + sauna.Name, sauna.ToldHeat, heat);
                    sauna.ToldHeat = heat;
                }
            }

            // Knobs turned here.
            int power = sauna.Power == null ? 0 : Centi(sauna.Power.Value.Value);
            int timer = sauna.TimerKnob == null ? 0 : Centi(sauna.TimerKnob.Value.Value);
            if (power != sauna.KnownPower || timer != sauna.KnownTimer)
            {
                current.Tell(KnobKey + sauna.Name, new[] { sauna.KnownPower, sauna.KnownTimer }, new[] { power, timer });
                sauna.KnownPower = power;
                sauna.KnownTimer = timer;
                log.Info(LogCategory.Fsm, "Saunas: the knobs of the " + sauna.Name + "'s were turned here: power " + (power / 100) + ", timer " + (timer / 100) + "; told.");
            }

            // Water thrown here.
            bool steaming = sauna.Steam.ActiveStateName == SteamState;
            if (steaming && !sauna.WasSteaming)
            {
                current.Tell(SteamKey + sauna.Name, new[] { sauna.KnownSteam }, new[] { sauna.KnownSteam + 1 });
                sauna.KnownSteam++;
                log.Info(LogCategory.Fsm, "Saunas: water on the " + sauna.Name + "'s stove here; told.");
            }

            sauna.WasSteaming = steaming;
        }

        private static int Centi(float value)
        {
            return Mathf.RoundToInt(value * 100f);
        }

        private sealed class Knob
        {
            public FsmFloat Value;
            public Transform Mesh;
        }

        private sealed class Sauna
        {
            public string Name;
            public PlayMakerFSM Time;
            public PlayMakerFSM Burn;
            public bool BurnWasOn;
            public PlayMakerFSM Steam;
            public Transform Stove;
            public FsmFloat StoveHeat;
            public FsmFloat RoomHeat;
            public FsmFloat Timer;
            public FsmFloat MaxHeat;
            public ParticleEmitter Cloud;
            public Knob Power;
            public Knob TimerKnob;
            public int KnownPower;
            public int KnownTimer;
            public int KnownSteam;
            public bool WasSteaming;
            public double CloudUntil;
            public double NextHeat;
            public int[] ToldHeat = new int[4];
        }

        private struct Heard
        {
            public string Key;
            public int[] Values;
            public bool IsChange;
            public byte Player;

            /// <summary>Part of the host's answer to this game's request: how things are, not what just happened.</summary>
            public bool CatchUp;
        }
    }
}
