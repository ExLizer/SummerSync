using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SummerSync.Core.Json
{
    /// <summary>
    /// Reads a JSON text into plain values: an object is a <see cref="Dictionary{TKey,TValue}"/> of string to
    /// value, an array a <see cref="List{T}"/> of values, a number a <see cref="double"/>, and then string, bool
    /// and null. Enough for the data files the mod ships with; the game's runtime has no JSON reader of its own.
    /// </summary>
    public static class JsonReader
    {
        private const int MaxDepth = 64;

        /// <exception cref="FormatException">The text is not JSON. The message says where.</exception>
        public static object Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException("text");
            }

            int at = 0;
            object value = ReadValue(text, ref at, 0);
            SkipSpace(text, ref at);
            if (at < text.Length)
            {
                throw Error(text, at, "Something follows the end of the value");
            }

            return value;
        }

        /// <summary>The object's property as an object, or a <see cref="FormatException"/> that names it.</summary>
        public static Dictionary<string, object> Object(object value, string what)
        {
            var result = value as Dictionary<string, object>;
            if (result == null)
            {
                throw new FormatException(what + " must be an object.");
            }

            return result;
        }

        public static List<object> Array(object value, string what)
        {
            var result = value as List<object>;
            if (result == null)
            {
                throw new FormatException(what + " must be an array.");
            }

            return result;
        }

        public static string String(Dictionary<string, object> parent, string name, string what)
        {
            object value;
            var result = parent.TryGetValue(name, out value) ? value as string : null;
            if (result == null)
            {
                throw new FormatException(what + " needs a text \"" + name + "\".");
            }

            return result;
        }

        public static double Number(Dictionary<string, object> parent, string name, string what)
        {
            object value;
            if (!parent.TryGetValue(name, out value) || !(value is double))
            {
                throw new FormatException(what + " needs a number \"" + name + "\".");
            }

            return (double)value;
        }

        /// <summary>An array of exactly <paramref name="count"/> numbers.</summary>
        public static double[] Numbers(Dictionary<string, object> parent, string name, int count, string what)
        {
            object value;
            var items = parent.TryGetValue(name, out value) ? value as List<object> : null;
            if (items == null || items.Count != count)
            {
                throw new FormatException(what + " needs \"" + name + "\" as an array of " + count + " numbers.");
            }

            var result = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (!(items[i] is double))
                {
                    throw new FormatException(what + " needs \"" + name + "\" as an array of " + count + " numbers.");
                }

                result[i] = (double)items[i];
            }

            return result;
        }

        private static object ReadValue(string text, ref int at, int depth)
        {
            if (depth > MaxDepth)
            {
                throw Error(text, at, "Nested too deep");
            }

            SkipSpace(text, ref at);
            if (at >= text.Length)
            {
                throw Error(text, at, "The text ends where a value should be");
            }

            char c = text[at];
            if (c == '{')
            {
                return ReadObject(text, ref at, depth);
            }

            if (c == '[')
            {
                return ReadArray(text, ref at, depth);
            }

            if (c == '"')
            {
                return ReadString(text, ref at);
            }

            if (c == '-' || (c >= '0' && c <= '9'))
            {
                return ReadNumber(text, ref at);
            }

            if (Word(text, ref at, "true"))
            {
                return true;
            }

            if (Word(text, ref at, "false"))
            {
                return false;
            }

            if (Word(text, ref at, "null"))
            {
                return null;
            }

            throw Error(text, at, "Unexpected '" + c + "'");
        }

        private static Dictionary<string, object> ReadObject(string text, ref int at, int depth)
        {
            var result = new Dictionary<string, object>();
            at++;
            SkipSpace(text, ref at);
            if (at < text.Length && text[at] == '}')
            {
                at++;
                return result;
            }

            for (;;)
            {
                SkipSpace(text, ref at);
                if (at >= text.Length || text[at] != '"')
                {
                    throw Error(text, at, "A property name should start here");
                }

                string name = ReadString(text, ref at);
                SkipSpace(text, ref at);
                if (at >= text.Length || text[at] != ':')
                {
                    throw Error(text, at, "A ':' should follow the property name");
                }

                at++;
                result[name] = ReadValue(text, ref at, depth + 1);
                SkipSpace(text, ref at);
                if (at < text.Length && text[at] == ',')
                {
                    at++;
                    continue;
                }

                if (at < text.Length && text[at] == '}')
                {
                    at++;
                    return result;
                }

                throw Error(text, at, "A ',' or a '}' should be here");
            }
        }

        private static List<object> ReadArray(string text, ref int at, int depth)
        {
            var result = new List<object>();
            at++;
            SkipSpace(text, ref at);
            if (at < text.Length && text[at] == ']')
            {
                at++;
                return result;
            }

            for (;;)
            {
                result.Add(ReadValue(text, ref at, depth + 1));
                SkipSpace(text, ref at);
                if (at < text.Length && text[at] == ',')
                {
                    at++;
                    continue;
                }

                if (at < text.Length && text[at] == ']')
                {
                    at++;
                    return result;
                }

                throw Error(text, at, "A ',' or a ']' should be here");
            }
        }

        private static string ReadString(string text, ref int at)
        {
            var result = new StringBuilder();
            int start = at;
            at++;
            while (at < text.Length)
            {
                char c = text[at++];
                if (c == '"')
                {
                    return result.ToString();
                }

                if (c != '\\')
                {
                    result.Append(c);
                    continue;
                }

                if (at >= text.Length)
                {
                    break;
                }

                char escaped = text[at++];
                switch (escaped)
                {
                    case 'n':
                        result.Append('\n');
                        break;
                    case 't':
                        result.Append('\t');
                        break;
                    case 'r':
                        result.Append('\r');
                        break;
                    case 'b':
                        result.Append('\b');
                        break;
                    case 'f':
                        result.Append('\f');
                        break;
                    case 'u':
                        int code;
                        if (at + 4 > text.Length || !int.TryParse(text.Substring(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                        {
                            throw Error(text, at, "Four hex digits should follow \\u");
                        }

                        result.Append((char)code);
                        at += 4;
                        break;
                    case '"':
                    case '\\':
                    case '/':
                        result.Append(escaped);
                        break;
                    default:
                        throw Error(text, at - 1, "Unknown escape '\\" + escaped + "'");
                }
            }

            throw Error(text, start, "This text is never closed");
        }

        private static double ReadNumber(string text, ref int at)
        {
            int start = at;
            while (at < text.Length && "+-0123456789.eE".IndexOf(text[at]) >= 0)
            {
                at++;
            }

            double value;
            if (!double.TryParse(text.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw Error(text, start, "Not a number");
            }

            return value;
        }

        private static bool Word(string text, ref int at, string word)
        {
            if (string.CompareOrdinal(text, at, word, 0, word.Length) != 0)
            {
                return false;
            }

            at += word.Length;
            return true;
        }

        private static void SkipSpace(string text, ref int at)
        {
            while (at < text.Length && (text[at] == ' ' || text[at] == '\n' || text[at] == '\r' || text[at] == '\t'))
            {
                at++;
            }
        }

        private static FormatException Error(string text, int at, string what)
        {
            int line = 1;
            int column = 1;
            for (int i = 0; i < at && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return new FormatException(what + " (line " + line + ", column " + column + ").");
        }
    }
}
