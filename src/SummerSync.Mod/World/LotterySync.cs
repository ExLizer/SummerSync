using System;
using System.Collections;
using System.Collections.Generic;
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
    /// One lottery for everyone: the host's draw, and one ticket for the world, as the game has.
    ///
    /// What is assumed about the game, from the dumps of 2026-10-14 and the game with the test bridge
    /// (docs/GAME_NOTES.md, "La lotería"): everything is in the state machine "Numbers" of "Systems/Lottery" and in its
    /// array lists. A ticket is bought at Teimo's counter on the sheet "Sheets/LottoTicket": each number clicked goes
    /// straight into the lists "PlayerLine1" to "PlayerLine3", and paying ("Pay", state "Date") sets "TicketPaid",
    /// "TicketChecked" off, "PlayerTicketWinnings" to nothing, "TicketRound" to the next round, and puts the ticket
    /// ("LotteryTicket") on the counter. On Saturday at 20 the machine draws: seven numbers and three extra out of 39
    /// into "Results" and "ResultsBonus", a national pot ("NationalPotFull") and its winners, the prize classes into
    /// "ResultsLinesWon" and "ResultsLinesWinnings", shows them on the teletext (page 301, "Texts" switched off and on
    /// again), checks the ticket ("TicketChecked", "PlayerTicketWinnings") and lets Teimo pay it ("CheckWinnings").
    /// Cashing it in sets "TicketPaid" off again.
    ///
    /// The host's game draws and checks; a client's "Numbers" is switched off for the session, so that it never draws
    /// by itself, and is given the host's draw and ticket: its lists and numbers written, its teletext switched off and
    /// on as the game does. The ticket is said by whichever game changed it (bought, cashed in, or checked), once its
    /// sheet is closed. Each game's ticket goes on lying where it lies; only whether there is one is shared.
    ///
    /// Cashing in: "CheckWinnings :: FSM" (STORE/LOD) waits for the click ("State 4"), plays its sound and the ticket's
    /// animation ("State 5") and pays in "State 2". The mod's own action first in "State 5" asks the host before it goes
    /// on (<see cref="LotteryClaims"/>): until the host says this player cashes the ticket, the machine goes back to
    /// "State 3" and waits; when it says so, it is sent on to "State 5". So one winning ticket is paid once, whoever clicks.
    /// </summary>
    internal sealed class LotterySync
    {
        private const string LotteryPath = "Systems/Lottery";
        private const string MachineName = "Numbers";
        private const string SheetPath = "Sheets/LottoTicket";
        private const string PivotPath = "STORE/LOD/ActivateStore/LOTTO/LotteryTicketPivot";
        private const string CashInPath = "STORE/LOD/ActivateStore/LOTTO/CheckWinnings";
        private const string TextsPath = "Systems/Teletext/VKTekstiTV/PAGES/301/Texts";
        private const double SearchInterval = 2.0;
        private const double LookInterval = 0.25;
        private const string CashMachine = "FSM";
        private const string CashState = "State 5";
        private const string CashWaitState = "State 3";
        private const string CashWaitEvent = "SUMMERSYNC LOTTO WAIT";
        private const string CashGoEvent = "SUMMERSYNC LOTTO PAY";

        private static readonly HashSet<int> HookedCash = new HashSet<int>();
        private static LotterySync active;

        private static readonly string[] LineLists = { "PlayerLine1", "PlayerLine2", "PlayerLine3" };
        private static readonly string[] Settled = { "Idle", "Idle 2", "Idle 3" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();
        private readonly List<int[]> asked = new List<int[]>();
        private readonly List<int[]> grants = new List<int[]>();
        private LotteryClaims claims = new LotteryClaims();
        private PlayMakerFSM cashMachine;
        private int pendingRound = -1;
        private int grantedRound = -1;
        private NetSession session;
        private readonly LotteryTicket seenTicket = new LotteryTicket();
        private readonly LotteryDraw seenDraw = new LotteryDraw();
        private TallyReplication current;
        private LotteryBook book;
        private bool host;
        private double nextSearch;
        private double nextLook;
        private bool found;
        private bool searchReported;

        private PlayMakerFSM numbers;
        private bool switchedOff;
        private FsmInt currentRound;
        private FsmInt ticketRound;
        private FsmInt pot;
        private FsmInt jackpots;
        private FsmBool paid;
        private FsmBool ticketChecked;
        private FsmBool drawDone;
        private FsmFloat winnings;
        private FsmGameObject ticketObject;
        private ArrayList[] lines;
        private ArrayList results;
        private ArrayList extra;
        private ArrayList winners;
        private ArrayList prizes;
        private GameObject sheet;
        private Transform pivot;
        private GameObject cashIn;
        private GameObject texts;

        // What this machine's game had when it last told or was told, so that what was written here is not told back.
        private LotteryTicket knownTicket;
        private LotteryDraw knownDraw;

        public LotterySync(Logger log, NetworkManager manager)
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
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                if (session != null)
                {
                    session.PlayerLeft -= OnPlayerLeft;
                }

                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                session = current == null ? null : manager.Session;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }

                if (session != null)
                {
                    session.PlayerLeft += OnPlayerLeft;
                }

                active = current == null ? null : this;
                heard.Clear();
                asked.Clear();
                grants.Clear();
                claims = new LotteryClaims();
                pendingRound = -1;
                grantedRound = -1;
                book = new LotteryBook();
                found = false;
                knownTicket = null;
                knownDraw = null;
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

            // Unity's null check is also true for a destroyed object.
            if (!found || numbers == null)
            {
                return;
            }

            if (!host && !switchedOff && numbers.enabled && Array.IndexOf(Settled, numbers.ActiveStateName) >= 0)
            {
                // A client never draws by itself: the host's draw is the draw. Back on in Release.
                numbers.enabled = false;
                switchedOff = true;
                log.Info(LogCategory.Fsm, "Lottery: this game's draw is off for the session; the host draws.");
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value);
            }

            heard.Clear();
            for (int i = 0; i < asked.Count; i++)
            {
                HearAsked(asked[i]);
            }

            asked.Clear();
            for (int i = 0; i < grants.Count; i++)
            {
                HearGrant(grants[i]);
            }

            grants.Clear();
            if (now >= nextLook)
            {
                nextLook = now + LookInterval;
                Look();
            }
        }

        /// <summary>The lottery here goes back to how the game had it.</summary>
        public void Release()
        {
            // Unity's null check is also true for a destroyed object.
            if (switchedOff && numbers != null)
            {
                numbers.enabled = true;
            }

            switchedOff = false;
            if (active == this)
            {
                active = null;
            }
        }

        private void Search()
        {
            Transform lottery = FindPath(LotteryPath);
            numbers = lottery == null ? null : DoorSync.FindMachine(lottery, MachineName);
            Transform sheetAt = FindPath(SheetPath);
            pivot = FindPath(PivotPath);
            Transform cashInAt = FindPath(CashInPath);
            Transform textsAt = FindPath(TextsPath);
            FsmVariables variables = numbers == null ? null : numbers.FsmVariables;
            if (variables != null)
            {
                currentRound = variables.FindFsmInt("CurrentRound");
                ticketRound = variables.FindFsmInt("TicketRound");
                pot = variables.FindFsmInt("NationalPotFull");
                jackpots = variables.FindFsmInt("NationalLine7");
                paid = variables.FindFsmBool("TicketPaid");
                ticketChecked = variables.FindFsmBool("TicketChecked");
                drawDone = variables.FindFsmBool("DrawDone");
                winnings = variables.FindFsmFloat("PlayerTicketWinnings");
                ticketObject = variables.FindFsmGameObject("LotteryTicket");
            }

            PlayMakerArrayListProxy[] proxies = lottery == null ? new PlayMakerArrayListProxy[0] : lottery.GetComponents<PlayMakerArrayListProxy>();
            lines = new ArrayList[LineLists.Length];
            for (int i = 0; i < LineLists.Length; i++)
            {
                lines[i] = List(proxies, LineLists[i]);
            }

            results = List(proxies, "Results");
            extra = List(proxies, "ResultsBonus");
            winners = List(proxies, "ResultsLinesWon");
            prizes = List(proxies, "ResultsLinesWinnings");
            bool whole = numbers != null && currentRound != null && ticketRound != null && pot != null && jackpots != null && paid != null
                && ticketChecked != null && drawDone != null && winnings != null && sheetAt != null && pivot != null && cashInAt != null
                && textsAt != null && results != null && extra != null && winners != null && prizes != null
                && lines[0] != null && lines[1] != null && lines[2] != null;
            if (!whole)
            {
                if (!searchReported)
                {
                    searchReported = true;
                    log.Warning(LogCategory.Fsm, "Lottery: '" + LotteryPath + "' (state machine '" + MachineName + "', its lists), the ticket sheet, the counter or the teletext page are not as this build expects: each game keeps its own lottery.");
                }

                numbers = null;
                return;
            }

            sheet = sheetAt.gameObject;
            cashIn = cashInAt.gameObject;
            texts = textsAt.gameObject;
            found = true;
            HookCashIn(cashInAt);
            Read(seenTicket);
            Read(seenDraw);
            if (host)
            {
                // The host's lottery is the lottery: said at once, for clients that join later too.
                knownTicket = seenTicket.Copy();
                knownDraw = seenDraw.Copy();
                Tell(knownDraw);
                Tell(knownTicket);
                log.Info(LogCategory.Fsm, "Lottery: shared; this game draws. " + Describe(knownTicket) + " " + Describe(knownDraw));
            }
            else
            {
                // A client's own, from the host's save: kept until the host says otherwise, and never told as it is.
                knownTicket = seenTicket.Copy();
                knownDraw = seenDraw.Copy();
                log.Info(LogCategory.Fsm, "Lottery: shared; the host draws. Here, from the save: " + Describe(knownTicket));
            }
        }

        private static ArrayList List(PlayMakerArrayListProxy[] proxies, string name)
        {
            for (int i = 0; i < proxies.Length; i++)
            {
                if (proxies[i] != null && proxies[i].referenceName == name)
                {
                    return proxies[i].arrayList;
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
            if (key.StartsWith(LotteryBook.Prefix, StringComparison.Ordinal))
            {
                heard.Add(new KeyValuePair<string, int[]>(key, values));
            }
            else if (key == LotteryClaims.AskKey && values.Length >= 2 && host)
            {
                asked.Add(values);
            }
            else if (key == LotteryClaims.GrantKey && values.Length >= 2 && !host && !current.CatchingUp)
            {
                // Only news: a catch-up's word is about a click of long ago.
                grants.Add(values);
            }
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            claims.Forget(player.Id);
        }

        // The mod's action first in the cash-in's paying state, and its two ways out of it. Once per machine.
        private void HookCashIn(Transform at)
        {
            cashMachine = DoorSync.FindMachine(at, CashMachine);
            if (cashMachine == null || !DoorSync.HasState(cashMachine, CashState) || !DoorSync.HasState(cashMachine, CashWaitState))
            {
                cashMachine = null;
                log.Warning(LogCategory.Fsm, "Lottery: the cash-in '" + CashInPath + " :: " + CashMachine + "' is not as this build expects: a winning ticket could be cashed in twice.");
                return;
            }

            int id = cashMachine.GetInstanceID();
            if (HookedCash.Contains(id))
            {
                return;
            }

            if (cashMachine.GetGlobalTransition(CashWaitEvent) == null)
            {
                cashMachine.AddGlobalTransition(CashWaitEvent, CashWaitState);
            }

            if (cashMachine.GetGlobalTransition(CashGoEvent) == null)
            {
                cashMachine.AddGlobalTransition(CashGoEvent, CashState);
            }

            PlayMakerFSM machine = cashMachine;
            if (machine.FsmInject(CashState, () => OnCashing(machine), false, 0, false))
            {
                HookedCash.Add(id);
            }
        }

        private static void OnCashing(PlayMakerFSM machine)
        {
            LotterySync sync = active;
            if (sync != null)
            {
                sync.Cashing(machine);
            }
        }

        // The player clicked to cash the ticket in: on, if the host says this player does; back to waiting otherwise.
        private void Cashing(PlayMakerFSM machine)
        {
            if (current == null || session == null || session.State != SessionState.Connected || ticketRound == null)
            {
                return;
            }

            int round = ticketRound.Value;
            byte me = session.LocalPlayerId;
            if (grantedRound == round)
            {
                log.Info(LogCategory.Fsm, "Lottery: this player cashes the ticket of round " + round + " in, as the host said.");
                return;
            }

            if (host)
            {
                byte holder = claims.Grant(round, me, StillHere);
                current.Set(LotteryClaims.GrantKey, new[] { round, (int)holder });
                if (holder == me)
                {
                    grantedRound = round;
                    log.Info(LogCategory.Fsm, "Lottery: the host's player cashes the ticket of round " + round + " in; nobody asked before.");
                    return;
                }

                log.Info(LogCategory.Fsm, "Lottery: player " + holder + " cashes the ticket of round " + round + " in; the host's player's click does nothing.");
                machine.SendEvent(CashWaitEvent);
                return;
            }

            // Asked once per click; the machine waits for the answer.
            pendingRound = round;
            current.Set(LotteryClaims.AskKey, new[] { round, (int)me });
            log.Info(LogCategory.Fsm, "Lottery: this player clicked to cash the ticket of round " + round + " in; asked the host first.");
            machine.SendEvent(CashWaitEvent);
        }

        // On the host: a client asks to cash a ticket in. The answer goes to everyone.
        private void HearAsked(int[] values)
        {
            int round = values[0];
            byte asker = (byte)values[1];
            byte holder = claims.Grant(round, asker, StillHere);
            current.Set(LotteryClaims.GrantKey, new[] { round, (int)holder });
            log.Info(
                LogCategory.Fsm,
                "Lottery: player " + asker + " asks to cash the ticket of round " + round + " in: "
                + (holder == asker ? "yes." : "player " + holder + " asked first and cashes it."));
        }

        // On a client: the host says who cashes a round's ticket.
        private void HearGrant(int[] values)
        {
            int round = values[0];
            byte holder = (byte)values[1];
            if (session == null || round != pendingRound)
            {
                return;
            }

            pendingRound = -1;
            if (holder != session.LocalPlayerId)
            {
                log.Info(LogCategory.Fsm, "Lottery: the host says player " + holder + " cashes the ticket of round " + round + " in: this click does nothing.");
                return;
            }

            grantedRound = round;

            // Unity's null check is also true for a destroyed object.
            if (cashMachine != null && cashMachine.gameObject.activeInHierarchy && cashMachine.enabled)
            {
                cashMachine.SendEvent(CashGoEvent);
                log.Info(LogCategory.Fsm, "Lottery: the host says this player cashes the ticket of round " + round + " in: paid here.");
            }
            else
            {
                log.Info(LogCategory.Fsm, "Lottery: the host says this player cashes the ticket of round " + round + " in, but the counter is far now: the next click pays.");
            }
        }

        private bool StillHere(byte player)
        {
            if (session == null)
            {
                return false;
            }

            if (player == session.LocalPlayerId)
            {
                return true;
            }

            IList<PlayerInfo> players = session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].Id == player)
                {
                    return true;
                }
            }

            return false;
        }

        private void Hear(string key, int[] values)
        {
            int whole = book.Hear(key, values);
            if (whole == 1)
            {
                LotteryTicket ticket = book.Ticket;
                if (!ticket.Equals(knownTicket))
                {
                    Write(ticket);
                    log.Info(LogCategory.Fsm, "Lottery: the ticket changed in another game. " + Describe(ticket));
                }

                knownTicket = ticket.Copy();
            }
            else if (whole == 2 && !host)
            {
                LotteryDraw draw = book.Draw;
                if (!draw.Equals(knownDraw))
                {
                    Write(draw);
                    log.Info(LogCategory.Fsm, "Lottery: the host's draw. " + Describe(draw));
                }

                knownDraw = draw.Copy();
            }
        }

        // What this game has now, against what it had: what changed here is told.
        private void Look()
        {
            if (host)
            {
                Read(seenDraw);
                if (!seenDraw.Equals(knownDraw))
                {
                    knownDraw = seenDraw.Copy();
                    Tell(knownDraw);
                    log.Info(LogCategory.Fsm, "Lottery: this game drew. " + Describe(knownDraw));
                }
            }

            // Unity's null check is also true for a destroyed object.
            if (sheet == null || sheet.activeSelf)
            {
                // Numbers go into the lists one by one while the sheet is open: only a closed sheet's ticket is told.
                return;
            }

            Read(seenTicket);
            if (!seenTicket.Equals(knownTicket))
            {
                knownTicket = seenTicket.Copy();
                Tell(knownTicket);
                log.Info(LogCategory.Fsm, "Lottery: the ticket changed in this game. " + Describe(knownTicket));
            }
        }

        private void Tell(LotteryTicket ticket)
        {
            List<KeyValuePair<string, int[]>> tallies = LotteryBook.Tallies(ticket);
            for (int i = 0; i < tallies.Count; i++)
            {
                current.Set(tallies[i].Key, tallies[i].Value);
            }
        }

        private void Tell(LotteryDraw draw)
        {
            List<KeyValuePair<string, int[]>> tallies = LotteryBook.Tallies(draw);
            for (int i = 0; i < tallies.Count; i++)
            {
                current.Set(tallies[i].Key, tallies[i].Value);
            }
        }

        private void Read(LotteryTicket into)
        {
            into.Round = ticketRound.Value;
            into.Paid = paid.Value;
            into.Checked = ticketChecked.Value;
            into.Winnings = Mathf.RoundToInt(winnings.Value);
            for (int i = 0; i < LotteryTicket.LineCount; i++)
            {
                ReadList(lines[i], into.Lines[i]);
            }
        }

        private void Read(LotteryDraw into)
        {
            into.Round = currentRound.Value;
            into.Pot = pot.Value;
            into.Jackpots = jackpots.Value;
            into.Done = drawDone.Value;
            ReadList(results, into.Numbers);
            ReadList(extra, into.Extra);
            ReadList(winners, into.Winners);
            ReadList(prizes, into.Prizes);
        }

        // A game list into so many numbers: what it lacks is 0, what is not a whole number is 0.
        private static void ReadList(ArrayList list, int[] into)
        {
            for (int i = 0; i < into.Length; i++)
            {
                object value = i < list.Count ? list[i] : null;
                into[i] = value is int ? (int)value : 0;
            }
        }

        private static void WriteList(ArrayList list, int[] values)
        {
            while (list.Count > values.Length)
            {
                list.RemoveAt(list.Count - 1);
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (i < list.Count)
                {
                    list[i] = values[i];
                }
                else
                {
                    list.Add(values[i]);
                }
            }
        }

        // The ticket of another game, written into this one: its numbers, and the ticket on the counter or gone.
        private void Write(LotteryTicket ticket)
        {
            for (int i = 0; i < LotteryTicket.LineCount; i++)
            {
                WriteList(lines[i], ticket.Lines[i]);
            }

            ticketRound.Value = ticket.Round;
            ticketChecked.Value = ticket.Checked;
            winnings.Value = ticket.Winnings;
            bool hadOne = paid.Value;
            paid.Value = ticket.Paid;
            GameObject thing = ticketObject == null ? null : ticketObject.Value;

            // Unity's null check is also true for a destroyed object.
            if (thing != null && ticket.Paid && (!hadOne || !thing.activeSelf))
            {
                // As paying puts it: on the counter.
                thing.transform.parent = pivot;
                thing.transform.localPosition = Vector3.zero;
                thing.transform.localRotation = Quaternion.identity;
                thing.SetActive(true);
            }
            else if (thing != null && !ticket.Paid && thing.activeSelf)
            {
                thing.SetActive(false);
            }

            // Teimo pays a checked ticket; one that is not paid any more has been cashed in.
            if (cashIn != null)
            {
                cashIn.SetActive(ticket.Paid && ticket.Checked);
            }
        }

        // The host's draw, written into this game, and its teletext page made to show it.
        private void Write(LotteryDraw draw)
        {
            WriteList(results, draw.Numbers);
            WriteList(extra, draw.Extra);
            WriteList(winners, draw.Winners);
            WriteList(prizes, draw.Prizes);
            currentRound.Value = draw.Round;
            pot.Value = draw.Pot;
            jackpots.Value = draw.Jackpots;
            drawDone.Value = draw.Done;

            // Unity's null check is also true for a destroyed object.
            if (texts != null)
            {
                texts.SetActive(false);
                texts.SetActive(true);
            }
        }

        private static string Describe(LotteryTicket ticket)
        {
            if (!ticket.Paid)
            {
                return "No ticket.";
            }

            var text = new System.Text.StringBuilder("Ticket for round " + ticket.Round + ":");
            for (int i = 0; i < LotteryTicket.LineCount; i++)
            {
                if (ticket.Lines[i][0] == 0)
                {
                    continue;
                }

                text.Append(i == 0 ? " " : " / ");
                for (int n = 0; n < LotteryTicket.LineLength; n++)
                {
                    text.Append(n == 0 ? string.Empty : " ").Append(ticket.Lines[i][n]);
                }
            }

            return text.Append(ticket.Checked ? "; checked, won " + ticket.Winnings.ToString(CultureInfo.InvariantCulture) + " mk." : "; not drawn yet.").ToString();
        }

        private static string Describe(LotteryDraw draw)
        {
            var text = new System.Text.StringBuilder("Round " + draw.Round + (draw.Done ? ", drawn: " : ", last drawn: "));
            for (int i = 0; i < LotteryDraw.NumberCount; i++)
            {
                text.Append(i == 0 ? string.Empty : " ").Append(draw.Numbers[i]);
            }

            text.Append(" + ");
            for (int i = 0; i < LotteryDraw.ExtraCount; i++)
            {
                text.Append(i == 0 ? string.Empty : " ").Append(draw.Extra[i]);
            }

            return text.Append("; pot " + draw.Pot.ToString(CultureInfo.InvariantCulture) + ".").ToString();
        }
    }
}
