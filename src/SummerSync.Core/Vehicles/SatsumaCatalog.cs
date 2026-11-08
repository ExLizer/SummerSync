using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Json;

namespace SummerSync.Core.Vehicles
{
    /// <summary>How a part goes onto the Satsuma, as the game's assembly trigger does it.</summary>
    public enum PartFamily
    {
        /// <summary>Family A: the loose part is destroyed and a copy already in the car, "(xxxxx)", is switched on.
        /// Taking it off makes the game create a new loose part.</summary>
        Clone,

        /// <summary>Family B1: the loose part loses its body and is parented to the car or to another part.</summary>
        Parented,

        /// <summary>Family B2: the loose part keeps its body and is held by a joint (doors, hood, bumpers, seats…).</summary>
        Jointed,

        /// <summary>A wheel on one of the four hubs; the trigger also writes the wheel's size into the car.</summary>
        Wheel,

        /// <summary>A wire, made by the wiring tool between two connectors.</summary>
        Wiring,

        /// <summary>A shop item with a state of its own (spark plugs, bulbs, belt, oil filter, battery).</summary>
        Status,

        /// <summary>The engine block: hung from the hoist and bolted to the engine mounts, with no trigger.</summary>
        Hoist,

        /// <summary>Anything else the game's database lists (bought items, settings).</summary>
        Other,
    }

    /// <summary>Where a place of the catalogue is measured from.</summary>
    public enum PartPlaceKind
    {
        /// <summary>Below the Satsuma's root object.</summary>
        Car,

        /// <summary>From the top of the scene.</summary>
        Scene,

        /// <summary>Below the part object of an entry (found through that entry's database variable): it moves.</summary>
        Part,

        /// <summary>Below a wheel: the one at a wheel slot for that slot's bolts, or any wheel for a hubcap's trigger.</summary>
        Wheel,
    }

    /// <summary>
    /// A game object of the catalogue: "Body/trigger_door_left" is below the car's root, "/ITEMS/x" from the top of
    /// the scene, "DatabaseBody/Door_Left:Bolts/BoltPM" below the part object of that entry, "wheel:trigger_hubcap"
    /// below a wheel. Several bolts of one part can share a place: they differ by their index.
    /// </summary>
    public sealed class PartPlace
    {
        public PartPlaceKind Kind { get; internal set; }

        /// <summary>For <see cref="PartPlaceKind.Part"/>, the entry whose part holds it.</summary>
        public string Entry { get; internal set; }

        /// <summary>The path below what <see cref="Kind"/> says; empty for that object itself.</summary>
        public string Path { get; internal set; }

        /// <summary>As the catalogue writes it.</summary>
        public string Text { get; internal set; }

        public override string ToString()
        {
            return Text;
        }

        internal static PartPlace Parse(string text, string what)
        {
            if (string.IsNullOrEmpty(text))
            {
                throw new FormatException(what + " has an empty place.");
            }

            var place = new PartPlace { Text = text };
            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                string host = text.Substring(0, colon);
                place.Path = text.Substring(colon + 1);
                if (host.Length == 0 || place.Path.IndexOf(':') >= 0)
                {
                    throw new FormatException(what + " has the place '" + text + "', which is not \"entry:path\".");
                }

                place.Kind = host == "wheel" ? PartPlaceKind.Wheel : PartPlaceKind.Part;
                place.Entry = place.Kind == PartPlaceKind.Part ? host : null;
            }
            else if (text[0] == '/')
            {
                place.Kind = PartPlaceKind.Scene;
                place.Path = text.Substring(1);
            }
            else
            {
                place.Kind = PartPlaceKind.Car;
                place.Path = text;
            }

            if (place.Kind != PartPlaceKind.Part && place.Kind != PartPlaceKind.Wheel && place.Path.Length == 0)
            {
                throw new FormatException(what + " has the place '" + text + "', which names nothing.");
            }

            return place;
        }
    }

    /// <summary>A database variable of another entry that a trigger reads before it takes a part, and what it must be.</summary>
    public sealed class PartRequirement
    {
        public string Entry { get; internal set; }

        /// <summary>The variable of that entry's Data machine: "Installed", "Bolted"…</summary>
        public string Variable { get; internal set; }

        /// <summary>What it must be. For a removal blocker, the value that blocks (always true).</summary>
        public bool Value { get; internal set; }
    }

    /// <summary>
    /// A slot where a part goes: the game's assembly trigger, and how to drive it without the player. Set the trigger's
    /// <see cref="PartVariable"/> to the part, then either send ASSEMBLE (if <see cref="AssembleEvent"/>) or enter
    /// <see cref="State"/>, the state the player's click leads to, past any check of the slot.
    /// </summary>
    public sealed class PartTrigger
    {
        /// <summary>The object with the trigger's "Assembly" machine.</summary>
        public PartPlace Place { get; internal set; }

        /// <summary>The name of the loose part this slot takes, as the game names it: "door left(Clone)".</summary>
        public string Part { get; internal set; }

        /// <summary>The state to enter, with a global transition, to assemble without the mouse.</summary>
        public string State { get; internal set; }

        /// <summary>
        /// Whether the trigger's resting state already has an ASSEMBLE transition that leads to the same assembly: the
        /// game's own path when it loads a save (it may skip the sound).
        /// </summary>
        public bool AssembleEvent { get; internal set; }

        /// <summary>The flag the game sets to assemble on load ("Setup"), or null.</summary>
        public string Setup { get; internal set; }

        /// <summary>Whether entering <see cref="State"/> plays the assembly sound.</summary>
        public bool Sound { get; internal set; }

        /// <summary>What this slot does with the part.</summary>
        public PartFamily Family { get; internal set; }

        /// <summary>Never null.</summary>
        public PartRequirement[] Requires { get; internal set; }

        /// <summary>
        /// The dump that made the catalogue had not loaded this trigger's actions: what is written was read from the
        /// level file and the state graph, and needs a dump where it is loaded to be confirmed.
        /// </summary>
        public bool Unloaded { get; internal set; }

        /// <summary>The same trigger takes the parts of several entries, and tells them apart by the part it is given.</summary>
        public bool Shared { get; internal set; }

        /// <summary>Name of the trigger machine's variable that holds the part being assembled.</summary>
        public const string PartVariable = "Part";
    }

    /// <summary>One bolt: a "BoltPM" object whose "Screw" machine takes TIGHTEN and UNTIGHTEN.</summary>
    public sealed class PartBolt
    {
        /// <summary>The game's own index of the bolt in its part's list: the last digit of its child "bolt&lt;n&gt;".</summary>
        public int Index { get; internal set; }

        public PartPlace Place { get; internal set; }

        /// <summary>The bolt's local scale on x: the wrench size the game asks for (0.8 is the 8 mm wrench).</summary>
        public float Size { get; internal set; }

        /// <summary>The highest stage the bolt reaches; each TIGHTEN adds one.</summary>
        public int Stages { get; internal set; }

        /// <summary>Its position among all the bolts of its entry, in catalogue order: how the session names it.</summary>
        public int Slot { get; internal set; }

        /// <summary>The check that counts its turns.</summary>
        public PartBoltCheck Check { get; internal set; }
    }

    /// <summary>The machine that adds up a part's bolts ("BoltCheck", or "Use" for a wheel) and marks it bolted.</summary>
    public sealed class PartBoltCheck
    {
        public PartPlace Place { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>Whether the game marks the part bolted by its tightness at all.</summary>
        public bool HasThresholds { get; internal set; }

        /// <summary>The tightness at which it is bolted (BoltedYES) and at which it is not any more (BoltedNO).</summary>
        public float Yes { get; internal set; }

        public float No { get; internal set; }

        /// <summary>Never null.</summary>
        public PartBolt[] Bolts { get; internal set; }
    }

    /// <summary>A wire: two connectors, either of which, taken to <see cref="State"/>, makes it.</summary>
    public sealed class PartWiring
    {
        /// <summary>The objects with the "Assemble" machine of each end.</summary>
        public PartPlace[] Ends { get; internal set; }

        public string State { get; internal set; }

        /// <summary>What the game switches on when the wire is made.</summary>
        public PartPlace[] Activates { get; internal set; }

        /// <summary>Name of the connectors' machine.</summary>
        public const string Machine = "Assemble";
    }

    /// <summary>One entry of the game's part database (Database/&lt;group&gt;/&lt;name&gt;, machine "Data"), or a wheel slot.</summary>
    public sealed class PartEntry
    {
        /// <summary>Its position in the catalogue: how the session names it.</summary>
        public int Index { get; internal set; }

        /// <summary>Its path below "Database/" ("DatabaseBody/Door_Left"), or "Wheels/FL" for a wheel slot.</summary>
        public string Name { get; internal set; }

        /// <summary>"Body", "Motor", "Mechanics", "Orders", "Wiring", "Computer", "PartsStatus" or "Wheels".</summary>
        public string Group { get; internal set; }

        public PartFamily Family { get; internal set; }

        /// <summary>Which flags its Data machine has: "Installed", "Bolted", "Damaged", "Purchased", "InHoist".</summary>
        public string[] Data { get; internal set; }

        /// <summary>The loose part's name as the game names it, or null.</summary>
        public string Part { get; internal set; }

        /// <summary>The Data machine's variable that holds the part object ("ThisPart", "SpawnThis", "CurrentBulb"…), or null.</summary>
        public string PartVariable { get; internal set; }

        /// <summary>Family A: the copies in the car the game switches on. Never null.</summary>
        public PartPlace[] Copies { get; internal set; }

        /// <summary>Family A: the name of the loose part the game creates when it is taken off, or null.</summary>
        public string Spawn { get; internal set; }

        /// <summary>Never null.</summary>
        public PartTrigger[] Triggers { get; internal set; }

        /// <summary>The objects whose "Removal" machine takes it off when its "Detach" is set. Never null.</summary>
        public PartPlace[] Removals { get; internal set; }

        /// <summary>Entries whose variable, while true, keeps the player from taking it off. Never null.</summary>
        public PartRequirement[] BlockedBy { get; internal set; }

        /// <summary>Never null.</summary>
        public PartBoltCheck[] BoltChecks { get; internal set; }

        /// <summary>Every bolt of every check, by <see cref="PartBolt.Slot"/>. Never null.</summary>
        public PartBolt[] Bolts { get; internal set; }

        /// <summary>The wire, for a wiring entry; null otherwise.</summary>
        public PartWiring Wiring { get; internal set; }

        /// <summary>Wiring entries whose wire marks this one installed too (the battery terminals). Never null.</summary>
        public string[] InstalledBy { get; internal set; }

        public bool Has(string flag)
        {
            return Array.IndexOf(Data, flag) >= 0;
        }
    }

    /// <summary>
    /// The catalogue of the Satsuma's parts (data/satsuma/parts.json, made by tools/satsuma/make-parts.py from a dump
    /// and the game's level file): every entry of the game's part database with its slots, bolts and wires, and what it
    /// takes to drive each the game's own way. Entries are sorted by name, so the same file gives the same indices on
    /// every machine, and <see cref="Shape"/> tells two catalogues apart.
    /// </summary>
    public sealed class SatsumaCatalog
    {
        public const int FormatVersion = 1;

        private readonly Dictionary<string, PartEntry> byName = new Dictionary<string, PartEntry>();

        /// <summary>The Satsuma's root object's name.</summary>
        public string Car { get; private set; }

        /// <summary>Sorted by name (ordinal). Never null.</summary>
        public PartEntry[] Entries { get; private set; }

        /// <summary>
        /// A hash of what the session refers to: entry names and families, each trigger's place, state and part, each
        /// bolt's index and place, each wire's ends. Two machines with the same shape name the same things the same way.
        /// </summary>
        public uint Shape { get; private set; }

        public int BoltCount { get; private set; }

        /// <summary>The entry of that name, or null.</summary>
        public PartEntry Find(string name)
        {
            PartEntry entry;
            return name != null && byName.TryGetValue(name, out entry) ? entry : null;
        }

        /// <summary>The entry at that index, or null.</summary>
        public PartEntry At(int index)
        {
            return index >= 0 && index < Entries.Length ? Entries[index] : null;
        }

        /// <exception cref="FormatException">The text is not a valid catalogue. The message says what is wrong.</exception>
        public static SatsumaCatalog Parse(string json)
        {
            Dictionary<string, object> file = JsonReader.Object(JsonReader.Parse(json), "The part catalogue");
            double format = JsonReader.Number(file, "format", "The part catalogue");
            if (format != FormatVersion)
            {
                throw new FormatException("A part catalogue of format " + format.ToString(CultureInfo.InvariantCulture) + "; this build reads format " + FormatVersion + ".");
            }

            var catalogue = new SatsumaCatalog();
            catalogue.Car = JsonReader.String(file, "car", "The part catalogue");
            if (catalogue.Car.Length == 0)
            {
                throw new FormatException("The part catalogue needs the car's name.");
            }

            List<object> items = JsonReader.Array(file.ContainsKey("entries") ? file["entries"] : null, "The part catalogue's \"entries\"");
            var entries = new List<PartEntry>();
            for (int i = 0; i < items.Count; i++)
            {
                PartEntry entry = ParseEntry(JsonReader.Object(items[i], "Part entry " + (i + 1)), i + 1);
                if (catalogue.byName.ContainsKey(entry.Name))
                {
                    throw new FormatException("The part catalogue has two entries called '" + entry.Name + "'.");
                }

                catalogue.byName[entry.Name] = entry;
                entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                throw new FormatException("The part catalogue has no entries.");
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].Index = i;
            }

            catalogue.Entries = entries.ToArray();
            for (int i = 0; i < entries.Count; i++)
            {
                catalogue.Check(entries[i]);
                catalogue.BoltCount += entries[i].Bolts.Length;
            }

            catalogue.Shape = ShapeOf(catalogue.Entries);
            return catalogue;
        }

        // Everything an entry refers to must exist.
        private void Check(PartEntry entry)
        {
            string what = "The part entry '" + entry.Name + "'";
            for (int i = 0; i < entry.Triggers.Length; i++)
            {
                CheckPlace(entry.Triggers[i].Place, what);
                CheckRequirements(entry.Triggers[i].Requires, what);
            }

            CheckRequirements(entry.BlockedBy, what);
            CheckPlaces(entry.Copies, what);
            CheckPlaces(entry.Removals, what);
            for (int i = 0; i < entry.BoltChecks.Length; i++)
            {
                CheckPlace(entry.BoltChecks[i].Place, what);
                for (int b = 0; b < entry.BoltChecks[i].Bolts.Length; b++)
                {
                    CheckPlace(entry.BoltChecks[i].Bolts[b].Place, what);
                }
            }

            if (entry.Wiring != null)
            {
                CheckPlaces(entry.Wiring.Ends, what);
                CheckPlaces(entry.Wiring.Activates, what);
            }

            for (int i = 0; i < entry.InstalledBy.Length; i++)
            {
                PartEntry other = Find(entry.InstalledBy[i]);
                if (other == null || other.Wiring == null)
                {
                    throw new FormatException(what + " is installed by '" + entry.InstalledBy[i] + "', which is not a wiring entry of the catalogue.");
                }
            }
        }

        private void CheckRequirements(PartRequirement[] requirements, string what)
        {
            for (int i = 0; i < requirements.Length; i++)
            {
                if (Find(requirements[i].Entry) == null)
                {
                    throw new FormatException(what + " depends on '" + requirements[i].Entry + "', which is not an entry of the catalogue.");
                }
            }
        }

        private void CheckPlaces(PartPlace[] places, string what)
        {
            for (int i = 0; i < places.Length; i++)
            {
                CheckPlace(places[i], what);
            }
        }

        private void CheckPlace(PartPlace place, string what)
        {
            if (place.Kind == PartPlaceKind.Part && Find(place.Entry) == null)
            {
                throw new FormatException(what + " has the place '" + place.Text + "', below the part of an entry the catalogue does not have.");
            }
        }

        private static PartEntry ParseEntry(Dictionary<string, object> item, int number)
        {
            var entry = new PartEntry();
            entry.Name = JsonReader.String(item, "name", "Part entry " + number);
            string what = "The part entry '" + entry.Name + "'";
            if (entry.Name.Length == 0 || entry.Name.IndexOf(':') >= 0 || entry.Name == "wheel")
            {
                throw new FormatException("Part entry " + number + " needs a name without ':'.");
            }

            entry.Group = JsonReader.String(item, "group", what);
            entry.Family = ParseFamily(JsonReader.String(item, "family", what), what);
            entry.Data = Texts(item, "data", what);
            for (int i = 0; i < entry.Data.Length; i++)
            {
                if (Array.IndexOf(Flags, entry.Data[i]) < 0)
                {
                    throw new FormatException(what + " has the unknown flag '" + entry.Data[i] + "'.");
                }
            }

            entry.Part = Optional(item, "part", what);
            entry.PartVariable = Optional(item, "partVariable", what);
            entry.Spawn = Optional(item, "spawn", what);
            entry.Copies = Places(item, "copies", what);
            entry.Removals = Places(item, "removals", what);
            entry.InstalledBy = Texts(item, "installedBy", what);

            var triggers = new List<PartTrigger>();
            foreach (Dictionary<string, object> trigger in Objects(item, "triggers", what))
            {
                triggers.Add(ParseTrigger(trigger, what));
            }

            // In the order the generator writes them, whatever order the file has, so that a trigger's position in its
            // entry names it the same way on every machine.
            triggers.Sort((a, b) =>
            {
                int order = string.CompareOrdinal(a.Place.Text, b.Place.Text);
                order = order != 0 ? order : string.CompareOrdinal(a.State, b.State);
                return order != 0 ? order : string.CompareOrdinal(a.Part ?? string.Empty, b.Part ?? string.Empty);
            });
            entry.Triggers = triggers.ToArray();

            var blockers = new List<PartRequirement>();
            foreach (Dictionary<string, object> blocker in Objects(item, "blockedBy", what))
            {
                blockers.Add(new PartRequirement
                {
                    Entry = JsonReader.String(blocker, "entry", what + "'s \"blockedBy\""),
                    Variable = JsonReader.String(blocker, "variable", what + "'s \"blockedBy\""),
                    Value = true,
                });
            }

            entry.BlockedBy = blockers.ToArray();

            var checks = new List<PartBoltCheck>();
            foreach (Dictionary<string, object> check in Objects(item, "boltChecks", what))
            {
                checks.Add(ParseCheck(check, what));
            }

            checks.Sort((a, b) => string.CompareOrdinal(a.Place.Text, b.Place.Text));
            entry.BoltChecks = checks.ToArray();
            var bolts = new List<PartBolt>();
            var keys = new Dictionary<string, bool>();
            for (int c = 0; c < checks.Count; c++)
            {
                for (int b = 0; b < checks[c].Bolts.Length; b++)
                {
                    PartBolt bolt = checks[c].Bolts[b];
                    string key = bolt.Index.ToString(CultureInfo.InvariantCulture) + "@" + bolt.Place.Text;
                    if (keys.ContainsKey(key))
                    {
                        throw new FormatException(what + " has two bolts with the index " + bolt.Index + " at '" + bolt.Place.Text + "'.");
                    }

                    keys[key] = true;
                    bolt.Slot = bolts.Count;
                    bolts.Add(bolt);
                }
            }

            entry.Bolts = bolts.ToArray();

            object wiring;
            if (item.TryGetValue("wiring", out wiring) && wiring != null)
            {
                Dictionary<string, object> wire = JsonReader.Object(wiring, what + "'s \"wiring\"");
                entry.Wiring = new PartWiring
                {
                    Ends = Places(wire, "ends", what + "'s wire"),
                    State = JsonReader.String(wire, "state", what + "'s wire"),
                    Activates = Places(wire, "activates", what + "'s wire"),
                };
                if (entry.Wiring.Ends.Length != 2 || entry.Wiring.State.Length == 0)
                {
                    throw new FormatException(what + "'s wire needs two ends and the state that makes it.");
                }
            }

            return entry;
        }

        private static PartTrigger ParseTrigger(Dictionary<string, object> item, string entry)
        {
            string what = entry + "'s trigger";
            var trigger = new PartTrigger
            {
                Place = PartPlace.Parse(JsonReader.String(item, "path", what), what),
                Part = Optional(item, "part", what),
                State = JsonReader.String(item, "state", what),
                AssembleEvent = Flag(item, "event", what),
                Setup = Optional(item, "setup", what),
                Sound = Flag(item, "sound", what),
                Family = ParseFamily(JsonReader.String(item, "family", what), what),
                Unloaded = Flag(item, "unloaded", what),
                Shared = Flag(item, "shared", what),
            };
            if (trigger.State.Length == 0)
            {
                throw new FormatException(what + " at '" + trigger.Place.Text + "' needs the state to enter.");
            }

            var requires = new List<PartRequirement>();
            foreach (Dictionary<string, object> requirement in Objects(item, "requires", what))
            {
                object value;
                if (!requirement.TryGetValue("value", out value) || !(value is bool))
                {
                    throw new FormatException(what + " at '" + trigger.Place.Text + "' has a requirement without a true or false \"value\".");
                }

                requires.Add(new PartRequirement
                {
                    Entry = JsonReader.String(requirement, "entry", what),
                    Variable = JsonReader.String(requirement, "variable", what),
                    Value = (bool)value,
                });
            }

            trigger.Requires = requires.ToArray();
            return trigger;
        }

        private static PartBoltCheck ParseCheck(Dictionary<string, object> item, string entry)
        {
            string what = entry + "'s bolt check";
            var check = new PartBoltCheck
            {
                Place = PartPlace.Parse(JsonReader.String(item, "path", what), what),
                Machine = Optional(item, "fsm", what) ?? "BoltCheck",
                HasThresholds = item.ContainsKey("yes") && item["yes"] != null,
            };
            if (check.HasThresholds)
            {
                check.Yes = (float)JsonReader.Number(item, "yes", what);
                check.No = item.ContainsKey("no") ? (float)JsonReader.Number(item, "no", what) : 0f;
            }

            var bolts = new List<PartBolt>();
            foreach (Dictionary<string, object> b in Objects(item, "bolts", what))
            {
                var bolt = new PartBolt
                {
                    Index = (int)JsonReader.Number(b, "index", what),
                    Place = PartPlace.Parse(JsonReader.String(b, "path", what), what),
                    Size = (float)JsonReader.Number(b, "size", what),
                    Stages = (int)JsonReader.Number(b, "stages", what),
                    Check = check,
                };
                if (bolt.Index < 0 || bolt.Index > 9 || bolt.Stages < 1 || bolt.Stages > 8 || !(bolt.Size > 0f))
                {
                    throw new FormatException(what + " has a bolt at '" + bolt.Place.Text + "' with an index outside 0 to 9, stages outside 1 to 8 or no size.");
                }

                bolts.Add(bolt);
            }

            bolts.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : string.CompareOrdinal(a.Place.Text, b.Place.Text));
            check.Bolts = bolts.ToArray();
            return check;
        }

        private static readonly string[] Flags = { "Installed", "Bolted", "Damaged", "Purchased", "InHoist" };

        private static readonly string[] FamilyNames = { "A", "B1", "B2", "wheel", "wiring", "status", "hoist", "other" };

        private static PartFamily ParseFamily(string text, string what)
        {
            int index = Array.IndexOf(FamilyNames, text);
            if (index < 0)
            {
                throw new FormatException(what + " has the unknown family '" + text + "'.");
            }

            return (PartFamily)index;
        }

        /// <summary>How the catalogue writes a family: "A", "B1", "B2", "wheel"…</summary>
        public static string NameOf(PartFamily family)
        {
            return FamilyNames[(int)family];
        }

        private static uint ShapeOf(PartEntry[] entries)
        {
            uint shape = 2166136261;
            for (int i = 0; i < entries.Length; i++)
            {
                PartEntry entry = entries[i];
                shape = Mix(shape, entry.Name);
                shape = Mix(shape, NameOf(entry.Family));
                for (int t = 0; t < entry.Triggers.Length; t++)
                {
                    PartTrigger trigger = entry.Triggers[t];
                    shape = Mix(shape, "t " + trigger.Place.Text + "|" + trigger.State + "|" + trigger.Part);
                }

                for (int b = 0; b < entry.Bolts.Length; b++)
                {
                    shape = Mix(shape, "b " + entry.Bolts[b].Index.ToString(CultureInfo.InvariantCulture) + "|" + entry.Bolts[b].Place.Text);
                }

                if (entry.Wiring != null)
                {
                    shape = Mix(shape, "w " + entry.Wiring.Ends[0].Text + "|" + entry.Wiring.Ends[1].Text + "|" + entry.Wiring.State);
                }
            }

            return shape;
        }

        private static uint Mix(uint shape, string text)
        {
            unchecked
            {
                for (int c = 0; c < text.Length; c++)
                {
                    shape = (shape ^ text[c]) * 16777619;
                }

                return (shape ^ '\n') * 16777619;
            }
        }

        private static IEnumerable<object> Objects(Dictionary<string, object> parent, string name, string what)
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

        private static PartPlace[] Places(Dictionary<string, object> parent, string name, string what)
        {
            string[] texts = Texts(parent, name, what);
            var places = new PartPlace[texts.Length];
            for (int i = 0; i < texts.Length; i++)
            {
                places[i] = PartPlace.Parse(texts[i], what + "'s \"" + name + "\"");
            }

            return places;
        }

        private static string[] Texts(Dictionary<string, object> parent, string name, string what)
        {
            object value;
            if (!parent.TryGetValue(name, out value) || value == null)
            {
                return new string[0];
            }

            List<object> items = JsonReader.Array(value, what + "'s \"" + name + "\"");
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

        private static bool Flag(Dictionary<string, object> parent, string name, string what)
        {
            object value;
            if (!parent.TryGetValue(name, out value) || value == null)
            {
                return false;
            }

            if (!(value is bool))
            {
                throw new FormatException(what + " has a \"" + name + "\" that is not true or false.");
            }

            return (bool)value;
        }
    }
}
