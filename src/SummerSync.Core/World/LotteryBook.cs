using System;
using System.Collections.Generic;

namespace SummerSync.Core.World
{
    /// <summary>
    /// The lottery ticket of the world: the game has one, kept in the save (docs/GAME_NOTES.md, "La lotería"). Three
    /// lines of seven numbers from 1 to 39 (0 where a line is not filled), the round it plays in, whether it is paid
    /// and checked, and what it won, in whole marks.
    /// </summary>
    public sealed class LotteryTicket : IEquatable<LotteryTicket>
    {
        public const int LineCount = 3;
        public const int LineLength = 7;
        public const int Highest = 39;

        public LotteryTicket()
        {
            Lines = new int[LineCount][];
            for (int i = 0; i < LineCount; i++)
            {
                Lines[i] = new int[LineLength];
            }
        }

        public int Round { get; set; }

        public bool Paid { get; set; }

        public bool Checked { get; set; }

        public int Winnings { get; set; }

        /// <summary>Three lines of seven numbers.</summary>
        public int[][] Lines { get; private set; }

        public LotteryTicket Copy()
        {
            var copy = new LotteryTicket { Round = Round, Paid = Paid, Checked = Checked, Winnings = Winnings };
            for (int i = 0; i < LineCount; i++)
            {
                Array.Copy(Lines[i], copy.Lines[i], LineLength);
            }

            return copy;
        }

        public bool Equals(LotteryTicket other)
        {
            if (other == null || Round != other.Round || Paid != other.Paid || Checked != other.Checked || Winnings != other.Winnings)
            {
                return false;
            }

            for (int i = 0; i < LineCount; i++)
            {
                for (int n = 0; n < LineLength; n++)
                {
                    if (Lines[i][n] != other.Lines[i][n])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as LotteryTicket);
        }

        public override int GetHashCode()
        {
            return Round * 31 + Winnings;
        }
    }

    /// <summary>
    /// The last draw of the world: its round, the national pot and how many hit all seven, whether it has been drawn,
    /// the seven numbers and the three extra, and for each of the five prize classes (7, 6+1, 6, 5, 4 right) how many
    /// won and how much each.
    /// </summary>
    public sealed class LotteryDraw : IEquatable<LotteryDraw>
    {
        public const int NumberCount = 7;
        public const int ExtraCount = 3;
        public const int ClassCount = 5;

        public int Round { get; set; }

        public int Pot { get; set; }

        public int Jackpots { get; set; }

        public bool Done { get; set; }

        public int[] Numbers { get; private set; } = new int[NumberCount];

        public int[] Extra { get; private set; } = new int[ExtraCount];

        public int[] Winners { get; private set; } = new int[ClassCount];

        public int[] Prizes { get; private set; } = new int[ClassCount];

        public LotteryDraw Copy()
        {
            var copy = new LotteryDraw { Round = Round, Pot = Pot, Jackpots = Jackpots, Done = Done };
            Array.Copy(Numbers, copy.Numbers, NumberCount);
            Array.Copy(Extra, copy.Extra, ExtraCount);
            Array.Copy(Winners, copy.Winners, ClassCount);
            Array.Copy(Prizes, copy.Prizes, ClassCount);
            return copy;
        }

        public bool Equals(LotteryDraw other)
        {
            return other != null && Round == other.Round && Pot == other.Pot && Jackpots == other.Jackpots && Done == other.Done
                && Same(Numbers, other.Numbers) && Same(Extra, other.Extra) && Same(Winners, other.Winners) && Same(Prizes, other.Prizes);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as LotteryDraw);
        }

        public override int GetHashCode()
        {
            return Round * 31 + Pot;
        }

        private static bool Same(int[] a, int[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The lottery as tallies (<see cref="TallyReplication"/>), which carry eight numbers at most: the ticket in four
    /// ("lotto/line1" to "lotto/line3", then "lotto/ticket"), the draw in five ("lotto/numbers", "lotto/extra",
    /// "lotto/winners", "lotto/prizes", then "lotto/draw"). The last of each is told last, and a ticket or a draw counts
    /// once every part of it has been heard; numbers that cannot be the game's are refused.
    ///
    /// Whose word each is: the ticket is said by the machine whose game bought it, cashed it in, or (the host) checked
    /// it after a draw; the draw only by the host, whose game draws.
    /// </summary>
    public sealed class LotteryBook
    {
        public const string Prefix = "lotto/";
        public const string TicketKey = "lotto/ticket";
        public const string LineKey = "lotto/line";
        public const string DrawKey = "lotto/draw";
        public const string NumbersKey = "lotto/numbers";
        public const string ExtraKey = "lotto/extra";
        public const string WinnersKey = "lotto/winners";
        public const string PrizesKey = "lotto/prizes";

        private readonly LotteryTicket ticket = new LotteryTicket();
        private readonly LotteryDraw draw = new LotteryDraw();
        private readonly bool[] ticketParts = new bool[LotteryTicket.LineCount + 1];
        private readonly bool[] drawParts = new bool[5];

        /// <summary>The ticket heard, once whole; or null.</summary>
        public LotteryTicket Ticket
        {
            get { return All(ticketParts) ? ticket : null; }
        }

        /// <summary>The draw heard, once whole; or null.</summary>
        public LotteryDraw Draw
        {
            get { return All(drawParts) ? draw : null; }
        }

        /// <summary>Numbers refused so far.</summary>
        public int Refused { get; private set; }

        /// <summary>The tallies that say <paramref name="said"/>, in the order they are told.</summary>
        public static List<KeyValuePair<string, int[]>> Tallies(LotteryTicket said)
        {
            var tallies = new List<KeyValuePair<string, int[]>>();
            for (int i = 0; i < LotteryTicket.LineCount; i++)
            {
                tallies.Add(new KeyValuePair<string, int[]>(LineKey + (i + 1), (int[])said.Lines[i].Clone()));
            }

            tallies.Add(new KeyValuePair<string, int[]>(TicketKey, new[] { said.Round, said.Paid ? 1 : 0, said.Checked ? 1 : 0, said.Winnings }));
            return tallies;
        }

        /// <summary>The tallies that say <paramref name="said"/>, in the order they are told.</summary>
        public static List<KeyValuePair<string, int[]>> Tallies(LotteryDraw said)
        {
            return new List<KeyValuePair<string, int[]>>
            {
                new KeyValuePair<string, int[]>(NumbersKey, (int[])said.Numbers.Clone()),
                new KeyValuePair<string, int[]>(ExtraKey, (int[])said.Extra.Clone()),
                new KeyValuePair<string, int[]>(WinnersKey, (int[])said.Winners.Clone()),
                new KeyValuePair<string, int[]>(PrizesKey, (int[])said.Prizes.Clone()),
                new KeyValuePair<string, int[]>(DrawKey, new[] { said.Round, said.Pot, said.Jackpots, said.Done ? 1 : 0 }),
            };
        }

        /// <summary>
        /// A tally heard. Returns 1 if it completed or changed the ticket, 2 the draw, 0 neither (not the lottery's,
        /// refused, or a part of something not whole yet).
        /// </summary>
        public int Hear(string key, int[] values)
        {
            if (key == null || values == null || !key.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return 0;
            }

            if (key == TicketKey)
            {
                if (values.Length < 4 || values[0] < 0 || values[3] < 0)
                {
                    return Refuse();
                }

                ticket.Round = values[0];
                ticket.Paid = values[1] != 0;
                ticket.Checked = values[2] != 0;
                ticket.Winnings = values[3];
                return Mark(ticketParts, LotteryTicket.LineCount, 1);
            }

            if (key.StartsWith(LineKey, StringComparison.Ordinal))
            {
                int line;
                if (!int.TryParse(key.Substring(LineKey.Length), out line) || line < 1 || line > LotteryTicket.LineCount
                    || !Fill(ticket.Lines[line - 1], values, 0, LotteryTicket.Highest))
                {
                    return Refuse();
                }

                return Mark(ticketParts, line - 1, 1);
            }

            switch (key)
            {
                case NumbersKey:
                    return Fill(draw.Numbers, values, 0, LotteryTicket.Highest) ? Mark(drawParts, 0, 2) : Refuse();
                case ExtraKey:
                    return Fill(draw.Extra, values, 0, LotteryTicket.Highest) ? Mark(drawParts, 1, 2) : Refuse();
                case WinnersKey:
                    return Fill(draw.Winners, values, 0, int.MaxValue) ? Mark(drawParts, 2, 2) : Refuse();
                case PrizesKey:
                    return Fill(draw.Prizes, values, 0, int.MaxValue) ? Mark(drawParts, 3, 2) : Refuse();
                case DrawKey:
                    if (values.Length < 4 || values[0] < 0 || values[1] < 0 || values[2] < 0)
                    {
                        return Refuse();
                    }

                    draw.Round = values[0];
                    draw.Pot = values[1];
                    draw.Jackpots = values[2];
                    draw.Done = values[3] != 0;
                    return Mark(drawParts, 4, 2);
                default:
                    return Refuse();
            }
        }

        private int Refuse()
        {
            Refused++;
            return 0;
        }

        // Copies a part in, if it has the right count and every number is in range.
        private static bool Fill(int[] into, int[] values, int lowest, int highest)
        {
            if (values.Length != into.Length)
            {
                return false;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] < lowest || values[i] > highest)
                {
                    return false;
                }
            }

            Array.Copy(values, into, into.Length);
            return true;
        }

        private static int Mark(bool[] parts, int index, int which)
        {
            parts[index] = true;
            return All(parts) ? which : 0;
        }

        private static bool All(bool[] parts)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (!parts[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
