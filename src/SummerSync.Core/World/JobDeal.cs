using System;

namespace SummerSync.Core.World
{
    /// <summary>What a game does with a state of a dealt machine that another game entered.</summary>
    public enum JobDealStep
    {
        /// <summary>Nothing: it is not this deal's, or this game deals.</summary>
        Ignore,

        /// <summary>Go there, as any relayed state: nobody deals (an end said by a game that did not deal).</summary>
        Follow,

        /// <summary>Another game deals: the machine here is paused (if it is not yet) and only goes where that game's goes.</summary>
        FollowPaused,

        /// <summary>The deal is over: go there and let the machine here go on by itself from it.</summary>
        FollowAndResume,
    }

    /// <summary>
    /// Who deals with a person of a job that one player deals with at a time (the kilju buyer tasting the bottles one player
    /// brought): the game of whoever deals runs the machine and the others' are paused and go where it goes
    /// (the people one deals with are run by the game of whoever deals with them). The first game that
    /// enters one of the machine's listed states deals, until it enters one that ends a deal; two games that start at once
    /// leave it to the smaller player number, as with the hitchhikers. This class only decides; reading and moving the game's
    /// machines is the game side's.
    /// </summary>
    public sealed class JobDeal
    {
        /// <summary>The value of <see cref="Dealer"/> while nobody deals.</summary>
        public const int Nobody = -1;

        public JobDeal()
        {
            Dealer = Nobody;
        }

        /// <summary>The player whose game deals, or <see cref="Nobody"/>.</summary>
        public int Dealer { get; private set; }

        /// <summary>
        /// This game's machine entered a listed state by itself (it is not paused). Returns whether the others are told:
        /// always, unless another game with a smaller number deals (a state entered here before the news of that deal came).
        /// </summary>
        public bool Entered(byte local, bool ends)
        {
            if (Dealer != Nobody && Dealer != local)
            {
                if (local > Dealer)
                {
                    return false;
                }

                // Both started at once: the smaller number, this one, keeps the deal; the other gives way when it hears.
                Dealer = local;
            }

            Dealer = ends ? Nobody : local;
            return true;
        }

        /// <summary>Another game (<paramref name="teller"/>) entered a listed state by itself: what this game does.</summary>
        public JobDealStep Heard(byte local, byte teller, bool ends)
        {
            if (teller == local)
            {
                return JobDealStep.Ignore;
            }

            if (Dealer != Nobody && Dealer != teller && teller > Dealer)
            {
                // This game (or a smaller number) deals: that game gives way when it hears it.
                return JobDealStep.Ignore;
            }

            if (ends)
            {
                bool wasDealt = Dealer != Nobody;
                Dealer = Nobody;
                return wasDealt ? JobDealStep.FollowAndResume : JobDealStep.Follow;
            }

            Dealer = teller;
            return JobDealStep.FollowPaused;
        }

        /// <summary>A player left the session: returns whether their deal was the one going on (the machine here goes on by itself).</summary>
        public bool Left(byte player)
        {
            if (Dealer != player)
            {
                return false;
            }

            Dealer = Nobody;
            return true;
        }

        /// <summary>The session is over or begins anew: nobody deals.</summary>
        public void Reset()
        {
            Dealer = Nobody;
        }
    }

    /// <summary>
    /// The numbers a relayed state travels as (the tally "job/(name)/s(n)"): the state's place in the relay's list, a running
    /// number (a later one is news), the player whose game entered it, and the shared numbers of the things in the relay's
    /// object variables (0 for none).
    /// </summary>
    public struct JobRelayNews
    {
        public int State;
        public int Serial;
        public int Player;
        public ushort[] Things;

        /// <summary>The tally's numbers.</summary>
        public int[] Encode()
        {
            int count = Things == null ? 0 : Math.Min(Things.Length, JobRelay.MaxObjects);
            var values = new int[3 + count];
            values[0] = State;
            values[1] = Serial;
            values[2] = Player;
            for (int i = 0; i < count; i++)
            {
                values[3 + i] = Things[i];
            }

            return values;
        }

        /// <summary>
        /// The news in a tally's numbers; false if they cannot be (fewer than two). An older build says only the state and the
        /// number: the player is then unknown (-1) and the things none.
        /// </summary>
        public static bool TryDecode(int[] values, out JobRelayNews news)
        {
            news = new JobRelayNews();
            if (values == null || values.Length < 2)
            {
                return false;
            }

            news.State = values[0];
            news.Serial = values[1];
            news.Player = values.Length >= 3 ? values[2] : -1;
            int count = Math.Max(0, Math.Min(values.Length - 3, JobRelay.MaxObjects));
            news.Things = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                news.Things[i] = values[3 + i] < 0 || values[3 + i] > ushort.MaxValue ? (ushort)0 : (ushort)values[3 + i];
            }

            return true;
        }
    }
}
