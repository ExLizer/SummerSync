using System;
using System.Collections.Generic;

namespace SummerSync.Core.World
{
    /// <summary>
    /// The host's word on who cashes a winning ticket in. There is one ticket for the world, and every game would pay it
    /// out the moment its player clicks; two players clicking before either hears of the other would both be paid, from the
    /// one purse. So a game asks before it pays: the first player to ask for a round's ticket cashes it, and whoever asks
    /// after is told who did. A player who left the session no longer holds the ticket: the next one to ask gets it.
    /// </summary>
    public sealed class LotteryClaims
    {
        /// <summary>A tally said by a client: the round of the ticket it would cash, and its player.</summary>
        public const string AskKey = "lottoclaim/ask";

        /// <summary>A tally said by the host: a round, and the player who cashes its ticket.</summary>
        public const string GrantKey = "lottoclaim/grant";

        private readonly Dictionary<int, byte> granted = new Dictionary<int, byte>();

        /// <summary>
        /// On the host: <paramref name="asker"/> wants to cash the ticket of <paramref name="round"/>. The player who cashes
        /// it: the asker, unless another player still in the session asked first.
        /// </summary>
        public byte Grant(int round, byte asker, Func<byte, bool> stillHere)
        {
            byte holder;
            if (granted.TryGetValue(round, out holder) && holder != asker && (stillHere == null || stillHere(holder)))
            {
                return holder;
            }

            granted[round] = asker;
            return asker;
        }

        /// <summary>Who was given the ticket of that round, if anyone.</summary>
        public bool TryGetHolder(int round, out byte holder)
        {
            return granted.TryGetValue(round, out holder);
        }

        /// <summary>A player left: whatever they were given is free again.</summary>
        public void Forget(byte player)
        {
            var rounds = new List<int>();
            foreach (KeyValuePair<int, byte> entry in granted)
            {
                if (entry.Value == player)
                {
                    rounds.Add(entry.Key);
                }
            }

            for (int i = 0; i < rounds.Count; i++)
            {
                granted.Remove(rounds[i]);
            }
        }
    }
}
