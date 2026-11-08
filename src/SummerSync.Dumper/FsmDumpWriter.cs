using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using SummerSync.Core.Dump;
using SummerSync.Core.Json;
using UnityEngine;

namespace SummerSync.Dumper
{
    /// <summary>
    /// Writes one PlayMaker FSM: identity, active state and variable values, plus states, transitions and
    /// actions in a full dump. Strictly read-only towards the game.
    /// </summary>
    internal sealed class FsmDumpWriter
    {
        private const string ActionsNamespace = "HutongGames.PlayMaker.Actions";
        private const string StringSection = "string";

        // A state materialises its action objects on first use. Reading FsmState.Actions would force that for
        // states the game has not touched yet, so for those the serialized names are read instead.
        private static readonly FieldInfo ActionNamesField =
            typeof(ActionData).GetField("actionNames", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ActionEnabledField =
            typeof(ActionData).GetField("actionEnabled", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly string[] ActionAssemblies = { "Assembly-CSharp", "Assembly-CSharp-firstpass", "PlayMaker" };

        private readonly PathCache paths;
        private readonly GameValueWriter values;
        private readonly Dictionary<Type, string> categories = new Dictionary<Type, string>();
        private readonly Dictionary<string, Type> actionTypes = new Dictionary<string, Type>();
        private readonly Dictionary<int, PlayMakerFSM[]> siblings = new Dictionary<int, PlayMakerFSM[]>();
        private readonly Dictionary<string, int> usedNames = new Dictionary<string, int>();

        public FsmDumpWriter(PathCache paths, GameValueWriter values)
        {
            this.paths = paths;
            this.values = values;
        }

        public int StateCount { get; private set; }

        public int ActionCount { get; private set; }

        public int VariableCount { get; private set; }

        public void Write(JsonWriter writer, PlayMakerFSM component, bool full)
        {
            Fsm fsm = component.Fsm;
            GameObject gameObject = component.gameObject;

            writer.BeginObject();
            writer.Property(DumpFormat.Path, paths.NamePath(component.transform));
            writer.Property(DumpFormat.IndexPath, paths.IndexPath(component.transform));
            writer.Property(DumpFormat.Name, fsm.Name ?? string.Empty);
            writer.Property(DumpFormat.Ordinal, OrdinalOf(component, fsm.Name));
            writer.Property(DumpFormat.InstanceId, component.GetInstanceID());
            writer.Property(DumpFormat.Enabled, component.enabled);
            writer.Property(DumpFormat.ActiveInHierarchy, gameObject.activeInHierarchy);
            writer.Property(DumpFormat.Initialized, fsm.Initialized);
            writer.Property(DumpFormat.ActiveState, fsm.ActiveStateName ?? string.Empty);

            writer.Name(DumpFormat.Variables);
            WriteVariables(writer, fsm.Variables);

            if (full)
            {
                WriteStructure(writer, fsm);
            }

            writer.EndObject();
        }

        /// <summary>Writes variables grouped by type: { "float": { name: value }, "bool": { ... } }.</summary>
        public void WriteVariables(JsonWriter writer, FsmVariables variables)
        {
            writer.BeginObject();
            if (variables != null)
            {
                WriteSection(writer, "float", variables.FloatVariables);
                WriteSection(writer, "int", variables.IntVariables);
                WriteSection(writer, "bool", variables.BoolVariables);
                WriteSection(writer, StringSection, variables.StringVariables);
                WriteSection(writer, "vector2", variables.Vector2Variables);
                WriteSection(writer, "vector3", variables.Vector3Variables);
                WriteSection(writer, "rect", variables.RectVariables);
                WriteSection(writer, "quaternion", variables.QuaternionVariables);
                WriteSection(writer, "color", variables.ColorVariables);
                WriteSection(writer, "gameObject", variables.GameObjectVariables);
                WriteSection(writer, "object", variables.ObjectVariables);
                WriteSection(writer, "material", variables.MaterialVariables);
                WriteSection(writer, "texture", variables.TextureVariables);
            }

            writer.EndObject();
        }

        private void WriteSection(JsonWriter writer, string section, NamedVariable[] variables)
        {
            if (variables == null || variables.Length == 0)
            {
                return;
            }

            writer.Name(section).BeginObject();
            usedNames.Clear();
            for (int i = 0; i < variables.Length; i++)
            {
                NamedVariable variable = variables[i];
                if (variable == null)
                {
                    continue;
                }

                string name = variable.Name ?? string.Empty;
                writer.Name(UniqueName(name));
                if (section == StringSection && DumpRedaction.IsSecret(name))
                {
                    // The game keeps credentials for its own online services in plain string variables.
                    writer.Value(DumpRedaction.Placeholder);
                }
                else
                {
                    values.WriteVariableValue(writer, variable);
                }

                VariableCount++;
            }

            writer.EndObject();
        }

        // Variable names should be unique per type, but a JSON object must not repeat a name if they are not.
        private string UniqueName(string name)
        {
            int count;
            usedNames.TryGetValue(name, out count);
            usedNames[name] = count + 1;
            return count == 0 ? name : name + "#" + (count + 1);
        }

        /// <summary>Position among the FSMs with the same name on the same GameObject, in component order.</summary>
        private int OrdinalOf(PlayMakerFSM component, string name)
        {
            int key = component.gameObject.GetInstanceID();
            PlayMakerFSM[] onObject;
            if (!siblings.TryGetValue(key, out onObject))
            {
                onObject = component.gameObject.GetComponents<PlayMakerFSM>();
                siblings[key] = onObject;
            }

            int ordinal = 0;
            for (int i = 0; i < onObject.Length; i++)
            {
                if (ReferenceEquals(onObject[i], component))
                {
                    break;
                }

                if (onObject[i] != null && onObject[i].Fsm.Name == name)
                {
                    ordinal++;
                }
            }

            return ordinal;
        }

        private void WriteStructure(JsonWriter writer, Fsm fsm)
        {
            writer.Property(DumpFormat.StartState, fsm.StartState ?? string.Empty);

            writer.Name(DumpFormat.Events).BeginArray();
            FsmEvent[] events = fsm.Events;
            if (events != null)
            {
                for (int i = 0; i < events.Length; i++)
                {
                    if (events[i] != null)
                    {
                        writer.Value(events[i].Name);
                    }
                }
            }

            writer.EndArray();

            writer.Name(DumpFormat.GlobalTransitions);
            WriteTransitions(writer, fsm.GlobalTransitions);

            writer.Name(DumpFormat.States).BeginArray();
            FsmState[] states = fsm.States;
            if (states != null)
            {
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i] != null)
                    {
                        WriteState(writer, states[i]);
                    }
                }
            }

            writer.EndArray();
        }

        private void WriteState(JsonWriter writer, FsmState state)
        {
            StateCount++;
            bool loaded = state.ActionsLoaded;

            writer.BeginObject();
            writer.Property(DumpFormat.Name, state.Name ?? string.Empty);
            writer.Property(DumpFormat.ActionsLoaded, loaded);
            writer.Name(DumpFormat.Transitions);
            WriteTransitions(writer, state.Transitions);

            writer.Name(DumpFormat.Actions).BeginArray();
            if (loaded)
            {
                WriteLoadedActions(writer, state.Actions);
            }
            else
            {
                WriteSerializedActions(writer, state.ActionData);
            }

            writer.EndArray();
            writer.EndObject();
        }

        private static void WriteTransitions(JsonWriter writer, FsmTransition[] transitions)
        {
            writer.BeginArray();
            if (transitions != null)
            {
                for (int i = 0; i < transitions.Length; i++)
                {
                    FsmTransition transition = transitions[i];
                    if (transition == null)
                    {
                        continue;
                    }

                    writer.BeginObject()
                        .Property(DumpFormat.Event, transition.EventName ?? string.Empty)
                        .Property(DumpFormat.ToState, transition.ToState ?? string.Empty)
                        .EndObject();
                }
            }

            writer.EndArray();
        }

        private void WriteLoadedActions(JsonWriter writer, FsmStateAction[] actions)
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; i++)
            {
                FsmStateAction action = actions[i];
                if (action == null)
                {
                    continue;
                }

                ActionCount++;
                Type type = action.GetType();
                writer.BeginObject();
                writer.Property(DumpFormat.ActionType, DisplayName(type));
                writer.Property(DumpFormat.ActionCategory, CategoryOf(type));
                writer.Property(DumpFormat.ActionEnabled, action.Enabled);
                writer.Name(DumpFormat.ActionParameters).BeginObject();
                values.Parameters.WriteFieldProperties(writer, action);
                writer.EndObject();
                writer.EndObject();
            }
        }

        /// <summary>Action type names of a state whose actions were never materialised. No parameter values.</summary>
        private void WriteSerializedActions(JsonWriter writer, ActionData data)
        {
            if (data == null || ActionNamesField == null)
            {
                return;
            }

            var names = ActionNamesField.GetValue(data) as List<string>;
            if (names == null)
            {
                return;
            }

            var enabled = ActionEnabledField == null ? null : ActionEnabledField.GetValue(data) as List<bool>;
            for (int i = 0; i < names.Count; i++)
            {
                ActionCount++;
                string name = names[i] ?? string.Empty;
                Type type = ResolveActionType(name);
                writer.BeginObject();
                writer.Property(DumpFormat.ActionType, type != null ? DisplayName(type) : ShortName(name));
                writer.Property(DumpFormat.ActionCategory, type != null ? CategoryOf(type) : string.Empty);
                writer.Property(DumpFormat.ActionEnabled, enabled == null || i >= enabled.Count || enabled[i]);
                writer.EndObject();
            }
        }

        private static string DisplayName(Type type)
        {
            return type.Namespace == ActionsNamespace ? type.Name : type.FullName;
        }

        private static string ShortName(string fullName)
        {
            return fullName.StartsWith(ActionsNamespace + ".", StringComparison.Ordinal)
                ? fullName.Substring(ActionsNamespace.Length + 1)
                : fullName;
        }

        /// <summary>The category PlayMaker shows for an action in its editor, e.g. "Audio" or "Physics".</summary>
        private string CategoryOf(Type type)
        {
            string category;
            if (!categories.TryGetValue(type, out category))
            {
                var attribute = (ActionCategoryAttribute)Attribute.GetCustomAttribute(type, typeof(ActionCategoryAttribute));
                category = attribute == null ? string.Empty : attribute.Category ?? string.Empty;
                categories[type] = category;
            }

            return category;
        }

        // Resolved here rather than through ActionData.GetActionType, which logs errors and fills PlayMaker's
        // own caches as a side effect.
        private Type ResolveActionType(string fullName)
        {
            Type type;
            if (actionTypes.TryGetValue(fullName, out type))
            {
                return type;
            }

            for (int i = 0; i < ActionAssemblies.Length && type == null; i++)
            {
                type = Type.GetType(fullName + ", " + ActionAssemblies[i], false);
            }

            actionTypes[fullName] = type;
            return type;
        }
    }
}
