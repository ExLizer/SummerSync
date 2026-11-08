using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Json;
using SummerSync.Core.World;

namespace SummerSync.Core.Vehicles
{
    /// <summary>Whether a number of the car is a float or an int variable of its state machine.</summary>
    public enum CarValueKind
    {
        Float,
        Int,
    }

    /// <summary>
    /// Which changes of a number, on a machine that does not run the car, are that machine's player's doing and go to
    /// the machine that runs it as an amount to add. The others are that machine's own simulation, which the number of
    /// the machine that runs the car overwrites.
    /// </summary>
    public enum CarValueLocal
    {
        /// <summary>Only the game of the machine that runs the car changes it (a wear, the battery's charge).</summary>
        None,

        /// <summary>The simulation only lowers it; what rises was the player's (a repair sets a wear back up).</summary>
        Up,

        /// <summary>Every change is the player's (a setting turned by hand), or every change while a gate is open.</summary>
        Any,
    }

    /// <summary>How a state machine that decides by chance is kept from deciding on a machine that does not run the car.</summary>
    public enum ChanceBlock
    {
        /// <summary>Nothing is done: listed with the reason it needs nothing.</summary>
        None,

        /// <summary>Switched off whole (it has nothing else to do and no event it must hear).</summary>
        Pause,

        /// <summary>The weights of some events of its random choice, in one state, are made nothing: it goes on with the rest.</summary>
        Weights,

        /// <summary>Entering one state, it is sent straight to another: the chain behind that state does not run.</summary>
        Hold,

        /// <summary>One of its variables is held at a value that keeps it from getting anywhere.</summary>
        Force,
    }

    /// <summary>A state machine of the game in one of some states: while it is, a number is changing by the local player's hand.</summary>
    public sealed class FsmGate
    {
        public PartPlace Place { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>Never null nor empty.</summary>
        public string[] States { get; internal set; }

        public bool Holds(string state)
        {
            for (int i = 0; i < States.Length; i++)
            {
                if (States[i] == state)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// One number of the car that changes by itself or by hand, and where the game keeps it: a variable of the state
    /// machine <see cref="Machine"/> of the object <see cref="Entry"/> of the part database (Database/&lt;entry&gt;) or
    /// at <see cref="Place"/>; with <see cref="FollowVariable"/>, of the machine <see cref="FollowMachine"/> of the
    /// object that game object variable holds (the tyre on a hub).
    /// </summary>
    public sealed class CarValue
    {
        /// <summary>Its position in the file: how the session names it.</summary>
        public int Index { get; internal set; }

        public string Name { get; internal set; }

        /// <summary>The database entry ("DatabaseMotor/Oilpan") whose object holds it, or null when <see cref="Place"/> says.</summary>
        public string Entry { get; internal set; }

        /// <summary>Where the object that holds it is, when it is not a database entry; null otherwise.</summary>
        public PartPlace Place { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>A game object variable of <see cref="Machine"/> that holds the object with the number, or null.</summary>
        public string FollowVariable { get; internal set; }

        /// <summary>The machine of the followed object that holds the number, or null.</summary>
        public string FollowMachine { get; internal set; }

        public string Variable { get; internal set; }

        public CarValueKind Kind { get; internal set; }

        /// <summary>How much it has to change before the machine that runs the car says it at once.</summary>
        public float Step { get; internal set; }

        public bool HasMin { get; internal set; }

        public float Min { get; internal set; }

        public bool HasMax { get; internal set; }

        public float Max { get; internal set; }

        public CarValueLocal Local { get; internal set; }

        /// <summary>
        /// When not empty, a change on a machine that does not run the car counts as its player's only while one of
        /// these is open (the local player pours). Never null.
        /// </summary>
        public FsmGate[] While { get; internal set; }

        /// <summary>A transform the game turns to show the number (the distributor's pivot), or null.</summary>
        public PartPlace Mirror { get; internal set; }

        /// <summary>The local Euler angle <see cref="Mirror"/> is turned about: 0 x, 1 y, 2 z.</summary>
        public int MirrorAxis { get; internal set; }

        /// <summary>The number as the game would keep it: within its range, and whole for an int.</summary>
        public float Clamp(float value)
        {
            if (HasMin && value < Min)
            {
                value = Min;
            }

            if (HasMax && value > Max)
            {
                value = Max;
            }

            return Kind == CarValueKind.Int ? (float)Math.Round(value) : value;
        }

        /// <summary>
        /// Whether a change seen on a machine that does not run the car, while the number's gates allow it, is the
        /// local player's: by the direction <see cref="Local"/> allows.
        /// </summary>
        public bool CountsAsLocal(float change)
        {
            switch (Local)
            {
                case CarValueLocal.Up:
                    return change > 0f;
                case CarValueLocal.Any:
                    return change != 0f;
                default:
                    return false;
            }
        }
    }

    /// <summary>A state machine under the car that decides by chance, and how a machine that does not run the car keeps it from deciding.</summary>
    public sealed class CarChance
    {
        public string Name { get; internal set; }

        /// <summary>Where it is; null when <see cref="EachBoltCheck"/>.</summary>
        public PartPlace Place { get; internal set; }

        /// <summary>It is the machine of that name on every bolt check of the part catalogue that has the state.</summary>
        public bool EachBoltCheck { get; internal set; }

        public string Machine { get; internal set; }

        public ChanceBlock Block { get; internal set; }

        /// <summary>Weights: the state whose random choice is changed. Hold: the state it is sent away from.</summary>
        public string State { get; internal set; }

        /// <summary>Weights: the events whose weight is made nothing. Never null.</summary>
        public string[] Events { get; internal set; }

        /// <summary>Hold: the state it is sent to.</summary>
        public string To { get; internal set; }

        /// <summary>Force: the float variable held.</summary>
        public string Variable { get; internal set; }

        /// <summary>Force: the value it is held at.</summary>
        public float Value { get; internal set; }
    }

    /// <summary>
    /// Something the game of the machine that runs the car does to an entry that the others are told as an outcome:
    /// its machine leaves <see cref="From"/> for one of <see cref="Into"/>; the others are sent <see cref="Event"/>.
    /// </summary>
    public sealed class CarOutcomeWatch
    {
        public int Code { get; internal set; }

        public string Entry { get; internal set; }

        public PartPlace Place { get; internal set; }

        public string Machine { get; internal set; }

        public string From { get; internal set; }

        /// <summary>Never null nor empty.</summary>
        public string[] Into { get; internal set; }

        public string Event { get; internal set; }
    }

    /// <summary>
    /// The Satsuma's book of numbers (data/satsuma/values.json): the fluids, the wear, the battery's charge and the
    /// settings turned by hand, each with where the game keeps it, how much it has to change to be said at once, the
    /// range the game holds it in, and which of its changes on a machine that does not run the car are that player's
    /// doing; the state machines under the car that decide by chance, with how a machine that does not run the car
    /// keeps each from deciding; and the outcomes watched for. The numbers are listed in the file's order, and that is
    /// how the session names them (<see cref="AddTo"/>): the file is the same on every machine.
    ///
    /// Settings turned by a "Screw" machine with TIGHTEN and UNTIGHTEN (valves, toe, the rally dampers, the idle
    /// screws) are numbers too, any player's to change: the part catalogue has no step for those screws (Fase 8).
    /// </summary>
    public sealed class SatsumaValues
    {
        public const int FormatVersion = 1;

        /// <summary>An outcome on a wheel entry: its tyre went flat (the game's "Condition" machine took PUNCTURE or wore it through).</summary>
        public const int OutcomePuncture = AssemblyWire.FirstGameOutcome;

        /// <summary>
        /// An outcome on an entry: its bolt check broke it off (loose at speed, hit hard, worn out). Its bolts going to
        /// nothing and the parts that fall are steps of their own; the outcome says why.
        /// </summary>
        public const int OutcomeBrokeOff = AssemblyWire.FirstGameOutcome + 1;

        private static readonly string[] ValueKeys = { "name", "entry", "path", "fsm", "variable", "type", "step", "min", "max", "local", "while", "mirror", "follow", "note" };
        private static readonly string[] GateKeys = { "path", "fsm", "states" };
        private static readonly string[] MirrorKeys = { "path", "axis" };
        private static readonly string[] FollowKeys = { "variable", "fsm" };
        private static readonly string[] ChanceKeys = { "name", "path", "each", "fsm", "block", "state", "events", "to", "variable", "value", "note" };
        private static readonly string[] OutcomeKeys = { "code", "entry", "path", "fsm", "from", "into", "event", "note" };
        private static readonly string[] Blocks = { "none", "pause", "weights", "hold", "force" };
        private static readonly string[] Locals = { "none", "up", "any" };

        private readonly Dictionary<string, CarValue> byName = new Dictionary<string, CarValue>();

        /// <summary>In the file's order. Never null.</summary>
        public CarValue[] Values { get; private set; }

        /// <summary>Never null.</summary>
        public CarChance[] Chances { get; private set; }

        /// <summary>Never null.</summary>
        public CarOutcomeWatch[] Outcomes { get; private set; }

        /// <summary>The number of that name, or null.</summary>
        public CarValue Find(string name)
        {
            CarValue value;
            return name != null && byName.TryGetValue(name, out value) ? value : null;
        }

        /// <summary>Lists every number in the session's catalogue, in the file's order. Call it before the catalogue is in use.</summary>
        public void AddTo(AssemblyCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }

            for (int i = 0; i < Values.Length; i++)
            {
                catalog.AddValue(Values[i].Name, Values[i].Step);
            }
        }

        /// <exception cref="FormatException">An entry the book names is not in the part catalogue. The message says which.</exception>
        public void Check(SatsumaCatalog parts)
        {
            if (parts == null)
            {
                throw new ArgumentNullException("parts");
            }

            for (int i = 0; i < Values.Length; i++)
            {
                CarValue value = Values[i];
                string what = "The number '" + value.Name + "'";
                if (value.Entry != null)
                {
                    RequireEntry(parts, value.Entry, what);
                }

                CheckPlace(parts, value.Place, what);
                CheckPlace(parts, value.Mirror, what);
                for (int g = 0; g < value.While.Length; g++)
                {
                    CheckPlace(parts, value.While[g].Place, what);
                }
            }

            for (int i = 0; i < Chances.Length; i++)
            {
                CheckPlace(parts, Chances[i].Place, "The chance machine '" + Chances[i].Name + "'");
            }

            for (int i = 0; i < Outcomes.Length; i++)
            {
                string what = "The outcome on '" + Outcomes[i].Entry + "'";
                RequireEntry(parts, Outcomes[i].Entry, what);
                CheckPlace(parts, Outcomes[i].Place, what);
            }
        }

        /// <summary>"damaged", "repaired", "puncture", "broke off", or "outcome N".</summary>
        public static string DescribeOutcome(int code)
        {
            switch (code)
            {
                case AssemblyWire.OutcomeDamaged:
                    return "damaged";
                case AssemblyWire.OutcomeRepaired:
                    return "repaired";
                case OutcomePuncture:
                    return "puncture";
                case OutcomeBrokeOff:
                    return "broke off";
                default:
                    return "outcome " + code.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <exception cref="FormatException">The text is not a valid book of numbers. The message says what is wrong.</exception>
        public static SatsumaValues Parse(string json)
        {
            Dictionary<string, object> file = JsonReader.Object(JsonReader.Parse(json), "The book of numbers");
            double format = JsonReader.Number(file, "format", "The book of numbers");
            if (format != FormatVersion)
            {
                throw new FormatException("A book of numbers of format " + format.ToString(CultureInfo.InvariantCulture) + "; this build reads format " + FormatVersion + ".");
            }

            var book = new SatsumaValues();
            var values = new List<CarValue>();
            List<object> items = JsonReader.Array(file.ContainsKey("values") ? file["values"] : null, "The book of numbers' \"values\"");
            for (int i = 0; i < items.Count; i++)
            {
                CarValue value = ParseValue(JsonReader.Object(items[i], "Number " + (i + 1)), i + 1);
                if (book.byName.ContainsKey(value.Name))
                {
                    throw new FormatException("The book of numbers has two numbers called '" + value.Name + "'.");
                }

                value.Index = values.Count;
                book.byName[value.Name] = value;
                values.Add(value);
            }

            if (values.Count == 0 || values.Count > AssemblyCatalog.MaxValues)
            {
                throw new FormatException("The book of numbers has " + values.Count + " numbers; a car has 1 to " + AssemblyCatalog.MaxValues + ".");
            }

            book.Values = values.ToArray();

            var chances = new List<CarChance>();
            var chanceNames = new Dictionary<string, bool>();
            foreach (Dictionary<string, object> item in Objects(file, "chance", "The book of numbers"))
            {
                CarChance chance = ParseChance(item, chances.Count + 1);
                if (chanceNames.ContainsKey(chance.Name))
                {
                    throw new FormatException("The book of numbers has two chance machines called '" + chance.Name + "'.");
                }

                chanceNames[chance.Name] = true;
                chances.Add(chance);
            }

            book.Chances = chances.ToArray();

            var outcomes = new List<CarOutcomeWatch>();
            foreach (Dictionary<string, object> item in Objects(file, "outcomes", "The book of numbers"))
            {
                outcomes.Add(ParseOutcome(item, outcomes.Count + 1));
            }

            book.Outcomes = outcomes.ToArray();
            return book;
        }

        private static CarValue ParseValue(Dictionary<string, object> item, int number)
        {
            string name = JsonReader.String(item, "name", "Number " + number);
            string what = "The number '" + name + "'";
            if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > AssemblyCatalog.MaxNameBytes)
            {
                throw new FormatException("Number " + number + " needs a name of 1 to " + AssemblyCatalog.MaxNameBytes + " bytes.");
            }

            RequireKeys(item, ValueKeys, what);
            var value = new CarValue { Name = name };
            value.Entry = Optional(item, "entry", what);
            string path = Optional(item, "path", what);
            if ((value.Entry == null) == (path == null))
            {
                throw new FormatException(what + " needs either an \"entry\" of the part database or a \"path\", not both.");
            }

            if (path != null)
            {
                value.Place = Place(path, what);
                value.Machine = JsonReader.String(item, "fsm", what);
            }
            else
            {
                value.Machine = Optional(item, "fsm", what) ?? "Data";
            }

            value.Variable = JsonReader.String(item, "variable", what);
            if (value.Variable.Length == 0 || value.Machine.Length == 0)
            {
                throw new FormatException(what + " needs a machine and a variable.");
            }

            string kind = Optional(item, "type", what) ?? "float";
            if (kind == "float")
            {
                value.Kind = CarValueKind.Float;
            }
            else if (kind == "int")
            {
                value.Kind = CarValueKind.Int;
            }
            else
            {
                throw new FormatException(what + " has the type '" + kind + "'; it is \"float\" or \"int\".");
            }

            value.Step = (float)JsonReader.Number(item, "step", what);
            if (!(value.Step > 0f) || float.IsInfinity(value.Step))
            {
                throw new FormatException(what + " needs a \"step\" above nothing.");
            }

            value.HasMin = item.ContainsKey("min");
            value.Min = value.HasMin ? (float)JsonReader.Number(item, "min", what) : 0f;
            value.HasMax = item.ContainsKey("max");
            value.Max = value.HasMax ? (float)JsonReader.Number(item, "max", what) : 0f;
            if (value.HasMin && value.HasMax && value.Min > value.Max)
            {
                throw new FormatException(what + " has a \"min\" above its \"max\".");
            }

            value.Local = (CarValueLocal)Choice(Optional(item, "local", what) ?? "none", Locals, "local", what);
            var gates = new List<FsmGate>();
            foreach (Dictionary<string, object> gate in Objects(item, "while", what))
            {
                RequireKeys(gate, GateKeys, what + "'s gate");
                gates.Add(new FsmGate
                {
                    Place = Place(JsonReader.String(gate, "path", what + "'s gate"), what + "'s gate"),
                    Machine = JsonReader.String(gate, "fsm", what + "'s gate"),
                    States = Texts(gate, "states", what + "'s gate", true),
                });
            }

            value.While = gates.ToArray();
            if (value.While.Length > 0 && value.Local == CarValueLocal.None)
            {
                throw new FormatException(what + " has gates but no \"local\" changes for them to let through.");
            }

            object mirror;
            if (item.TryGetValue("mirror", out mirror) && mirror != null)
            {
                Dictionary<string, object> turn = JsonReader.Object(mirror, what + "'s \"mirror\"");
                RequireKeys(turn, MirrorKeys, what + "'s \"mirror\"");
                value.Mirror = Place(JsonReader.String(turn, "path", what + "'s \"mirror\""), what + "'s \"mirror\"");
                string axis = JsonReader.String(turn, "axis", what + "'s \"mirror\"");
                value.MirrorAxis = axis == "x" ? 0 : axis == "y" ? 1 : axis == "z" ? 2 : -1;
                if (value.MirrorAxis < 0)
                {
                    throw new FormatException(what + " turns its mirror about '" + axis + "'; it is \"x\", \"y\" or \"z\".");
                }
            }

            object follow;
            if (item.TryGetValue("follow", out follow) && follow != null)
            {
                Dictionary<string, object> through = JsonReader.Object(follow, what + "'s \"follow\"");
                RequireKeys(through, FollowKeys, what + "'s \"follow\"");
                value.FollowVariable = JsonReader.String(through, "variable", what + "'s \"follow\"");
                value.FollowMachine = JsonReader.String(through, "fsm", what + "'s \"follow\"");
                if (value.FollowVariable.Length == 0 || value.FollowMachine.Length == 0)
                {
                    throw new FormatException(what + " follows nothing: its \"follow\" needs a variable and a machine.");
                }
            }

            return value;
        }

        private static CarChance ParseChance(Dictionary<string, object> item, int number)
        {
            string name = JsonReader.String(item, "name", "Chance machine " + number);
            string what = "The chance machine '" + name + "'";
            RequireKeys(item, ChanceKeys, what);
            var chance = new CarChance { Name = name, Machine = JsonReader.String(item, "fsm", what) };
            string path = Optional(item, "path", what);
            string each = Optional(item, "each", what);
            if ((path == null) == (each == null))
            {
                throw new FormatException(what + " needs either a \"path\" or \"each\", not both.");
            }

            if (each != null && each != "boltChecks")
            {
                throw new FormatException(what + " is on each '" + each + "'; the book knows \"boltChecks\".");
            }

            chance.EachBoltCheck = each != null;
            chance.Place = path == null ? null : Place(path, what);
            chance.Block = (ChanceBlock)Choice(JsonReader.String(item, "block", what), Blocks, "block", what);
            chance.State = Optional(item, "state", what);
            chance.To = Optional(item, "to", what);
            chance.Variable = Optional(item, "variable", what);
            chance.Events = Texts(item, "events", what, false);
            switch (chance.Block)
            {
                case ChanceBlock.Weights:
                    if (chance.State == null || chance.Events.Length == 0)
                    {
                        throw new FormatException(what + " changes the weights of a random choice: it needs the \"state\" and the \"events\".");
                    }

                    break;
                case ChanceBlock.Hold:
                    if (chance.State == null || chance.To == null)
                    {
                        throw new FormatException(what + " is held: it needs the \"state\" it is sent from and the state it is sent \"to\".");
                    }

                    break;
                case ChanceBlock.Force:
                    if (chance.Variable == null || !item.ContainsKey("value"))
                    {
                        throw new FormatException(what + " is held by a variable: it needs the \"variable\" and its \"value\".");
                    }

                    chance.Value = (float)JsonReader.Number(item, "value", what);
                    break;
            }

            if (chance.EachBoltCheck && chance.Block != ChanceBlock.Weights)
            {
                throw new FormatException(what + " is on every bolt check; only its weights can be changed there.");
            }

            return chance;
        }

        private static CarOutcomeWatch ParseOutcome(Dictionary<string, object> item, int number)
        {
            string what = "Outcome " + number;
            RequireKeys(item, OutcomeKeys, what);
            string code = JsonReader.String(item, "code", what);
            if (code != "puncture")
            {
                throw new FormatException(what + " has the code '" + code + "'; the book knows \"puncture\".");
            }

            var watch = new CarOutcomeWatch
            {
                Code = OutcomePuncture,
                Entry = JsonReader.String(item, "entry", what),
                Place = Place(JsonReader.String(item, "path", what), what),
                Machine = JsonReader.String(item, "fsm", what),
                From = JsonReader.String(item, "from", what),
                Into = Texts(item, "into", what, true),
                Event = JsonReader.String(item, "event", what),
            };
            if (watch.Entry.Length == 0 || watch.Machine.Length == 0 || watch.From.Length == 0 || watch.Event.Length == 0)
            {
                throw new FormatException(what + " needs an entry, a machine, the state it leaves and the event that does it.");
            }

            return watch;
        }

        private static void RequireEntry(SatsumaCatalog parts, string entry, string what)
        {
            if (parts.Find(entry) == null)
            {
                throw new FormatException(what + " is on '" + entry + "', which is not an entry of the part catalogue.");
            }
        }

        private static void CheckPlace(SatsumaCatalog parts, PartPlace place, string what)
        {
            if (place == null)
            {
                return;
            }

            if (place.Kind == PartPlaceKind.Wheel)
            {
                throw new FormatException(what + " has the place '" + place.Text + "', below any wheel; the book needs one object.");
            }

            if (place.Kind == PartPlaceKind.Part)
            {
                RequireEntry(parts, place.Entry, what);
            }
        }

        private static PartPlace Place(string text, string what)
        {
            PartPlace place = PartPlace.Parse(text, what);
            if (place.Kind == PartPlaceKind.Wheel)
            {
                throw new FormatException(what + " has the place '" + text + "', below any wheel; the book needs one object.");
            }

            return place;
        }

        private static int Choice(string text, string[] choices, string name, string what)
        {
            int index = Array.IndexOf(choices, text);
            if (index < 0)
            {
                throw new FormatException(what + " has the \"" + name + "\" '" + text + "'; it is one of " + string.Join(", ", choices) + ".");
            }

            return index;
        }

        private static void RequireKeys(Dictionary<string, object> item, string[] known, string what)
        {
            foreach (KeyValuePair<string, object> pair in item)
            {
                if (Array.IndexOf(known, pair.Key) < 0)
                {
                    throw new FormatException(what + " has the unknown field \"" + pair.Key + "\".");
                }
            }
        }

        private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> parent, string name, string what)
        {
            object value;
            if (!parent.TryGetValue(name, out value) || value == null)
            {
                yield break;
            }

            List<object> items = JsonReader.Array(value, what + "'s \"" + name + "\"");
            for (int i = 0; i < items.Count; i++)
            {
                yield return JsonReader.Object(items[i], what + "'s \"" + name + "\" " + (i + 1));
            }
        }

        private static string[] Texts(Dictionary<string, object> parent, string name, string what, bool required)
        {
            object value;
            if (!parent.TryGetValue(name, out value) || value == null)
            {
                if (required)
                {
                    throw new FormatException(what + " needs \"" + name + "\" as a list of texts.");
                }

                return new string[0];
            }

            List<object> items = JsonReader.Array(value, what + "'s \"" + name + "\"");
            if (required && items.Count == 0)
            {
                throw new FormatException(what + " needs \"" + name + "\" as a list of texts.");
            }

            var result = new string[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                result[i] = items[i] as string;
                if (string.IsNullOrEmpty(result[i]))
                {
                    throw new FormatException(what + "'s \"" + name + "\" must be a list of texts.");
                }
            }

            return result;
        }

        private static string Optional(Dictionary<string, object> parent, string name, string what)
        {
            object value;
            if (!parent.TryGetValue(name, out value) || value == null)
            {
                return null;
            }

            var text = value as string;
            if (text == null)
            {
                throw new FormatException(what + " has a \"" + name + "\" that is not a text.");
            }

            return text.Length == 0 ? null : text;
        }
    }
}
