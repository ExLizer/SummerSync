using System.Globalization;
using SummerSync.Core.Net;
using SummerSync.Core.World;

/// <summary>
/// A guest of Teimo's pub, to try a real game's pub, video poker and lottery with: it orders at the bar, plays the
/// video poker once someone does, and buys a lottery ticket, the way a client's game would tell them, and counts what
/// it hears back.
/// </summary>
internal sealed class ProbePub
{
    private const int Poker = 0;
    private const double FirstOrder = 5.0;
    private const double FirstTicket = 6.0;
    private const double PressEvery = 4.0;

    // The video poker's buttons, in the order they travel (src/SummerSync.Mod/World/PokerSync.cs).
    private static readonly int[] Presses = { 1, 1, 0, 1, 1 };
    private static readonly string[] ButtonNames = { "InsertCoin", "Deal", "TakeWin", "Bet", "Double", "DoubleHigh", "DoubleLow", "Hold1", "Hold2", "Hold3", "Hold4", "Hold5" };

    private readonly int order;
    private readonly bool lotto;
    private readonly int presses;
    private readonly int slots;
    private readonly uint shape;
    private readonly LotteryBook book = new LotteryBook();
    private readonly bool phone;
    private int ringing;
    private double ringHeardAt = double.NaN;
    private bool phoneToldAnswer;
    private readonly bool ringHere;
    private int rang;
    private readonly bool sleeps;
    private bool saidSleep;
    private readonly int busBits;
    private readonly double busPaidAt;
    private int busTold = -1;
    private readonly double busDoorAt;
    private readonly int policePlace;
    private bool policeTold;
    private readonly string? tallyName;
    private readonly int[] tallyValue;
    private readonly double tallyAt;
    private bool tallyTold;
    private readonly string? addName;
    private readonly int addValue;
    private readonly double addAt;
    private bool addTold;
    private bool changesWatched;
    private int busDoorTold = -1;
    private TallyReplication? tallies;
    private PanelReplication? panels;
    private bool ordered;
    private bool ticketBought;
    private double nextPress;

    /// <param name="ringHere">As a host: make the phone ring (a call about the first sewage well) and hear who answers.</param>
    /// <param name="sleeps">Say this player sleeps, the whole session.</param>
    /// <param name="busBits">Say what this player needs of the host's bus (1 in it, 2 sitting, 4 a stop asked from inside, 8 Loppe, 16 Kesseli, 32 Rykipohja).</param>
    /// <param name="busPaidAt">At that second, say a ticket was paid; 0 for never.</param>
    /// <param name="busDoorAt">As a host: at that second the bus's door opens, and ten seconds later it closes; 0 for never.</param>
    /// <param name="policePlace">As a host: a checkpoint at that place (1 Airport, 2 Skihill, 3 Fields), from the start; 0 for none.</param>
    /// <param name="tallyName">At <paramref name="tallyAt"/> seconds, say that a tally of that name is <paramref name="tallyValue"/>.</param>
    /// <param name="addName">At <paramref name="addAt"/> seconds, say that this game changed a tally of that name by <paramref name="addValue"/> (as a client).</param>
    public ProbePub(int order, bool lotto, int presses, int slots, uint shape, bool phone = false, bool ringHere = false, bool sleeps = false, int busBits = 0, double busPaidAt = 0, double busDoorAt = 0, int policePlace = -1, string? tallyName = null, int[]? tallyValue = null, double tallyAt = 0, string? addName = null, int addValue = 0, double addAt = 0)
    {
        this.addName = addName;
        this.addValue = addValue;
        this.addAt = addAt;
        this.tallyName = tallyName;
        this.tallyValue = tallyValue ?? new[] { 0 };
        this.tallyAt = tallyAt;
        this.policePlace = policePlace;
        this.busDoorAt = busDoorAt;
        this.sleeps = sleeps;
        this.busBits = busBits;
        this.busPaidAt = busPaidAt;
        this.phone = phone;
        this.ringHere = ringHere;
        this.order = order;
        this.lotto = lotto;
        this.presses = presses;
        this.slots = slots;
        this.shape = shape;
    }

    public int PressesSent { get; private set; }

    public int PressesMade { get; private set; }

    public int PartsShown { get; private set; }

    public byte Worker { get; private set; } = PanelWire.NoOperator;

    public bool HeardDraw { get; private set; }

    public bool HeardOwnTicket { get; private set; }

    public int Answered { get; private set; }

    public bool Passed =>
        (order == 0 || ordered)
        && (!lotto || (HeardDraw && HeardOwnTicket))
        && (presses == 0 || (PressesSent == presses && PartsShown > 0))
        && (!phone || Answered > 0) && (!ringHere || Answered > 0);

    private double lastNow;

    public void Step(NetworkManager manager, double now)
    {
        lastNow = now;
        // A world is ready only once connected: then the host is asked for what it has so far.
        if (manager.Session == null || manager.Session.State != SessionState.Connected)
        {
            return;
        }

        if (manager.Tallies != null && manager.Tallies != tallies)
        {
            tallies = manager.Tallies;
            tallies.StateWanted += OnTally;
            tallies.RequestAll();
        }

        if (presses > 0 && manager.Panels != null && manager.Panels != panels)
        {
            panels = manager.Panels;
            panels.Define(Poker, slots, shape);
            panels.PressWanted += (panel, button) =>
            {
                PressesMade++;
                Console.WriteLine("Poker: asked to make '" + ButtonNames[button] + "', which a probe cannot.");
            };
            panels.ShowWanted += (panel, slot, value) => PartsShown++;
            panels.OperatorChanged += (panel, player) =>
            {
                Worker = player;
                Console.WriteLine(player == PanelWire.NoOperator ? "Poker: nobody plays it." : "Poker: player " + player + " plays it.");
            };
            panels.RequestAll();
            Worker = panels.Operator(Poker);
        }

        if (tallies == null)
        {
            return;
        }

        if (order > 0 && !ordered && now >= FirstOrder)
        {
            ordered = true;
            tallies.Set("pub/order/" + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { 1, order, order == 1 ? 3 : 0 });
            Console.WriteLine("Pub: ordered thing " + order + " at the bar.");
        }

        if (lotto && !ticketBought && now >= FirstTicket && book.Draw != null)
        {
            ticketBought = true;
            var ticket = new LotteryTicket { Round = book.Draw.Round + 1, Paid = true };
            new[] { 2, 9, 14, 21, 26, 30, 38 }.CopyTo(ticket.Lines[0], 0);
            new[] { 1, 5, 11, 17, 23, 29, 35 }.CopyTo(ticket.Lines[1], 0);
            foreach (KeyValuePair<string, int[]> tally in LotteryBook.Tallies(ticket))
            {
                tallies.Set(tally.Key, tally.Value);
            }

            Console.WriteLine("Lottery: bought a ticket for round " + ticket.Round + ".");
        }

        if (!changesWatched)
        {
            // Whatever a client changes in a job is heard and printed (a host probe adds nothing to a game of its own).
            changesWatched = true;
            tallies.ChangeWanted += (name, change, player) =>
            {
                if (name.StartsWith("job/", StringComparison.Ordinal))
                {
                    Console.WriteLine("Tally: player " + player + " changed " + name + " by " + change[0] + ".");
                }
            };
        }

        if (addName != null && !addTold && now >= addAt)
        {
            addTold = true;
            tallies.Tell(addName, new[] { 0 }, new[] { addValue });
            Console.WriteLine("Tally: " + addName + " changed by " + addValue + " here; said.");
        }

        if (Say != null && sayings == null)
        {
            sayings = new List<(double, string, string[])>();
            foreach (string part in Say.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = part.Split('|');
                sayings.Add((double.Parse(fields[0], CultureInfo.InvariantCulture), fields[1], fields[2].Split(',')));
            }
        }

        for (int s = 0; sayings != null && s < sayings.Count; s++)
        {
            if (now >= sayings[s].At)
            {
                int[] values = sayings[s].Values
                    .Select(v => v.StartsWith('#') ? (IdOf == null ? 0 : IdOf(v.Substring(1))) : int.Parse(v, CultureInfo.InvariantCulture))
                    .ToArray();
                tallies.Set(sayings[s].Name, values);
                Console.WriteLine("Tally: " + sayings[s].Name + " = " + string.Join(",", values) + " said.");
                sayings.RemoveAt(s);
                s--;
            }
        }

        if (tallyName != null && !tallyTold && now >= tallyAt)
        {
            tallyTold = true;
            tallies.Set(tallyName, tallyValue);
            Console.WriteLine("Tally: " + tallyName + " = " + string.Join(",", tallyValue) + " said.");
        }

        if (policePlace >= 0 && !policeTold && manager.Mode == NetworkMode.Host)
        {
            policeTold = true;
            tallies.Set("police/checkpoint", new[] { policePlace, policePlace > 0 ? 1 : 0 });
            Console.WriteLine("Police: " + (policePlace > 0 ? "a checkpoint at place " + policePlace + "." : "no checkpoint."));
        }

        if (busDoorAt > 0 && manager.Mode == NetworkMode.Host)
        {
            int open = now >= busDoorAt && now < busDoorAt + 10.0 ? 1 : 0;
            if (open != busDoorTold && now >= busDoorAt)
            {
                busDoorTold = open;
                tallies.Set("bus/door", new[] { open });
                Console.WriteLine("Bus: the door " + (open == 1 ? "opens." : "closes."));
            }
        }

        if (busBits != 0)
        {
            int paid = busPaidAt > 0 && now >= busPaidAt ? 1 : 0;
            if (paid != busTold)
            {
                busTold = paid;
                tallies.Set("bus/rider/" + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { busBits, paid });
                Console.WriteLine("Bus: this player needs " + busBits + (paid > 0 ? ", and paid a ticket." : "."));
            }
        }

        if (sleeps && !saidSleep)
        {
            saidSleep = true;
            tallies.Set("sleep/" + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { 1 });
            Console.WriteLine("Sleep: this player sleeps.");
        }

        // As a host: the phone rings at 8 s, and stops at 45 s if nobody took it.
        if (ringHere && manager.Mode == NetworkMode.Host)
        {
            if (rang == 0 && now >= 8.0)
            {
                rang = 1;
                tallies.Set("phone/ring", new[] { 1, 4, 1 });
                Console.WriteLine("Phone: rings (made up: the first sewage well).");
            }
            else if (rang == 1 && now >= 45.0)
            {
                rang = 2;
                tallies.Set("phone/ring", new[] { 1, 4, 0 });
                Console.WriteLine("Phone: stops ringing.");
            }
        }

        // The phone: two seconds after the host's starts ringing, picked up here.
        if (phone && ringing > 0 && !phoneToldAnswer && now - ringHeardAt >= 2.0)
        {
            phoneToldAnswer = true;
            Answered++;
            tallies.Set("phone/answer", new[] { ringing, manager.Session.LocalPlayerId + 1 });
            Console.WriteLine("Phone: picked up call " + ringing + ".");
        }

        // Presses only while the host's game plays it: a probe cannot run the machine itself.
        if (panels != null && PressesSent < presses && Worker == Protocol.HostPlayerId && now >= nextPress)
        {
            nextPress = now + PressEvery;
            int button = Presses[PressesSent % Presses.Length];
            PressesSent++;
            panels.Press(Poker, button);
            Console.WriteLine("Poker: pressed '" + ButtonNames[button] + "'.");
        }
    }

    /// <summary>Tallies whose name starts with this are printed as they are heard.</summary>
    public string? Hear { get; init; }

    /// <summary>
    /// With --say: tallies said at their seconds ("&lt;s&gt;|&lt;name&gt;|&lt;v,v…&gt;", separated by ';'). A value "#&lt;name&gt;" is the
    /// newest shared number of an object of that name when it is said (0 if there is none).
    /// </summary>
    public string? Say { get; init; }

    /// <summary>The newest shared number of an object of that name, or 0.</summary>
    public Func<string, int>? IdOf { get; set; }

    private List<(double At, string Name, string[] Values)>? sayings;

    private void OnTally(string key, int[] values)
    {
        if (Hear != null && key.StartsWith(Hear, StringComparison.Ordinal))
        {
            Console.WriteLine("Tally heard: " + key + " = " + string.Join(",", values) + ".");
        }

        if (key == "phone/answer" && values.Length >= 2)
        {
            Console.WriteLine("Phone: call " + values[0] + " picked up by player " + (values[1] - 1) + ".");
            Answered++;
        }

        if (key == "phone/ring" && values.Length >= 3)
        {
            Console.WriteLine("Phone: the host's call " + values[0] + " (topic " + values[1] + ") " + (values[2] != 0 ? "rings." : "is over."));
            if (values[2] != 0 && values[0] != ringing)
            {
                ringing = values[0];
                ringHeardAt = lastNow;
                phoneToldAnswer = false;
            }
        }

        int whole = book.Hear(key, values);
        if (whole == 2 && !HeardDraw)
        {
            HeardDraw = true;
            Console.WriteLine("Lottery: the host's draw is round " + book.Draw!.Round + ", numbers " + string.Join(" ", book.Draw.Numbers) + ".");
        }
        else if (whole == 1 && ticketBought && book.Ticket!.Paid && book.Ticket.Lines[0][0] == 2)
        {
            HeardOwnTicket = true;
        }
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (order > 0)
        {
            parts.Add("Pub: " + (ordered ? "ordered thing " + order : "did not order"));
        }

        if (lotto)
        {
            parts.Add("Lottery: " + (HeardDraw ? "heard the host's draw" : "never heard the host's draw") + ", " + (HeardOwnTicket ? "its ticket came back" : "its ticket did not come back"));
        }

        if (phone)
        {
            parts.Add("Phone: picked up " + Answered + " call(s)");
        }

        if (presses > 0)
        {
            parts.Add("Poker: pressed " + PressesSent + " of " + presses + ", " + PartsShown + " part(s) shown, " + PressesMade + " press(es) it was asked to make, played by "
                + (Worker == PanelWire.NoOperator ? "nobody" : "player " + Worker));
        }

        return string.Join("\n", parts) + (parts.Count > 0 ? "\n" : string.Empty);
    }
}
