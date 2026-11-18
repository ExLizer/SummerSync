namespace SummerSync.Core.World
{
    /// <summary>
    /// The unloading of the tractor's flatbed at the firewood customer's, the same in every game.
    ///
    /// Each game unloads its own pile the game's way: with the box tipped and its hatch open near the customer, the game
    /// sets the pile's "Unload", and its unloading state takes the logs down (100 each time it is entered) and has the
    /// customer pay for them. Whether the box tips is up to each game's own physics and hatch, so one game could unload while
    /// another did not: that game's pile, and what its save keeps, stayed loaded. So a game that starts unloading by itself
    /// says so (the tally <see cref="Tally"/>: 1, and who says it), and every other game with a loaded pile at rest unloads
    /// its own too, the game's way: its logs come down, the customer's pile grows and the customer pays there as well (the
    /// pay is taken once, by whoever takes it first). One that stops halfway (the box lowered) says that too (0), and the
    /// games that unloaded because of it stop as well; until then they keep unloading even if their own box says otherwise.
    ///
    /// A small pile is unloaded and paid for within the frame the unloading starts in: the start is known from the game
    /// entering its unloading state (<see cref="Entering"/>), not from its "Unload" seen between two frames.
    ///
    /// A game whose pile was emptied a moment ago does not unload an empty pile again when its own box comes to tip a little
    /// later: the game would then have the customer pay nothing, over the pay of the unload just done.
    ///
    /// This class only decides; reading and writing the game's pile is the game side's.
    /// </summary>
    public sealed class FlatbedUnload
    {
        /// <summary>The tally a game says its own unloading in: 1 started or 0 stopped halfway, and the player who says it.</summary>
        public const string Tally = "wood/flatbed/unload";

        /// <summary>The least a pile has with something to unload: a piece of firewood adds 10, a log 20.</summary>
        public const int Loaded = 10;

        /// <summary>For how long after its pile was emptied a game turns back an unload of an empty pile.</summary>
        public const double EmptyAgainSeconds = 20.0;

        /// <summary>For how long another game's unloading waits for this game's pile to be at rest (counting a piece).</summary>
        public const double StartWaitSeconds = 3.0;

        /// <summary>What <see cref="Entering"/> says to do: let the game unload.</summary>
        public const int GoOn = 0;

        /// <summary>What <see cref="Entering"/> says to do: let the game unload, and say that it started.</summary>
        public const int GoOnAndSay = 1;

        /// <summary>What <see cref="Entering"/> says to do: turn the game back to waiting, its "Unload" false.</summary>
        public const int TurnBack = 2;

        private bool known;
        private int lastLogs;
        private bool inRound;
        private bool told;
        private bool relayed;
        private bool startWanted;
        private double startUntil;
        private bool stopWanted;
        private double emptiedAt = double.NegativeInfinity;

        /// <summary>Whether this game is unloading because another game said it unloads.</summary>
        public bool Relayed
        {
            get { return relayed; }
        }

        /// <summary>Whether another game's unloading waits to be done here.</summary>
        public bool Pending
        {
            get { return startWanted; }
        }

        /// <summary>
        /// The game enters its unloading state with <paramref name="logs"/> on the pile, before it takes any off: what to do
        /// (<see cref="GoOn"/>, <see cref="GoOnAndSay"/> or <see cref="TurnBack"/>).
        /// </summary>
        public int Entering(int logs, double now)
        {
            if (logs < Loaded)
            {
                if (now - emptiedAt < EmptyAgainSeconds)
                {
                    inRound = false;
                    return TurnBack;
                }

                // An empty pile long after the last unload: the game's own business, as without the mod.
                inRound = true;
                return GoOn;
            }

            if (inRound)
            {
                // The same unloading, the next 100.
                return GoOn;
            }

            inRound = true;
            if (relayed)
            {
                return GoOn;
            }

            told = true;
            return GoOnAndSay;
        }

        /// <summary>
        /// Once per frame, before <see cref="Want"/>: what this game's pile is now. <paramref name="waiting"/>: the game waits
        /// for wood (its first state), <paramref name="unload"/> its "Unload". Returns 0 to say that this game stopped
        /// unloading halfway, or -1 for nothing.
        /// </summary>
        public int Observe(bool waiting, bool unload, int logs, double now)
        {
            if (!known)
            {
                known = true;
                lastLogs = logs;
                return -1;
            }

            if (lastLogs >= Loaded && logs < Loaded)
            {
                emptiedAt = now;
            }

            lastLogs = logs;
            if (!inRound || !waiting || unload)
            {
                return -1;
            }

            // Back to waiting: paid for, or stopped with logs on it.
            bool halfway = logs >= Loaded;
            if (relayed && halfway && !stopWanted)
            {
                // Stopped here by this game's own box while the game that unloads goes on: taken up again in Want.
                return -1;
            }

            inRound = false;
            relayed = false;
            if (told)
            {
                told = false;
                if (halfway)
                {
                    return 0;
                }
            }

            return -1;
        }

        /// <summary>
        /// What another game said in <see cref="Tally"/>. <paramref name="catchUp"/>: part of the host's answer on joining (an
        /// unload that is long over); <paramref name="ownWord"/>: what this game said, coming back.
        /// </summary>
        public void Hear(int value, bool catchUp, bool ownWord, double now)
        {
            if (catchUp || ownWord)
            {
                return;
            }

            if (value == 1)
            {
                startWanted = true;
                startUntil = now + StartWaitSeconds;
                stopWanted = false;
            }
            else
            {
                startWanted = false;
                stopWanted = true;
            }
        }

        /// <summary>
        /// Once per frame, after <see cref="Observe"/>: true when the game side should write this game's "Unload" as
        /// <paramref name="value"/>.
        /// </summary>
        public bool Want(bool waiting, bool unload, int logs, double now, out bool value)
        {
            value = false;
            if (stopWanted)
            {
                stopWanted = false;
                if (relayed)
                {
                    relayed = false;
                    told = false;
                    if (unload)
                    {
                        return true;
                    }
                }

                return false;
            }

            if (startWanted)
            {
                if (now > startUntil || unload || inRound || logs < Loaded)
                {
                    // Too late, unloading already, or nothing to unload (this game's own unload came first).
                    startWanted = false;
                    return false;
                }

                if (!waiting)
                {
                    // Counting a piece just thrown in: in a moment.
                    return false;
                }

                startWanted = false;
                relayed = true;
                value = true;
                return true;
            }

            if (relayed && inRound && waiting && !unload && logs >= Loaded)
            {
                // This game's own box said stop; the game that unloads did not.
                value = true;
                return true;
            }

            return false;
        }
    }
}
