using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Json;

namespace SummerSync.Core.World
{
    /// <summary>
    /// A step of the game's own that mounts a thing or takes it off: a state machine taken to a state, or sent an event
    /// (after one of its object variables is given the thing).
    /// </summary>
    public sealed class MountStep
    {
        /// <summary>
        /// The object with the machine, from the scene's top (it starts with '/'), or below an object of a name anywhere (it starts
        /// with '*': "*cd player(Clone)/trigger_disc", a part that hangs from wherever it is installed); null for the thing itself.
        /// </summary>
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>The state to take the machine to; null when an event is sent instead.</summary>
        public string State { get; internal set; }

        /// <summary>The event to send it (one of its global transitions); null when it is taken to a state.</summary>
        public string Event { get; internal set; }

        /// <summary>An object variable of the machine that is given the thing first, or null.</summary>
        public string Variable { get; internal set; }

        /// <summary>How near the machine's object the thing has to be for this step, in metres; 0 for anywhere.</summary>
        public float Within { get; internal set; }
    }

    /// <summary>
    /// Something the game mounts a loose thing onto that is not the Satsuma (data/mounts/*.json): the lid on the bucket,
    /// a diskette in the computer, the charger's clamps on a battery. The game where a player mounts it says which thing
    /// is mounted; the others mount theirs the game's own way (<see cref="Mount"/>) and take it off the same way.
    /// </summary>
    public sealed class MountDefinition
    {
        public const int FormatVersion = 1;

        /// <summary>What the log calls it, and the name its state travels by ("mount/" and this).</summary>
        public string Name { get; private set; }

        /// <summary>The name of the loose things that are mounted ("diskette(itemx)").</summary>
        public string Part { get; private set; }

        /// <summary>
        /// Where the thing sits once mounted, from the scene's top or below an object of a name anywhere (as
        /// <see cref="MountStep.Path"/>): it is a child of that. Null with <see cref="Onto"/>.
        /// </summary>
        public string Into { get; private set; }

        /// <summary>
        /// Or what the game puts onto the thing, from the scene's top: the thing is mounted while that is its child (the
        /// charger's clamps on a battery). The thing stays loose. Null with <see cref="Into"/>.
        /// </summary>
        public string Onto { get; private set; }

        public MountStep Mount { get; private set; }

        /// <summary>How it comes off; null when the game takes it off by itself (the charger, once the battery is moved).</summary>
        public MountStep Unmount { get; private set; }

        /// <summary>The name the state travels by.</summary>
        public string Key
        {
            get { return "mount/" + Name; }
        }

        /// <exception cref="FormatException">The text is not a valid mount file. The message says what is wrong.</exception>
        public static MountDefinition Parse(string json)
        {
            Dictionary<string, object> file = JsonReader.Object(JsonReader.Parse(json), "A mount file");
            double format = JsonReader.Number(file, "format", "A mount file");
            if (format != FormatVersion)
            {
                throw new FormatException("A mount file of format " + format.ToString(CultureInfo.InvariantCulture) + "; this build reads format " + FormatVersion + ".");
            }

            var definition = new MountDefinition();
            definition.Name = JsonReader.String(file, "name", "A mount file");
            string what = "The mount '" + definition.Name + "'";
            if (definition.Name.Length == 0 || definition.Name.IndexOf('/') >= 0)
            {
                throw new FormatException(what + " needs a name without '/'.");
            }

            definition.Part = JsonReader.String(file, "part", what);
            definition.Into = Optional(file, "into", what);
            definition.Onto = Optional(file, "onto", what);
            if ((definition.Into == null) == (definition.Onto == null))
            {
                throw new FormatException(what + " says either \"into\" (where the thing sits) or \"onto\" (what goes onto it).");
            }

            CheckScenePath(definition.Into ?? definition.Onto, what);
            if (!file.ContainsKey("mount"))
            {
                throw new FormatException(what + " needs a \"mount\" step.");
            }

            definition.Mount = ParseStep(JsonReader.Object(file["mount"], what + "'s \"mount\""), what + "'s \"mount\"");
            definition.Unmount = file.ContainsKey("unmount") && file["unmount"] != null
                ? ParseStep(JsonReader.Object(file["unmount"], what + "'s \"unmount\""), what + "'s \"unmount\"")
                : null;
            return definition;
        }

        private static MountStep ParseStep(Dictionary<string, object> item, string what)
        {
            var step = new MountStep
            {
                Path = Optional(item, "path", what),
                Machine = JsonReader.String(item, "fsm", what),
                State = Optional(item, "state", what),
                Event = Optional(item, "event", what),
                Variable = Optional(item, "variable", what),
                Within = item.ContainsKey("within") ? (float)JsonReader.Number(item, "within", what) : 0f,
            };

            if (step.Path != null)
            {
                CheckScenePath(step.Path, what);
            }

            if ((step.State == null) == (step.Event == null))
            {
                throw new FormatException(what + " says either a \"state\" or an \"event\".");
            }

            if (step.Within < 0f || step.Within > 100f)
            {
                throw new FormatException(what + " has a \"within\" from 0 to 100 metres.");
            }

            return step;
        }

        private static void CheckScenePath(string path, string what)
        {
            if (path.Length < 2 || (path[0] != '/' && path[0] != '*'))
            {
                throw new FormatException(what + " has the path '" + path + "', which is not from the scene's top ('/…') or below an object of a name ('*name/…').");
            }
        }

        private static string Optional(Dictionary<string, object> item, string name, string what)
        {
            if (!item.ContainsKey(name) || item[name] == null)
            {
                return null;
            }

            string text = JsonReader.String(item, name, what);
            return text.Length == 0 ? null : text;
        }
    }
}
