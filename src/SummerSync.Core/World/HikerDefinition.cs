using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Json;

namespace SummerSync.Core.World
{
    /// <summary>A state machine of the game: the object's path from the scene's top and the machine's name.</summary>
    public sealed class HikerMachine
    {
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>A variable of it; null when the use needs none.</summary>
        public string Variable { get; internal set; }
    }

    /// <summary>The clip of getting in, for a vehicle (by its root's name) or for any (null).</summary>
    public sealed class HikerClip
    {
        public string Vehicle { get; internal set; }

        public string Clip { get; internal set; }

        /// <summary>
        /// The child of that vehicle they sit at when it is not the one named like <see cref="HikerDefinition.Pivot"/> (Suski
        /// sits at the Ferndale's "GetInPivotSuski"), or null.
        /// </summary>
        public string Pivot { get; internal set; }
    }

    /// <summary>
    /// One of the game's hitchhikers, as a data file of the mod describes it (<c>data/hikers/*.json</c>): the machine that
    /// runs them, where they walk, and the states that matter between games. The game of the player who picks them up
    /// runs the ride; the others show them sitting in that vehicle and go on from where they get out.
    /// </summary>
    public sealed class HikerDefinition
    {
        public const int FormatVersion = 1;

        /// <summary>What the log calls them, and the tally's name: "hiker/(name)".</summary>
        public string Name { get; private set; }

        /// <summary>Their story's machine (Jokke's "Logic").</summary>
        public HikerMachine Logic { get; private set; }

        /// <summary>The object that walks them around.</summary>
        public string Walker { get; private set; }

        /// <summary>The object that shows them (on and off).</summary>
        public string Body { get; private set; }

        /// <summary>
        /// The object whose clips get them in a vehicle (the moving Jokke's are on his "Pivot"); null when they are on the
        /// object of <see cref="Logic"/>.
        /// </summary>
        public string Animated { get; private set; }

        /// <summary>The other machines that move them, paused with <see cref="Logic"/> while another game runs the ride.</summary>
        public HikerMachine[] Paused { get; private set; }

        /// <summary>
        /// The state the host's game says they appear in (Jokke after his call), and the states of the others' games it is
        /// taken from to there; null when every game makes them appear by itself.
        /// </summary>
        public string Appears { get; private set; }

        public string[] AppearsFrom { get; private set; }

        /// <summary>The state they are in the vehicle in, in the game that picked them up.</summary>
        public string Aboard { get; private set; }

        /// <summary>The state they walk away in after the ride; the others' games are taken there too.</summary>
        public string Off { get; private set; }

        /// <summary>The state they die in; the others' games are taken there too.</summary>
        public string Dies { get; private set; }

        /// <summary>The variable of <see cref="Logic"/> that holds the vehicle's place to sit (its "GetInPivot").</summary>
        public string Pivot { get; private set; }

        /// <summary>The variable of <see cref="Logic"/> that holds the vehicle's mass of a passenger.</summary>
        public string PassengerMass { get; private set; }

        /// <summary>The clip of getting in, by vehicle; the last one without a vehicle is for any other.</summary>
        public HikerClip[] GetIn { get; private set; }

        /// <summary>A number or flag of their story the ride changes, told when they get out (Jokke's "DrunkStage"); or null.</summary>
        public HikerMachine Keep { get; private set; }

        /// <summary>
        /// The machine of the trigger that is hit by cars ("HumanTriggerCrime :: CarHit"), switched on as they get out inside
        /// the vehicle; off for a moment where the ride was only shown. Null if none.
        /// </summary>
        public HikerMachine Hit { get; private set; }

        /// <summary>The clip for a vehicle, or the one for any.</summary>
        public string ClipFor(string vehicle)
        {
            string any = null;
            for (int i = 0; i < GetIn.Length; i++)
            {
                if (GetIn[i].Vehicle == null)
                {
                    any = GetIn[i].Clip;
                }
                else if (GetIn[i].Vehicle == vehicle)
                {
                    return GetIn[i].Clip;
                }
            }

            return any;
        }

        /// <summary>The child of a vehicle (by its root's name) they sit at: the one its clip entry names, or <see cref="Pivot"/>.</summary>
        public string PivotFor(string vehicle)
        {
            for (int i = 0; i < GetIn.Length; i++)
            {
                if (GetIn[i].Vehicle == vehicle && GetIn[i].Pivot != null)
                {
                    return GetIn[i].Pivot;
                }
            }

            return Pivot;
        }

        /// <exception cref="FormatException">The text is not a valid hitchhiker file. The message says what is wrong.</exception>
        public static HikerDefinition Parse(string json)
        {
            Dictionary<string, object> file = JsonReader.Object(JsonReader.Parse(json), "A hitchhiker file");
            double format = JsonReader.Number(file, "format", "A hitchhiker file");
            if (format != FormatVersion)
            {
                throw new FormatException("A hitchhiker file of format " + format.ToString(CultureInfo.InvariantCulture) + "; this build reads format " + FormatVersion + ".");
            }

            var definition = new HikerDefinition();
            definition.Name = JsonReader.String(file, "name", "A hitchhiker file");
            string what = "The hitchhiker '" + definition.Name + "'";
            if (definition.Name.Length == 0 || definition.Name.IndexOf('/') >= 0)
            {
                throw new FormatException(what + " needs a name without '/'.");
            }

            definition.Logic = Machine(Value(file, "logic", what), what + "'s \"logic\"", false);
            definition.Walker = JsonReader.String(file, "walker", what);
            definition.Body = JsonReader.String(file, "body", what);
            definition.Animated = file.ContainsKey("animated") ? JsonReader.String(file, "animated", what) : null;
            if (definition.Animated != null && definition.Animated.Length == 0)
            {
                throw new FormatException(what + "'s \"animated\" is a path, or left out.");
            }

            definition.Aboard = JsonReader.String(file, "aboard", what);
            definition.Off = JsonReader.String(file, "off", what);
            definition.Dies = JsonReader.String(file, "dies", what);
            definition.Pivot = JsonReader.String(file, "pivot", what);
            definition.PassengerMass = JsonReader.String(file, "passengerMass", what);
            definition.Appears = file.ContainsKey("appears") ? JsonReader.String(file, "appears", what) : null;
            definition.AppearsFrom = Texts(file, "appearsFrom", what);
            if ((definition.Appears == null) != (definition.AppearsFrom.Length == 0))
            {
                throw new FormatException(what + " has \"appears\" and \"appearsFrom\" together, or neither.");
            }

            List<object> paused = file.ContainsKey("paused") ? JsonReader.Array(file["paused"], what + "'s \"paused\"") : new List<object>();
            definition.Paused = new HikerMachine[paused.Count];
            for (int i = 0; i < paused.Count; i++)
            {
                definition.Paused[i] = Machine(paused[i], what + "'s \"paused\" " + (i + 1), false);
            }

            List<object> clips = JsonReader.Array(Value(file, "getIn", what), what + "'s \"getIn\"");
            definition.GetIn = new HikerClip[clips.Count];
            for (int i = 0; i < clips.Count; i++)
            {
                string place = what + "'s \"getIn\" " + (i + 1);
                Dictionary<string, object> clip = JsonReader.Object(clips[i], place);
                definition.GetIn[i] = new HikerClip
                {
                    Vehicle = clip.ContainsKey("vehicle") ? JsonReader.String(clip, "vehicle", place) : null,
                    Clip = JsonReader.String(clip, "clip", place),
                    Pivot = clip.ContainsKey("pivot") ? JsonReader.String(clip, "pivot", place) : null,
                };
            }

            if (definition.ClipFor(null) == null)
            {
                throw new FormatException(what + "'s \"getIn\" needs a clip without a vehicle, for any other.");
            }

            definition.Keep = file.ContainsKey("keep") ? Machine(file["keep"], what + "'s \"keep\"", true) : null;
            definition.Hit = file.ContainsKey("hit") ? Machine(file["hit"], what + "'s \"hit\"", false) : null;
            string[] states = { definition.Aboard, definition.Off, definition.Dies, definition.Pivot, definition.PassengerMass };
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].Length == 0)
                {
                    throw new FormatException(what + " has an empty state or variable name.");
                }
            }

            return definition;
        }

        private static object Value(Dictionary<string, object> file, string name, string what)
        {
            if (!file.ContainsKey(name))
            {
                throw new FormatException(what + " needs \"" + name + "\".");
            }

            return file[name];
        }

        private static HikerMachine Machine(object value, string what, bool variable)
        {
            Dictionary<string, object> entry = JsonReader.Object(value, what);
            var machine = new HikerMachine
            {
                Path = JsonReader.String(entry, "path", what),
                Machine = JsonReader.String(entry, "fsm", what),
                Variable = variable ? JsonReader.String(entry, "variable", what) : null,
            };
            if (machine.Path.Length == 0 || machine.Machine.Length == 0 || (variable && machine.Variable.Length == 0))
            {
                throw new FormatException(what + " needs a path, a machine" + (variable ? " and a variable." : "."));
            }

            return machine;
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
    }
}
