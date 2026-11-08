using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The mail orders of car parts and their boxes:
    /// an order is the host's world's, whoever posts it; it is paid at the post office from any game, and its boxes come,
    /// are carried and are opened in every game.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Los pedidos por correo"): the parts magazine's order is the
    /// list "Order" of "Sheets/OrderList"; posting its envelope in Teimo's post box ("STORE/LOD/Post Box/OrderTrigger")
    /// copies it to "Delivering" and switches "Sheets/OrderList/Timer" on. Its "Delivery" counts 5000 s in "State 1", then
    /// in "Pay" rings the phone and opens the post office's counter ("PostOrderBuy"), and waits for "Data.Paid"; then it
    /// takes each part's box out of "STORE/Boxes" (marked bought, named "amis-auto ky package(xxxxx)", put at the root where
    /// it stood) and switches itself off. A box's "Use" opens it ("State 1": the part out, a folded box made, the box gone).
    /// The computer's order ("Sheets/OrderComputer") is the same with one box.
    ///
    /// In a session: the host's timer counts; a client's is held in "State 1". A client that posts tells the host its list,
    /// and the host's order takes it. When the host's order is at the post office, every client's timer is taken to "Pay"
    /// with the host's list, and whoever pays tells the others ("Paid"), so that every game takes the same boxes out the
    /// game's way. A box opened in one game is opened in the others.
    /// </summary>
    internal sealed class OrderSync
    {
        private const LogCategory Category = LogCategory.Fsm;
        private const string Key = "order/";
        private const string BoxKey = "order/box/";
        private const string DataMachine = "Data";
        private const string TimerName = "Timer";
        private const string TimerMachine = "Delivery";
        private const string CountState = "State 1";
        private const string PayState = "Pay";
        private const string TakeOutState = "Activate package";
        private const string PayEvent = "SSYNC ORDER PAY";
        private const string BoxMachine = "Use";
        private const string OpenState = "State 1";
        private const string OpenEvent = "SSYNC BOX OPEN";
        private const string DeliveringList = "Delivering";
        private const string ArrivalVariable = "Current";

        // When the timer counts this far, the order is at the post office (the "FloatCompare" of "State 1").
        private const float ArrivalSeconds = 5000f;

        // A client's post is read this long after its timer switched on: the post box copies the list in that frame.
        private const double PostReadDelay = 0.3;

        private static readonly HashSet<int> BoxesHooked = new HashSet<int>();
        private static readonly HashSet<int> CountersHooked = new HashSet<int>();
        private const string CounterMachine = "Use";
        private const string CounterPayState = "State 3";
        private static OrderSync active;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly Order[] orders =
        {
            new Order("parts", "Sheets/OrderList", true),
            new Order("computer", "Sheets/OrderComputer", false),
        };

        private readonly List<GameObject> boxes = new List<GameObject>();
        private readonly List<string> boxNames = new List<string>();
        private readonly List<PlayMakerFSM> boxMachines = new List<PlayMakerFSM>();
        private readonly HashSet<int> opening = new HashSet<int>();
        private readonly HashSet<int> openTold = new HashSet<int>();
        private TallyReplication current;
        private NetSession session;
        private bool host;
        private bool searched;
        private bool found;

        public OrderSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null without a world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                Release();
                current = tallies;
                session = current == null ? null : manager.Session;
                host = manager.Mode == NetworkMode.Host;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                    active = this;
                }
            }

            if (current == null || session == null || session.State != SessionState.Connected)
            {
                return;
            }

            if (!searched)
            {
                searched = true;
                Search();
            }

            if (!found)
            {
                return;
            }

            for (int i = 0; i < orders.Length; i++)
            {
                Update(orders[i], now);
            }
        }

        /// <summary>Everything back as the game had it.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            for (int i = 0; i < orders.Length; i++)
            {
                orders[i].Let();
            }

            if (active == this)
            {
                active = null;
            }

            current = null;
            session = null;
            searched = false;
            found = false;
            boxes.Clear();
            boxNames.Clear();
            boxMachines.Clear();
            opening.Clear();
            openTold.Clear();
        }

        /// <summary>
        /// A test aid ("ss order"): with names of boxes ("fiberglass hood, fender flares"), this game posts that order, as the
        /// post box does; "computer" posts the computer's; "now" has the host's order at the post office now; without
        /// anything, how the orders stand.
        /// </summary>
        public string TestAid(string what)
        {
            if (!found)
            {
                return "The mail orders are not shared here (no session, or the game is not as this build expects: the log says).";
            }

            what = (what ?? string.Empty).Trim();
            if (what.Length == 0)
            {
                var text = new StringBuilder();
                for (int i = 0; i < orders.Length; i++)
                {
                    text.Append(i == 0 ? string.Empty : " ").Append(orders[i].Describe(boxNames)).Append('.');
                }

                return text.ToString();
            }

            if (string.Equals(what, "now", StringComparison.OrdinalIgnoreCase))
            {
                if (!host)
                {
                    return "Only the host's order arrives: run it there.";
                }

                int moved = 0;
                for (int i = 0; i < orders.Length; i++)
                {
                    if (orders[i].Arrive())
                    {
                        moved++;
                    }
                }

                return moved == 0 ? "No order is on its way." : moved + " order(s) at the post office in a moment.";
            }

            if (string.Equals(what, "computer", StringComparison.OrdinalIgnoreCase))
            {
                orders[1].PostHere();
                return "The computer's order is posted from this game.";
            }

            Order parts = orders[0];
            var picked = new List<int>();
            string[] asked = what.Split(',');
            for (int i = 0; i < asked.Length; i++)
            {
                int index = BoxIndex(asked[i].Trim());
                if (index < 0)
                {
                    return "No box '" + asked[i].Trim() + "' in STORE/Boxes. Some: " + string.Join(", ", boxNames.GetRange(0, Math.Min(6, boxNames.Count)).ToArray()) + "…";
                }

                picked.Add(index);
            }

            parts.WriteList(Positions(picked), boxNames);
            parts.PostHere();
            return "An order of " + picked.Count + " box(es) is posted from this game, as the post box does.";
        }

        /// <summary>For the console's test aid.</summary>
        public static string Aid(string what)
        {
            OrderSync sync = active;
            return sync == null ? "The mod is not in a session with a game world." : sync.TestAid(what);
        }

        // The list as the magazine fills it: from slot 1, the game reads from there.
        private static int[] Positions(List<int> picked)
        {
            var list = new int[picked.Count + 1];
            list[0] = -1;
            for (int i = 0; i < picked.Count; i++)
            {
                list[i + 1] = picked[i];
            }

            return list;
        }

        private int BoxIndex(string name)
        {
            for (int i = 0; i < boxNames.Count; i++)
            {
                if (string.Equals(boxNames[i], name, StringComparison.OrdinalIgnoreCase) || string.Equals(boxNames[i], name + "(Clone)", StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private void Search()
        {
            int ready = 0;
            Transform boxRoot = null;
            for (int i = 0; i < orders.Length; i++)
            {
                if (orders[i].Find(log))
                {
                    ready++;
                    if (boxRoot == null)
                    {
                        boxRoot = orders[i].BoxRoot();
                    }
                }
            }

            if (ready == 0 || boxRoot == null)
            {
                log.Warning(Category, "Orders: the mail orders are not as this build expects (" + ready + " of " + orders.Length + " found, the boxes " + (boxRoot == null ? "NOT found" : "found") + "); each game has its own orders and boxes.");
                return;
            }

            // Every box, by its name: the same list in every game of the same build.
            var all = new List<Transform>();
            for (int i = 0; i < boxRoot.childCount; i++)
            {
                all.Add(boxRoot.GetChild(i));
            }

            all.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            for (int i = 0; i < all.Count; i++)
            {
                boxes.Add(all[i].gameObject);
                boxNames.Add(all[i].name);
                PlayMakerFSM use = DoorSync.FindMachine(all[i], BoxMachine);
                boxMachines.Add(use);
                if (use != null && Prepare(use) && DoorSync.HasState(use, OpenState) && BoxesHooked.Add(use.GetInstanceID()))
                {
                    int index = i;
                    use.FsmInject(OpenState, () => Opened(index), false, 0, false);
                }
            }

            for (int i = 0; i < orders.Length; i++)
            {
                orders[i].Watch();
                int which = i;
                orders[i].HookCounter(() => CounterPaid(which));
            }

            found = true;
            log.Info(Category, "Orders: the mail orders shared; " + boxes.Count + " box(es) known; " + (host ? "this game's order is the order." : "the host's order is followed here."));
        }

        // A machine of a switched-off object has not run, and has no actions yet: loaded on demand, as MSCLoader does.
        private static bool Prepare(PlayMakerFSM machine)
        {
            if (machine.Fsm != null && machine.Fsm.Initialized)
            {
                return true;
            }

            try
            {
                machine.InitializeFSM();
                return machine.Fsm != null && machine.Fsm.Initialized;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Update(Order order, double now)
        {
            if (!order.Found)
            {
                return;
            }

            bool on = order.TimerOn;

            // A post here: the timer switched on, and not by the mod following the host.
            if (on && !order.WasOn && !order.Following)
            {
                order.PostedAt = now;
                order.PostPending = true;
            }

            order.WasOn = on;
            if (order.PostPending && now - order.PostedAt >= PostReadDelay)
            {
                order.PostPending = false;
                Posted(order);
            }

            if (!host && on && order.TimerState == CountState && !order.PostPending)
            {
                // The host's timer counts; this one waits for it.
                order.Hold();
            }

            if (host)
            {
                TellPhase(order);
            }

        }

        private static void CounterPaid(int which)
        {
            OrderSync sync = active;
            if (sync != null && which >= 0 && which < sync.orders.Length)
            {
                sync.PaidHere(sync.orders[which]);
            }
        }

        // Paid here, at the post office's counter, by this player: the game takes the boxes out and clears "Paid" in the same
        // frame, so it is told from the counter's paying state, not from "Paid".
        private void PaidHere(Order order)
        {
            if (current == null || session == null || session.State != SessionState.Connected)
            {
                return;
            }

            order.PaidSaid = Math.Max(order.PaidSaid, order.PaidHeard) + 1;
            current.Set(Key + order.Name + "/paid", new[] { order.PaidSaid });
            log.Info(Category, "Orders: the " + order.Name + " order was paid in this game; the others take its boxes out.");
        }

        // This game posted an order: the host's own counts; a client tells the host.
        private void Posted(Order order)
        {
            order.ListDirty = true;
            if (host)
            {
                log.Info(Category, "Orders: the " + order.Name + " order was posted in this game; it is the order.");
                return;
            }

            order.PostSaid++;
            int[] list = order.ReadList(boxNames);
            current.Set(Key + order.Name + "/post", WithHead(order.PostSaid, ToWire(list)));
            order.Hold();
            log.Info(Category, "Orders: the " + order.Name + " order was posted in this game (" + Count(list) + " box(es)); the host's order takes it.");
        }

        // The host says where its order is, and what it brings, when that changes. The list is read only then: this runs
        // every frame.
        private void TellPhase(Order order)
        {
            int phase = order.Phase;
            if (phase == order.PhaseSaid && !order.ListDirty)
            {
                return;
            }

            order.ListDirty = false;
            int[] list = order.HasList ? order.ReadList(boxNames) : new int[0];
            if (phase == order.PhaseSaid && SameList(list, order.ListSaid))
            {
                return;
            }

            order.PhaseSaid = phase;
            order.ListSaid = list;
            current.Set(Key + order.Name, WithHead(phase, order.HasList ? ToWire(list) : new int[0]));
            if (phase == Order.AtPostOffice)
            {
                log.Info(Category, "Orders: the " + order.Name + " order is at the post office (" + Count(list) + " box(es)); it can be paid in any game.");
            }
        }

        private static bool SameList(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return a == b;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int Count(int[] list)
        {
            int count = 0;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] >= 0)
                {
                    count++;
                }
            }

            return count;
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (!found || values.Length < 1 || !key.StartsWith(Key, StringComparison.Ordinal))
            {
                return;
            }

            if (key.StartsWith(BoxKey, StringComparison.Ordinal))
            {
                int box;
                if (int.TryParse(key.Substring(BoxKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out box) && values[0] != 0)
                {
                    Open(box);
                }

                return;
            }

            for (int i = 0; i < orders.Length; i++)
            {
                Order order = orders[i];
                string name = key.Substring(Key.Length);
                if (name == order.Name)
                {
                    if (!host)
                    {
                        HostSays(order, values);
                    }
                }
                else if (name == order.Name + "/post")
                {
                    if (host && values[0] > order.PostHeard)
                    {
                        order.PostHeard = values[0];
                        int[] list = FromWire(Tail(values));
                        if (order.HasList)
                        {
                            order.WriteList(list, boxNames);
                        }

                        order.PostHere();
                        order.WasOn = order.TimerOn;
                        order.ListDirty = true;
                        log.Info(Category, "Orders: a guest posted the " + order.Name + " order (" + Count(list) + " box(es)); it is this game's order now.");
                    }
                }
                else if (name == order.Name + "/paid")
                {
                    if (values[0] > order.PaidHeard)
                    {
                        // Not what the host's answer brings to a client that joins: a payment of before, already over.
                        order.PaidHeard = values[0];
                        if (!current.CatchingUp && values[0] > order.PaidSaid)
                        {
                            order.Pay();
                            log.Info(Category, "Orders: the " + order.Name + " order was paid in another game; this game takes its boxes out.");
                        }
                    }
                }
            }
        }

        // A list of boxes travels as which boxes it has, a bit for each known box: a tally has room for 8 numbers, and the
        // list for 46 slots. Each box is in an order once, and the game takes them out and prices them by name, not by slot.
        private int[] ToWire(int[] list)
        {
            var mask = new int[(boxNames.Count + 31) / 32];
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] >= 0 && list[i] < boxNames.Count)
                {
                    mask[list[i] / 32] |= 1 << (list[i] % 32);
                }
            }

            return mask;
        }

        // The boxes as the magazine lists them: from slot 1, in the order of the known boxes.
        private int[] FromWire(int[] mask)
        {
            var picked = new List<int>();
            for (int i = 0; i < boxNames.Count; i++)
            {
                if (i / 32 < mask.Length && (mask[i / 32] & (1 << (i % 32))) != 0)
                {
                    picked.Add(i);
                }
            }

            return Positions(picked);
        }

        private static int[] WithHead(int head, int[] rest)
        {
            var values = new int[rest.Length + 1];
            values[0] = head;
            Array.Copy(rest, 0, values, 1, rest.Length);
            return values;
        }

        private static int[] Tail(int[] values)
        {
            var list = new int[values.Length - 1];
            Array.Copy(values, 1, list, 0, list.Length);
            return list;
        }

        // A client: the host's order is where it says.
        private void HostSays(Order order, int[] values)
        {
            int phase = values[0];
            int[] list = FromWire(Tail(values));
            if (order.HasList && values.Length > 1)
            {
                order.WriteList(list, boxNames);
            }

            if (phase == Order.AtPostOffice && order.TimerState != PayState)
            {
                order.FollowToPost();
                log.Info(Category, "Orders: the host's " + order.Name + " order is at the post office (" + Count(list) + " box(es)); it can be paid here too.");
            }
        }

        private static void Opened(int index)
        {
            OrderSync sync = active;
            if (sync != null)
            {
                sync.BoxOpened(index);
            }
        }

        // A box opened in this game: the others open theirs. One the mod opened for another game is not told back.
        private void BoxOpened(int index)
        {
            if (current == null || index < 0 || index >= boxNames.Count)
            {
                return;
            }

            if (opening.Remove(index) || !openTold.Add(index))
            {
                return;
            }

            current.Set(BoxKey + index.ToString(CultureInfo.InvariantCulture), new[] { 1 });
            log.Info(Category, "Orders: the box of '" + boxNames[index] + "' was opened in this game; the others open theirs.");
        }

        // Another game opened that box: this one opens its own, if it has it out.
        private void Open(int index)
        {
            if (index < 0 || index >= boxes.Count || openTold.Contains(index))
            {
                return;
            }

            GameObject box = boxes[index];
            PlayMakerFSM use = boxMachines[index];

            // Unity's null check is also true for a destroyed object: this one is already open.
            if (box == null || use == null || !box.activeInHierarchy || use.ActiveStateName == OpenState)
            {
                return;
            }

            openTold.Add(index);
            opening.Add(index);
            use.AddGlobalTransition(OpenEvent, OpenState);
            use.SendEvent(OpenEvent);
            log.Info(Category, "Orders: the box of '" + boxNames[index] + "' was opened in another game; opened here too.");
        }

        /// <summary>One of the game's mail orders: its data, its timer and its post office counter.</summary>
        private sealed class Order
        {
            public const int None = 0;
            public const int OnItsWay = 1;
            public const int AtPostOffice = 2;
            public const int TakenOut = 3;

            private readonly string dataPath;
            private PlayMakerFSM data;
            private GameObject timerObject;
            private PlayMakerFSM timer;
            private GameObject counter;
            private ArrayList delivering;
            private FsmBool paid;
            private PausedMachine held;

            public Order(string name, string dataPath, bool hasList)
            {
                Name = name;
                this.dataPath = dataPath;
                HasList = hasList;
                PhaseSaid = -1;
                PaidHeard = -1;
            }

            public string Name { get; private set; }

            public bool HasList { get; private set; }

            public bool Found { get; private set; }

            public bool WasOn { get; set; }

            public bool Following { get; private set; }

            public bool PostPending { get; set; }

            public double PostedAt { get; set; }

            public int PostSaid { get; set; }

            public int PostHeard { get; set; }

            public int PhaseSaid { get; set; }

            public int[] ListSaid { get; set; }

            /// <summary>The list may have changed since it was last told.</summary>
            public bool ListDirty { get; set; }

            public int PaidSaid { get; set; }

            public int PaidHeard { get; set; }


            public bool TimerOn
            {
                get { return timerObject != null && timerObject.activeInHierarchy; }
            }

            public string TimerState
            {
                get { return TimerOn && timer != null ? timer.ActiveStateName : string.Empty; }
            }

            public bool PaidNow
            {
                get { return paid != null && paid.Value; }
            }

            public int Phase
            {
                get
                {
                    if (!TimerOn)
                    {
                        return None;
                    }

                    string state = TimerState;
                    return state == CountState ? OnItsWay : state == PayState ? AtPostOffice : TakenOut;
                }
            }

            public bool Find(Logger log)
            {
                GameObject dataObject = GameObject.Find("/" + dataPath);
                data = dataObject == null ? null : DoorSync.FindMachine(dataObject.transform, DataMachine);
                Transform timerAt = dataObject == null ? null : dataObject.transform.Find(TimerName);
                timerObject = timerAt == null ? null : timerAt.gameObject;
                timer = timerAt == null ? null : DoorSync.FindMachine(timerAt, TimerMachine);
                if (timer != null)
                {
                    Prepare(timer);
                }

                paid = data == null ? null : data.FsmVariables.FindFsmBool("Paid");
                counter = timer == null ? null : Activated(timer, PayState);
                if (HasList && dataObject != null)
                {
                    PlayMakerArrayListProxy[] proxies = dataObject.GetComponents<PlayMakerArrayListProxy>();
                    for (int i = 0; i < proxies.Length; i++)
                    {
                        if (proxies[i] != null && proxies[i].referenceName == DeliveringList)
                        {
                            delivering = proxies[i].arrayList;
                        }
                    }
                }

                Found = data != null && timer != null && paid != null && counter != null && (!HasList || delivering != null)
                    && DoorSync.HasState(timer, CountState) && DoorSync.HasState(timer, PayState);
                if (!Found)
                {
                    log.Warning(Category, "Orders: the " + Name + " order ('" + dataPath + "') is not as this build expects; it is each game's own.");
                }

                return Found;
            }

            // Where the boxes are: the object the timer takes them out of.
            public Transform BoxRoot()
            {
                FsmState[] states = timer.FsmStates;
                for (int s = 0; states != null && s < states.Length; s++)
                {
                    if (states[s].Name != TakeOutState)
                    {
                        continue;
                    }

                    FsmStateAction[] actions = states[s].Actions;
                    for (int a = 0; actions != null && a < actions.Length; a++)
                    {
                        var find = actions[a] as FindChild;
                        GameObject root = find == null ? null : timer.Fsm.GetOwnerDefaultTarget(find.gameObject);
                        if (root != null)
                        {
                            return root.transform;
                        }
                    }
                }

                return null;
            }

            public void Watch()
            {
                WasOn = TimerOn;
            }

            // The counter's paying state ("State 3": the money taken, "Paid" set, the counter off) tells the others.
            public void HookCounter(Action paying)
            {
                PlayMakerFSM use = counter == null ? null : DoorSync.FindMachine(counter.transform, CounterMachine);
                if (use != null && Prepare(use) && DoorSync.HasState(use, CounterPayState) && CountersHooked.Add(use.GetInstanceID()))
                {
                    use.FsmInject(CounterPayState, paying, false, 0, false);
                }
            }

            // The object a state of the timer switches on: the post office's counter of this order.
            private static GameObject Activated(PlayMakerFSM machine, string stateName)
            {
                FsmState[] states = machine.FsmStates;
                for (int s = 0; states != null && s < states.Length; s++)
                {
                    if (states[s].Name != stateName)
                    {
                        continue;
                    }

                    FsmStateAction[] actions = states[s].Actions;
                    for (int a = 0; actions != null && a < actions.Length; a++)
                    {
                        var activate = actions[a] as ActivateGameObject;
                        if (activate != null && activate.activate.Value)
                        {
                            return machine.Fsm.GetOwnerDefaultTarget(activate.gameObject);
                        }
                    }
                }

                return null;
            }

            // The list of boxes as indices of the known boxes, -1 for an empty slot.
            public int[] ReadList(List<string> names)
            {
                if (delivering == null)
                {
                    return new int[0];
                }

                var list = new int[delivering.Count];
                for (int i = 0; i < list.Length; i++)
                {
                    string name = delivering[i] as string;
                    list[i] = string.IsNullOrEmpty(name) ? -1 : names.IndexOf(name);
                }

                return list;
            }

            public void WriteList(int[] list, List<string> names)
            {
                if (delivering == null)
                {
                    return;
                }

                for (int i = 0; i < delivering.Count; i++)
                {
                    int index = i < list.Length ? list[i] : -1;
                    delivering[i] = index >= 0 && index < names.Count ? names[index] : string.Empty;
                }
            }

            // As the post box does: the timer on, from its start.
            public void PostHere()
            {
                Release();
                if (!timerObject.activeSelf)
                {
                    timerObject.SetActive(true);
                }
            }

            // A client's timer waits where it is, for the host's.
            public void Hold()
            {
                if (held == null && timer != null && timer.enabled)
                {
                    held = new PausedMachine(timer);
                }
            }

            private void Release()
            {
                if (held != null)
                {
                    held.Resume();
                    held = null;
                }
            }

            // A client: the host's order is at the post office; this game's goes there too, with the counter open.
            public void FollowToPost()
            {
                Following = true;
                if (!timerObject.activeSelf)
                {
                    timerObject.SetActive(true);
                }

                Release();
                timer.AddGlobalTransition(PayEvent, PayState);
                timer.SendEvent(PayEvent);
                Following = false;

                // Switched on by the mod, not posted here.
                WasOn = TimerOn;
            }

            // Paid in another game: paid here, the counter closed as paying closes it.
            public void Pay()
            {
                if (paid != null)
                {
                    paid.Value = true;
                }

                if (counter != null && counter.activeSelf)
                {
                    counter.SetActive(false);
                }

                Release();
            }

            // The host's test aid: the order on its way is at the post office in a moment.
            public bool Arrive()
            {
                if (!TimerOn || TimerState != CountState)
                {
                    return false;
                }

                FsmFloat arrival = timer.FsmVariables.FindFsmFloat(ArrivalVariable);
                if (arrival == null)
                {
                    return false;
                }

                arrival.Value = ArrivalSeconds - 1f;
                return true;
            }

            public string Describe(List<string> names)
            {
                string phase = Phase == None ? "none" : Phase == OnItsWay ? "on its way" : Phase == AtPostOffice ? "at the post office" : "being taken out";
                return "The " + Name + " order: " + phase + (held != null ? ", held for the host's" : string.Empty) + (PaidNow ? ", paid" : string.Empty)
                    + (HasList ? ", " + Count(ReadList(names)) + " box(es)" : string.Empty);
            }

            /// <summary>Back as the game had it: a held timer goes on.</summary>
            public void Let()
            {
                Release();
                Found = false;
                PhaseSaid = -1;
                ListSaid = null;
                PaidHeard = -1;
                PostPending = false;
            }
        }
    }
}
