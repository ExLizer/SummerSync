using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using SummerSync.Unity.Objects;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Teimo's pub, one for everyone: what one player orders at the bar, Teimo serves in every game; the jukebox plays
    /// the host's song; and Teimo is angry, or fed up, in every game at once.
    ///
    /// What is assumed about the game, from the dumps of 2026-10-14 and the game with the test bridge
    /// (docs/GAME_NOTES.md, "El pub"): the bar is open from 20 ("STORE :: OpeningHours", "OpenBar"). An order
    /// ("STORE/LOD/ActivateBar/OrderList/n", state machine "Buy") sets the till's "Total" and "EventName" (BEER, VODKA,
    /// POTATOES, COFFEE, CIGARETTES) and sends the till ("STORE/LOD/GFX_Pub/PubCashRegister/Register", state machine
    /// "Data") PURCHASE; for a beer Teimo also says one of nine lines ("PubSpeak", states 17 to 25). The till charges
    /// "Total" in "Pay" and goes, by the order's name, to "Anim" (beer), "Anim 5" (vodka), "Anim 2" (sausage and fries,
    /// through the kitchen), "Anim 6" (coffee) or "Anim 7" (cigarettes): Teimo hands it over, and "Spawn …" makes the
    /// thing ("CreateObject") at "DrinkSpawnPoint" or "FoodSpawnPoint"; cigarettes come from the numbered maker
    /// (<see cref="SpawnSync"/> makes those everywhere). Then "State 3" waits for the next order. The jukebox
    /// ("ActivateBar/JukeboxStore", state machine "Logic") picks a random clip of "RADIO/Paikallisradio"'s "Music"
    /// through its list "Jukebox". Teimo's anger is "Angry" of "STORE/TeimoInShop :: Angry"; the pub's till has
    /// "PissedOff" for a customer he will not serve.
    ///
    /// An order here is told; elsewhere the till serves it for nothing (Total 0, the money is one for everyone) from
    /// "State 2", with the same line. A game whose bar is switched off (its player far away) makes the thing where
    /// Teimo would, and lets the buyer's game move it. The host's jukebox, while its bar is on, is the jukebox: a
    /// client's picks nothing and plays the host's clip (<see cref="SharedJukebox"/>; Fleetari's in the repair shop's
    /// office is the same machine, and the host's too).
    /// </summary>
    internal sealed class PubSync
    {
        private const string OrderKey = "pub/order/";
        private const string MoodKey = "teimo/mood";
        private const string TillPath = "STORE/LOD/GFX_Pub/PubCashRegister/Register";
        private const string OrderListPath = "STORE/LOD/ActivateBar/OrderList";
        private const string SongsPath = "RADIO/Paikallisradio";
        private const string TeimoPath = "STORE/TeimoInShop";
        private const string IdleState = "State 3";
        private const string ServeState = "State 2";
        private const string ServeEvent = "SSYNC PUB SERVE";
        private const string LineEvent = "SSYNC PUB LINE ";
        private const int FirstLineState = 17;
        private const int LineCount = 9;
        private const double SearchInterval = 2.0;
        private const double HistorySeconds = 3.0;
        private const double QueueSeconds = 30.0;
        private const double LineWindow = 1.0;

        // The orders, in the till's own names, with the state that starts handing each over and the one that makes it.
        private static readonly string[] Items = { "BEER", "VODKA", "POTATOES", "COFFEE", "CIGARETTES" };
        private static readonly string[] HandOver = { "Anim", "Anim 5", "Anim 2", "Anim 6", "Anim 7" };
        private static readonly string[] MakeStates = { "Spawn beer", "Spawn vodka", "Spawn food", "Spawn coffee", null };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly ObjectPresenter objects;
        private readonly List<Heard> heard = new List<Heard>();
        private readonly Dictionary<string, int> ordersHeard = new Dictionary<string, int>();
        private readonly List<Order> queue = new List<Order>();
        private readonly List<Made> made = new List<Made>();
        private readonly SharedJukebox[] jukeboxes;
        private TallyReplication current;
        private bool host;
        private double nextSearch;
        private bool found;
        private bool searchReported;
        private double historyUntil;

        private PlayMakerFSM till;
        private PlayMakerFSM speak;
        private GameObject orderList;
        private bool hooked;
        private FsmFloat total;
        private FsmString eventName;
        private FsmBool pissedOff;
        private Maker[] makers;
        private string lastTillState;
        private bool servingForOthers;
        private int lastLine;
        private double lastLineAt = double.NegativeInfinity;
        private string lastSpeakState;
        private int ordersTold;

        private ArrayList songs;
        private bool jukeboxesFound;

        private FsmBool teimoAngry;
        private bool knownAngry;
        private bool knownPissed;

        public PubSync(Logger log, NetworkManager manager, ObjectPresenter objects)
        {
            this.log = log;
            this.manager = manager;
            this.objects = objects;
            jukeboxes = new[]
            {
                new SharedJukebox(log, "pub/jukebox", "STORE/LOD/ActivateBar/JukeboxStore", "Pub"),
                new SharedJukebox(log, "repairshop/jukebox", "REPAIRSHOP/LOD/Office/Fleetari/JukeboxFleetari", "Fleetari's office"),
            };
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }

                heard.Clear();
                ordersHeard.Clear();
                queue.Clear();
                made.Clear();
                found = false;
                hooked = false;
                jukeboxesFound = false;
                for (int i = 0; i < jukeboxes.Length; i++)
                {
                    jukeboxes[i].Forget();
                }

                nextSearch = now;
            }

            if (current == null)
            {
                return;
            }

            if (!found && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                if (!jukeboxesFound)
                {
                    FindJukeboxes();
                }

                Search(now);
            }

            // The jukeboxes do not need the till.
            for (int i = 0; i < jukeboxes.Length; i++)
            {
                jukeboxes[i].Update(now, host, current, songs);
            }

            // Unity's null check is also true for a destroyed object.
            if (!found || till == null)
            {
                return;
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Values, heard[i].CatchUp, now);
            }

            heard.Clear();
            WatchTill(now);
            Serve(now);
            Name(now);
            WatchMood();
        }

        /// <summary>The pub here goes back to how the game had it.</summary>
        public void Release()
        {
            for (int i = 0; i < jukeboxes.Length; i++)
            {
                jukeboxes[i].Release();
            }
        }

        private void FindJukeboxes()
        {
            jukeboxesFound = true;
            Transform songsAt = FindPath(SongsPath);
            PlayMakerArrayListProxy[] proxies = songsAt == null ? new PlayMakerArrayListProxy[0] : songsAt.GetComponents<PlayMakerArrayListProxy>();
            songs = null;
            for (int i = 0; i < proxies.Length; i++)
            {
                if (proxies[i] != null && proxies[i].referenceName == "Music")
                {
                    songs = proxies[i].arrayList;
                }
            }

            for (int i = 0; i < jukeboxes.Length; i++)
            {
                jukeboxes[i].Find(FindPath(jukeboxes[i].Path));
            }

            log.Info(LogCategory.Fsm, songs == null
                ? "Jukeboxes: the songs '" + SongsPath + "' were not found; each game's jukeboxes play their own."
                : "Jukeboxes: " + (jukeboxes[0].Found ? "the pub's" : "not the pub's") + " and " + (jukeboxes[1].Found ? "Fleetari's" : "not Fleetari's")
                    + " found; each is " + (host ? "this game's, and the others hear it." : "the host's while it plays."));
        }

        private void Search(double now)
        {
            Transform tillAt = FindPath(TillPath);
            till = tillAt == null ? null : DoorSync.FindMachine(tillAt, "Data");
            speak = tillAt == null ? null : DoorSync.FindMachine(tillAt, "PubSpeak");
            Transform listAt = FindPath(OrderListPath);
            Transform teimoAt = FindPath(TeimoPath);
            PlayMakerFSM angry = teimoAt == null ? null : DoorSync.FindMachine(teimoAt, "Angry");
            if (till == null || speak == null || listAt == null || !DoorSync.HasState(till, ServeState) || !DoorSync.HasState(till, IdleState))
            {
                if (!searchReported)
                {
                    searchReported = true;
                    log.Warning(LogCategory.Fsm, "Pub: the till '" + TillPath + "' (state machines 'Data' and 'PubSpeak') is not as this build expects: each game's Teimo serves only its own player.");
                }

                till = null;
                return;
            }

            orderList = listAt.gameObject;
            makers = new Maker[Items.Length];
            FsmState[] states = till.Fsm == null ? null : till.Fsm.States;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                int item = Array.IndexOf(MakeStates, states[s].Name);
                FsmStateAction[] actions = item < 0 ? null : states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    var create = actions[a] as CreateObject;
                    if (create != null && create.gameObject != null && create.gameObject.Value != null)
                    {
                        makers[item] = new Maker { Prefab = create.gameObject.Value, Action = create };
                    }
                }
            }

            teimoAngry = angry == null ? null : angry.FsmVariables.FindFsmBool("Angry");
            ReadVariables();
            knownAngry = teimoAngry != null && teimoAngry.Value;
            knownPissed = pissedOff != null && pissedOff.Value;
            lastTillState = till.ActiveStateName;
            lastSpeakState = speak.ActiveStateName;
            historyUntil = now + HistorySeconds;
            found = true;
            int canMake = 0;
            for (int i = 0; i < makers.Length; i++)
            {
                canMake += makers[i] == null ? 0 : 1;
            }

            log.Info(LogCategory.Fsm, "Pub: shared; Teimo serves every order in every game (" + canMake + " of 4 things can be made where the bar is off)"
                + (teimoAngry != null ? "; Teimo's mood is one." : "."));
            if (host && (teimoAngry != null || pissedOff != null))
            {
                // Teimo's mood here is the session's from the start: he may have got angry since the save the guests load,
                // or before hosting; only a change from here on would be told otherwise.
                current.Set(MoodKey, new[] { knownAngry ? 1 : 0, knownPissed ? 1 : 0 });
                log.Info(LogCategory.Fsm, "Pub: Teimo told as he is now, for whoever joins: " + (knownAngry ? "angry" : "calm") + (knownPissed ? " and will not serve." : "."));
            }
        }

        // The machine's data is built again when a transition is added: its variables are found after that.
        private void ReadVariables()
        {
            total = till.FsmVariables.FindFsmFloat("Total");
            eventName = till.FsmVariables.FindFsmString("EventName");
            pissedOff = till.FsmVariables.FindFsmBool("PissedOff");
        }

        private void Hook()
        {
            if (hooked || !till.gameObject.activeInHierarchy)
            {
                return;
            }

            try
            {
                if (!DoorSync.HasGlobalTransition(till, ServeEvent))
                {
                    till.AddGlobalTransition(ServeEvent, ServeState);
                }

                for (int i = 0; i < LineCount; i++)
                {
                    string state = "State " + (FirstLineState + i).ToString(CultureInfo.InvariantCulture);
                    if (DoorSync.HasState(speak, state) && !DoorSync.HasGlobalTransition(speak, LineEvent + (i + 1)))
                    {
                        speak.AddGlobalTransition(LineEvent + (i + 1), state);
                    }
                }

                hooked = true;
                ReadVariables();
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Fsm, "Pub: could not prepare the till: " + exception.Message);
            }
        }

        private static Transform FindPath(string path)
        {
            int slash = path.IndexOf('/');
            GameObject root = GameObject.Find("/" + path.Substring(0, slash));
            return root == null ? null : root.transform.Find(path.Substring(slash + 1));
        }

        private void OnStateWanted(string key, int[] values)
        {
            for (int i = 0; i < jukeboxes.Length; i++)
            {
                if (key == jukeboxes[i].Key)
                {
                    if (!host)
                    {
                        jukeboxes[i].Heard(values);
                    }

                    return;
                }
            }

            if (key.StartsWith("pub/", StringComparison.Ordinal) || key == MoodKey)
            {
                heard.Add(new Heard { Key = key, Values = values, CatchUp = current.CatchingUp });
            }
        }

        private void Hear(string key, int[] values, bool catchUp, double now)
        {
            if (key.StartsWith(OrderKey, StringComparison.Ordinal) && values.Length >= 3)
            {
                byte player;
                if (!byte.TryParse(key.Substring(OrderKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out player)
                    || player == manager.Session.LocalPlayerId)
                {
                    return;
                }

                int known;
                bool any = ordersHeard.TryGetValue(key, out known);
                ordersHeard[key] = values[0];
                int item = values[1] - 1;
                if (catchUp)
                {
                    // The last order of that player, in the host's answer to this game's request: served long ago maybe,
                    // whenever the answer comes. Counted, not served again.
                    if (!any || values[0] != known)
                    {
                        log.Info(LogCategory.Fsm, "Pub: player " + player + " has ordered " + values[0] + " time(s) this session; history, not served here (a catch-up on joining).");
                    }

                    return;
                }

                if (now < historyUntil || (any && values[0] <= known) || item < 0 || item >= Items.Length)
                {
                    // Orders from before this game was here are history.
                    return;
                }

                queue.Add(new Order { Item = item, Line = values[2], Player = player, Until = now + QueueSeconds });
                log.Info(LogCategory.Fsm, "Pub: player " + player + " ordered " + Items[item] + (values[2] > 0 ? " (Teimo's line " + values[2] + ")" : string.Empty) + "; served here too.");
            }
            else if (key == MoodKey && values.Length >= 2)
            {
                knownAngry = values[0] != 0;
                knownPissed = values[1] != 0;
                if (teimoAngry != null)
                {
                    teimoAngry.Value = knownAngry;
                }

                if (pissedOff != null)
                {
                    pissedOff.Value = knownPissed;
                }
            }
        }

        // An order of this game's player: the till goes from waiting to handing something over by itself.
        private void WatchTill(double now)
        {
            string speakState = speak.ActiveStateName;
            if (speakState != lastSpeakState)
            {
                lastSpeakState = speakState;
                int line = LineOf(speakState);
                if (line > 0)
                {
                    lastLine = line;
                    lastLineAt = now;
                }
            }

            string state = till.ActiveStateName;
            if (state == lastTillState)
            {
                return;
            }

            lastTillState = state;
            if (state == IdleState)
            {
                servingForOthers = false;
                return;
            }

            int item = Array.IndexOf(HandOver, state);
            if (item < 0 || servingForOthers)
            {
                return;
            }

            int said = item == 0 && now - lastLineAt <= LineWindow ? lastLine : 0;
            ordersTold++;
            current.Set(OrderKey + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { ordersTold, item + 1, said });
            log.Info(LogCategory.Fsm, "Pub: this game's player ordered " + Items[item] + "; told.");
        }

        private static int LineOf(string state)
        {
            int number;
            if (state == null || !state.StartsWith("State ", StringComparison.Ordinal)
                || !int.TryParse(state.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return 0;
            }

            number -= FirstLineState - 1;
            return number >= 1 && number <= LineCount ? number : 0;
        }

        // Others' orders: Teimo serves them here when his till is free, or the thing is made where he would put it.
        private void Serve(double now)
        {
            if (queue.Count == 0)
            {
                return;
            }

            Order order = queue[0];
            bool open = till.gameObject.activeInHierarchy;
            if (open)
            {
                Hook();
            }

            if (open && hooked && till.ActiveStateName == IdleState)
            {
                queue.RemoveAt(0);
                total.Value = 0f;
                eventName.Value = Items[order.Item];
                orderList.SetActive(false);
                servingForOthers = true;
                till.SendEvent(ServeEvent);
                lastTillState = till.ActiveStateName;
                if (order.Line > 0 && order.Line <= LineCount)
                {
                    speak.SendEvent(LineEvent + order.Line);
                    lastSpeakState = speak.ActiveStateName;
                }

                return;
            }

            if (!open || now >= order.Until)
            {
                queue.RemoveAt(0);
                Make(order, now);
            }
        }

        // Teimo cannot hand it over here: the thing appears where he would have put it.
        private void Make(Order order, double now)
        {
            Maker maker = makers[order.Item];
            if (maker == null)
            {
                // Cigarettes: made everywhere by their own maker.
                return;
            }

            CreateObject action = maker.Action;
            GameObject point = action.spawnPoint == null ? null : action.spawnPoint.Value;
            Vector3 position = Vector3.zero;
            Vector3 rotation = Vector3.zero;
            if (point != null)
            {
                position = point.transform.position;
                if (action.position != null && !action.position.IsNone)
                {
                    position += action.position.Value;
                }

                rotation = action.rotation != null && !action.rotation.IsNone ? action.rotation.Value : point.transform.eulerAngles;
            }
            else
            {
                position = action.position == null || action.position.IsNone ? Vector3.zero : action.position.Value;
                rotation = action.rotation == null || action.rotation.IsNone ? Vector3.zero : action.rotation.Value;
            }

            var thing = (GameObject)UnityEngine.Object.Instantiate(maker.Prefab, position, Quaternion.Euler(rotation));

            // Held still until it has its name and the buyer's game moves it: with this pub switched off there is no
            // counter under it, and a loose object found before then would be this game's to move.
            Rigidbody body = thing.GetComponent<Rigidbody>();
            bool held = body != null && !body.isKinematic;
            if (held)
            {
                body.isKinematic = true;
            }

            made.Add(new Made { Thing = thing, Body = held ? body : null, Player = order.Player, Prefab = maker.Prefab.name, At = now });
            log.Info(LogCategory.Fsm, "Pub: Teimo is not here to hand over player " + order.Player + "'s " + Items[order.Item] + "; made where he would put it.");
        }

        // What was made here for another player is theirs to move, once it has its name.
        private void Name(double now)
        {
            for (int i = made.Count - 1; i >= 0; i--)
            {
                Made thing = made[i];

                // Unity's null check is also true for a destroyed object.
                bool named = thing.Thing != null && thing.Thing.name != thing.Prefab + "(Clone)";
                if (thing.Thing != null && !named && now - thing.At < 0.6)
                {
                    continue;
                }

                made.RemoveAt(i);
                if (thing.Thing != null)
                {
                    objects.Expect(thing.Thing, thing.Player, now);
                    if (thing.Body != null)
                    {
                        thing.Body.isKinematic = false;
                    }

                    objects.ScanSoon(now);
                }
            }
        }

        // Teimo's mood, as this game's own machines change it.
        private void WatchMood()
        {
            bool angry = teimoAngry != null && teimoAngry.Value;
            bool pissed = pissedOff != null && pissedOff.Value;
            if (angry == knownAngry && pissed == knownPissed)
            {
                return;
            }

            knownAngry = angry;
            knownPissed = pissed;
            current.Set(MoodKey, new[] { angry ? 1 : 0, pissed ? 1 : 0 });
            log.Info(LogCategory.Fsm, "Pub: Teimo is " + (angry ? "angry" : "calm") + (pissed ? " and will not serve" : string.Empty) + " in this game; told.");
        }

        private sealed class Maker
        {
            public GameObject Prefab;
            public CreateObject Action;
        }

        private struct Heard
        {
            public string Key;
            public int[] Values;

            /// <summary>Part of the host's answer to this game's request: how things are, not what just happened.</summary>
            public bool CatchUp;
        }

        private struct Order
        {
            public int Item;
            public int Line;
            public byte Player;
            public double Until;
        }

        private struct Made
        {
            public GameObject Thing;
            public Rigidbody Body;
            public byte Player;
            public string Prefab;
            public double At;
        }
    }
}
