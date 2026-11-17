using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Json;

namespace SummerSync.Core.World
{
    /// <summary>
    /// A person of the game whose body is let go as a rag doll when a car runs them over or they are knocked down, from
    /// <c>data/bodies/*.json</c> (each file lists a few under "bodies").
    ///
    /// The rag doll hangs below the person, switched off, until the game lets it go: it takes it off its parent, switches
    /// it on and (for most) destroys the person. From then on it is a body of its own, made of a few rigidbodies held
    /// together by joints, that each game would move with its own physics. Shared, it lies where the game that simulates it
    /// has it, in every game.
    /// </summary>
    public sealed class BodyDefinition
    {
        public const int FormatVersion = 1;

        /// <summary>Longest name: it is part of the names of tallies ("body/&lt;name&gt;/&lt;bone&gt;").</summary>
        public const int MaxNameLength = 24;

        /// <summary>The name it is shared by, the same in every game: lower case letters and digits.</summary>
        public string Name { get; private set; }

        /// <summary>Where its rag doll hangs before it is let go, from the scene's top: the first that exists.</summary>
        public string[] Paths { get; private set; }

        /// <summary>
        /// Where the person it belongs to is, from the scene's top: the first that exists. While that object is still there
        /// and switched on in a game, the person is alive there, and only the game lets its rag doll go.
        /// </summary>
        public string[] Person { get; private set; }

        /// <summary>The machines below the rag doll that move it by themselves (a body that squirms): paused where it is only shown.</summary>
        public string[] Still { get; private set; }

        /// <summary>The bodies of one file.</summary>
        public static IList<BodyDefinition> Parse(string json)
        {
            Dictionary<string, object> file = JsonReader.Object(JsonReader.Parse(json), "A bodies file");
            double format = JsonReader.Number(file, "format", "A bodies file");
            if (format != FormatVersion)
            {
                throw new FormatException("A bodies file of format " + format.ToString(CultureInfo.InvariantCulture) + "; this build reads format " + FormatVersion + ".");
            }

            if (!file.ContainsKey("bodies"))
            {
                throw new FormatException("A bodies file needs \"bodies\".");
            }

            List<object> entries = JsonReader.Array(file["bodies"], "A bodies file's \"bodies\"");
            if (entries.Count == 0)
            {
                throw new FormatException("A bodies file's \"bodies\" is empty.");
            }

            var bodies = new List<BodyDefinition>();
            for (int i = 0; i < entries.Count; i++)
            {
                Dictionary<string, object> entry = JsonReader.Object(entries[i], "Body " + (i + 1));
                var body = new BodyDefinition();
                body.Name = JsonReader.String(entry, "name", "Body " + (i + 1));
                string what = "The body '" + body.Name + "'";
                if (!IsName(body.Name))
                {
                    throw new FormatException(what + " needs a name of lower case letters and digits, " + MaxNameLength + " at most.");
                }

                for (int j = 0; j < bodies.Count; j++)
                {
                    if (bodies[j].Name == body.Name)
                    {
                        throw new FormatException(what + " is in the file twice.");
                    }
                }

                body.Paths = Texts(entry, "paths", what, true);
                body.Person = Texts(entry, "person", what, false);
                body.Still = Texts(entry, "still", what, false);
                bodies.Add(body);
            }

            return bodies;
        }

        private static bool IsName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                return false;
            }

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9'))
                {
                    return false;
                }
            }

            return true;
        }

        private static string[] Texts(Dictionary<string, object> entry, string name, string what, bool needed)
        {
            if (!entry.ContainsKey(name))
            {
                if (needed)
                {
                    throw new FormatException(what + " needs \"" + name + "\".");
                }

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

            if (needed && result.Length == 0)
            {
                throw new FormatException(what + "'s \"" + name + "\" is empty.");
            }

            return result;
        }
    }
}
