using System;

namespace SummerSync.Core.World
{
    /// <summary>
    /// The books of one vehicle's tank, in hundredths of a litre: fuel, and fuel oil for the diesels.
    ///
    /// The tank that counts is the one of the machine that runs the vehicle: that machine burns it and says how much
    /// there is (<see cref="Word"/>), with how much of what the others poured it has taken in. What a machine that does
    /// not run it pours is added to a total everyone keeps (the host's count is the one that holds), and the machine
    /// that runs it takes in what it has not yet (<see cref="Absorb"/>). A machine that takes the vehicle over starts
    /// from the last word and what was poured since (<see cref="TakeOver"/>). A machine that does not run it shows the
    /// last word and what was poured since (<see cref="Shown"/>), its own pouring at once.
    /// </summary>
    public sealed class FuelTankLedger
    {
        /// <summary>Everyone's pouring, as known here.</summary>
        public int AddedFuel { get; private set; }

        public int AddedOil { get; private set; }

        /// <summary>How much of <see cref="AddedFuel"/> this machine's tank took in while running the vehicle.</summary>
        public int AppliedFuel { get; private set; }

        public int AppliedOil { get; private set; }

        public bool HasWord { get; private set; }

        public int WordLevel { get; private set; }

        public int WordOil { get; private set; }

        /// <summary>How much of the pouring the last word included.</summary>
        public int WordFuel { get; private set; }

        public int WordOilAdded { get; private set; }

        /// <summary>
        /// This machine runs the vehicle from now on, with <paramref name="level"/> and <paramref name="oil"/> in its
        /// tank. Returns what the tank should have: the last word and what was poured since, or what it has if nobody
        /// said anything yet.
        /// </summary>
        public void TakeOver(ref int level, ref int oil)
        {
            if (HasWord)
            {
                level = Math.Max(0, WordLevel + (AddedFuel - WordFuel));
                oil = Math.Max(0, WordOil + (AddedOil - WordOilAdded));
            }

            AppliedFuel = AddedFuel;
            AppliedOil = AddedOil;
        }

        /// <summary>While running the vehicle: what the others poured that this tank has not taken in yet, now taken.</summary>
        public void Absorb(out int fuel, out int oil)
        {
            fuel = AddedFuel - AppliedFuel;
            oil = AddedOil - AppliedOil;
            AppliedFuel = AddedFuel;
            AppliedOil = AddedOil;
        }

        /// <summary>What the machine that runs the vehicle says: the tank, and how much of the pouring it holds.</summary>
        public int[] Word(int level, int oil)
        {
            return new[] { level, oil, AppliedFuel, AppliedOil };
        }

        /// <summary>The word of the machine that runs it, heard.</summary>
        public void Heard(int[] word)
        {
            if (word == null || word.Length < 4)
            {
                return;
            }

            HasWord = true;
            WordLevel = word[0];
            WordOil = word[1];
            WordFuel = word[2];
            WordOilAdded = word[3];
        }

        /// <summary>The total poured, as the host counts it. It only grows: an older count is not news.</summary>
        public void HeardAdded(int fuel, int oil)
        {
            AddedFuel = Math.Max(AddedFuel, fuel);
            AddedOil = Math.Max(AddedOil, oil);
        }

        /// <summary>This machine, which does not run the vehicle, poured into it. Returns the totals before, for telling.</summary>
        public int[] Pour(int fuel, int oil)
        {
            var before = new[] { AddedFuel, AddedOil };
            AddedFuel += Math.Max(0, fuel);
            AddedOil += Math.Max(0, oil);
            return before;
        }

        /// <summary>The totals, for telling.</summary>
        public int[] Added()
        {
            return new[] { AddedFuel, AddedOil };
        }

        /// <summary>What a machine that does not run the vehicle shows: false while nobody has said anything.</summary>
        public bool Shown(out int level, out int oil)
        {
            level = Math.Max(0, WordLevel + Math.Max(0, AddedFuel - WordFuel));
            oil = Math.Max(0, WordOil + Math.Max(0, AddedOil - WordOilAdded));
            return HasWord;
        }
    }
}
