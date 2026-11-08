using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace SummerSync.Core.Json
{
    /// <summary>
    /// Writes arbitrary objects as JSON by reflecting over their public instance fields. Meant for diagnostics:
    /// it never calls property getters (they can have side effects), it is bounded in depth and item count, and
    /// a failing field becomes an error marker instead of an exception.
    /// </summary>
    public sealed class ObjectDumper
    {
        private readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
        private readonly List<object> path = new List<object>();
        private readonly CustomWriter custom;
        private readonly int maxDepth;
        private readonly int maxItems;

        /// <param name="custom">Gets the first chance to write a value; returns false to fall back to the generic rules.</param>
        /// <param name="maxDepth">How many levels of nested objects are expanded before only the type name is written.</param>
        /// <param name="maxItems">How many elements of a collection are written before the rest is summarised.</param>
        public ObjectDumper(CustomWriter custom, int maxDepth, int maxItems)
        {
            if (maxDepth < 1)
            {
                throw new ArgumentOutOfRangeException("maxDepth");
            }

            if (maxItems < 1)
            {
                throw new ArgumentOutOfRangeException("maxItems");
            }

            this.custom = custom;
            this.maxDepth = maxDepth;
            this.maxItems = maxItems;
        }

        public ObjectDumper()
            : this(null, 3, 64)
        {
        }

        /// <summary>Writes domain types the generic rules would handle badly. Returns true when it wrote the value.</summary>
        public delegate bool CustomWriter(object value, JsonWriter writer);

        /// <summary>Writes any value: null, primitive, enum, string, collection or object.</summary>
        public void Write(JsonWriter writer, object value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            if (custom != null && custom(value, writer))
            {
                return;
            }

            if (WritePrimitive(writer, value))
            {
                return;
            }

            if (value is Delegate || value is Type || value is MemberInfo)
            {
                writer.Value("<" + value.GetType().Name + ">");
                return;
            }

            bool tracked = !value.GetType().IsValueType;
            if (tracked && IsOnPath(value))
            {
                writer.Value("<cycle " + value.GetType().Name + ">");
                return;
            }

            if (path.Count >= maxDepth)
            {
                writer.Value("<" + value.GetType().Name + ">");
                return;
            }

            path.Add(tracked ? value : null);
            try
            {
                var dictionary = value as IDictionary;
                if (dictionary != null)
                {
                    WriteDictionary(writer, dictionary);
                    return;
                }

                var sequence = value as IEnumerable;
                if (sequence != null)
                {
                    WriteSequence(writer, sequence);
                    return;
                }

                writer.BeginObject();
                WriteFieldProperties(writer, value);
                writer.EndObject();
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        /// <summary>
        /// Writes the public instance fields of <paramref name="instance"/> as properties of the object that is
        /// currently open in <paramref name="writer"/>.
        /// </summary>
        public void WriteFieldProperties(JsonWriter writer, object instance)
        {
            FieldInfo[] fields = GetFields(instance.GetType());
            for (int i = 0; i < fields.Length; i++)
            {
                object value;
                try
                {
                    value = fields[i].GetValue(instance);
                }
                catch (Exception exception)
                {
                    writer.Property(fields[i].Name, ErrorMarker(exception));
                    continue;
                }

                writer.Name(fields[i].Name);
                Write(writer, value);
            }
        }

        public static string ErrorMarker(Exception exception)
        {
            return "<error " + exception.GetType().Name + ": " + exception.Message + ">";
        }

        private static bool WritePrimitive(JsonWriter writer, object value)
        {
            var text = value as string;
            if (text != null)
            {
                writer.Value(text);
                return true;
            }

            if (value is bool)
            {
                writer.Value((bool)value);
                return true;
            }

            if (value is int)
            {
                writer.Value((int)value);
                return true;
            }

            if (value is float)
            {
                writer.Value((float)value);
                return true;
            }

            if (value is double)
            {
                writer.Value((double)value);
                return true;
            }

            if (value is Enum)
            {
                writer.Value(value.ToString());
                return true;
            }

            if (value is long || value is short || value is byte || value is sbyte || value is ushort || value is uint)
            {
                writer.Value(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return true;
            }

            if (value is char || value is ulong || value is decimal || value is Guid || value is DateTime || value is TimeSpan)
            {
                writer.Value(Convert.ToString(value, CultureInfo.InvariantCulture));
                return true;
            }

            return false;
        }

        private void WriteDictionary(JsonWriter writer, IDictionary dictionary)
        {
            writer.BeginObject();
            var used = new Dictionary<string, int>();
            int written = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (written == maxItems)
                {
                    writer.Property("<truncated>", dictionary.Count - written);
                    break;
                }

                // Distinct keys can share a text form (1 and "1"); a JSON object needs unique names.
                string name = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "<null>";
                int count;
                used.TryGetValue(name, out count);
                used[name] = count + 1;
                writer.Name(count == 0 ? name : name + "#" + (count + 1));
                Write(writer, entry.Value);
                written++;
            }

            writer.EndObject();
        }

        private void WriteSequence(JsonWriter writer, IEnumerable sequence)
        {
            writer.BeginArray();
            int written = 0;
            foreach (object item in sequence)
            {
                if (written == maxItems)
                {
                    var collection = sequence as ICollection;
                    writer.Value(collection != null ? "<truncated " + (collection.Count - written) + " more>" : "<truncated>");
                    break;
                }

                Write(writer, item);
                written++;
            }

            writer.EndArray();
        }

        private bool IsOnPath(object value)
        {
            for (int i = 0; i < path.Count; i++)
            {
                if (ReferenceEquals(path[i], value))
                {
                    return true;
                }
            }

            return false;
        }

        private FieldInfo[] GetFields(Type type)
        {
            FieldInfo[] fields;
            if (!fieldCache.TryGetValue(type, out fields))
            {
                fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                fieldCache[type] = fields;
            }

            return fields;
        }
    }
}
