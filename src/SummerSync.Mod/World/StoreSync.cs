using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Teimo's shelves and the counter, one for every player: what anyone takes off a shelf is gone from it
    /// everywhere and is on everyone's bill at the till, and whoever goes to the till pays for all of it.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md):
    ///
    /// Each product on a shelf is a state machine "Buy" under "STORE/LOD/ActivateStore", named like the product's
    /// object. A left click puts one on the counter: the till ("STORE/StoreCashRegister/Register", state machine
    /// "Data") gets one more in its integer "Q" and the name, and the price added to "PriceTotal"; the hash table
    /// "Bought" of "STORE/Inventory" gets one more for the name, and the product's own integer "Bought" too; and
    /// the shelf's child of that number, under the product's "ProductList", is switched off. A right click
    /// undoes it. The price is the product's own "Price", which it works out when it starts from the inventory's
    /// "Prices" for the name times its "PriceMultiplier". Three things
    /// are one of a kind ("BuyDash", "BuySeat", "BuyWheel"): they set the till's "QSuomiDash" and so on to one,
    /// and switch off their own object and the one shown ("Mesh"). Paying empties the counters one by one, over
    /// several frames, while the till makes what was bought; "Total" is worked out again every two seconds.
    ///
    /// Each product is a tally of two numbers: on the counter, and taken off the shelf (for the one-of-a-kind:
    /// taken or not). What was pumped at the pumps is on the till too ("FuelPurchase", "DieselPurchase",
    /// "FuelOilPurchase", which a pump in use writes every frame): one tally of three, set by whichever game changed
    /// it last (the one pumping, or the one that paid). This machine's changes are told (<see cref="TallyReplication"/>); the others' are written
    /// into the game's numbers and the shelf, but never while the till is in the middle of a payment here, which
    /// reads them.
    ///
    /// Offline and in the loopback test nothing here touches the game.
    /// </summary>
    internal sealed class StoreSync
    {
        private const string StorePath = "STORE";
        private const string TillPath = "StoreCashRegister/Register";
        private const string TillMachine = "Data";
        private const string InventoryPath = "Inventory";
        private const string InventoryMachine = "Logic";
        private const string ShelvesPath = "LOD/ActivateStore";
        private const string BuyMachine = "Buy";
        private const string KeyPrefix = "store/";
        private const string FuelBillKey = KeyPrefix + "fuelbill";

        private const double SearchInterval = 2.0;
        private const int MaxWaiting = 2000;

        // The states of the till in which it is not paying: what the others did can be written then.
        private static readonly string[] TillAtRest =
        {
            "Wait player", "Wait button", "Check purchase", "State 4", "Add fuel", "Store Closed", "State 3", "Teimo", "State 2", "State 6", "State 7",
            "Pissed", "Load", "State 5", "Save data", string.Empty,
        };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Product> products = new List<Product>();
        private readonly Dictionary<string, Product> byKey = new Dictionary<string, Product>();
        private readonly List<Waiting> waiting = new List<Waiting>();
        private readonly int[] scratch = new int[2];
        private readonly int[] billNow = new int[3];
        private int[] billKnown;
        private double clock;
        private double nextBillTell;
        private TallyReplication current;
        private double nextSearch;
        private bool found;
        private bool searchReported;
        private bool waitReported;

        private PlayMakerFSM till;
        private FsmFloat priceTotal;
        private FsmFloat total;
        private FsmFloat fuel;
        private FsmFloat diesel;
        private FsmFloat fuelOil;
        private FsmFloat brokenWindow;
        private PlayMakerHashTableProxy bought;
        private PlayMakerHashTableProxy stocked;
        private PlayMakerHashTableProxy prices;
        private FsmFloat multiplier;

        public StoreSync(Logger log, NetworkManager manager)
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
            clock = now;
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
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

                products.Clear();
                byKey.Clear();
                waiting.Clear();
                found = false;
                searchReported = false;
                waitReported = false;
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
                if (found)
                {
                    current.RequestAll();
                }
            }

            if (!found)
            {
                return;
            }

            if (waiting.Count > 0 && AtRest())
            {
                Catch();
            }

            Watch();
        }

        private void Search()
        {
            GameObject store = GameObject.Find(StorePath);
            Transform root = store == null ? null : store.transform;
            Transform tillObject = root == null ? null : root.Find(TillPath);
            Transform inventory = root == null ? null : root.Find(InventoryPath);
            Transform shelves = root == null ? null : root.Find(ShelvesPath);
            till = tillObject == null ? null : Machine(tillObject.gameObject, TillMachine);
            if (till == null || inventory == null || shelves == null)
            {
                Unfound("the till '" + StorePath + "/" + TillPath + "' (state machine '" + TillMachine + "'), '" + StorePath + "/" + InventoryPath + "' and '" + StorePath + "/" + ShelvesPath + "'");
                return;
            }

            FsmVariables tillVariables = till.FsmVariables;
            priceTotal = tillVariables.FindFsmFloat("PriceTotal");
            total = tillVariables.FindFsmFloat("Total");
            fuel = tillVariables.FindFsmFloat("FuelPurchase");
            diesel = tillVariables.FindFsmFloat("DieselPurchase");
            fuelOil = tillVariables.FindFsmFloat("FuelOilPurchase");
            brokenWindow = tillVariables.FindFsmFloat("BrokenWindow");
            bought = Table(inventory.gameObject, "Bought");
            stocked = Table(inventory.gameObject, "Stocked");
            prices = Table(inventory.gameObject, "Prices");
            PlayMakerFSM logic = Machine(inventory.gameObject, InventoryMachine);
            multiplier = logic == null ? null : logic.FsmVariables.FindFsmFloat("PriceMultiplier");
            if (priceTotal == null || total == null || bought == null || stocked == null)
            {
                Unfound("the till's 'PriceTotal' and 'Total', and the inventory's tables 'Bought' and 'Stocked'");
                return;
            }

            var skipped = new StringBuilder();
            int shelf = 0;
            int unique = 0;
            PlayMakerFSM[] machines = shelves.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName != BuyMachine)
                {
                    continue;
                }

                Product product = Describe(machines[i]);
                if (product == null)
                {
                    skipped.Append(skipped.Length == 0 ? string.Empty : ", ").Append(machines[i].gameObject.name);
                    continue;
                }

                if (byKey.ContainsKey(product.Key))
                {
                    continue;
                }

                Read(product, scratch);
                product.KnownCounter = scratch[0];
                product.KnownTaken = scratch[1];
                products.Add(product);
                byKey[product.Key] = product;
                if (product.Unique)
                {
                    unique++;
                }
                else
                {
                    shelf++;
                }
            }

            found = products.Count > 0;
            billKnown = fuel != null && diesel != null && fuelOil != null ? ReadBill(new int[3]) : null;
            LogPrices();
            log.Info(
                LogCategory.Fsm,
                "Shop: " + shelf + " product(s) on the shelves and " + unique + " one of a kind are shared" + (skipped.Length > 0 ? "; left alone: " + skipped : string.Empty) + ".");
            if (found && manager.Mode == NetworkMode.Host)
            {
                TellAll();
            }
        }

        // The host: what its game has now is the session's from the start. Its player may have shopped since the save the
        // guests load, alone before hosting or since the last save; only what changes from here on would be told otherwise.
        private void TellAll()
        {
            int taken = 0;
            for (int i = 0; i < products.Count; i++)
            {
                Product product = products[i];
                int[] now = { product.KnownCounter, product.KnownTaken };
                current.Tell(product.Key, now, now);
                taken += product.KnownTaken;
            }

            if (billKnown != null)
            {
                current.Set(FuelBillKey, billKnown);
            }

            log.Info(LogCategory.Fsm, "Shop: this game's shelves and counter told as they are now (" + products.Count + " product(s), " + taken + " taken off the shelves)"
                + (billKnown != null ? " and its fuel bill" : string.Empty) + ", for whoever joins.");
        }

        private void Unfound(string what)
        {
            if (!searchReported)
            {
                searchReported = true;
                log.Warning(LogCategory.Fsm, "The shop was not found as this build expects it (" + what + "): the shelves and the till are each game's own.");
            }
        }

        // A product of the shop, or null if this one is not made the way this build knows.
        private Product Describe(PlayMakerFSM buy)
        {
            string name = buy.gameObject.name;
            FsmVariables variables = buy.FsmVariables;
            FsmVariables tillVariables = till.FsmVariables;
            if (name.StartsWith("Buy", StringComparison.Ordinal))
            {
                // One of a kind: "BuyDash" puts one "QSuomiDash" on the counter.
                FsmInt one = tillVariables.FindFsmInt("QSuomi" + name.Substring(3));
                FsmGameObject mesh = variables.FindFsmGameObject("Mesh");
                FsmFloat price = variables.FindFsmFloat("Price");
                if (one == null || mesh == null || mesh.Value == null)
                {
                    return null;
                }

                return new Product { Name = name, Key = KeyPrefix + name, Unique = true, Counter = one, Shown = mesh.Value, Trigger = buy.gameObject, Price = price == null ? 0f : price.Value };
            }

            FsmInt counter = tillVariables.FindFsmInt("Q" + name);
            FsmGameObject list = variables.FindFsmGameObject("ProductList");
            if (counter == null || list == null || list.Value == null)
            {
                return null;
            }

            return new Product
            {
                Name = name,
                Key = KeyPrefix + name,
                Counter = counter,
                Shelf = list.Value.transform,
                OwnTaken = variables.FindFsmInt("Bought"),
                Buy = buy,
                OwnPrice = variables.FindFsmFloat("Price"),
            };
        }

        private bool AtRest()
        {
            bool atRest = Array.IndexOf(TillAtRest, till.ActiveStateName ?? string.Empty) >= 0;
            if (!atRest && !waitReported && waiting.Count > 0)
            {
                waitReported = true;
                log.Info(LogCategory.Fsm, "Shop: the till here is in the middle of a payment ('" + till.ActiveStateName + "'); what the others did waits for it.");
            }

            return atRest;
        }

        // What this machine's game did since the last frame is told.
        private void Watch()
        {
            if (billKnown != null)
            {
                // A pump in use writes it every frame: told at most four times a second, and the last value always.
                ReadBill(billNow);
                if ((billNow[0] != billKnown[0] || billNow[1] != billKnown[1] || billNow[2] != billKnown[2]) && clock >= nextBillTell)
                {
                    nextBillTell = clock + 0.25;
                    billKnown = new[] { billNow[0], billNow[1], billNow[2] };
                    current.Set(FuelBillKey, billKnown);
                }
            }

            for (int i = 0; i < products.Count; i++)
            {
                Product product = products[i];
                Read(product, scratch);
                if (scratch[0] == product.KnownCounter && scratch[1] == product.KnownTaken)
                {
                    continue;
                }

                int[] before = { product.KnownCounter, product.KnownTaken };
                int[] after = { scratch[0], scratch[1] };
                product.KnownCounter = after[0];
                product.KnownTaken = after[1];
                current.Tell(product.Key, before, after);
                log.Info(LogCategory.Fsm, "Shop: this game " + Describe(product, before, after) + "; told.");
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            Wait(new Waiting { Key = key, Values = values, CatchUp = current.CatchingUp });
        }

        private void OnChangeWanted(string key, int[] change, byte player)
        {
            Wait(new Waiting { Key = key, Values = change, IsChange = true, Player = player });
        }

        private void Wait(Waiting next)
        {
            if (waiting.Count >= MaxWaiting)
            {
                log.Warning(LogCategory.Fsm, "Shop: too much waiting for the till here; '" + next.Key + "' is dropped.");
                return;
            }

            waiting.Add(next);
        }

        // What the others did, written into this game, in the order it came.
        private void Catch()
        {
            waitReported = false;
            bool changed = false;
            for (int i = 0; i < waiting.Count; i++)
            {
                Waiting next = waiting[i];
                if (next.Key == FuelBillKey)
                {
                    if (billKnown != null && next.Values.Length >= 3 && !next.IsChange)
                    {
                        changed |= WriteBill(next.Values);
                    }

                    continue;
                }

                Product product;
                if (!byKey.TryGetValue(next.Key, out product) || next.Values.Length < 2)
                {
                    continue;
                }

                // What the game has now: if something here was not told yet, it is told first.
                Read(product, scratch);
                int[] before = { scratch[0], scratch[1] };
                int[] after;
                if (next.IsChange)
                {
                    // On the host: a client changed it by this much. Never less than nothing, never more than there is.
                    after = new[] { Math.Max(0, before[0] + next.Values[0]), Math.Max(0, Math.Min(Stock(product), before[1] + next.Values[1])) };
                }
                else
                {
                    // The host's numbers are the numbers: not cut down to this game's stock, which may be older than the
                    // host's (a one of a kind is taken or not).
                    int taken = Math.Max(0, next.Values[1]);
                    int stock = Stock(product);
                    if (taken > stock && !product.Unique)
                    {
                        log.Info(LogCategory.Fsm, "Shop: the host has " + taken + " '" + product.Name + "' taken off the shelf, more than this game's stock of " + stock + "; taken as the host has it.");
                    }

                    after = new[] { Math.Max(0, next.Values[0]), product.Unique ? Math.Min(1, taken) : taken };
                }

                if (before[0] != product.KnownCounter || before[1] != product.KnownTaken)
                {
                    current.Tell(product.Key, new[] { product.KnownCounter, product.KnownTaken }, before);
                }

                Write(product, after);
                product.KnownCounter = after[0];
                product.KnownTaken = after[1];
                changed = true;
                if (next.IsChange)
                {
                    current.Tell(product.Key, before, after);
                }

                if (before[0] != after[0] || before[1] != after[1])
                {
                    string who = next.IsChange ? "player " + next.Player + "'s game " : next.CatchUp ? "as the host has it now (a catch-up on joining), " : "as the host says, ";
                    log.Info(LogCategory.Fsm, "Shop: " + who + Describe(product, before, after) + ".");
                }
            }

            waiting.Clear();
            if (changed)
            {
                Bill();
            }
        }

        // What was pumped and not paid yet, in hundredths: 98, diesel, fuel oil.
        private int[] ReadBill(int[] values)
        {
            values[0] = Mathf.RoundToInt(fuel.Value * 100f);
            values[1] = Mathf.RoundToInt(diesel.Value * 100f);
            values[2] = Mathf.RoundToInt(fuelOil.Value * 100f);
            return values;
        }

        private bool WriteBill(int[] values)
        {
            if (values[0] == billKnown[0] && values[1] == billKnown[1] && values[2] == billKnown[2])
            {
                return false;
            }

            fuel.Value = values[0] / 100f;
            diesel.Value = values[1] / 100f;
            fuelOil.Value = values[2] / 100f;
            billKnown = new[] { values[0], values[1], values[2] };
            log.Info(LogCategory.Fsm, "Shop: the fuel on the bill, as another game has it: " + (values[0] / 100f).ToString("0.00", CultureInfo.InvariantCulture) + " + "
                + (values[1] / 100f).ToString("0.00", CultureInfo.InvariantCulture) + " + " + (values[2] / 100f).ToString("0.00", CultureInfo.InvariantCulture) + ".");
            return true;
        }

        // On the counter, and taken off the shelf (one of a kind: 1 if taken).
        private void Read(Product product, int[] values)
        {
            values[0] = product.Counter.Value;
            if (product.Unique)
            {
                values[1] = product.Trigger != null && product.Trigger.activeSelf ? 0 : 1;
                return;
            }

            Hashtable table = bought.hashTable;
            values[1] = (int)Number(table == null ? null : table[product.Name], 0f);
        }

        private void Write(Product product, int[] values)
        {
            product.Counter.Value = values[0];
            if (product.Unique)
            {
                bool there = values[1] == 0;
                if (product.Trigger != null && product.Trigger.activeSelf != there)
                {
                    product.Trigger.SetActive(there);
                }

                if (product.Shown != null && product.Shown.activeSelf != there)
                {
                    product.Shown.SetActive(there);
                }

                return;
            }

            Hashtable table = bought.hashTable;
            if (table != null)
            {
                table[product.Name] = values[1];
            }

            if (product.OwnTaken != null)
            {
                product.OwnTaken.Value = values[1];
            }

            // The shelf's children are numbered from one; the first so many are the ones taken.
            for (int n = 1; n <= product.Shelf.childCount; n++)
            {
                Transform item = product.Shelf.Find(n.ToString(CultureInfo.InvariantCulture));
                bool there = n > values[1];
                if (item != null && item.gameObject.activeSelf != there)
                {
                    item.gameObject.SetActive(there);
                }
            }
        }

        private int Stock(Product product)
        {
            if (product.Unique)
            {
                return 1;
            }

            Hashtable table = stocked.hashTable;
            return (int)Number(table == null ? null : table[product.Name], product.Shelf.childCount);
        }

        // The bill at the till, as the game would have it with what is on the counter now.
        private void Bill()
        {
            float sum = 0f;
            for (int i = 0; i < products.Count; i++)
            {
                Product product = products[i];
                int count = product.Counter.Value;
                if (count != 0)
                {
                    sum += count * PriceOf(product);
                }
            }

            priceTotal.Value = sum;
            total.Value = Value(fuel) + sum + Value(diesel) + Value(fuelOil) + Value(brokenWindow);
        }

        // What one more of the product adds to the bill here: what its own state machine adds when clicked, once it has
        // started (it works its price out then); before that, the inventory's price times the day's multiplier.
        private float PriceOf(Product product)
        {
            if (product.Unique)
            {
                return product.Price;
            }

            if (product.Buy != null && product.Buy.Fsm != null && product.Buy.Fsm.Started && product.OwnPrice != null)
            {
                return product.OwnPrice.Value;
            }

            Hashtable table = prices == null ? null : prices.hashTable;
            return Number(table == null ? null : table[product.Name], 0f) * (multiplier == null ? 1f : multiplier.Value);
        }

        // Both prices of a few products, once: if they disagree, the log says so.
        private void LogPrices()
        {
            Hashtable table = prices == null ? null : prices.hashTable;
            var text = new StringBuilder();
            for (int i = 0; i < products.Count && i < 6; i++)
            {
                Product product = products[i];
                if (product.Unique)
                {
                    continue;
                }

                text.Append(text.Length == 0 ? string.Empty : ", ").Append(product.Name).Append(' ')
                    .Append(product.OwnPrice == null ? "?" : product.OwnPrice.Value.ToString("0.00", CultureInfo.InvariantCulture))
                    .Append(product.Buy != null && product.Buy.Fsm != null && product.Buy.Fsm.Started ? string.Empty : " (not started)")
                    .Append(" / table ").Append(Number(table == null ? null : table[product.Name], -1f).ToString("0.00", CultureInfo.InvariantCulture));
            }

            log.Info(LogCategory.Fsm, "Shop: prices, the product's own and the inventory's (times " + (multiplier == null ? "?" : multiplier.Value.ToString("0.000", CultureInfo.InvariantCulture)) + "): " + text + ".");
        }

        // A number from one of the game's tables, whatever kind of number the table keeps.
        private static float Number(object value, float otherwise)
        {
            if (value is int)
            {
                return (int)value;
            }

            if (value is float)
            {
                return (float)value;
            }

            if (value is double)
            {
                return (float)(double)value;
            }

            return otherwise;
        }

        private static float Value(FsmFloat variable)
        {
            return variable == null ? 0f : variable.Value;
        }

        private static string Describe(Product product, int[] before, int[] after)
        {
            if (product.Unique)
            {
                return (after[1] > before[1] ? "took '" : after[1] < before[1] ? "put back '" : "left '") + product.Name + "' (" + after[0] + " on the counter)";
            }

            string verb = after[1] > before[1] ? "took " + (after[1] - before[1]) : after[1] < before[1] ? "put back " + (before[1] - after[1]) : after[0] < before[0] ? "paid for " + (before[0] - after[0]) : "changed";
            return verb + " '" + product.Name + "': " + after[0] + " on the counter, " + after[1] + " taken off the shelf";
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

        private static PlayMakerHashTableProxy Table(GameObject owner, string referenceName)
        {
            PlayMakerHashTableProxy[] tables = owner.GetComponents<PlayMakerHashTableProxy>();
            for (int i = 0; i < tables.Length; i++)
            {
                if (tables[i].referenceName == referenceName)
                {
                    return tables[i];
                }
            }

            return null;
        }

        private sealed class Product
        {
            public string Name;
            public string Key;
            public bool Unique;
            public FsmInt Counter;
            public Transform Shelf;
            public FsmInt OwnTaken;
            public GameObject Shown;
            public GameObject Trigger;
            public float Price;
            public PlayMakerFSM Buy;
            public FsmFloat OwnPrice;
            public int KnownCounter;
            public int KnownTaken;
        }

        private struct Waiting
        {
            public string Key;
            public int[] Values;
            public bool IsChange;
            public byte Player;

            /// <summary>Part of the host's answer to this game's request: how the shop is, not what someone just did.</summary>
            public bool CatchUp;
        }
    }
}
