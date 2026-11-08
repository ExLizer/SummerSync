using System;
using System.Globalization;

namespace SummerSync.Core.World
{
    /// <summary>What a step of putting a car together does.</summary>
    public enum AssemblyStepKind : byte
    {
        None = 0,

        /// <summary>A part goes into its slot (<see cref="AssemblyStep.Variant"/>, <see cref="AssemblyStep.PartRef"/>).</summary>
        Install = 1,

        /// <summary>The part in a slot comes off.</summary>
        Remove = 2,

        /// <summary>One bolt of an entry turns one stage (<see cref="AssemblyStep.Bolt"/>, <see cref="AssemblyStep.Delta"/>).</summary>
        Bolt = 3,

        /// <summary>A wire of the car is connected or cut (<see cref="AssemblyStep.Connected"/>).</summary>
        Wire = 4,

        /// <summary>A part is painted (<see cref="AssemblyStep.PaintType"/>, <see cref="AssemblyStep.Color"/>).</summary>
        Paint = 5,

        /// <summary>
        /// Something the game of the machine that runs the car decided by chance (<see cref="AssemblyStep.Code"/>): a
        /// part wears out, a tyre goes flat. <see cref="AssemblyWire.OutcomeDamaged"/> and
        /// <see cref="AssemblyWire.OutcomeRepaired"/> change the entry's damage; the game side's own codes, from
        /// <see cref="AssemblyWire.FirstGameOutcome"/> up, only travel.
        /// </summary>
        Outcome = 6,
    }

    /// <summary>
    /// One step of putting a car together, by the entry of the <see cref="AssemblyCatalog"/> it acts on and never by
    /// where anything is in the game's scene. The two numbers mean different things for each kind; the properties
    /// name them.
    /// </summary>
    public struct AssemblyStep : IEquatable<AssemblyStep>
    {
        public AssemblyStepKind Kind;

        /// <summary>The entry's place in the catalogue.</summary>
        public int Entry;

        /// <summary>Install: the variant. Bolt: which bolt. Wire: 1 connected, 0 cut. Paint: the paint type. Outcome: the code.</summary>
        public int Argument;

        /// <summary>Install: the part's reference. Bolt: +1 or -1. Paint: the colour.</summary>
        public int Value;

        /// <summary>
        /// Whose game did it, filled in by the replication when it hands a step to the game; <see cref="AssemblyWire.NoPlayer"/>
        /// for a step the replication made up to correct this game (undoing what the host refused, matching the host's car).
        /// </summary>
        public byte Player;

        /// <summary>Install: which of the slot's ways in it takes (the state the game's trigger enters; a wheel's kind).</summary>
        public int Variant
        {
            get { return Argument; }
        }

        /// <summary>
        /// Install: what the game side calls the part that goes in, opaque to the replication (the shared number of
        /// the loose object in the player's hand, or 0 when there is none to name).
        /// </summary>
        public int PartRef
        {
            get { return Value; }
        }

        /// <summary>Bolt: which of the entry's bolts.</summary>
        public int Bolt
        {
            get { return Argument; }
        }

        /// <summary>Bolt: +1 one stage tighter, -1 one stage looser.</summary>
        public int Delta
        {
            get { return Value; }
        }

        /// <summary>Wire: connected, or cut.</summary>
        public bool Connected
        {
            get { return Argument != 0; }
        }

        public int PaintType
        {
            get { return Argument; }
        }

        /// <summary>Paint: the colour, packed by the game side (RGBA, a byte each).</summary>
        public int Color
        {
            get { return Value; }
        }

        /// <summary>Outcome: what happened.</summary>
        public int Code
        {
            get { return Argument; }
        }

        public static AssemblyStep Install(int entry, int variant, int partRef)
        {
            return new AssemblyStep { Kind = AssemblyStepKind.Install, Entry = entry, Argument = variant, Value = partRef };
        }

        public static AssemblyStep Remove(int entry)
        {
            return new AssemblyStep { Kind = AssemblyStepKind.Remove, Entry = entry };
        }

        /// <summary>One bolt one stage: <paramref name="delta"/> +1 tighter or -1 looser.</summary>
        public static AssemblyStep Turn(int entry, int bolt, int delta)
        {
            return new AssemblyStep { Kind = AssemblyStepKind.Bolt, Entry = entry, Argument = bolt, Value = delta };
        }

        public static AssemblyStep Tighten(int entry, int bolt)
        {
            return Turn(entry, bolt, 1);
        }

        public static AssemblyStep Loosen(int entry, int bolt)
        {
            return Turn(entry, bolt, -1);
        }

        public static AssemblyStep Wire(int entry, bool connected)
        {
            return new AssemblyStep { Kind = AssemblyStepKind.Wire, Entry = entry, Argument = connected ? 1 : 0 };
        }

        public static AssemblyStep Paint(int entry, int paintType, int color)
        {
            return new AssemblyStep { Kind = AssemblyStepKind.Paint, Entry = entry, Argument = paintType, Value = color };
        }

        public static AssemblyStep Outcome(int entry, int code)
        {
            return new AssemblyStep { Kind = AssemblyStepKind.Outcome, Entry = entry, Argument = code };
        }

        /// <summary>Same kind, entry and numbers; who did it does not count.</summary>
        public bool Equals(AssemblyStep other)
        {
            return Kind == other.Kind && Entry == other.Entry && Argument == other.Argument && Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is AssemblyStep && Equals((AssemblyStep)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((((int)Kind * 397) ^ Entry) * 397 ^ Argument) * 397 ^ Value;
            }
        }

        /// <summary>"Install #3 variant 0 part 12", for logs.</summary>
        public override string ToString()
        {
            return Describe(null);
        }

        /// <summary>The same, with the entry's name from the catalogue when there is one.</summary>
        public string Describe(AssemblyCatalog catalog)
        {
            string entry = catalog != null && Entry >= 0 && Entry < catalog.Count ? catalog.Name(Entry) : "#" + Entry.ToString(CultureInfo.InvariantCulture);
            switch (Kind)
            {
                case AssemblyStepKind.Install:
                    return "Install " + entry + " variant " + Number(Variant) + " part " + Number(PartRef);
                case AssemblyStepKind.Remove:
                    return "Remove " + entry;
                case AssemblyStepKind.Bolt:
                    return (Delta > 0 ? "Tighten " : "Loosen ") + entry + " bolt " + Number(Bolt);
                case AssemblyStepKind.Wire:
                    return (Connected ? "Connect " : "Cut ") + entry;
                case AssemblyStepKind.Paint:
                    return "Paint " + entry + " type " + Number(PaintType) + " colour " + Color.ToString("x8", CultureInfo.InvariantCulture);
                case AssemblyStepKind.Outcome:
                    return "Outcome " + Number(Code) + " on " + entry;
                default:
                    return "Step " + Number((int)Kind) + " on " + entry;
            }
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
