using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Json;

namespace SummerSync.Core.World
{
    /// <summary>How the game moves one of its people, animals or machines.</summary>
    public enum NpcMotion
    {
        /// <summary>Its state machines write its transform (the train, the moose): where it is shown, so does the mod.</summary>
        Transform,

        /// <summary>Forces move its body (the boats): where it is shown, it is kinematic and moved in the physics steps.</summary>
        Body,

        /// <summary>
        /// A car of the game that drives by itself (the bus, the police): as <see cref="Body"/>, and also its controller and
        /// engine off, its wheels turning, its engine sounding and its brake lights as the host's.
        /// </summary>
        Car,
    }

    /// <summary>A state machine somewhere in the scene, or below an NPC: the object's path and the machine's name.</summary>
    public sealed class NpcMachine
    {
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>A variable of it, or a state to take it to; null when the use needs neither.</summary>
        public string Name { get; internal set; }
    }

    /// <summary>
    /// A state machine below an NPC whose state is the host's: the host's game says which of its listed states it is in,
    /// whenever that changes and to a game that joins, and the others' copies (switched off, so that they do not go on by
    /// themselves) are taken there, the game's way. Teimo's ride to the shop: where he is, and whether he is in the shop.
    /// </summary>
    public sealed class NpcMirror
    {
        /// <summary>At most this many states.</summary>
        public const int MaxStates = 32;

        /// <summary>The object's path below the NPC; empty for the NPC itself.</summary>
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>The states said, by their place in this list: the host's machine in one of them.</summary>
        public string[] States { get; internal set; }

        /// <summary>
        /// For each of <see cref="States"/>, the state the others' copies are taken to: the same one, or one that leads to
        /// it in the same frame and does on the way what it needs (Teimo's "State 2" puts him in the shop, then "At store").
        /// </summary>
        public string[] Enter { get; internal set; }
    }

    /// <summary>
    /// One of the game's people, animals or machines that move by themselves, as a data file of the mod describes it
    /// (<c>data/npcs/*.json</c>): where to find it, how the game moves it, and what of it the host says. The host's game
    /// moves it; the others show it where the host has it.
    /// </summary>
    public sealed class NpcDefinition
    {
        public const int FormatVersion = 1;

        /// <summary>At most this many children shown or hidden as the host has them: one bit each.</summary>
        public const int MaxShown = 8;

        /// <summary>For a car, the bit that says its brake lights are on: one fewer child can be shown.</summary>
        public const byte BrakeBit = 0x80;

        /// <summary>At most this many of one name.</summary>
        public const int MaxCount = 8;

        /// <summary>What the log calls it, and the start of the name it travels by: "train".</summary>
        public string Name { get; private set; }

        /// <summary>The scene's top object it is found under: "TRAIN".</summary>
        public string Root { get; private set; }

        /// <summary>
        /// Another top object to look under when it is not under <see cref="Root"/> (a friends' car the game has moved to
        /// the drag strip); null if none. Only with <see cref="Find"/>.
        /// </summary>
        public string AlsoRoot { get; private set; }

        /// <summary>Its path below <see cref="Root"/>, or null when it is found by <see cref="Find"/>.</summary>
        public string Path { get; private set; }

        /// <summary>
        /// The name of the object, anywhere below <see cref="Root"/>, when its path changes while the game runs (the
        /// train is moved from one parent to the other); null when it is found by <see cref="Path"/>.
        /// </summary>
        public string Find { get; private set; }

        /// <summary>How many there are by that name (two moose): each is one, in the order the scene has them.</summary>
        public int Count { get; private set; }

        public NpcMotion Motion { get; private set; }

        /// <summary>The state machines below it that move it, switched off where it is only shown.</summary>
        public string[] Off { get; private set; }

        /// <summary>State machines elsewhere in the scene that switch it on and off, switched off where it is only shown.</summary>
        public NpcMachine[] OffElsewhere { get; private set; }

        /// <summary>Children, by path below it, that are on and off as the host's are.</summary>
        public string[] Shown { get; private set; }

        /// <summary>The float variable (below it) that its engine sound reads, written as the host's; null if none.</summary>
        public NpcMachine Rpm { get; private set; }

        /// <summary>The path below it of the animation whose clip plays as the host's; null if none.</summary>
        public string Animation { get; private set; }

        /// <summary>
        /// When it is destroyed in one game (a moose run over), the state (below it) that the others' copies are taken
        /// to, the game's way; null if it is not.
        /// </summary>
        public NpcMachine Gone { get; private set; }

        /// <summary>
        /// States (below it) that its copies go to, the game's way, when the host's goes there: the crash that kills the
        /// driver of a car the mod moves where it is shown (where it cannot crash). Empty if none.
        /// </summary>
        public NpcMachine[] Follows { get; private set; }

        /// <summary>A state machine of it whose state is the host's (<see cref="NpcMirror"/>); null if none.</summary>
        public NpcMirror Mirror { get; private set; }

        /// <summary>The names it travels by, one for each of <see cref="Count"/>: "moose#0", "moose#1"; or just "train".</summary>
        public string[] Keys
        {
            get
            {
                var keys = new string[Count];
                for (int i = 0; i < Count; i++)
                {
                    keys[i] = Count == 1 ? Name : Name + "#" + i.ToString(CultureInfo.InvariantCulture);
                }

                return keys;
            }
        }

        /// <exception cref="FormatException">The text is not a valid NPC file. The message says what is wrong.</exception>
        public static NpcDefinition Parse(string json)
        {
            Dictionary<string, object> file = JsonReader.Object(JsonReader.Parse(json), "An NPC file");
            double format = JsonReader.Number(file, "format", "An NPC file");
            if (format != FormatVersion)
            {
                throw new FormatException("An NPC file of format " + format.ToString(CultureInfo.InvariantCulture) + "; this build reads format " + FormatVersion + ".");
            }

            var definition = new NpcDefinition();
            definition.Name = JsonReader.String(file, "name", "An NPC file");
            string what = "The NPC '" + definition.Name + "'";
            if (definition.Name.Length == 0 || definition.Name.IndexOf('#') >= 0 || definition.Name.IndexOf('/') >= 0)
            {
                throw new FormatException(what + " needs a name without '#' or '/'.");
            }

            definition.Root = JsonReader.String(file, "root", what);
            definition.Path = file.ContainsKey("path") ? JsonReader.String(file, "path", what) : null;
            definition.Find = file.ContainsKey("find") ? JsonReader.String(file, "find", what) : null;
            if ((definition.Path == null) == (definition.Find == null))
            {
                throw new FormatException(what + " needs either a \"path\" or a \"find\".");
            }

            definition.AlsoRoot = file.ContainsKey("alsoRoot") ? JsonReader.String(file, "alsoRoot", what) : null;
            if (definition.AlsoRoot != null && definition.Find == null)
            {
                throw new FormatException(what + " has an \"alsoRoot\" only with \"find\".");
            }

            definition.Count = file.ContainsKey("count") ? (int)JsonReader.Number(file, "count", what) : 1;
            if (definition.Count < 1 || definition.Count > MaxCount || (definition.Count > 1 && definition.Find == null))
            {
                throw new FormatException(what + " has a \"count\" from 1 to " + MaxCount + ", and more than one only with \"find\".");
            }

            string motion = JsonReader.String(file, "moves", what);
            if (motion == "transform")
            {
                definition.Motion = NpcMotion.Transform;
            }
            else if (motion == "body")
            {
                definition.Motion = NpcMotion.Body;
            }
            else if (motion == "car")
            {
                definition.Motion = NpcMotion.Car;
            }
            else
            {
                throw new FormatException(what + " moves by \"transform\", \"body\" or \"car\", not \"" + motion + "\".");
            }

            definition.Off = Texts(file, "off", what);
            definition.Shown = Texts(file, "shown", what);
            int shown = definition.Motion == NpcMotion.Car ? MaxShown - 1 : MaxShown;
            if (definition.Shown.Length > shown)
            {
                throw new FormatException(what + " shows at most " + shown + " children.");
            }

            definition.OffElsewhere = Machines(file, "offElsewhere", what, null);
            definition.Rpm = file.ContainsKey("rpm") ? Machine(file["rpm"], what + "'s \"rpm\"", "variable") : null;
            definition.Animation = file.ContainsKey("animation") ? JsonReader.String(file, "animation", what) : null;
            definition.Gone = file.ContainsKey("gone") ? Machine(file["gone"], what + "'s \"gone\"", "state") : null;
            definition.Follows = Machines(file, "follows", what, "state");
            definition.Mirror = file.ContainsKey("mirror") ? MirrorOf(file["mirror"], what + "'s \"mirror\"") : null;
            return definition;
        }

        // "mirror": { "path": "...", "fsm": "Move", "states": ["At home", { "state": "At store", "enter": "State 2" }] }
        private static NpcMirror MirrorOf(object value, string what)
        {
            Dictionary<string, object> entry = JsonReader.Object(value, what);
            List<object> items = JsonReader.Array(entry.ContainsKey("states") ? entry["states"] : null, what + "'s \"states\"");
            if (items.Count == 0 || items.Count > NpcMirror.MaxStates)
            {
                throw new FormatException(what + " needs from 1 to " + NpcMirror.MaxStates + " states.");
            }

            var mirror = new NpcMirror
            {
                Path = entry.ContainsKey("path") ? JsonReader.String(entry, "path", what) : string.Empty,
                Machine = JsonReader.String(entry, "fsm", what),
                States = new string[items.Count],
                Enter = new string[items.Count],
            };
            for (int i = 0; i < items.Count; i++)
            {
                string place = what + "'s state " + (i + 1);
                var name = items[i] as string;
                if (name != null)
                {
                    mirror.States[i] = name;
                    mirror.Enter[i] = name;
                }
                else
                {
                    Dictionary<string, object> state = JsonReader.Object(items[i], place);
                    mirror.States[i] = JsonReader.String(state, "state", place);
                    mirror.Enter[i] = state.ContainsKey("enter") ? JsonReader.String(state, "enter", place) : mirror.States[i];
                }

                if (mirror.States[i].Length == 0 || mirror.Enter[i].Length == 0 || Array.IndexOf(mirror.States, mirror.States[i]) < i)
                {
                    throw new FormatException(place + " needs a name, and each state once.");
                }
            }

            return mirror;
        }

        /// <summary>
        /// Every name the NPCs travel by, in the order both sides list them, and the list's shape: a message of
        /// another shape is from a build that lists them differently.
        /// </summary>
        public static string[] List(IList<NpcDefinition> definitions, out uint shape)
        {
            var keys = new List<string>();
            for (int i = 0; i < definitions.Count; i++)
            {
                keys.AddRange(definitions[i].Keys);
            }

            keys.Sort(string.CompareOrdinal);
            shape = 2166136261;
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                for (int c = 0; c < key.Length; c++)
                {
                    shape = (shape ^ key[c]) * 16777619;
                }

                shape = (shape ^ '\n') * 16777619;
            }

            return keys.ToArray();
        }

        private static string[] Texts(Dictionary<string, object> file, string name, string what)
        {
            if (!file.ContainsKey(name))
            {
                return new string[0];
            }

            List<object> items = JsonReader.Array(file[name], what + "'s \"" + name + "\"");
            var result = new string[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                result[i] = items[i] as string;
                if (string.IsNullOrEmpty(result[i]))
                {
                    throw new FormatException(what + "'s \"" + name + "\" must be a list of names.");
                }
            }

            return result;
        }

        private static NpcMachine[] Machines(Dictionary<string, object> file, string name, string what, string needs)
        {
            if (!file.ContainsKey(name))
            {
                return new NpcMachine[0];
            }

            List<object> items = JsonReader.Array(file[name], what + "'s \"" + name + "\"");
            var result = new NpcMachine[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                result[i] = Machine(items[i], what + "'s \"" + name + "\" " + (i + 1), needs);
            }

            return result;
        }

        private static NpcMachine Machine(object value, string what, string needs)
        {
            Dictionary<string, object> entry = JsonReader.Object(value, what);
            var machine = new NpcMachine
            {
                Path = entry.ContainsKey("path") ? JsonReader.String(entry, "path", what) : string.Empty,
                Machine = JsonReader.String(entry, "fsm", what),
                Name = needs == null ? null : JsonReader.String(entry, needs, what),
            };
            return machine;
        }
    }
}
