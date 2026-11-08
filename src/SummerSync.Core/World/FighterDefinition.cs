using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Json;

namespace SummerSync.Core.World
{
    /// <summary>A machine of the game, by its object's path, and one of its states.</summary>
    public sealed class FighterMachine
    {
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        public string State { get; internal set; }
    }

    /// <summary>
    /// A man who picks a fight with the player near him (the pub's, the dance hall's), from <c>data/fighters/*.json</c>.
    ///
    /// The game of one player runs him: the nearest one, kept while he fights (2026-10-18;
    /// <see cref="FighterOwnership"/>). His machines chase that game's own player and hit it, as in the game. The other
    /// games pause his machines and show him where that game has him, with its clips and its sounds; a punch of their
    /// player on him is told to that game, which counts it as its own. Whether he is out at all is said by the host's game
    /// (<see cref="DecidedByHost"/>: the pub's), or by the game that runs him.
    /// </summary>
    public sealed class FighterDefinition
    {
        public const int FormatVersion = 1;

        /// <summary>Most clips this build shows of him (<see cref="FighterPose"/> packs one byte each).</summary>
        public const int MaxAnimations = 4;

        /// <summary>Most objects whose being on (<see cref="Shown"/>) or loose (<see cref="Loose"/>) is shown.</summary>
        public const int MaxFlags = 8;

        public string Name { get; private set; }

        /// <summary>Where he is, from the scene's top: the first that exists (the game moves the pub's to another parent).</summary>
        public string[] Paths { get; private set; }

        /// <summary>What is switched on and off with him: empty for himself, or a path from the scene's top (a parent).</summary>
        public string Switch { get; private set; }

        /// <summary>
        /// Optional, a path from the scene's top: what he hangs from when out. The game that calls him out moves him there
        /// (the pub's, to <c>HUMANS</c>), and his machines put him on the ground in that parent's space; a game that
        /// switches him on because another says so moves him there too. Empty: he stays where he is.
        /// </summary>
        public string OutParent { get; private set; }

        /// <summary>True: the host's game says whether he is out (the others pause what decides it). False: the game that runs him.</summary>
        public bool DecidedByHost { get; private set; }

        /// <summary>His machine that decides when he fights.</summary>
        public string Brain { get; private set; }

        /// <summary>Its states in which he does not fight: only then can another game take him over.</summary>
        public string[] Calm { get; private set; }

        /// <summary>The state of <see cref="Brain"/> a game that takes him over puts him in, calm.</summary>
        public string CalmEntry { get; private set; }

        /// <summary>Paths below him whose machines go on in every game (his ragdoll, which each game lets fall).</summary>
        public string[] KeepBelow { get; private set; }

        /// <summary>Names of machines below him that go on in every game (what drops his hat when its joint breaks).</summary>
        public string[] KeepMachines { get; private set; }

        /// <summary>Paths below him of the animations whose clip is shown (the body, each arm).</summary>
        public string[] Animations { get; private set; }

        /// <summary>Paths below him of objects shown on or off as in the game that runs him (the knife).</summary>
        public string[] Shown { get; private set; }

        /// <summary>Paths below him of things held by a joint (his hat, his glasses): when it breaks there, it breaks in the others.</summary>
        public string[] Loose { get; private set; }

        /// <summary>The machines his body is punched on, with the state a punch takes them to (where it is counted).</summary>
        public FighterMachine[] Hits { get; private set; }

        /// <summary>The name of the <c>GameObject</c> variable of the <see cref="Hits"/> machines that holds the player's fist.</summary>
        public string Fist { get; private set; }

        /// <summary>The state that knocks him down for good (his ragdoll, he is gone).</summary>
        public FighterMachine Down { get; private set; }

        /// <summary>
        /// Optional: a state, from the scene's top, that calls him out (sends <see cref="CallTo"/> its event). In a guest it
        /// is told to the host, whose game does it: the pub's bar, which only the player's own game sees open.
        /// </summary>
        public FighterMachine Call { get; private set; }

        /// <summary>The machine <see cref="Call"/> sends <see cref="CallEvent"/> to, from the scene's top.</summary>
        public FighterMachine CallTo { get; private set; }

        public string CallEvent { get; private set; }

        /// <summary>
        /// Optional: the player's machine he hits, from the scene's top (its sounds are told too, where he is), and its
        /// <c>GameObject</c> variable that says which fighter hits.
        /// </summary>
        public FighterMachine Struck { get; private set; }

        public string StruckBy { get; private set; }

        /// <summary>Where, below him, the sounds of <see cref="Struck"/> are played in the other games: his fist.</summary>
        public string StruckAt { get; private set; }

        /// <exception cref="FormatException">The text is not a valid fighter file. The message says what is wrong.</exception>
        public static FighterDefinition Parse(string json)
        {
            Dictionary<string, object> file = JsonReader.Object(JsonReader.Parse(json), "A fighter file");
            double format = JsonReader.Number(file, "format", "A fighter file");
            if (format != FormatVersion)
            {
                throw new FormatException("A fighter file of format " + format.ToString(CultureInfo.InvariantCulture) + "; this build reads format " + FormatVersion + ".");
            }

            var definition = new FighterDefinition();
            definition.Name = JsonReader.String(file, "name", "A fighter file");
            string what = "The fighter '" + definition.Name + "'";
            if (definition.Name.Length == 0 || definition.Name.IndexOf('/') >= 0)
            {
                throw new FormatException(what + " needs a name without '/'.");
            }

            definition.Paths = Texts(file, "paths", what, true);
            definition.Switch = file.ContainsKey("switch") ? JsonReader.String(file, "switch", what) : string.Empty;
            definition.OutParent = file.ContainsKey("outParent") ? JsonReader.String(file, "outParent", what) : string.Empty;
            string decides = JsonReader.String(file, "decidedBy", what);
            if (decides != "host" && decides != "runner")
            {
                throw new FormatException(what + "'s \"decidedBy\" is \"host\" or \"runner\", not \"" + decides + "\".");
            }

            definition.DecidedByHost = decides == "host";
            definition.Brain = JsonReader.String(file, "brain", what);
            definition.Calm = Texts(file, "calm", what, true);
            definition.CalmEntry = JsonReader.String(file, "calmEntry", what);
            if (definition.Brain.Length == 0 || Array.IndexOf(definition.Calm, definition.CalmEntry) < 0)
            {
                throw new FormatException(what + " needs a \"brain\" machine and a \"calmEntry\" among its \"calm\" states.");
            }

            definition.KeepBelow = Texts(file, "keepBelow", what, false);
            definition.KeepMachines = Texts(file, "keepMachines", what, false);
            definition.Animations = Texts(file, "animations", what, false);
            definition.Shown = Texts(file, "shown", what, false);
            definition.Loose = Texts(file, "loose", what, false);
            if (definition.Animations.Length > MaxAnimations || definition.Shown.Length + definition.Loose.Length > MaxFlags)
            {
                throw new FormatException(what + " has more than " + MaxAnimations + " animations, or more than " + MaxFlags + " shown and loose things.");
            }

            List<object> hits = JsonReader.Array(Value(file, "hits", what), what + "'s \"hits\"");
            if (hits.Count == 0)
            {
                throw new FormatException(what + " needs the machines his body is punched on (\"hits\").");
            }

            definition.Hits = new FighterMachine[hits.Count];
            for (int i = 0; i < hits.Count; i++)
            {
                definition.Hits[i] = Machine(hits[i], what + "'s hit " + (i + 1), true);
            }

            definition.Fist = JsonReader.String(file, "fist", what);
            definition.Down = Machine(Value(file, "down", what), what + "'s \"down\"", true);
            if (file.ContainsKey("call"))
            {
                Dictionary<string, object> call = JsonReader.Object(file["call"], what + "'s \"call\"");
                definition.Call = Machine(Value(call, "from", what + "'s \"call\""), what + "'s call", true);
                definition.CallTo = Machine(Value(call, "to", what + "'s \"call\""), what + "'s call", false);
                definition.CallEvent = JsonReader.String(call, "event", what + "'s \"call\"");
                if (definition.CallEvent.Length == 0)
                {
                    throw new FormatException(what + "'s \"call\" needs its event.");
                }
            }

            if (file.ContainsKey("struck"))
            {
                Dictionary<string, object> struck = JsonReader.Object(file["struck"], what + "'s \"struck\"");
                definition.Struck = Machine(struck, what + "'s \"struck\"", false);
                definition.StruckBy = JsonReader.String(struck, "by", what + "'s \"struck\"");
                definition.StruckAt = JsonReader.String(struck, "at", what + "'s \"struck\"");
                if (definition.StruckBy.Length == 0)
                {
                    throw new FormatException(what + "'s \"struck\" needs the variable that says who hits (\"by\").");
                }
            }

            if (definition.Fist.Length == 0 || definition.CalmEntry.Length == 0)
            {
                throw new FormatException(what + " has an empty variable or state name.");
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

        private static FighterMachine Machine(object value, string what, bool state)
        {
            Dictionary<string, object> entry = JsonReader.Object(value, what);
            var machine = new FighterMachine
            {
                Path = JsonReader.String(entry, "path", what),
                Machine = JsonReader.String(entry, "fsm", what),
                State = state ? JsonReader.String(entry, "state", what) : null,
            };
            if (machine.Machine.Length == 0 || (state && machine.State.Length == 0))
            {
                throw new FormatException(what + " needs a machine (\"fsm\")" + (state ? " and its state." : "."));
            }

            return machine;
        }

        private static string[] Texts(Dictionary<string, object> file, string name, string what, bool needed)
        {
            if (!file.ContainsKey(name))
            {
                if (needed)
                {
                    throw new FormatException(what + " needs \"" + name + "\".");
                }

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

            if (needed && result.Length == 0)
            {
                throw new FormatException(what + "'s \"" + name + "\" is empty.");
            }

            return result;
        }
    }
}
