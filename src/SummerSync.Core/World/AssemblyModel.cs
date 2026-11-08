using System;
using System.Collections.Generic;
using System.Text;
using SummerSync.Core.Hashing;

namespace SummerSync.Core.World
{
    /// <summary>
    /// How far a car is put together, entry by entry of its <see cref="AssemblyCatalog"/>: whether a part is in each
    /// slot (and which way in and which part), the stage of every bolt, the wires connected, the paint and the damage.
    /// It is what the game keeps in its database of the car, and what every machine keeps the same by applying the
    /// same steps in the order the host gives them.
    ///
    /// Whether a part is bolted down is not here: the game works it out from the stages of its bolts, and so can the
    /// game side, from <see cref="Tightness"/>. The continuous numbers of the car (fluids, wear) are not here either;
    /// they are the replication's book of values.
    ///
    /// A model only says what is valid for each entry by itself (installing what is not installed, a bolt within its
    /// stages); what one part needs of another (a wishbone needs the subframe) is the game's to check, and the order of
    /// the catalogue's ranks is what keeps a rebuilt car possible.
    /// </summary>
    public sealed class AssemblyModel
    {
        /// <summary>The highest variant, paint type and outcome code a step can carry.</summary>
        public const int MaxArgument = ushort.MaxValue;

        private readonly AssemblyCatalog catalog;
        private readonly bool[] installed;
        private readonly int[] variants;
        private readonly int[] partRefs;
        private readonly bool[] wired;
        private readonly bool[] painted;
        private readonly int[] paintTypes;
        private readonly int[] colors;
        private readonly bool[] damaged;
        private readonly byte[] stages;

        public AssemblyModel(AssemblyCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }

            catalog.Freeze();
            this.catalog = catalog;
            int count = catalog.Count;
            installed = new bool[count];
            variants = new int[count];
            partRefs = new int[count];
            wired = new bool[count];
            painted = new bool[count];
            paintTypes = new int[count];
            colors = new int[count];
            damaged = new bool[count];
            stages = new byte[catalog.TotalBolts];
        }

        public AssemblyCatalog Catalog
        {
            get { return catalog; }
        }

        public bool IsInstalled(int entry)
        {
            return installed[entry];
        }

        /// <summary>The variant the part went in by; meaningful while installed.</summary>
        public int Variant(int entry)
        {
            return variants[entry];
        }

        /// <summary>The game side's reference of the part that went in, 0 when not known; meaningful while installed.</summary>
        public int PartRef(int entry)
        {
            return partRefs[entry];
        }

        public bool IsWired(int entry)
        {
            return wired[entry];
        }

        public bool IsPainted(int entry)
        {
            return painted[entry];
        }

        public int PaintType(int entry)
        {
            return paintTypes[entry];
        }

        public int Color(int entry)
        {
            return colors[entry];
        }

        public bool IsDamaged(int entry)
        {
            return damaged[entry];
        }

        public int Stage(int entry, int bolt)
        {
            CheckBolt(entry, bolt);
            return stages[catalog.BoltOffset(entry) + bolt];
        }

        /// <summary>The stages of all the entry's bolts added up: what the game compares with its "bolted" threshold.</summary>
        public int Tightness(int entry)
        {
            int offset = catalog.BoltOffset(entry);
            int total = 0;
            for (int i = 0; i < catalog.Bolts(entry); i++)
            {
                total += stages[offset + i];
            }

            return total;
        }

        /// <summary>Whether the entry's bolts can turn now: it is installed, or its bolts turn while it is loose.</summary>
        public bool BoltsActive(int entry)
        {
            return installed[entry] || (catalog.Flags(entry) & AssemblyEntryFlags.BoltsWhileLoose) != 0;
        }

        /// <summary>For a game side that reads its own game (or a test): whether a part is in the slot, and how.</summary>
        public void SetInstalled(int entry, bool value, int variant = 0, int partRef = 0)
        {
            CheckEntry(entry);
            if (variant < 0 || variant > MaxArgument)
            {
                throw new ArgumentOutOfRangeException("variant");
            }

            installed[entry] = value;
            variants[entry] = value ? variant : 0;
            partRefs[entry] = value ? partRef : 0;
        }

        public void SetStage(int entry, int bolt, int stage)
        {
            CheckBolt(entry, bolt);
            if (stage < 0 || stage > catalog.MaxStage(entry, bolt))
            {
                throw new ArgumentOutOfRangeException("stage", "Bolt " + bolt + " of '" + catalog.Name(entry) + "' turns from 0 to " + catalog.MaxStage(entry, bolt) + ", not to " + stage + ".");
            }

            stages[catalog.BoltOffset(entry) + bolt] = (byte)stage;
        }

        public void SetWired(int entry, bool value)
        {
            CheckEntry(entry);
            wired[entry] = value;
        }

        public void SetPaint(int entry, int paintType, int color)
        {
            CheckEntry(entry);
            if (paintType < 0 || paintType > MaxArgument)
            {
                throw new ArgumentOutOfRangeException("paintType");
            }

            painted[entry] = true;
            paintTypes[entry] = paintType;
            colors[entry] = color;
        }

        public void ClearPaint(int entry)
        {
            CheckEntry(entry);
            painted[entry] = false;
            paintTypes[entry] = 0;
            colors[entry] = 0;
        }

        public void SetDamaged(int entry, bool value)
        {
            CheckEntry(entry);
            damaged[entry] = value;
        }

        /// <summary>Back to a car with nothing in it, nothing bolted, wired, painted or damaged.</summary>
        public void Clear()
        {
            Array.Clear(installed, 0, installed.Length);
            Array.Clear(variants, 0, variants.Length);
            Array.Clear(partRefs, 0, partRefs.Length);
            Array.Clear(wired, 0, wired.Length);
            Array.Clear(painted, 0, painted.Length);
            Array.Clear(paintTypes, 0, paintTypes.Length);
            Array.Clear(colors, 0, colors.Length);
            Array.Clear(damaged, 0, damaged.Length);
            Array.Clear(stages, 0, stages.Length);
        }

        public void CopyFrom(AssemblyModel other)
        {
            RequireSameCatalog(other);
            if (other == this)
            {
                return;
            }

            Array.Copy(other.installed, installed, installed.Length);
            Array.Copy(other.variants, variants, variants.Length);
            Array.Copy(other.partRefs, partRefs, partRefs.Length);
            Array.Copy(other.wired, wired, wired.Length);
            Array.Copy(other.painted, painted, painted.Length);
            Array.Copy(other.paintTypes, paintTypes, paintTypes.Length);
            Array.Copy(other.colors, colors, colors.Length);
            Array.Copy(other.damaged, damaged, damaged.Length);
            Array.Copy(other.stages, stages, stages.Length);
        }

        public AssemblyModel Clone()
        {
            var copy = new AssemblyModel(catalog);
            copy.CopyFrom(this);
            return copy;
        }

        /// <summary>Everything the same, the parts' references too.</summary>
        public bool SameAs(AssemblyModel other)
        {
            RequireSameCatalog(other);
            for (int i = 0; i < installed.Length; i++)
            {
                if (!SameEntry(other, i) || partRefs[i] != other.partRefs[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether a step names things this catalogue has, with numbers a step of its kind can have: a known kind, an
        /// entry and a bolt of the list, a turn of one stage, a wire connected or cut. What is not well formed is never
        /// applied; it comes from a different build or a broken message.
        /// </summary>
        public bool IsWellFormed(AssemblyStep step)
        {
            if (step.Entry < 0 || step.Entry >= catalog.Count)
            {
                return false;
            }

            switch (step.Kind)
            {
                case AssemblyStepKind.Install:
                    return step.Argument >= 0 && step.Argument <= MaxArgument;
                case AssemblyStepKind.Remove:
                    return step.Argument == 0 && step.Value == 0;
                case AssemblyStepKind.Bolt:
                    return step.Argument >= 0 && step.Argument < catalog.Bolts(step.Entry) && (step.Value == 1 || step.Value == -1);
                case AssemblyStepKind.Wire:
                    return (step.Argument == 0 || step.Argument == 1) && step.Value == 0;
                case AssemblyStepKind.Paint:
                    return step.Argument >= 0 && step.Argument <= MaxArgument;
                case AssemblyStepKind.Outcome:
                    return step.Argument > 0 && step.Argument <= MaxArgument && step.Value == 0;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether the step can happen to this car as it is: installing into an empty slot, removing what is installed,
        /// a bolt that can turn and stays within its stages, connecting what is cut and the other way round. Painting
        /// and outcomes can always happen.
        /// </summary>
        public bool Allows(AssemblyStep step)
        {
            if (!IsWellFormed(step))
            {
                return false;
            }

            int entry = step.Entry;
            switch (step.Kind)
            {
                case AssemblyStepKind.Install:
                    return !installed[entry];
                case AssemblyStepKind.Remove:
                    return installed[entry];
                case AssemblyStepKind.Bolt:
                    int stage = stages[catalog.BoltOffset(entry) + step.Bolt] + step.Delta;
                    return BoltsActive(entry) && stage >= 0 && stage <= catalog.MaxStage(entry, step.Bolt);
                case AssemblyStepKind.Wire:
                    return wired[entry] != step.Connected;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Makes the step happen, whether it was allowed or not, as near as it can: installing over a part replaces it,
        /// a bolt stops at its ends. It has to be well formed.
        /// </summary>
        public void Apply(AssemblyStep step)
        {
            if (!IsWellFormed(step))
            {
                throw new ArgumentException("Not a step of this car: " + step.Describe(catalog) + ".", "step");
            }

            int entry = step.Entry;
            switch (step.Kind)
            {
                case AssemblyStepKind.Install:
                    installed[entry] = true;
                    variants[entry] = step.Variant;
                    partRefs[entry] = step.PartRef;
                    break;
                case AssemblyStepKind.Remove:
                    installed[entry] = false;
                    variants[entry] = 0;
                    partRefs[entry] = 0;
                    if ((catalog.Flags(entry) & AssemblyEntryFlags.ClearsBoltsOnRemove) != 0)
                    {
                        Array.Clear(stages, catalog.BoltOffset(entry), catalog.Bolts(entry));
                    }

                    break;
                case AssemblyStepKind.Bolt:
                    int at = catalog.BoltOffset(entry) + step.Bolt;
                    stages[at] = (byte)Math.Max(0, Math.Min(catalog.MaxStage(entry, step.Bolt), stages[at] + step.Delta));
                    break;
                case AssemblyStepKind.Wire:
                    wired[entry] = step.Connected;
                    break;
                case AssemblyStepKind.Paint:
                    painted[entry] = true;
                    paintTypes[entry] = step.PaintType;
                    colors[entry] = step.Color;
                    break;
                case AssemblyStepKind.Outcome:
                    if (step.Code == AssemblyWire.OutcomeDamaged)
                    {
                        damaged[entry] = true;
                    }
                    else if (step.Code == AssemblyWire.OutcomeRepaired)
                    {
                        damaged[entry] = false;
                    }

                    break;
            }
        }

        /// <summary>
        /// A number made from the whole car, entry by entry in the catalogue's order: whether installed and by which
        /// variant, wired, painted (type and colour), damaged, and the stage of every bolt. The parts' references are
        /// left out: a game that reads its own car does not know them. Two machines whose cars are the same have the
        /// same number. FNV-1a, 64 bit; it creates no garbage.
        /// </summary>
        public ulong Digest()
        {
            ulong hash = Fnv1a.OffsetBasis;
            for (int i = 0; i < installed.Length; i++)
            {
                int flags = (installed[i] ? 1 : 0) | (wired[i] ? 2 : 0) | (painted[i] ? 4 : 0) | (damaged[i] ? 8 : 0);
                hash = Mix(hash, (byte)flags);
                if (installed[i])
                {
                    hash = Mix(hash, variants[i]);
                }

                if (painted[i])
                {
                    hash = Mix(hash, paintTypes[i]);
                    hash = Mix(hash, colors[i]);
                }

                int offset = catalog.BoltOffset(i);
                for (int bolt = 0; bolt < catalog.Bolts(i); bolt++)
                {
                    hash = Mix(hash, stages[offset + bolt]);
                }
            }

            return hash;
        }

        /// <summary>
        /// The steps that take a game showing <paramref name="from"/> to <paramref name="to"/>, in an order the game can
        /// make them: bolts loosened first (the deepest parts first), wires cut, parts taken off from the top down, parts
        /// put on from the bottom up (by the catalogue's ranks), bolts tightened, wires connected, paint, and damage
        /// (<see cref="AssemblyWire.OutcomeDamaged"/>, <see cref="AssemblyWire.OutcomeRepaired"/>).
        ///
        /// A part in its slot whose variant differs, or whose reference differs where both are known, comes off and goes
        /// back in. What a game cannot do is left out: bolts of a part that stays off, and paint that would have to come
        /// off. A bolt's change is one step per stage, as a wrench turns it.
        /// </summary>
        public static List<AssemblyStep> Reconcile(AssemblyModel from, AssemblyModel to)
        {
            var steps = new List<AssemblyStep>();
            Reconcile(from, to, steps, new AssemblyModel(from.Catalog));
            return steps;
        }

        /// <summary>The same into a list the caller keeps, with a model to work in, so that nothing is allocated.</summary>
        public static void Reconcile(AssemblyModel from, AssemblyModel to, List<AssemblyStep> steps, AssemblyModel work)
        {
            if (from == null || to == null || steps == null || work == null)
            {
                throw new ArgumentNullException(from == null ? "from" : to == null ? "to" : steps == null ? "steps" : "work");
            }

            from.RequireSameCatalog(to);
            from.RequireSameCatalog(work);
            AssemblyCatalog catalog = from.catalog;
            int[] order = catalog.InstallOrder;
            work.CopyFrom(from);

            // Bolts first, the deepest parts first: what comes off has to be loose, and what goes back to fewer stages can.
            for (int n = order.Length - 1; n >= 0; n--)
            {
                int entry = order[n];
                if (!work.BoltsActive(entry))
                {
                    continue;
                }

                bool comesOff = work.installed[entry] && (!to.installed[entry] || work.MustReinstall(to, entry));
                bool both = comesOff && (catalog.Flags(entry) & AssemblyEntryFlags.ClearsBoltsOnRemove) == 0;
                work.TurnTowards(to, entry, steps, both ? 0 : -1);
            }

            for (int n = order.Length - 1; n >= 0; n--)
            {
                int entry = order[n];
                if (work.wired[entry] && !to.wired[entry])
                {
                    work.Emit(AssemblyStep.Wire(entry, false), steps);
                }
            }

            for (int n = order.Length - 1; n >= 0; n--)
            {
                int entry = order[n];
                if (work.installed[entry] && (!to.installed[entry] || work.MustReinstall(to, entry)))
                {
                    work.Emit(AssemblyStep.Remove(entry), steps);
                }
            }

            for (int n = 0; n < order.Length; n++)
            {
                int entry = order[n];
                if (!work.installed[entry] && to.installed[entry])
                {
                    work.Emit(AssemblyStep.Install(entry, to.variants[entry], to.partRefs[entry]), steps);
                }
            }

            for (int n = 0; n < order.Length; n++)
            {
                int entry = order[n];
                if (work.BoltsActive(entry))
                {
                    work.TurnTowards(to, entry, steps, 0);
                }
            }

            for (int n = 0; n < order.Length; n++)
            {
                int entry = order[n];
                if (!work.wired[entry] && to.wired[entry])
                {
                    work.Emit(AssemblyStep.Wire(entry, true), steps);
                }
            }

            for (int n = 0; n < order.Length; n++)
            {
                int entry = order[n];
                if (to.painted[entry] && (!work.painted[entry] || work.paintTypes[entry] != to.paintTypes[entry] || work.colors[entry] != to.colors[entry]))
                {
                    work.Emit(AssemblyStep.Paint(entry, to.paintTypes[entry], to.colors[entry]), steps);
                }
            }

            for (int n = 0; n < order.Length; n++)
            {
                int entry = order[n];
                if (work.damaged[entry] != to.damaged[entry])
                {
                    work.Emit(AssemblyStep.Outcome(entry, to.damaged[entry] ? AssemblyWire.OutcomeDamaged : AssemblyWire.OutcomeRepaired), steps);
                }
            }
        }

        /// <summary>
        /// The entries where this car and <paramref name="other"/> differ (by the digest's terms), at most
        /// <paramref name="most"/> of them, as "Door_Left (installed here, not there), ..."; empty when none.
        /// </summary>
        public string DescribeDifferences(AssemblyModel other, int most)
        {
            RequireSameCatalog(other);
            var text = new StringBuilder();
            int found = 0;
            for (int i = 0; i < installed.Length; i++)
            {
                if (SameEntry(other, i))
                {
                    continue;
                }

                found++;
                if (found > most)
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                text.Append(catalog.Name(i)).Append(" (");
                if (installed[i] != other.installed[i])
                {
                    text.Append(installed[i] ? "installed here, not there" : "installed there, not here");
                }
                else if (installed[i] && variants[i] != other.variants[i])
                {
                    text.Append("variant ").Append(variants[i]).Append(" here, ").Append(other.variants[i]).Append(" there");
                }
                else if (wired[i] != other.wired[i])
                {
                    text.Append(wired[i] ? "wired here, not there" : "wired there, not here");
                }
                else if (damaged[i] != other.damaged[i])
                {
                    text.Append(damaged[i] ? "damaged here, not there" : "damaged there, not here");
                }
                else if (painted[i] != other.painted[i] || paintTypes[i] != other.paintTypes[i] || colors[i] != other.colors[i])
                {
                    text.Append("paint");
                }
                else
                {
                    text.Append("bolts ").Append(Tightness(i)).Append(" here, ").Append(other.Tightness(i)).Append(" there");
                }

                text.Append(')');
            }

            if (found > most)
            {
                text.Append(" and ").Append(found - most).Append(" more");
            }

            return text.ToString();
        }

        private bool SameEntry(AssemblyModel other, int i)
        {
            if (installed[i] != other.installed[i] || variants[i] != other.variants[i] || wired[i] != other.wired[i] || painted[i] != other.painted[i]
                || paintTypes[i] != other.paintTypes[i] || colors[i] != other.colors[i] || damaged[i] != other.damaged[i])
            {
                return false;
            }

            int offset = catalog.BoltOffset(i);
            for (int bolt = 0; bolt < catalog.Bolts(i); bolt++)
            {
                if (stages[offset + bolt] != other.stages[offset + bolt])
                {
                    return false;
                }
            }

            return true;
        }

        // A part that is in on both sides but is not the same part: another way in, or another known part.
        private bool MustReinstall(AssemblyModel to, int entry)
        {
            return to.installed[entry] && installed[entry]
                && (variants[entry] != to.variants[entry] || (partRefs[entry] != 0 && to.partRefs[entry] != 0 && partRefs[entry] != to.partRefs[entry]));
        }

        // Turns the entry's bolts towards the other car's stages, one step a stage: only looser (-1), or both ways (0).
        private void TurnTowards(AssemblyModel to, int entry, List<AssemblyStep> steps, int direction)
        {
            int offset = catalog.BoltOffset(entry);
            for (int bolt = 0; bolt < catalog.Bolts(entry); bolt++)
            {
                int target = to.stages[offset + bolt];
                while (stages[offset + bolt] > target)
                {
                    Emit(AssemblyStep.Loosen(entry, bolt), steps);
                }

                while (direction == 0 && stages[offset + bolt] < target)
                {
                    Emit(AssemblyStep.Tighten(entry, bolt), steps);
                }
            }
        }

        private void Emit(AssemblyStep step, List<AssemblyStep> steps)
        {
            step.Player = AssemblyWire.NoPlayer;
            Apply(step);
            steps.Add(step);
        }

        private void CheckEntry(int entry)
        {
            if (entry < 0 || entry >= installed.Length)
            {
                throw new ArgumentOutOfRangeException("entry", "The car has " + installed.Length + " entries, not one numbered " + entry + ".");
            }
        }

        private void CheckBolt(int entry, int bolt)
        {
            CheckEntry(entry);
            if (bolt < 0 || bolt >= catalog.Bolts(entry))
            {
                throw new ArgumentOutOfRangeException("bolt", "'" + catalog.Name(entry) + "' has " + catalog.Bolts(entry) + " bolts, not one numbered " + bolt + ".");
            }
        }

        private void RequireSameCatalog(AssemblyModel other)
        {
            if (other == null)
            {
                throw new ArgumentNullException("other");
            }

            if (other.catalog != catalog)
            {
                throw new ArgumentException("The two cars are listed by different catalogues.", "other");
            }
        }

        private static ulong Mix(ulong hash, byte value)
        {
            unchecked
            {
                return (hash ^ value) * 1099511628211UL;
            }
        }

        private static ulong Mix(ulong hash, int value)
        {
            hash = Mix(hash, (byte)value);
            hash = Mix(hash, (byte)(value >> 8));
            hash = Mix(hash, (byte)(value >> 16));
            return Mix(hash, (byte)(value >> 24));
        }
    }
}
