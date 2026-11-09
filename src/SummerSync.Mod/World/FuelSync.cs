using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The fuel: one tank per vehicle for every player, Teimo's pumps one for everyone, and the host's prices.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "El combustible"): a vehicle keeps its fuel in the state
    /// machine "Data" of its child "FuelTank" ("FuelLevel", and "FuelOil" in the diesel ones); its "FuelUsage" burns it
    /// and the cap's trigger adds what a nozzle or a can pours in. Each pump ("STORE/LOD/Pumps/GasolinePump98", "D",
    /// "PÖ") has its nozzle "gasoline_pistol_…", a trigger "Functions/Trigger…" whose machine "Use" holds the nozzle in
    /// the player's hand in "Activate hand", "Calculate", "Wait drop" and "Anim drop", counts "Fuel" litres and
    /// "Cost", and its meter "Functions/Meter/Litres" and "Price". The prices are "Price98", "PriceDiesel" and
    /// "PriceFuelOil" of "STORE"'s machine "Fuelprices", given to each pump's "FuelPrice".
    ///
    /// A tank is the vehicle's simulating machine's. That machine burns it and says how much
    /// there is ("fuel/…", with how much of what the others poured it includes); what anyone else pours is added to a
    /// total ("fueladd/…") that the simulating machine adds to its tank. The machine that takes the vehicle over
    /// starts from the last word and what was poured since. A pump's nozzle in someone's hand is gone from it for the
    /// others, and its meter shows what that player pumps ("pump/…"). The prices are the host's ("fuelprices").
    ///
    /// Offline and in the loopback test nothing here touches the game.
    /// </summary>
    internal sealed class FuelSync
    {
        private const string FuelKey = "fuel/";
        private const string AddedKey = "fueladd/";
        private const string PumpKey = "pump/";
        private const string PricesKey = "fuelprices";
        private const double SearchInterval = 2.0;
        private const double ReportInterval = 0.5;
        private const double QuietReportInterval = 5.0;
        private const float ReportStep = 0.02f;
        private const float Poured = 0.001f;
        private const double PumpInterval = 0.25;
        private const double PourInterval = 0.25;
        private const double PourLogInterval = 2.0;

        // A vehicle not found with the first tanks is looked for again, for so long.
        private const double TankSearchSeconds = 30.0;

        // For so long after a tank is found, what goes into it is the save being read, not a pour (MoneySync waits the same
        // when it cannot see the save being read). Then the word of the machine that runs it is shown again.
        private const double SettleSeconds = 5.0;

        private static readonly string[] PumpNames = { "98", "D", "PÖ" };
        private static readonly string[] Holding = { "Activate hand", "Calculate", "Wait drop", "Anim drop" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly List<Tank> tanks = new List<Tank>();
        private readonly Dictionary<string, Tank> byNick = new Dictionary<string, Tank>(StringComparer.Ordinal);
        private readonly Pump[] pumps = new Pump[3];
        private TallyReplication current;
        private double nextSearch;
        private double searchTanksUntil;
        private bool found;
        private PlayMakerFSM priceMachine;
        private FsmFloat[] prices;
        private int[] pricesTold;
        private int[] pricesWanted;
        private TextMesh[] priceTable;
        private double nextPriceCheck;

        public FuelSync(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
        }

        /// <summary>
        /// Once per frame, after the vehicles have been placed. <paramref name="localPlayer"/> is the player's root
        /// object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                Release();
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                    current.ChangeWanted -= OnChangeWanted;
                }

                current = tallies;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                    current.ChangeWanted += OnChangeWanted;
                }

                tanks.Clear();
                byNick.Clear();
                Array.Clear(pumps, 0, pumps.Length);
                priceMachine = null;
                pricesTold = null;
                pricesWanted = null;
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
                Search(now);
                if (found)
                {
                    searchTanksUntil = now + TankSearchSeconds;
                }

                if (found && !IsHost)
                {
                    // What the others said before this world was ready: the host keeps the last of each.
                    current.RequestAll();
                }
            }
            else if (found && now >= nextSearch && now < searchTanksUntil && tanks.Count < VehiclePresenter.Catalog.Length)
            {
                nextSearch = now + SearchInterval;
                if (SearchTanks(now, null) > 0 && !IsHost)
                {
                    // What the host said of it before it was found here was not listened to: asked again.
                    current.RequestAll();
                }
            }

            if (!found)
            {
                return;
            }

            for (int i = 0; i < tanks.Count; i++)
            {
                StepTank(tanks[i], now);
            }

            for (int i = 0; i < pumps.Length; i++)
            {
                if (pumps[i] != null)
                {
                    StepPump(pumps[i], now);
                }
            }

            StepPrices(now);
        }

        /// <summary>Gives back every nozzle hidden here: the session is over.</summary>
        public void Release()
        {
            for (int i = 0; i < pumps.Length; i++)
            {
                Pump pump = pumps[i];
                if (pump != null && pump.HiddenHere)
                {
                    Show(pump, true);
                }
            }
        }

        private bool IsHost
        {
            get { return manager.Mode == NetworkMode.Host; }
        }

        // ---- Finding the game's ----

        private void Search(double now)
        {
            var missing = new List<string>();
            SearchTanks(now, missing);
            GameObject store = GameObject.Find("STORE");
            int pumpsFound = 0;
            int pumpsTold = 0;
            for (int p = 0; store != null && p < PumpNames.Length; p++)
            {
                if (pumps[p] == null)
                {
                    pumps[p] = FindPump(store.transform, PumpNames[p]);
                    if (pumps[p] != null && IsHost)
                    {
                        // The host: its meter as it is now is the session's from the start (pumped since the save the guests
                        // load, or before hosting); a pump nobody touches would never be told otherwise.
                        Pump pump = pumps[p];
                        TellPump(pump, 0, Centi(pump.Fuel.Value), Centi(pump.Cost.Value), now);
                        pumpsTold++;
                    }
                }

                pumpsFound += pumps[p] != null ? 1 : 0;
            }

            if (pumpsTold > 0)
            {
                log.Info(LogCategory.Fsm, "Fuel: " + pumpsTold + " pump meter(s) told as they are now, for whoever joins.");
            }

            if (store != null && priceMachine == null)
            {
                priceMachine = Machine(store, "Fuelprices");
                if (priceMachine != null)
                {
                    FsmVariables variables = priceMachine.FsmVariables;
                    prices = new[] { variables.FindFsmFloat("Price98"), variables.FindFsmFloat("PriceDiesel"), variables.FindFsmFloat("PriceFuelOil") };
                    if (prices[0] == null || prices[1] == null || prices[2] == null)
                    {
                        priceMachine = null;
                    }
                    else
                    {
                        priceTable = FindPriceTable(store.transform);
                    }
                }
            }

            found = tanks.Count > 0 || pumpsFound > 0;
            if (found)
            {
                log.Info(
                    LogCategory.Fsm,
                    "Fuel: " + tanks.Count + " vehicle tank(s) shared" + (missing.Count > 0 ? " (not found: " + string.Join(", ", missing.ToArray()) + ")" : string.Empty)
                    + ", " + pumpsFound + " of 3 pumps, prices " + (priceMachine != null ? "the host's" + (priceTable == null ? " (their table not found: only the pumps)" : string.Empty) : "NOT found") + ".");
            }
        }

        // The vehicles' tanks not found yet; with <paramref name="missing"/>, the ones still not found are named in it.
        // Returns how many were found now.
        private int SearchTanks(double now, List<string> missing)
        {
            VehicleDefinition[] catalog = VehiclePresenter.Catalog;
            int added = 0;
            for (int i = 0; i < catalog.Length; i++)
            {
                // Something only towed has no tank.
                if (byNick.ContainsKey(catalog[i].Nick) || !catalog[i].CanBeDriven)
                {
                    continue;
                }

                Transform root = VehiclePresenter.FindRoot(catalog[i].RootName);
                Transform tankObject = root == null ? null : root.Find("FuelTank");
                PlayMakerFSM data = tankObject == null ? null : Machine(tankObject.gameObject, "Data");
                FsmFloat level = data == null ? null : data.FsmVariables.FindFsmFloat("FuelLevel");
                if (level == null)
                {
                    if (missing != null)
                    {
                        missing.Add(catalog[i].Nick);
                    }

                    continue;
                }

                var tank = new Tank { Nick = catalog[i].Nick, Root = root, Level = level, Oil = data.FsmVariables.FindFsmFloat("FuelOil") };
                tank.LastLevel = tank.Level.Value;
                tank.LastOil = tank.Oil == null ? 0f : tank.Oil.Value;
                tank.SettleUntil = now + SettleSeconds;
                tanks.Add(tank);
                byNick[tank.Nick] = tank;
                added++;
                if (found)
                {
                    log.Info(LogCategory.Fsm, "Fuel: the " + tank.Nick + "'s tank found now (" + Litres(tank.LastLevel, tank.LastOil) + "); shared from here on.");
                }
            }

            return added;
        }

        private Pump FindPump(Transform store, string name)
        {
            Transform root = store.Find("LOD/Pumps/GasolinePump" + name);
            Transform trigger = root == null ? null : root.Find("Functions/Trigger" + name);
            PlayMakerFSM use = trigger == null ? null : Machine(trigger.gameObject, "Use");
            Transform pistol = root == null ? null : root.Find("gasoline_pistol_" + name);
            FsmFloat fuel = use == null ? null : use.FsmVariables.FindFsmFloat("Fuel");
            FsmFloat cost = use == null ? null : use.FsmVariables.FindFsmFloat("Cost");
            if (use == null || pistol == null || fuel == null || cost == null)
            {
                log.Warning(LogCategory.Fsm, "Fuel: the pump '" + name + "' is not made as this build expects (its trigger, machine 'Use', nozzle or counters): it is each game's own.");
                return null;
            }

            Transform litres = root.Find("Functions/Meter/Litres");
            Transform price = root.Find("Functions/Meter/Price");
            return new Pump
            {
                Name = name,
                Use = use,
                Trigger = trigger.gameObject,
                Pistol = pistol.gameObject,
                Fuel = fuel,
                Cost = cost,
                Litres = litres == null ? null : litres.GetComponent<TextMesh>(),
                Price = price == null ? null : price.GetComponent<TextMesh>(),
            };
        }

        // The price table on the wall: the text meshes "98", "Diesel" and "FuelOil" somewhere under the store.
        private static TextMesh[] FindPriceTable(Transform store)
        {
            var table = new TextMesh[3];
            string[] names = { "98", "Diesel", "FuelOil" };
            TextMesh[] texts = store.GetComponentsInChildren<TextMesh>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                int at = Array.IndexOf(names, texts[i].gameObject.name);
                if (at >= 0 && table[at] == null)
                {
                    table[at] = texts[i];
                }
            }

            return table[0] != null && table[1] != null && table[2] != null ? table : null;
        }

        // ---- The tanks ----

        private void StepTank(Tank tank, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (tank.Root == null)
            {
                return;
            }

            bool simulating = vehicles.SimulatesRoot(tank.Root);
            if (simulating && !tank.Simulating)
            {
                TakeOver(tank);
            }

            tank.Simulating = simulating;
            float level = tank.Level.Value;
            float oil = tank.Oil == null ? 0f : tank.Oil.Value;
            if (simulating)
            {
                // What the others poured since, into the tank that counts.
                int addFuel;
                int addOil;
                tank.Books.Absorb(out addFuel, out addOil);
                if (addFuel != 0 || addOil != 0)
                {
                    level = Mathf.Max(0f, level + (addFuel / 100f));
                    tank.Level.Value = level;
                    if (tank.Oil != null)
                    {
                        oil = Mathf.Max(0f, oil + (addOil / 100f));
                        tank.Oil.Value = oil;
                    }

                    tank.ReportNow = true;
                    tank.LoggedFuel += addFuel;
                    tank.LoggedOil += addOil;
                    if (now >= tank.NextLog)
                    {
                        tank.NextLog = now + PourLogInterval;
                        log.Info(LogCategory.Fsm, "Fuel: " + Litres(tank.LoggedFuel, tank.LoggedOil) + " poured by others into the " + tank.Nick + " here, which runs it; it has " + Litres(level, oil) + ".");
                        tank.LoggedFuel = 0;
                        tank.LoggedOil = 0;
                    }
                }

                bool moved = Mathf.Abs(level - tank.ReportedLevel) >= ReportStep || Mathf.Abs(oil - tank.ReportedOil) >= ReportStep;
                bool stale = (level != tank.ReportedLevel || oil != tank.ReportedOil) && now >= tank.LastReport + QuietReportInterval;
                if ((tank.ReportNow || moved || stale) && now >= tank.LastReport + ReportInterval)
                {
                    current.Set(FuelKey + tank.Nick, tank.Books.Word(Centi(level), Centi(oil)));
                    tank.ReportedLevel = level;
                    tank.ReportedOil = oil;
                    tank.LastReport = now;
                    tank.ReportNow = false;
                }
            }
            else if (now < tank.SettleUntil)
            {
                // Just found: the world may still be loading, and what goes into the tank now is the save being read.
                if ((level > tank.LastLevel + Poured || oil > tank.LastOil + Poured) && !tank.SettleSaid)
                {
                    tank.SettleSaid = true;
                    log.Info(LogCategory.Fsm, "Fuel: the " + tank.Nick + "'s tank went from " + Litres(tank.LastLevel, tank.LastOil) + " to " + Litres(level, oil)
                        + " while this world was loading; not a pour.");
                }
            }
            else
            {
                if (!tank.Settled)
                {
                    // Whatever the save put in, the word of the machine that runs it is what the tank has.
                    tank.Settled = true;
                    ShowWord(tank);
                    level = tank.Level.Value;
                    oil = tank.Oil == null ? 0f : tank.Oil.Value;
                }

                // A tank only fills by being poured into: what went up here since the last frame, this game poured.
                // Told a few times a second while it goes on. Up from below nothing is not a pour: the engine burns from
                // the fuel oil too, empty or not, and the tank puts it back to nothing a second later (FuelLine and the
                // tank's "Activate"), and in a game that shows a diesel running that went to the vehicle's machine as
                // fuel oil poured, as much as it burnt (log of 2026-11-08, the tractor).
                tank.PendingFuel += Rise(tank.LastLevel, level);
                tank.PendingOil += Rise(tank.LastOil, oil);
                if ((tank.PendingFuel > 0 || tank.PendingOil > 0) && now >= tank.NextPour)
                {
                    tank.NextPour = now + PourInterval;
                    Pour(tank, tank.PendingFuel, tank.PendingOil, now);
                    tank.PendingFuel = 0;
                    tank.PendingOil = 0;
                }
            }

            tank.LastLevel = level;
            tank.LastOil = oil;
        }

        // This machine runs the vehicle from now on: it starts from the last word on the tank and what was poured since.
        private void TakeOver(Tank tank)
        {
            bool word = tank.Books.HasWord;
            int level = Centi(tank.Level.Value);
            int oil = tank.Oil == null ? 0 : Centi(tank.Oil.Value);
            tank.Books.TakeOver(ref level, ref oil);
            if (word)
            {
                tank.Level.Value = level / 100f;
                if (tank.Oil != null)
                {
                    tank.Oil.Value = oil / 100f;
                }

                log.Info(LogCategory.Fsm, "Fuel: this machine runs the " + tank.Nick + " now; its tank starts with " + Litres(level, oil) + ".");
            }

            tank.ReportNow = true;
        }

        // Hundredths of a litre a tank went up from one frame to the next, counted from nothing if it was below.
        private static int Rise(float was, float now)
        {
            float from = Mathf.Max(was, 0f);
            return now > from + Poured ? Centi(now - from) : 0;
        }

        private void Pour(Tank tank, int fuel, int oil, double now)
        {
            int[] before = tank.Books.Pour(fuel, oil);
            current.Tell(AddedKey + tank.Nick, before, tank.Books.Added());
            tank.LoggedFuel += fuel;
            tank.LoggedOil += oil;
            if (now >= tank.NextLog)
            {
                tank.NextLog = now + PourLogInterval;
                log.Info(LogCategory.Fsm, "Fuel: " + Litres(tank.LoggedFuel, tank.LoggedOil) + " poured into the " + tank.Nick + " here; told to the machine that runs it.");
                tank.LoggedFuel = 0;
                tank.LoggedOil = 0;
            }
        }

        // What the machine that runs it says the tank has, and what the others poured that it has not taken in yet.
        private void ShowWord(Tank tank)
        {
            int shownLevel;
            int shownOil;
            if (tank.Simulating || !tank.Books.Shown(out shownLevel, out shownOil))
            {
                return;
            }

            // What was poured here and not told yet is in it too.
            float level = (shownLevel + tank.PendingFuel) / 100f;
            float oil = (shownOil + tank.PendingOil) / 100f;
            tank.Level.Value = level;
            tank.LastLevel = level;
            if (tank.Oil != null)
            {
                tank.Oil.Value = oil;
                tank.LastOil = oil;
            }
        }

        // ---- The pumps ----

        private void StepPump(Pump pump, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (pump.Use == null)
            {
                return;
            }

            bool holding = Array.IndexOf(Holding, pump.Use.ActiveStateName ?? string.Empty) >= 0;
            int fuel = Centi(pump.Fuel.Value);
            int cost = Centi(pump.Cost.Value);
            if (holding)
            {
                if (!pump.Holding || ((fuel != pump.ToldFuel || cost != pump.ToldCost) && now >= pump.LastTold + PumpInterval))
                {
                    TellPump(pump, manager.Session.LocalPlayerId + 1, fuel, cost, now);
                }
            }
            else if (pump.Holding)
            {
                TellPump(pump, 0, fuel, cost, now);
                log.Info(LogCategory.Fsm, "Fuel: the nozzle of pump " + pump.Name + " is back; " + (fuel / 100f).ToString("0.##", CultureInfo.InvariantCulture) + " l pumped.");
            }

            pump.Holding = holding;

            // In another player's hand: gone from the pump here, and nobody here can take it.
            bool elsewhere = pump.Holder > 0 && pump.Holder != manager.Session.LocalPlayerId + 1 && !holding;
            if (elsewhere && !pump.HiddenHere)
            {
                Show(pump, false);
            }
            else if (!elsewhere && pump.HiddenHere)
            {
                Show(pump, true);
            }
            else if (elsewhere && pump.Trigger.activeSelf)
            {
                // The game gives back the other pumps when this player puts a nozzle down.
                pump.Trigger.SetActive(false);
            }
        }

        private void TellPump(Pump pump, int holder, int fuel, int cost, double now)
        {
            current.Set(PumpKey + pump.Name, new[] { holder, fuel, cost });
            pump.ToldFuel = fuel;
            pump.ToldCost = cost;
            pump.LastTold = now;
        }

        private void Show(Pump pump, bool shown)
        {
            pump.HiddenHere = !shown;
            if (pump.Pistol != null && pump.Pistol.activeSelf != shown)
            {
                pump.Pistol.SetActive(shown);
            }

            // Given back only if no nozzle is in this player's hand: then the game has the others off itself.
            bool anyHeld = false;
            for (int i = 0; i < pumps.Length; i++)
            {
                anyHeld |= pumps[i] != null && pumps[i].Holding;
            }

            if (pump.Trigger != null && (!shown || !anyHeld) && pump.Trigger.activeSelf != shown)
            {
                pump.Trigger.SetActive(shown);
            }

            log.Info(LogCategory.Fsm, "Fuel: the nozzle of pump " + pump.Name + (shown ? " is back on it here." : " is in player " + (pump.Holder - 1) + "'s hand: gone from it here."));
        }

        private void ShowMeter(Pump pump, int fuel, int cost)
        {
            if (pump.Holding)
            {
                return;
            }

            pump.Fuel.Value = fuel / 100f;
            pump.Cost.Value = cost / 100f;
            if (pump.Litres != null)
            {
                pump.Litres.text = Mathf.FloorToInt(fuel / 10f).ToString("0000", CultureInfo.InvariantCulture);
            }

            if (pump.Price != null)
            {
                pump.Price.text = Mathf.FloorToInt(cost / 100f).ToString("0000", CultureInfo.InvariantCulture);
            }
        }

        // ---- The prices ----

        private void StepPrices(double now)
        {
            if (priceMachine == null || now < nextPriceCheck)
            {
                return;
            }

            nextPriceCheck = now + 1.0;
            int[] mine = { Milli(prices[0].Value), Milli(prices[1].Value), Milli(prices[2].Value) };
            if (IsHost)
            {
                if (pricesTold == null || mine[0] != pricesTold[0] || mine[1] != pricesTold[1] || mine[2] != pricesTold[2])
                {
                    current.Set(PricesKey, mine);
                    pricesTold = mine;
                    log.Info(LogCategory.Fsm, "Fuel: the prices here, everyone's: " + PriceText(mine) + ".");
                }

                return;
            }

            // The game here may have drawn prices of its own: the host's are put back.
            if (pricesWanted != null && (mine[0] != pricesWanted[0] || mine[1] != pricesWanted[1] || mine[2] != pricesWanted[2]))
            {
                WritePrices(pricesWanted);
            }
        }

        private void WritePrices(int[] wanted)
        {
            for (int i = 0; i < 3; i++)
            {
                prices[i].Value = wanted[i] / 1000f;
                if (pumps[i] != null)
                {
                    FsmFloat price = pumps[i].Use.FsmVariables.FindFsmFloat("FuelPrice");
                    if (price != null)
                    {
                        price.Value = wanted[i] / 1000f;
                    }
                }

                if (priceTable != null)
                {
                    priceTable[i].text = (wanted[i] / 1000f).ToString("0.00", CultureInfo.InvariantCulture);
                }
            }

            log.Info(LogCategory.Fsm, "Fuel: the host's prices, here too: " + PriceText(wanted) + ".");
        }

        // ---- What the others say ----

        private void OnStateWanted(string key, int[] values)
        {
            if (!found)
            {
                return;
            }

            Tank tank;
            if (key.StartsWith(FuelKey, StringComparison.Ordinal) && values.Length >= 4 && byNick.TryGetValue(key.Substring(FuelKey.Length), out tank))
            {
                tank.Books.Heard(values);
                ShowWord(tank);
                return;
            }

            if (key.StartsWith(AddedKey, StringComparison.Ordinal) && values.Length >= 2 && byNick.TryGetValue(key.Substring(AddedKey.Length), out tank))
            {
                tank.Books.HeardAdded(values[0], values[1]);
                ShowWord(tank);
                return;
            }

            if (key.StartsWith(PumpKey, StringComparison.Ordinal) && values.Length >= 3)
            {
                int at = Array.IndexOf(PumpNames, key.Substring(PumpKey.Length));
                Pump pump = at >= 0 ? pumps[at] : null;
                if (pump != null)
                {
                    pump.Holder = values[0];
                    ShowMeter(pump, values[1], values[2]);
                }

                return;
            }

            if (key == PricesKey && values.Length >= 3 && !IsHost && priceMachine != null)
            {
                pricesWanted = new[] { values[0], values[1], values[2] };
                WritePrices(pricesWanted);
            }
        }

        // On the host: a client poured into a tank. The total is kept here and told to everyone.
        private void OnChangeWanted(string key, int[] change, byte player)
        {
            Tank tank;
            if (!found || !key.StartsWith(AddedKey, StringComparison.Ordinal) || change.Length < 2 || !byNick.TryGetValue(key.Substring(AddedKey.Length), out tank))
            {
                return;
            }

            int[] before = tank.Books.Pour(change[0], change[1]);
            current.Tell(key, before, tank.Books.Added());
            ShowWord(tank);
        }

        // ---- Small things ----

        private static PlayMakerFSM Machine(GameObject owner, string name)
        {
            PlayMakerFSM[] machines = owner.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i] != null && machines[i].FsmName == name)
                {
                    return machines[i];
                }
            }

            return null;
        }

        private static int Centi(float litres)
        {
            return Mathf.RoundToInt(litres * 100f);
        }

        private static int Milli(float price)
        {
            return Mathf.RoundToInt(price * 1000f);
        }

        private static string Litres(int fuel, int oil)
        {
            return Litres(fuel / 100f, oil / 100f);
        }

        private static string Litres(float fuel, float oil)
        {
            return fuel.ToString("0.##", CultureInfo.InvariantCulture) + " l" + (oil > 0f ? " and " + oil.ToString("0.##", CultureInfo.InvariantCulture) + " l of fuel oil" : string.Empty);
        }

        private static string PriceText(int[] milli)
        {
            return "98 " + (milli[0] / 1000f).ToString("0.000", CultureInfo.InvariantCulture) + ", diesel " + (milli[1] / 1000f).ToString("0.000", CultureInfo.InvariantCulture)
                + ", fuel oil " + (milli[2] / 1000f).ToString("0.000", CultureInfo.InvariantCulture);
        }

        private sealed class Tank
        {
            public string Nick;
            public Transform Root;
            public FsmFloat Level;
            public FsmFloat Oil;
            public bool Simulating;
            public float LastLevel;
            public float LastOil;

            // What everyone poured, the last word of the machine that runs it, and how much of the pouring it holds.
            public readonly FuelTankLedger Books = new FuelTankLedger();

            public int PendingFuel;
            public int PendingOil;
            public double NextPour;
            public int LoggedFuel;
            public int LoggedOil;
            public double NextLog;
            public float ReportedLevel = -1f;
            public float ReportedOil = -1f;
            public double LastReport = double.NegativeInfinity;
            public bool ReportNow;

            // Until then, what goes into it while another machine runs it is not a pour (see SettleSeconds).
            public double SettleUntil;
            public bool SettleSaid;
            public bool Settled;
        }

        private sealed class Pump
        {
            public string Name;
            public PlayMakerFSM Use;
            public GameObject Trigger;
            public GameObject Pistol;
            public FsmFloat Fuel;
            public FsmFloat Cost;
            public TextMesh Litres;
            public TextMesh Price;
            public bool Holding;
            public int Holder;
            public bool HiddenHere;
            public int ToldFuel;
            public int ToldCost;
            public double LastTold = double.NegativeInfinity;
        }
    }
}
