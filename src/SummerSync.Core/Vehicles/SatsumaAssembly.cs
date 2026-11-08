using System;
using System.Collections.Generic;
using SummerSync.Core.World;

namespace SummerSync.Core.Vehicles
{
    /// <summary>
    /// The Satsuma's catalogue of parts (<see cref="SatsumaCatalog"/>) as the assembly replication lists it
    /// (<see cref="AssemblyCatalog"/>): one entry per entry of the catalogue, in the same order, with its bolts by their
    /// place among the entry's bolts; the order a car is put together in (<see cref="AssemblyCatalog.Rank"/>); and how
    /// each entry's bolts behave. Every machine builds it the same way from the same file, so the shapes agree.
    ///
    /// What the flags say is the game's (dump of 2026-10-14, the machines "Removal" of the loose parts):
    /// <list type="bullet">
    /// <item>A part that the game installs whole and takes off whole, with bolts (families B1 and B2), has its bolts
    /// set back to nothing when it comes off: its "Remove part" resets the part's list of bolt stages and its
    /// tightness. The grilles and the second radiator hose do not.</item>
    /// <item>The engine block has no trigger: it hangs from the hoist, and its three bolts to the engine mounts turn
    /// before it counts as installed; bolting them is what installs it.</item>
    /// <item>A wire, and an entry with bolts and no trigger (the fuel line), counts as installed by the game's own
    /// doing, not by a player's step: their bolts turn whenever the game lets them.</item>
    /// </list>
    /// </summary>
    public static class SatsumaAssembly
    {
        /// <summary>
        /// An install's variant says which of the entry's triggers took the part, and for a trigger that sits on a wheel
        /// (a hubcap's), on which corner's wheel: trigger + corner × this.
        /// </summary>
        public const int CornerFactor = 16;

        /// <summary>The corners of the car in the order the wheels' slots are named: "Wheels/FL" is corner 0.</summary>
        public static readonly string[] Corners = { "FL", "FR", "RL", "RR" };

        // Parts of families B1 and B2 whose removal leaves their bolts' stages as they are (dump of 2026-10-14).
        private static readonly string[] KeepBolts = { "grille(Clone)", "grille gt(Clone)", "radiator hose2(Clone)" };

        /// <summary>The assembly replication's list of the car: the catalogue's entries and, after them, the car's numbers.</summary>
        public static AssemblyCatalog Build(SatsumaCatalog parts, IList<string> valueNames, IList<float> valueSteps)
        {
            if (parts == null)
            {
                throw new ArgumentNullException("parts");
            }

            int values = valueNames == null ? 0 : valueNames.Count;
            if (values != (valueSteps == null ? 0 : valueSteps.Count))
            {
                throw new ArgumentException("Every number of the car needs its step.");
            }

            int[] ranks = Ranks(parts);
            var catalog = new AssemblyCatalog();
            PartEntry[] entries = parts.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                PartEntry entry = entries[i];
                var stages = new byte[entry.Bolts.Length];
                for (int b = 0; b < stages.Length; b++)
                {
                    int most = entry.Bolts[b].Stages;
                    stages[b] = (byte)(most < 1 ? 1 : most > byte.MaxValue ? byte.MaxValue : most);
                }

                int index = catalog.Add(entry.Name, stages, ranks[i], FlagsOf(entry));
                if (index != entry.Index)
                {
                    throw new InvalidOperationException("The catalogue's entry '" + entry.Name + "' landed at " + index + " instead of " + entry.Index + ".");
                }
            }

            for (int v = 0; v < values; v++)
            {
                catalog.AddValue(valueNames[v], valueSteps[v]);
            }

            catalog.Freeze();
            return catalog;
        }

        /// <summary>How the entry's bolts behave, as the game makes them.</summary>
        public static AssemblyEntryFlags FlagsOf(PartEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException("entry");
            }

            AssemblyEntryFlags flags = AssemblyEntryFlags.None;
            if (!InstalledBySteps(entry))
            {
                flags |= AssemblyEntryFlags.BoltsWhileLoose;
            }

            bool whole = entry.Family == PartFamily.Parented || entry.Family == PartFamily.Jointed;
            if (whole && entry.Bolts.Length > 0 && Array.IndexOf(KeepBolts, entry.Part) < 0)
            {
                flags |= AssemblyEntryFlags.ClearsBoltsOnRemove;
            }

            return flags;
        }

        /// <summary>
        /// Whether the entry is a tool's bolts and not a part of the car (the hoist's hook, whose bolts hang the engine
        /// block): it has no record in the game's database and is never put together with the car.
        /// </summary>
        public static bool IsTool(PartEntry entry)
        {
            return entry != null && entry.Group == "Tools";
        }

        /// <summary>
        /// Whether a player installs and removes the entry by a step of its own (a trigger takes the part, its removal
        /// gives it back); false for the block, the wires and what the game installs by itself.
        /// </summary>
        public static bool InstalledBySteps(PartEntry entry)
        {
            return entry.Family != PartFamily.Hoist && entry.Family != PartFamily.Wiring && entry.Triggers.Length > 0;
        }

        /// <summary>The corner of a wheel slot ("Wheels/RL" is 2), or -1 for an entry that is not one.</summary>
        public static int CornerOf(PartEntry entry)
        {
            if (entry == null || entry.Family != PartFamily.Wheel)
            {
                return -1;
            }

            int slash = entry.Name.LastIndexOf('/');
            return Array.IndexOf(Corners, slash < 0 ? entry.Name : entry.Name.Substring(slash + 1));
        }

        /// <summary>
        /// Where each entry goes when a car is put together: 0 for one that needs nothing installed first, and one more
        /// than the highest of what it needs. An entry needs what its triggers ask to be installed or bolted, and the
        /// part its trigger sits on; a wheel's corner, the hub. And what a trigger asks not to be installed yet (the head
        /// gasket, the cylinder head) needs that entry, as it covers it, unless that goes against what is needed installed
        /// (a halfshaft asks for its disc brake both ways). A loop (there should be none) counts as needing nothing.
        /// </summary>
        public static int[] Ranks(SatsumaCatalog parts)
        {
            if (parts == null)
            {
                throw new ArgumentNullException("parts");
            }

            PartEntry[] entries = parts.Entries;
            var needs = new List<int>[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                needs[i] = new List<int>();
            }

            var covers = new List<int[]>();
            for (int i = 0; i < entries.Length; i++)
            {
                List<int> list = needs[i];
                PartEntry entry = entries[i];
                for (int t = 0; t < entry.Triggers.Length; t++)
                {
                    PartTrigger trigger = entry.Triggers[t];
                    for (int r = 0; r < trigger.Requires.Length; r++)
                    {
                        if (trigger.Requires[r].Value)
                        {
                            Need(parts, list, trigger.Requires[r].Entry, i);
                        }
                        else
                        {
                            PartEntry cover = parts.Find(trigger.Requires[r].Entry);
                            if (cover != null && cover.Index != i)
                            {
                                covers.Add(new[] { cover.Index, i });
                            }
                        }
                    }

                    if (trigger.Place.Kind == PartPlaceKind.Part)
                    {
                        Need(parts, list, trigger.Place.Entry, i);
                    }
                }
            }

            // The covering part needs what it covers, if what it covers does not already need it, however far down.
            for (int c = 0; c < covers.Count; c++)
            {
                int cover = covers[c][0];
                int under = covers[c][1];
                if (!needs[cover].Contains(under) && !Reaches(needs, under, cover, new bool[entries.Length]))
                {
                    needs[cover].Add(under);
                }
            }

            var ranks = new int[entries.Length];
            var state = new byte[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                RankOf(i, needs, ranks, state);
            }

            return ranks;
        }

        private static void Need(SatsumaCatalog parts, List<int> list, string name, int self)
        {
            PartEntry other = parts.Find(name);
            if (other != null && other.Index != self && !list.Contains(other.Index))
            {
                list.Add(other.Index);
            }
        }

        // Whether an entry needs another, however far down.
        private static bool Reaches(List<int>[] needs, int from, int to, bool[] seen)
        {
            if (from == to)
            {
                return true;
            }

            seen[from] = true;
            List<int> list = needs[from];
            for (int i = 0; i < list.Count; i++)
            {
                if (!seen[list[i]] && Reaches(needs, list[i], to, seen))
                {
                    return true;
                }
            }

            return false;
        }

        // Depth first; state 1 is being worked out (a loop), 2 is known.
        private static int RankOf(int entry, List<int>[] needs, int[] ranks, byte[] state)
        {
            if (state[entry] == 2)
            {
                return ranks[entry];
            }

            if (state[entry] == 1)
            {
                return -1;
            }

            state[entry] = 1;
            int rank = 0;
            List<int> list = needs[entry];
            for (int i = 0; i < list.Count; i++)
            {
                int under = RankOf(list[i], needs, ranks, state);
                if (under >= 0 && under + 1 > rank)
                {
                    rank = under + 1;
                }
            }

            ranks[entry] = rank;
            state[entry] = 2;
            return rank;
        }
    }
}
