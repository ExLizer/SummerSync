using System;
using System.Collections.Generic;
using System.Text;
using SummerSync.Core.Hashing;

namespace SummerSync.Core.World
{
    /// <summary>How an entry of the <see cref="AssemblyCatalog"/> behaves besides installing and removing.</summary>
    [Flags]
    public enum AssemblyEntryFlags : byte
    {
        None = 0,

        /// <summary>
        /// Its bolts can be turned while it is not installed: the engine block hangs from the hoist and is installed by
        /// bolting it to its mounts, which the game shows before the block counts as installed.
        /// </summary>
        BoltsWhileLoose = 1,

        /// <summary>
        /// Taking it off sets its bolts back to nothing, as the game does for the doors (it resets the part's list of
        /// bolt stages when it comes off). Without it, an entry that comes off keeps its stages, as the game keeps them in
        /// the copy inside the car.
        /// </summary>
        ClearsBoltsOnRemove = 2,
    }

    /// <summary>
    /// The list of what can be built into a car, the same on every machine: its entries (one per part slot of the game's
    /// database of the car, a wheel, a wire…), the bolts of each with the highest stage each one turns to, and the
    /// numbers of the car that change by themselves (fluids, wear, charge). Entries and numbers travel by their place in
    /// this list, never by name, so both ends have to list them the same way; the list's <see cref="Shape"/>, a number
    /// made from all of it, travels with every message and a message of another shape is not read.
    ///
    /// The <see cref="Rank"/> of an entry is where it goes when a car is put together: whatever an entry needs installed
    /// first has a lower rank. Equal ranks go in the order of the list.
    ///
    /// The list can grow until the first model or replication uses it; from then on it is fixed.
    /// </summary>
    public sealed class AssemblyCatalog
    {
        public const int MaxEntries = 1024;
        public const int MaxBolts = 64;
        public const int MaxValues = 128;
        public const int MaxNameBytes = 64;
        public const int DefaultMaxStage = 8;

        private readonly List<string> names = new List<string>();
        private readonly List<byte[]> maxStages = new List<byte[]>();
        private readonly List<int> ranks = new List<int>();
        private readonly List<AssemblyEntryFlags> flags = new List<AssemblyEntryFlags>();
        private readonly Dictionary<string, int> byName = new Dictionary<string, int>();
        private readonly List<string> valueNames = new List<string>();
        private readonly List<float> valueSteps = new List<float>();
        private readonly Dictionary<string, int> valuesByName = new Dictionary<string, int>();
        private int[] boltOffsets;
        private int[] installOrder;
        private int totalBolts;
        private uint shape;

        /// <summary>Entries listed.</summary>
        public int Count
        {
            get { return names.Count; }
        }

        /// <summary>Numbers of the car listed.</summary>
        public int ValueCount
        {
            get { return valueNames.Count; }
        }

        /// <summary>Bolts of every entry together.</summary>
        public int TotalBolts
        {
            get
            {
                Freeze();
                return totalBolts;
            }
        }

        /// <summary>Whether the list is fixed: a model or a replication uses it.</summary>
        public bool Frozen
        {
            get { return boltOffsets != null; }
        }

        /// <summary>A number made from the whole list: names, bolts and their stages, ranks, flags and the numbers' names.</summary>
        public uint Shape
        {
            get
            {
                Freeze();
                return shape;
            }
        }

        /// <summary>Adds an entry whose bolts all turn up to the same stage. Returns its place in the list.</summary>
        public int Add(string name, int bolts, int maxStage = DefaultMaxStage, int rank = 0, AssemblyEntryFlags entryFlags = AssemblyEntryFlags.None)
        {
            if (bolts < 0 || bolts > MaxBolts)
            {
                throw new ArgumentOutOfRangeException("bolts", "An entry has 0 to " + MaxBolts + " bolts, not " + bolts + ".");
            }

            if (maxStage < 1 || maxStage > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException("maxStage", "A bolt turns to a stage of 1 to 255, not " + maxStage + ".");
            }

            var stages = new byte[bolts];
            for (int i = 0; i < bolts; i++)
            {
                stages[i] = (byte)maxStage;
            }

            return Add(name, stages, rank, entryFlags);
        }

        /// <summary>Adds an entry with the highest stage of each of its bolts. Returns its place in the list.</summary>
        public int Add(string name, byte[] boltMaxStages, int rank, AssemblyEntryFlags entryFlags)
        {
            RequireOpen();
            if (string.IsNullOrEmpty(name) || Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
            {
                throw new ArgumentException("An entry needs a name of 1 to " + MaxNameBytes + " bytes.", "name");
            }

            if (byName.ContainsKey(name))
            {
                throw new ArgumentException("The list already has an entry called '" + name + "'.", "name");
            }

            if (names.Count >= MaxEntries)
            {
                throw new InvalidOperationException("A car has at most " + MaxEntries + " entries.");
            }

            boltMaxStages = boltMaxStages ?? new byte[0];
            if (boltMaxStages.Length > MaxBolts)
            {
                throw new ArgumentOutOfRangeException("boltMaxStages", "An entry has 0 to " + MaxBolts + " bolts, not " + boltMaxStages.Length + ".");
            }

            for (int i = 0; i < boltMaxStages.Length; i++)
            {
                if (boltMaxStages[i] == 0)
                {
                    throw new ArgumentOutOfRangeException("boltMaxStages", "Bolt " + i + " of '" + name + "' turns to no stage at all.");
                }
            }

            int index = names.Count;
            names.Add(name);
            maxStages.Add((byte[])boltMaxStages.Clone());
            ranks.Add(rank);
            flags.Add(entryFlags);
            byName[name] = index;
            return index;
        }

        /// <summary>
        /// Adds a number of the car. <paramref name="reportStep"/> is how much it has to change before the machine that
        /// runs the car says it at once; smaller changes are said a little later. Returns its place in the list.
        /// </summary>
        public int AddValue(string name, float reportStep)
        {
            RequireOpen();
            if (string.IsNullOrEmpty(name) || Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
            {
                throw new ArgumentException("A number of the car needs a name of 1 to " + MaxNameBytes + " bytes.", "name");
            }

            if (valuesByName.ContainsKey(name))
            {
                throw new ArgumentException("The list already has a number called '" + name + "'.", "name");
            }

            if (valueNames.Count >= MaxValues)
            {
                throw new InvalidOperationException("A car has at most " + MaxValues + " numbers.");
            }

            if (!(reportStep >= 0f) || float.IsInfinity(reportStep))
            {
                throw new ArgumentOutOfRangeException("reportStep");
            }

            valuesByName[name] = valueNames.Count;
            valueNames.Add(name);
            valueSteps.Add(reportStep);
            return valueNames.Count - 1;
        }

        public string Name(int entry)
        {
            return names[entry];
        }

        /// <summary>The entry with that name, or -1.</summary>
        public int Find(string name)
        {
            int index;
            return name != null && byName.TryGetValue(name, out index) ? index : -1;
        }

        public int Bolts(int entry)
        {
            return maxStages[entry].Length;
        }

        public int MaxStage(int entry, int bolt)
        {
            return maxStages[entry][bolt];
        }

        public int Rank(int entry)
        {
            return ranks[entry];
        }

        public AssemblyEntryFlags Flags(int entry)
        {
            return flags[entry];
        }

        public string ValueName(int value)
        {
            return valueNames[value];
        }

        public float ValueStep(int value)
        {
            return valueSteps[value];
        }

        /// <summary>The number with that name, or -1.</summary>
        public int FindValue(string name)
        {
            int index;
            return name != null && valuesByName.TryGetValue(name, out index) ? index : -1;
        }

        /// <summary>Where the stages of an entry's bolts start among everyone's.</summary>
        internal int BoltOffset(int entry)
        {
            Freeze();
            return boltOffsets[entry];
        }

        /// <summary>The entries in the order a car is put together: by rank, then by place in the list.</summary>
        internal int[] InstallOrder
        {
            get
            {
                Freeze();
                return installOrder;
            }
        }

        /// <summary>Fixes the list: called by whatever starts using it.</summary>
        public void Freeze()
        {
            if (boltOffsets != null)
            {
                return;
            }

            var offsets = new int[names.Count];
            int total = 0;
            for (int i = 0; i < names.Count; i++)
            {
                offsets[i] = total;
                total += maxStages[i].Length;
            }

            var order = new int[names.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            // A stable sort by rank: equal ranks keep the order of the list.
            Array.Sort(order, (a, b) => ranks[a] != ranks[b] ? ranks[a].CompareTo(ranks[b]) : a.CompareTo(b));
            totalBolts = total;
            installOrder = order;
            shape = ComputeShape();
            boltOffsets = offsets;
        }

        private void RequireOpen()
        {
            if (boltOffsets != null)
            {
                throw new InvalidOperationException("The list of the car is in use and can no longer change.");
            }
        }

        private uint ComputeShape()
        {
            ulong hash = Fnv1a.OffsetBasis;
            var number = new byte[4];
            for (int i = 0; i < names.Count; i++)
            {
                hash = Text(hash, names[i]);
                byte[] stages = maxStages[i];
                hash = Number(hash, number, stages.Length);
                hash = Fnv1a.Hash64(stages, 0, stages.Length, hash);
                hash = Number(hash, number, ranks[i]);
                hash = Number(hash, number, (int)flags[i]);
            }

            hash = Number(hash, number, -1);
            for (int i = 0; i < valueNames.Count; i++)
            {
                hash = Text(hash, valueNames[i]);
            }

            return (uint)(hash ^ (hash >> 32));
        }

        private static ulong Text(ulong hash, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            hash = Fnv1a.Hash64(bytes, 0, bytes.Length, hash);
            return Fnv1a.Hash64(new byte[] { 0 }, 0, 1, hash);
        }

        private static ulong Number(ulong hash, byte[] buffer, int value)
        {
            buffer[0] = (byte)value;
            buffer[1] = (byte)(value >> 8);
            buffer[2] = (byte)(value >> 16);
            buffer[3] = (byte)(value >> 24);
            return Fnv1a.Hash64(buffer, 0, 4, hash);
        }
    }
}
