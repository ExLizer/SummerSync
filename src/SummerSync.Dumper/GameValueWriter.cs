using System;
using HutongGames.PlayMaker;
using SummerSync.Core.Dump;
using SummerSync.Core.Json;
using UnityEngine;

namespace SummerSync.Dumper
{
    /// <summary>
    /// Writes Unity and PlayMaker values as JSON: vectors as arrays, object references as paths, PlayMaker
    /// variables as their value. Everything else falls back to the generic field dumper.
    /// </summary>
    internal sealed class GameValueWriter
    {
        private const int MaxDepth = 3;
        private const int MaxParameterItems = 48;
        private const int MaxCollectionItems = 512;

        private readonly PathCache paths;

        public GameValueWriter(PathCache paths)
        {
            this.paths = paths;
            Parameters = new ObjectDumper(TryWrite, MaxDepth, MaxParameterItems);
            CollectionItems = new ObjectDumper(TryWrite, MaxDepth, MaxCollectionItems);
        }

        /// <summary>For action parameters: small collection limit, since they repeat thousands of times.</summary>
        public ObjectDumper Parameters { get; }

        /// <summary>For ArrayMaker contents, which are game state and worth dumping in full.</summary>
        public ObjectDumper CollectionItems { get; }

        public static void WriteVector(JsonWriter writer, Vector3 value)
        {
            writer.BeginArray().Value(value.x).Value(value.y).Value(value.z).EndArray();
        }

        public static void WriteQuaternion(JsonWriter writer, Quaternion value)
        {
            writer.BeginArray().Value(value.x).Value(value.y).Value(value.z).Value(value.w).EndArray();
        }

        /// <summary>
        /// Writes the current value of a PlayMaker variable. Object references are written compactly: a GameObject
        /// as its path, anything else as "Type:name".
        /// </summary>
        public void WriteVariableValue(JsonWriter writer, NamedVariable variable)
        {
            var asFloat = variable as FsmFloat;
            if (asFloat != null)
            {
                writer.Value(asFloat.Value);
                return;
            }

            var asInt = variable as FsmInt;
            if (asInt != null)
            {
                writer.Value(asInt.Value);
                return;
            }

            var asBool = variable as FsmBool;
            if (asBool != null)
            {
                writer.Value(asBool.Value);
                return;
            }

            var asString = variable as FsmString;
            if (asString != null)
            {
                writer.Value(asString.Value);
                return;
            }

            var asVector3 = variable as FsmVector3;
            if (asVector3 != null)
            {
                WriteVector(writer, asVector3.Value);
                return;
            }

            var asVector2 = variable as FsmVector2;
            if (asVector2 != null)
            {
                writer.BeginArray().Value(asVector2.Value.x).Value(asVector2.Value.y).EndArray();
                return;
            }

            var asQuaternion = variable as FsmQuaternion;
            if (asQuaternion != null)
            {
                WriteQuaternion(writer, asQuaternion.Value);
                return;
            }

            var asColor = variable as FsmColor;
            if (asColor != null)
            {
                WriteColor(writer, asColor.Value);
                return;
            }

            var asRect = variable as FsmRect;
            if (asRect != null)
            {
                WriteRect(writer, asRect.Value);
                return;
            }

            var asGameObject = variable as FsmGameObject;
            if (asGameObject != null)
            {
                WriteCompactReference(writer, asGameObject.Value);
                return;
            }

            var asMaterial = variable as FsmMaterial;
            if (asMaterial != null)
            {
                WriteCompactReference(writer, asMaterial.Value);
                return;
            }

            var asTexture = variable as FsmTexture;
            if (asTexture != null)
            {
                WriteCompactReference(writer, asTexture.Value);
                return;
            }

            var asObject = variable as FsmObject;
            if (asObject != null)
            {
                WriteCompactReference(writer, asObject.Value);
                return;
            }

            writer.Value("<" + variable.GetType().Name + ">");
        }

        private static void WriteColor(JsonWriter writer, Color value)
        {
            writer.BeginArray().Value(value.r).Value(value.g).Value(value.b).Value(value.a).EndArray();
        }

        private static void WriteRect(JsonWriter writer, Rect value)
        {
            writer.BeginArray().Value(value.x).Value(value.y).Value(value.width).Value(value.height).EndArray();
        }

        private void WriteCompactReference(JsonWriter writer, UnityEngine.Object value)
        {
            if (ReferenceEquals(value, null))
            {
                writer.Null();
            }
            else if (value == null)
            {
                // Destroyed on the native side while a managed reference is still held.
                writer.Value("<destroyed>");
            }
            else
            {
                var gameObject = value as GameObject;
                writer.Value(gameObject != null ? paths.NamePath(gameObject.transform) : value.GetType().Name + ":" + value.name);
            }
        }

        private bool TryWrite(object value, JsonWriter writer)
        {
            var unityObject = value as UnityEngine.Object;
            if (!ReferenceEquals(unityObject, null))
            {
                WriteReference(writer, unityObject);
                return true;
            }

            if (value is Vector3)
            {
                WriteVector(writer, (Vector3)value);
                return true;
            }

            if (value is Vector2)
            {
                var vector = (Vector2)value;
                writer.BeginArray().Value(vector.x).Value(vector.y).EndArray();
                return true;
            }

            if (value is Quaternion)
            {
                WriteQuaternion(writer, (Quaternion)value);
                return true;
            }

            if (value is Vector4)
            {
                var vector = (Vector4)value;
                writer.BeginArray().Value(vector.x).Value(vector.y).Value(vector.z).Value(vector.w).EndArray();
                return true;
            }

            if (value is Color)
            {
                WriteColor(writer, (Color)value);
                return true;
            }

            if (value is Rect)
            {
                WriteRect(writer, (Rect)value);
                return true;
            }

            if (value is LayerMask)
            {
                writer.Value(((LayerMask)value).value);
                return true;
            }

            var curve = value as AnimationCurve;
            if (curve != null)
            {
                writer.Value("<AnimationCurve " + curve.length + " keys>");
                return true;
            }

            var variable = value as NamedVariable;
            if (variable != null)
            {
                WriteParameterVariable(writer, variable);
                return true;
            }

            var fsmEvent = value as FsmEvent;
            if (fsmEvent != null)
            {
                writer.Value(fsmEvent.Name);
                return true;
            }

            var owner = value as FsmOwnerDefault;
            if (owner != null)
            {
                if (owner.OwnerOption == OwnerDefaultOption.UseOwner)
                {
                    writer.Value("<owner>");
                }
                else if (owner.GameObject == null)
                {
                    writer.Null();
                }
                else
                {
                    WriteParameterVariable(writer, owner.GameObject);
                }

                return true;
            }

            var fsmVar = value as FsmVar;
            if (fsmVar != null)
            {
                WriteFsmVar(writer, fsmVar);
                return true;
            }

            return false;
        }

        /// <summary>
        /// An action parameter is either a literal or bound to a named FSM variable; the binding matters for
        /// classifying what an action touches, so it is kept: { "var": name, "v": value }.
        /// </summary>
        private void WriteParameterVariable(JsonWriter writer, NamedVariable variable)
        {
            if (variable.IsNone)
            {
                writer.Null();
                return;
            }

            if (string.IsNullOrEmpty(variable.Name))
            {
                WriteVariableValue(writer, variable);
                return;
            }

            writer.BeginObject().Property(DumpFormat.VariableName, variable.Name).Name(DumpFormat.VariableValue);
            WriteVariableValue(writer, variable);
            writer.EndObject();
        }

        private void WriteFsmVar(JsonWriter writer, FsmVar fsmVar)
        {
            // Read everything that can throw before opening the object, so a failure cannot leave it half written.
            string type = fsmVar.Type.ToString();
            object current;
            try
            {
                current = fsmVar.GetValue();
            }
            catch (Exception exception)
            {
                current = ObjectDumper.ErrorMarker(exception);
            }

            writer.BeginObject().Property(DumpFormat.VariableName, fsmVar.variableName ?? string.Empty).Property("type", type)
                .Name(DumpFormat.VariableValue);
            Parameters.Write(writer, current);
            writer.EndObject();
        }

        private void WriteReference(JsonWriter writer, UnityEngine.Object value)
        {
            if (value == null)
            {
                writer.Value("<destroyed>");
                return;
            }

            var gameObject = value as GameObject;
            if (gameObject != null)
            {
                writer.BeginObject().Property("go", paths.NamePath(gameObject.transform)).EndObject();
                return;
            }

            var component = value as Component;
            if (component != null)
            {
                writer.BeginObject().Property("comp", component.GetType().Name).Property("go", paths.NamePath(component.transform)).EndObject();
                return;
            }

            writer.BeginObject().Property("obj", value.GetType().Name).Property("name", value.name).EndObject();
        }
    }
}
