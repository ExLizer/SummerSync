using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Json;

namespace SummerSync.Core.World
{
    /// <summary>How a value of a job travels between the games.</summary>
    public enum JobValueKind
    {
        /// <summary>
        /// A quantity the world changes in the host's game (a well that fills) and the players change with what they do
        /// (pumping it): what a client's player did travels as a difference, added in the host's game, and what the host's
        /// game ends up with is everyone's.
        /// </summary>
        Amount,

        /// <summary>A setting or a flag (the man has money for whoever did the job): whoever's game changed it last says it.</summary>
        Said,
    }

    /// <summary>Whose game entering a relayed state is news for the others.</summary>
    public enum JobRelayFrom
    {
        /// <summary>Any game's: the states travel both ways.</summary>
        Any,

        /// <summary>The host's only: the others follow it and say nothing (a machine of the host's world).</summary>
        Host,

        /// <summary>A client's only: the host follows it (what a guest's player did there, done in the host's world too).</summary>
        Clients,
    }

    /// <summary>
    /// A state in which a relayed machine looks at a thing in one of its variables (a bottle in the buyer's stash): if the
    /// thing is another game's to move here (a copy, in that player's hand or on its way), the variable is emptied before
    /// the state's actions run, so that this game does not take it for its own. The game that moves the thing deals with it.
    /// </summary>
    public sealed class JobGuard
    {
        public string State { get; internal set; }

        public string Variable { get; internal set; }
    }

    /// <summary>
    /// A state in which a relayed machine moves a vehicle in one of its variables (the uncle takes his van away): only the
    /// game that simulates the vehicle moves it, and none while a player is in it or by it. Where it is not to be moved, the
    /// variable, and the others named with it (the van's tank), are emptied for the state's actions and put back after.
    /// </summary>
    public sealed class JobSpare
    {
        public string State { get; internal set; }

        /// <summary>The game object variable that holds the vehicle's root, or null if <see cref="Object"/> names it.</summary>
        public string Vehicle { get; internal set; }

        /// <summary>The vehicle's root by its path from the scene's top (a state that names it directly), or null.</summary>
        public string Object { get; internal set; }

        /// <summary>Other game object variables the state changes along with the vehicle (its tank); empty if none.</summary>
        public string[] Also { get; internal set; }

        /// <summary>
        /// The state's own actions (by place, the game's, from 0) that do it: held back where the vehicle is not this game's
        /// to move, instead of emptying the variables. Empty: the variables are emptied.
        /// </summary>
        public int[] Actions { get; internal set; }

        /// <summary>True: done where the vehicle is simulated even with a player in it or near (Fleetari works on it with
        /// its owner waiting by). False: nowhere then.</summary>
        public bool EvenNear { get; internal set; }
    }

    /// <summary>
    /// A state machine of a job that runs in every game, whose states (by name) travel both ways: the game that enters one
    /// by itself (its player delivered the wood, answered the phone) says it, and the others' go there, the game's way.
    /// </summary>
    public sealed class JobRelay
    {
        /// <summary>The most game objects a state carries.</summary>
        public const int MaxObjects = 5;

        /// <summary>The object's path below the job, or from the scene's top if it starts with '/'.</summary>
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        public string[] States { get; internal set; }

        /// <summary>
        /// Game object variables of the machine that hold a shared loose thing (the berry box delivered): they travel with
        /// each state as the thing's shared number, and the others' variables are set to their copy of it before they go
        /// there. Empty if none.
        /// </summary>
        public string[] Objects { get; internal set; }

        /// <summary>
        /// The float variable a state adds to the player's money (the pay in a hand): the others set it to 0 before they go
        /// to the state, so that what the state does besides paying happens in every game and the pay in one. Null if none.
        /// </summary>
        public string Pays { get; internal set; }

        public JobRelayFrom From { get; internal set; }

        /// <summary>Whether the game's subtitle is put back as it was after the others go to a state (a voice for one player).</summary>
        public bool Quiet { get; internal set; }

        /// <summary>
        /// For a machine one player deals with at a time (the person of a job one player deals with: the game of whoever
        /// deals runs them): the states that end a deal. The first game that enters one of <see cref="States"/> by itself
        /// runs the machine until it enters one of these, and the others' are paused and only go where it goes. Empty when
        /// the machine is not dealt.
        /// </summary>
        public string[] DealEnds { get; internal set; }

        public JobGuard[] Guards { get; internal set; }

        public JobSpare[] Spares { get; internal set; }

        public bool IsDeal
        {
            get { return DealEnds.Length > 0; }
        }
    }

    /// <summary>A variable of a job's state machine, or a global one of the game, that every game has the same.</summary>
    public sealed class JobValue
    {
        /// <summary>The object's path below the job, or from the scene's top if it starts with '/' (the farmer who pays).</summary>
        public string Path { get; internal set; }

        /// <summary>The state machine; null for a global variable.</summary>
        public string Machine { get; internal set; }

        /// <summary>The variable's name: the machine's, or the global one (<see cref="Global"/>).</summary>
        public string Variable { get; internal set; }

        /// <summary>Whether it is a global variable of the game (the player's keys), not a machine's.</summary>
        public bool Global { get; internal set; }

        public JobValueKind Kind { get; internal set; }

        /// <summary>For a float: how many travel per unit (1000: thousandths).</summary>
        public double Scale { get; internal set; }
    }

    /// <summary>An object of a job that is switched on and off.</summary>
    public sealed class JobActive
    {
        /// <summary>The object's path below the job, or from the scene's top if it starts with '/'.</summary>
        public string Path { get; internal set; }

        /// <summary>
        /// A state machine of the object, switched on and off instead of the object (the box stand that gives one box at a
        /// time: its machine is switched off while a box is out); null for the object itself.
        /// </summary>
        public string Machine { get; internal set; }

        /// <summary>
        /// False: the world's machines switch it (where the pay for a job is taken), as the host's game has it. True: what a
        /// player does switches it (the pay put down, then taken), whatever the game that switched it last says.
        /// </summary>
        public bool Said { get; internal set; }
    }

    /// <summary>An object of a job that the world's machines move: put where the host's game has it.</summary>
    public sealed class JobPlace
    {
        /// <summary>The object's path below the job.</summary>
        public string Path { get; internal set; }

        /// <summary>
        /// Put there once in a session only, where the host's game had it first (where a hitchhiker turns up at load, by a
        /// draw of each game): after that each game moves it its own way.
        /// </summary>
        public bool Once { get; internal set; }
    }

    /// <summary>
    /// A world machine the host's game holds where it is, while it is in one of <see cref="States"/> and another player is
    /// within <see cref="Within"/> metres of its object: its checks of the distance see only the host's own player (the drunks'
    /// camp closes when "the player" is far from it).
    /// </summary>
    public sealed class JobHold
    {
        /// <summary>Its object, below the job.</summary>
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        public string[] States { get; internal set; }

        public float Within { get; internal set; }
    }

    /// <summary>
    /// One of the game's jobs as a data file of the mod describes it (<c>data/jobs/*.json</c>): which of its state machines
    /// are the world's (the host's game runs them, the others' are paused), which of their values every game has the same,
    /// and which of its objects those machines move (put where the host has them). What a player does in a job (pump a
    /// well, take the pay) is done in their own game, as always, and reaches the others through those values.
    /// </summary>
    public sealed class JobDefinition
    {
        public const int FormatVersion = 1;

        /// <summary>What the log calls it, and the start of the names its values travel by: "shit1".</summary>
        public string Name { get; private set; }

        /// <summary>The scene's top object it is under: "JOBS".</summary>
        public string Root { get; private set; }

        /// <summary>Its path below <see cref="Root"/>: "HouseShit1".</summary>
        public string Path { get; private set; }

        /// <summary>
        /// Paths below <see cref="Root"/> where the game puts it later on, looked for when <see cref="Path"/> is not there: the
        /// kilju buyer's camp, which Jokke's move takes elsewhere. None if it stays.
        /// </summary>
        public string[] Moved { get; private set; }

        /// <summary>The world's state machines, by path below the job: run by the host's game, paused in the others.</summary>
        public NpcMachine[] Paused { get; private set; }

        public JobValue[] Values { get; private set; }

        public JobRelay[] Relays { get; private set; }

        /// <summary>Objects, by path below the job or from the scene's top, that are switched on and off.</summary>
        public JobActive[] Actives { get; private set; }

        /// <summary>The world's machines the host's game holds while another player is near (<see cref="JobHold"/>).</summary>
        public JobHold[] Holds { get; private set; }

        /// <summary>Objects, by path below the job, that the world's machines move: where the host has them.</summary>
        public JobPlace[] Positions { get; private set; }

        /// <summary>
        /// Objects, by path from the scene's top, whose children are the job's things that a player moves (the hay bales,
        /// the furniture, the berry box): shared as loose objects although they are neither at the top of the scene nor in a
        /// container of the game's loose things.
        /// </summary>
        public string[] Loose { get; private set; }

        /// <exception cref="FormatException">The text is not a valid job file. The message says what is wrong.</exception>
        public static JobDefinition Parse(string json)
        {
            Dictionary<string, object> file = JsonReader.Object(JsonReader.Parse(json), "A job file");
            double format = JsonReader.Number(file, "format", "A job file");
            if (format != FormatVersion)
            {
                throw new FormatException("A job file of format " + format.ToString(CultureInfo.InvariantCulture) + "; this build reads format " + FormatVersion + ".");
            }

            var definition = new JobDefinition();
            definition.Name = JsonReader.String(file, "name", "A job file");
            string what = "The job '" + definition.Name + "'";
            if (definition.Name.Length == 0 || definition.Name.IndexOf('/') >= 0)
            {
                throw new FormatException(what + " needs a name without '/'.");
            }

            definition.Root = JsonReader.String(file, "root", what);
            definition.Path = JsonReader.String(file, "path", what);
            definition.Moved = Names(file, "moved", what);
            var holds = new List<JobHold>();
            if (file.ContainsKey("holds"))
            {
                List<object> items = JsonReader.Array(file["holds"], what + "'s \"holds\"");
                for (int i = 0; i < items.Count; i++)
                {
                    Dictionary<string, object> entry = JsonReader.Object(items[i], what + "'s hold");
                    var hold = new JobHold
                    {
                        Path = JsonReader.String(entry, "path", what + "'s hold"),
                        Machine = JsonReader.String(entry, "fsm", what + "'s hold"),
                        States = Names(entry, "states", what + "'s hold"),
                        Within = (float)JsonReader.Number(entry, "within", what + "'s hold"),
                    };
                    if (hold.Machine.Length == 0 || hold.States.Length == 0 || hold.Within <= 0f || hold.Within > 5000f)
                    {
                        throw new FormatException(what + "'s hold needs a machine (\"fsm\"), its states and \"within\" from 0 to 5000 metres.");
                    }

                    holds.Add(hold);
                }
            }

            definition.Holds = holds.ToArray();

            var paused = new List<NpcMachine>();
            if (file.ContainsKey("pause"))
            {
                List<object> items = JsonReader.Array(file["pause"], what + "'s \"pause\"");
                for (int i = 0; i < items.Count; i++)
                {
                    Dictionary<string, object> entry = JsonReader.Object(items[i], what + "'s \"pause\" " + (i + 1));
                    paused.Add(new NpcMachine
                    {
                        Path = JsonReader.String(entry, "path", what + "'s \"pause\""),
                        Machine = JsonReader.String(entry, "fsm", what + "'s \"pause\""),
                    });
                }
            }

            definition.Paused = paused.ToArray();

            var values = new List<JobValue>();
            if (file.ContainsKey("values"))
            {
                List<object> items = JsonReader.Array(file["values"], what + "'s \"values\"");
                for (int i = 0; i < items.Count; i++)
                {
                    string place = what + "'s value " + (i + 1);
                    Dictionary<string, object> entry = JsonReader.Object(items[i], place);
                    bool global = entry.ContainsKey("global");
                    if (global && (entry.ContainsKey("path") || entry.ContainsKey("fsm") || entry.ContainsKey("variable")))
                    {
                        throw new FormatException(place + " is a \"global\" or a machine's \"path\", \"fsm\" and \"variable\", not both.");
                    }

                    var value = new JobValue
                    {
                        Path = global ? string.Empty : JsonReader.String(entry, "path", place),
                        Machine = global ? null : JsonReader.String(entry, "fsm", place),
                        Variable = JsonReader.String(entry, global ? "global" : "variable", place),
                        Global = global,
                        Scale = entry.ContainsKey("scale") ? JsonReader.Number(entry, "scale", place) : 1000,
                    };
                    if (value.Variable.Length == 0)
                    {
                        throw new FormatException(place + " needs the name of a variable.");
                    }
                    string kind = JsonReader.String(entry, "kind", place);
                    if (kind == "amount")
                    {
                        value.Kind = JobValueKind.Amount;
                    }
                    else if (kind == "said")
                    {
                        value.Kind = JobValueKind.Said;
                    }
                    else
                    {
                        throw new FormatException(place + " is an \"amount\" or \"said\", not \"" + kind + "\".");
                    }

                    if (value.Scale < 0.001 || value.Scale > 1000000)
                    {
                        throw new FormatException(place + " has a \"scale\" from 0.001 to 1000000.");
                    }

                    values.Add(value);
                }
            }

            definition.Values = values.ToArray();

            var positions = new List<JobPlace>();
            if (file.ContainsKey("positions"))
            {
                List<object> items = JsonReader.Array(file["positions"], what + "'s \"positions\"");
                for (int i = 0; i < items.Count; i++)
                {
                    // A path, or { "path": …, "once": true }.
                    var entry = items[i] as Dictionary<string, object>;
                    string path = entry == null ? items[i] as string : JsonReader.String(entry, "path", what + "'s position " + (i + 1));
                    if (string.IsNullOrEmpty(path))
                    {
                        throw new FormatException(what + "'s \"positions\" must be a list of paths, or of objects with a \"path\".");
                    }

                    positions.Add(new JobPlace { Path = path, Once = entry != null && Flag(entry, "once", what + "'s position " + (i + 1)) });
                }
            }

            definition.Positions = positions.ToArray();

            var relays = new List<JobRelay>();
            if (file.ContainsKey("relay"))
            {
                List<object> items = JsonReader.Array(file["relay"], what + "'s \"relay\"");
                for (int i = 0; i < items.Count; i++)
                {
                    relays.Add(ParseRelay(JsonReader.Object(items[i], what + "'s relay " + (i + 1)), what + "'s relay " + (i + 1)));
                }
            }

            definition.Relays = relays.ToArray();

            var actives = new List<JobActive>();
            if (file.ContainsKey("actives"))
            {
                List<object> items = JsonReader.Array(file["actives"], what + "'s \"actives\"");
                for (int i = 0; i < items.Count; i++)
                {
                    // A path (the host's), or { "path": …, "said": true }.
                    var entry = items[i] as Dictionary<string, object>;
                    string path = entry == null ? items[i] as string : JsonReader.String(entry, "path", what + "'s active " + (i + 1));
                    if (string.IsNullOrEmpty(path))
                    {
                        throw new FormatException(what + "'s \"actives\" must be a list of paths, or of objects with a \"path\".");
                    }

                    actives.Add(new JobActive
                    {
                        Path = path,
                        Machine = entry != null && entry.ContainsKey("fsm") ? JsonReader.String(entry, "fsm", what + "'s active " + (i + 1)) : null,
                        Said = entry != null && Flag(entry, "said", what + "'s active " + (i + 1)),
                    });
                }
            }

            definition.Actives = actives.ToArray();
            definition.Loose = Names(file, "loose", what);
            for (int i = 0; i < definition.Loose.Length; i++)
            {
                if (definition.Loose[i][0] == '/' || definition.Loose[i].IndexOf('/') < 0)
                {
                    throw new FormatException(what + "'s \"loose\" are paths from the scene's top, without a leading '/', below a top object: not \"" + definition.Loose[i] + "\".");
                }
            }

            if (definition.Values.Length == 0 && definition.Positions.Length == 0 && definition.Paused.Length == 0 && definition.Relays.Length == 0
                && definition.Actives.Length == 0 && definition.Loose.Length == 0)
            {
                throw new FormatException(what + " shares nothing: it needs \"pause\", \"values\", \"positions\", \"relay\", \"actives\" or \"loose\".");
            }

            return definition;
        }

        /// <summary>The name value <paramref name="index"/> travels by: "job/shit1/0".</summary>
        public string ValueKey(int index)
        {
            return "job/" + Name + "/" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The name whether object <paramref name="index"/> of <see cref="Actives"/> is on travels by: "job/teimoads/a0".</summary>
        public string ActiveKey(int index)
        {
            return "job/" + Name + "/a" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The name the states of relay <paramref name="index"/> travel by: "job/wood1/s0".</summary>
        public string RelayKey(int index)
        {
            return "job/" + Name + "/s" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The name the place of object <paramref name="index"/> travels by: "job/shit1/p0".</summary>
        public string PositionKey(int index)
        {
            return "job/" + Name + "/p" + index.ToString(CultureInfo.InvariantCulture);
        }

        private static JobRelay ParseRelay(Dictionary<string, object> entry, string place)
        {
            string[] states = Names(entry, "states", place);
            if (states.Length == 0 || states.Length > 64)
            {
                throw new FormatException(place + " needs from 1 to 64 states.");
            }

            var relay = new JobRelay
            {
                Path = entry.ContainsKey("path") ? JsonReader.String(entry, "path", place) : string.Empty,
                Machine = JsonReader.String(entry, "fsm", place),
                States = states,
                Objects = Names(entry, "objects", place),
                Pays = entry.ContainsKey("pays") ? JsonReader.String(entry, "pays", place) : null,
                Quiet = Flag(entry, "quiet", place),
                DealEnds = new string[0],
            };
            if (relay.Objects.Length > JobRelay.MaxObjects)
            {
                throw new FormatException(place + " carries at most " + JobRelay.MaxObjects + " \"objects\".");
            }

            if (relay.Pays != null && relay.Pays.Length == 0)
            {
                throw new FormatException(place + "'s \"pays\" is the name of a variable.");
            }

            string from = entry.ContainsKey("from") ? JsonReader.String(entry, "from", place) : "any";
            if (from == "any")
            {
                relay.From = JobRelayFrom.Any;
            }
            else if (from == "host")
            {
                relay.From = JobRelayFrom.Host;
            }
            else if (from == "clients")
            {
                relay.From = JobRelayFrom.Clients;
            }
            else
            {
                throw new FormatException(place + "'s \"from\" is \"any\", \"host\" or \"clients\", not \"" + from + "\".");
            }

            if (entry.ContainsKey("deal"))
            {
                Dictionary<string, object> deal = JsonReader.Object(entry["deal"], place + "'s \"deal\"");
                relay.DealEnds = Names(deal, "ends", place + "'s \"deal\"");
                if (relay.DealEnds.Length == 0)
                {
                    throw new FormatException(place + "'s \"deal\" needs the states that end it, \"ends\".");
                }

                if (relay.From != JobRelayFrom.Any)
                {
                    throw new FormatException(place + " is dealt: whoever deals says its states, so it cannot have a \"from\".");
                }

                for (int s = 0; s < relay.DealEnds.Length; s++)
                {
                    if (Array.IndexOf(states, relay.DealEnds[s]) < 0)
                    {
                        throw new FormatException(place + "'s deal ends in '" + relay.DealEnds[s] + "', which is not one of its \"states\".");
                    }
                }
            }

            var guards = new List<JobGuard>();
            if (entry.ContainsKey("guards"))
            {
                List<object> items = JsonReader.Array(entry["guards"], place + "'s \"guards\"");
                for (int g = 0; g < items.Count; g++)
                {
                    Dictionary<string, object> guard = JsonReader.Object(items[g], place + "'s guard " + (g + 1));
                    guards.Add(new JobGuard
                    {
                        State = JsonReader.String(guard, "state", place + "'s guard " + (g + 1)),
                        Variable = JsonReader.String(guard, "object", place + "'s guard " + (g + 1)),
                    });
                }
            }

            relay.Guards = guards.ToArray();
            var spares = new List<JobSpare>();
            if (entry.ContainsKey("spare"))
            {
                List<object> items = JsonReader.Array(entry["spare"], place + "'s \"spare\"");
                for (int s = 0; s < items.Count; s++)
                {
                    string where = place + "'s spare " + (s + 1);
                    Dictionary<string, object> spare = JsonReader.Object(items[s], where);
                    var item = new JobSpare
                    {
                        State = JsonReader.String(spare, "state", where),
                        Vehicle = spare.ContainsKey("vehicle") ? JsonReader.String(spare, "vehicle", where) : null,
                        Object = spare.ContainsKey("object") ? JsonReader.String(spare, "object", where) : null,
                        Also = Names(spare, "also", where),
                        EvenNear = Flag(spare, "evenNear", where),
                    };
                    var actions = new List<int>();
                    if (spare.ContainsKey("actions"))
                    {
                        List<object> places = JsonReader.Array(spare["actions"], where + "'s \"actions\"");
                        for (int a = 0; a < places.Count; a++)
                        {
                            if (!(places[a] is double) || (double)places[a] < 0 || (double)places[a] > 255 || (double)places[a] != Math.Floor((double)places[a]))
                            {
                                throw new FormatException(where + "'s \"actions\" are the places of the state's actions, from 0.");
                            }

                            actions.Add((int)(double)places[a]);
                        }
                    }

                    item.Actions = actions.ToArray();
                    if (item.State.Length == 0 || (item.Vehicle == null) == (item.Object == null) || (item.Vehicle != null && item.Vehicle.Length == 0)
                        || (item.Object != null && (item.Object.Length < 2 || item.Object[0] != '/')))
                    {
                        throw new FormatException(where + " needs a \"state\" and the vehicle: a \"vehicle\" variable or an \"object\" path from the scene's top ('/…'), not both.");
                    }

                    if (item.Object != null && item.Actions.Length == 0)
                    {
                        throw new FormatException(where + " names the vehicle by its path: it needs the \"actions\" that move it.");
                    }

                    spares.Add(item);
                }
            }

            relay.Spares = spares.ToArray();
            return relay;
        }

        private static bool Flag(Dictionary<string, object> entry, string name, string what)
        {
            if (!entry.ContainsKey(name))
            {
                return false;
            }

            if (!(entry[name] is bool))
            {
                throw new FormatException(what + "'s \"" + name + "\" is true or false.");
            }

            return (bool)entry[name];
        }

        // A list of names, or none if it is not there.
        private static string[] Names(Dictionary<string, object> entry, string name, string what)
        {
            if (!entry.ContainsKey(name))
            {
                return new string[0];
            }

            List<object> items = JsonReader.Array(entry[name], what + "'s \"" + name + "\"");
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
