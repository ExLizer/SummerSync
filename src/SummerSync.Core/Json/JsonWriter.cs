using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SummerSync.Core.Json
{
    /// <summary>
    /// Forward-only JSON writer. The game's BCL has no JSON support and dumps can be large, so values are
    /// streamed straight to the output instead of building a document in memory.
    /// Output is compact; call <see cref="Break"/> between elements to keep big arrays one item per line.
    /// </summary>
    public sealed class JsonWriter
    {
        private readonly TextWriter output;
        private readonly Stack<Scope> scopes = new Stack<Scope>();
        private bool nameWritten;
        private bool rootWritten;

        public JsonWriter(TextWriter output)
        {
            if (output == null)
            {
                throw new ArgumentNullException("output");
            }

            this.output = output;
        }

        private enum ScopeKind
        {
            Object,
            Array,
        }

        /// <summary>Whether every opened object and array has been closed and a root value was written.</summary>
        public bool IsComplete
        {
            get { return rootWritten && scopes.Count == 0; }
        }

        public JsonWriter BeginObject()
        {
            BeforeValue();
            output.Write('{');
            scopes.Push(new Scope(ScopeKind.Object));
            return this;
        }

        public JsonWriter EndObject()
        {
            Close(ScopeKind.Object, '}');
            return this;
        }

        public JsonWriter BeginArray()
        {
            BeforeValue();
            output.Write('[');
            scopes.Push(new Scope(ScopeKind.Array));
            return this;
        }

        public JsonWriter EndArray()
        {
            Close(ScopeKind.Array, ']');
            return this;
        }

        /// <summary>Writes a property name. Must be followed by exactly one value.</summary>
        public JsonWriter Name(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException("name");
            }

            if (scopes.Count == 0 || scopes.Peek().Kind != ScopeKind.Object)
            {
                throw new InvalidOperationException("A property name is only valid inside an object.");
            }

            if (nameWritten)
            {
                throw new InvalidOperationException("The previous property name has no value yet.");
            }

            Scope scope = scopes.Peek();
            if (scope.HasItems)
            {
                output.Write(',');
            }

            scope.HasItems = true;
            WriteString(name);
            output.Write(':');
            nameWritten = true;
            return this;
        }

        public JsonWriter Null()
        {
            BeforeValue();
            output.Write("null");
            return this;
        }

        public JsonWriter Value(string value)
        {
            if (value == null)
            {
                return Null();
            }

            BeforeValue();
            WriteString(value);
            return this;
        }

        public JsonWriter Value(bool value)
        {
            BeforeValue();
            output.Write(value ? "true" : "false");
            return this;
        }

        public JsonWriter Value(int value)
        {
            BeforeValue();
            output.Write(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Value(long value)
        {
            BeforeValue();
            output.Write(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>NaN and infinities have no JSON number form and are written as strings.</summary>
        public JsonWriter Value(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return Value(NonFiniteName(value));
            }

            BeforeValue();
            output.Write(value.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>NaN and infinities have no JSON number form and are written as strings.</summary>
        public JsonWriter Value(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return Value(NonFiniteName(value));
            }

            BeforeValue();
            output.Write(value.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Property(string name, string value)
        {
            return Name(name).Value(value);
        }

        public JsonWriter Property(string name, bool value)
        {
            return Name(name).Value(value);
        }

        public JsonWriter Property(string name, int value)
        {
            return Name(name).Value(value);
        }

        public JsonWriter Property(string name, long value)
        {
            return Name(name).Value(value);
        }

        public JsonWriter Property(string name, float value)
        {
            return Name(name).Value(value);
        }

        public JsonWriter Property(string name, double value)
        {
            return Name(name).Value(value);
        }

        /// <summary>
        /// Writes text that is already a complete JSON value. Lets a caller render one item on the side and
        /// only commit it when it rendered without errors; the caller is responsible for its validity.
        /// </summary>
        public JsonWriter Raw(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("Raw JSON must be a complete value.", "json");
            }

            BeforeValue();
            output.Write(json);
            return this;
        }

        /// <summary>Emits a line break. Only allowed where whitespace is legal: not between a name and its value.</summary>
        public JsonWriter Break()
        {
            if (nameWritten)
            {
                throw new InvalidOperationException("A line break is not allowed between a property name and its value.");
            }

            output.Write('\n');
            return this;
        }

        private static string NonFiniteName(double value)
        {
            return double.IsNaN(value) ? "NaN" : (value > 0 ? "Infinity" : "-Infinity");
        }

        private void BeforeValue()
        {
            if (scopes.Count == 0)
            {
                if (rootWritten)
                {
                    throw new InvalidOperationException("The document already has a root value.");
                }

                rootWritten = true;
                return;
            }

            Scope scope = scopes.Peek();
            if (scope.Kind == ScopeKind.Object)
            {
                if (!nameWritten)
                {
                    throw new InvalidOperationException("A value inside an object needs a property name first.");
                }

                nameWritten = false;
                return;
            }

            if (scope.HasItems)
            {
                output.Write(',');
            }

            scope.HasItems = true;
        }

        private void Close(ScopeKind kind, char terminator)
        {
            if (scopes.Count == 0 || scopes.Peek().Kind != kind)
            {
                throw new InvalidOperationException("There is no open " + (kind == ScopeKind.Object ? "object" : "array") + " to close.");
            }

            if (nameWritten)
            {
                throw new InvalidOperationException("The last property name has no value.");
            }

            scopes.Pop();
            output.Write(terminator);
        }

        private void WriteString(string text)
        {
            output.Write('"');
            int runStart = 0;
            for (int i = 0; i < text.Length; i++)
            {
                string escape = Escape(text, i);
                if (escape == null)
                {
                    continue;
                }

                if (i > runStart)
                {
                    output.Write(text.Substring(runStart, i - runStart));
                }

                output.Write(escape);
                runStart = i + 1;
            }

            if (runStart == 0)
            {
                output.Write(text);
            }
            else if (runStart < text.Length)
            {
                output.Write(text.Substring(runStart));
            }

            output.Write('"');
        }

        /// <summary>Returns the escape sequence for the character at <paramref name="index"/>, or null if it can be written as is.</summary>
        private static string Escape(string text, int index)
        {
            char c = text[index];
            switch (c)
            {
                case '"': return "\\\"";
                case '\\': return "\\\\";
                case '\n': return "\\n";
                case '\r': return "\\r";
                case '\t': return "\\t";
                case '\b': return "\\b";
                case '\f': return "\\f";
            }

            if (c < ' ')
            {
                return Unicode(c);
            }

            // An unpaired surrogate cannot be encoded as UTF-8; escaping keeps the file valid.
            if (char.IsHighSurrogate(c))
            {
                return index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? null : Unicode(c);
            }

            if (char.IsLowSurrogate(c))
            {
                return index > 0 && char.IsHighSurrogate(text[index - 1]) ? null : Unicode(c);
            }

            return null;
        }

        private static string Unicode(char c)
        {
            return "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture);
        }

        private sealed class Scope
        {
            public Scope(ScopeKind kind)
            {
                Kind = kind;
            }

            public ScopeKind Kind { get; }

            public bool HasItems { get; set; }
        }
    }
}
