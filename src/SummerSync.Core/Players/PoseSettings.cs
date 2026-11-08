using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SummerSync.Core.Players
{
    /// <summary>One value that shapes how the people move: its key in the file, its name in the window, its range.</summary>
    public sealed class PoseValue
    {
        public PoseValue(string key, string label, float least, float most, float standard)
        {
            Key = key;
            Label = label;
            Least = least;
            Most = most;
            Standard = standard;
        }

        public string Key { get; private set; }

        /// <summary>What the window calls it, in Spanish: it is shown to the player.</summary>
        public string Label { get; private set; }

        public float Least { get; private set; }

        public float Most { get; private set; }

        public float Standard { get; private set; }
    }

    /// <summary>The ways a person can be shown moving, besides what the game's own clips do.</summary>
    public enum PoseKind
    {
        Stand,
        Walk,
        Run,
        Crouch,
        CrouchWalk,
        Prone,
        Sit,
    }

    /// <summary>
    /// The numbers the mod's own animations are made from (crouching, running, lying down) and how each person's head
    /// is turned, as the player tuned them in the character window. Kept in a text file of "key=value" lines in the
    /// mod's folder; a key that is missing or out of range takes its standard value.
    /// </summary>
    public sealed class PoseSettings
    {
        public static readonly PoseValue CrouchHips = new PoseValue("crouch.hips", "Altura de la cadera (m)", 0.25f, 0.85f, 0.47f);
        public static readonly PoseValue CrouchFeet = new PoseValue("crouch.feet", "Pies adelante (m)", -0.15f, 0.45f, 0.12f);
        public static readonly PoseValue CrouchWidth = new PoseValue("crouch.width", "Pies abiertos (m)", 0f, 0.35f, 0.08f);
        public static readonly PoseValue CrouchLean = new PoseValue("crouch.lean", "Espalda inclinada (°)", 0f, 70f, 28f);
        public static readonly PoseValue CrouchArms = new PoseValue("crouch.arms", "Brazos adelante (°)", -20f, 90f, 35f);
        public static readonly PoseValue CrouchWalkStride = new PoseValue("crouchwalk.stride", "Largo del paso (m)", 0.05f, 0.7f, 0.30f);
        public static readonly PoseValue CrouchWalkLift = new PoseValue("crouchwalk.lift", "Altura del pie (m)", 0f, 0.3f, 0.08f);
        public static readonly PoseValue CrouchWalkSpeed = new PoseValue("crouchwalk.speed", "Velocidad del ciclo (m/s)", 0.3f, 3f, 1.0f);
        public static readonly PoseValue RunFrom = new PoseValue("run.from", "Corre desde (m/s)", 1.5f, 5f, 2.7f);
        public static readonly PoseValue RunStride = new PoseValue("run.stride", "Piernas (°)", 10f, 75f, 38f);
        public static readonly PoseValue RunKnee = new PoseValue("run.knee", "Rodillas (°)", 20f, 150f, 100f);
        public static readonly PoseValue RunLean = new PoseValue("run.lean", "Inclinación (°)", 0f, 35f, 12f);
        public static readonly PoseValue RunArms = new PoseValue("run.arms", "Brazos (°)", 0f, 90f, 45f);
        public static readonly PoseValue RunElbow = new PoseValue("run.elbow", "Codos (°)", 0f, 150f, 85f);
        public static readonly PoseValue RunBob = new PoseValue("run.bob", "Rebote (m)", 0f, 0.12f, 0.03f);
        public static readonly PoseValue RunSpeed = new PoseValue("run.speed", "Velocidad del ciclo (m/s)", 2f, 9f, 3.5f);
        public static readonly PoseValue ProneHeight = new PoseValue("prone.height", "Altura de la cadera (m)", 0.05f, 0.5f, 0.16f);
        public static readonly PoseValue ProneLift = new PoseValue("prone.lift", "Pecho levantado (°)", 0f, 70f, 30f);
        public static readonly PoseValue ProneElbows = new PoseValue("prone.elbows", "Antebrazos (°)", 0f, 120f, 75f);
        public static readonly PoseValue ProneSpread = new PoseValue("prone.spread", "Piernas abiertas (m)", 0f, 0.4f, 0.12f);
        public static readonly PoseValue TurnFrom = new PoseValue("turn.from", "Da pasitos al girar desde (°/s)", 10f, 300f, 45f);

        /// <summary>Every value but the heads', grouped by what they shape, in the order the window shows them.</summary>
        public static readonly PoseValue[][] Groups =
        {
            new[] { CrouchHips, CrouchFeet, CrouchWidth, CrouchLean, CrouchArms },
            new[] { CrouchWalkStride, CrouchWalkLift, CrouchWalkSpeed },
            new[] { RunFrom, RunStride, RunKnee, RunLean, RunArms, RunElbow, RunBob, RunSpeed },
            new[] { ProneHeight, ProneLift, ProneElbows, ProneSpread },
            new[] { TurnFrom },
        };

        /// <summary>How far a head can be turned to set it straight, each way.</summary>
        public const float HeadTune = 45f;

        private readonly Dictionary<string, float> values = new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>Counts up whenever a value changes, so that whoever made something from them knows to make it again.</summary>
        public int Serial { get; private set; }

        public float this[PoseValue value]
        {
            get
            {
                float found;
                return values.TryGetValue(value.Key, out found) ? found : value.Standard;
            }
        }

        /// <summary>Sets a value, kept within its range.</summary>
        public void Set(PoseValue value, float number)
        {
            float kept = Clamp(number, value.Least, value.Most);
            float before;
            if (values.TryGetValue(value.Key, out before) && before == kept)
            {
                return;
            }

            values[value.Key] = kept;
            Serial++;
        }

        /// <summary>Back to the standard values: every one of these, or all of them.</summary>
        public void Reset(PoseValue[] some)
        {
            for (int i = 0; i < some.Length; i++)
            {
                values.Remove(some[i].Key);
            }

            Serial++;
        }

        /// <summary>How the head of the person at that place of the list is turned, in degrees: yaw, pitch, roll.</summary>
        public float Head(int look, int axis)
        {
            float found;
            return values.TryGetValue(HeadKey(look, axis), out found) ? found : 0f;
        }

        public void SetHead(int look, int axis, float degrees)
        {
            float kept = Clamp(degrees, -HeadTune, HeadTune);
            string key = HeadKey(look, axis);
            float before;
            if (values.TryGetValue(key, out before) ? before == kept : kept == 0f)
            {
                return;
            }

            values[key] = kept;
            Serial++;
        }

        public void ResetHead(int look)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                values.Remove(HeadKey(look, axis));
            }

            Serial++;
        }

        /// <summary>Reads "key=value" lines. What it does not know, or cannot read, it leaves out.</summary>
        public static PoseSettings Parse(string text)
        {
            var settings = new PoseSettings();
            if (string.IsNullOrEmpty(text))
            {
                return settings;
            }

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int equals = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || equals <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, equals).Trim();
                float number;
                if (!float.TryParse(line.Substring(equals + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || float.IsNaN(number) || float.IsInfinity(number))
                {
                    continue;
                }

                PoseValue known = Find(key);
                if (known != null)
                {
                    settings.Set(known, number);
                    continue;
                }

                int look;
                int axis;
                if (TryHeadKey(key, out look, out axis))
                {
                    settings.SetHead(look, axis, number);
                }
            }

            settings.Serial = 0;
            return settings;
        }

        /// <summary>Every value that is not the standard one, as "key=value" lines, in a fixed order.</summary>
        public string Write()
        {
            var keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            var text = new StringBuilder();
            text.Append("# SummerSync: how the people move and where their heads look (character window, F7).\n");
            for (int i = 0; i < keys.Count; i++)
            {
                text.Append(keys[i]).Append('=').Append(values[keys[i]].ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            }

            return text.ToString();
        }

        public static PoseValue Find(string key)
        {
            for (int g = 0; g < Groups.Length; g++)
            {
                for (int i = 0; i < Groups[g].Length; i++)
                {
                    if (Groups[g][i].Key == key)
                    {
                        return Groups[g][i];
                    }
                }
            }

            return null;
        }

        private static string HeadKey(int look, int axis)
        {
            return "head." + look.ToString(CultureInfo.InvariantCulture) + "." + (axis == 0 ? "yaw" : axis == 1 ? "pitch" : "roll");
        }

        private static bool TryHeadKey(string key, out int look, out int axis)
        {
            look = -1;
            axis = -1;
            string[] parts = key.Split('.');
            if (parts.Length != 3 || parts[0] != "head" || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out look) || look < 0 || look > 255)
            {
                return false;
            }

            axis = parts[2] == "yaw" ? 0 : parts[2] == "pitch" ? 1 : parts[2] == "roll" ? 2 : -1;
            return axis >= 0;
        }

        private static float Clamp(float number, float least, float most)
        {
            return number < least ? least : (number > most ? most : number);
        }
    }
}
