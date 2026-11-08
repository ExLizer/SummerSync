using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The Satsuma's paint. What is assumed about the game (dump of 2026-10-14, "bumper rear(Clone) :: Paint"): the body
    /// ("car body(xxxxx)") and the parts that can be painted have a machine "Paint" with an integer "PaintType" (0 not
    /// painted, 1 regular, 2 metallic, 3 matte…), a colour "Color" and the part's renderer in "MeshRenderer"; the spray
    /// can tints the renderer, the machine saves what it shows, and on load it takes the saved type and colour through
    /// "Paint type", which puts the type's material on and then the colour.
    ///
    /// Each painted thing is a tally under its name ("car/paint/hood(Clone)"): its type and its colour, said by the game
    /// where it was painted, last one standing (<see cref="TallyReplication.Set"/>). Another game is given the type and
    /// the colour and taken to "Paint type", as on load. A part is known by its own name wherever it is, so that a
    /// door painted on the floor is the same door on the car.
    /// </summary>
    internal sealed partial class PartSync
    {
        private const string PaintMachine = "Paint";
        private const string PaintTally = "car/paint/";
        private const string PaintTypeVariable = "PaintType";
        private const string PaintColorVariable = "Color";
        private const string PaintRendererVariable = "MeshRenderer";
        private const string PaintState = "Paint type";
        private const double PaintLookSeconds = 0.5;

        // The game's own "Set color" waits two seconds after setting the colour: what it shows meanwhile is its doing.
        private const double PaintSettleSeconds = 3.0;

        private readonly List<Paintable> paintables = new List<Paintable>();
        private TallyReplication paintTallies;
        private double nextPaintLook;

        private void BindPaint()
        {
            paintables.Clear();
            var seen = new HashSet<int>();
            var partObjects = new Dictionary<int, string>();
            for (int i = 0; i < parts.Entries.Length; i++)
            {
                PartEntry entry = parts.Entries[i];
                if (entry.Family != PartFamily.Parented && entry.Family != PartFamily.Jointed)
                {
                    continue;
                }

                GameObject part = SatsumaParts.PartOf(entry, slots[i].Data);
                if (part != null)
                {
                    partObjects[part.GetInstanceID()] = part.name;
                    AddPaintables(part.transform, part.name, seen);
                }
            }

            // The body, and whatever else of the car is painted that is not a part that comes off whole.
            PlayMakerFSM[] machines = car.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == PaintMachine && !seen.Contains(machines[i].GetInstanceID()) && !Below(machines[i].transform, partObjects))
                {
                    AddPaintable(machines[i], PlainPath(machines[i].transform, car), seen);
                }
            }

            paintTallies = manager.Tallies;
            if (paintTallies != null)
            {
                paintTallies.StateWanted += OnPaintWanted;

                // What the host had was handed out once, maybe before this listened (the shop asks as soon as it is
                // found; seen in a review on 2026-10-15): asked again, the paint included. On the host it does nothing.
                paintTallies.RequestAll();
            }

            log.Info(Category, "Car: " + paintables.Count + " painted thing(s) shared (the body and the parts that can be painted).");
            if (paintTallies != null && manager.Mode == NetworkMode.Host)
            {
                TellPaint();
            }
        }

        // The host: the paint as this game shows it now is the session's from the start. The car may have been painted
        // since the save the guests load, or before hosting; only what is painted from here on would be told otherwise.
        private void TellPaint()
        {
            int painted = 0;
            for (int i = 0; i < paintables.Count; i++)
            {
                Paintable paintable = paintables[i];
                paintTallies.Set(paintable.Name, paintable.Known);
                painted += paintable.Known[0] != 0 ? 1 : 0;
            }

            log.Info(Category, "Car: the paint told as this game shows it now (" + paintables.Count + " thing(s), " + painted + " painted), for whoever joins.");
        }

        private void ReleasePaint()
        {
            if (paintTallies != null)
            {
                paintTallies.StateWanted -= OnPaintWanted;
                paintTallies = null;
            }

            paintables.Clear();
        }

        // The machines "Paint" of a part, named by the part and the path below it.
        private void AddPaintables(Transform part, string name, HashSet<int> seen)
        {
            PlayMakerFSM[] machines = part.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName != PaintMachine || seen.Contains(machines[i].GetInstanceID()))
                {
                    continue;
                }

                string below = PlainPath(machines[i].transform, part);
                AddPaintable(machines[i], below.Length == 0 ? name : name + "/" + below, seen);
            }
        }

        private void AddPaintable(PlayMakerFSM machine, string name, HashSet<int> seen)
        {
            seen.Add(machine.GetInstanceID());
            FsmInt type = machine.FsmVariables.FindFsmInt(PaintTypeVariable);
            FsmColor color = machine.FsmVariables.FindFsmColor(PaintColorVariable);
            FsmObject renderer = machine.FsmVariables.FindFsmObject(PaintRendererVariable);
            if (type == null || color == null)
            {
                return;
            }

            var paintable = new Paintable { Machine = machine, Name = PaintTally + name, Type = type, Color = color, Renderer = renderer };
            paintable.Known = ReadPaint(paintable);
            paintables.Add(paintable);
        }

        private static bool Below(Transform at, Dictionary<int, string> partObjects)
        {
            for (Transform up = at; up != null; up = up.parent)
            {
                if (partObjects.ContainsKey(up.gameObject.GetInstanceID()))
                {
                    return true;
                }
            }

            return false;
        }

        private static string PlainPath(Transform transform, Transform root)
        {
            var names = new List<string>();
            for (Transform at = transform; at != null && at != root; at = at.parent)
            {
                names.Add(at.name);
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        // What it shows: its type and the colour of its renderer (the spray can tints it), 0 to 255 each.
        private static int[] ReadPaint(Paintable paintable)
        {
            Color color = paintable.Color.Value;
            Renderer renderer = paintable.Renderer == null ? null : paintable.Renderer.Value as Renderer;
            if (renderer != null && renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty("_Color"))
            {
                color = renderer.sharedMaterial.color;
            }

            return new[] { paintable.Type.Value, Byte(color.r), Byte(color.g), Byte(color.b), Byte(color.a) };
        }

        private static int Byte(float channel)
        {
            int value = Mathf.RoundToInt(channel * 255f);
            return value < 0 ? 0 : value > 255 ? 255 : value;
        }

        private static bool Same(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                // A channel one step off is the same colour read back.
                if (Math.Abs(a[i] - b[i]) > (i == 0 ? 0 : 1))
                {
                    return false;
                }
            }

            return true;
        }

        private void OnPaintWanted(string name, int[] values)
        {
            if (name == null || !name.StartsWith(PaintTally, StringComparison.Ordinal) || values == null || values.Length < 5)
            {
                return;
            }

            for (int i = 0; i < paintables.Count; i++)
            {
                if (paintables[i].Name == name)
                {
                    paintables[i].Wanted = values;
                    return;
                }
            }
        }

        private void WorkPaint(double now)
        {
            if (paintTallies == null || now < nextPaintLook)
            {
                return;
            }

            nextPaintLook = now + PaintLookSeconds;
            for (int i = 0; i < paintables.Count; i++)
            {
                Paintable paintable = paintables[i];

                // Unity's null check is also true for a destroyed object.
                if (paintable.Machine == null)
                {
                    continue;
                }

                int[] shown = ReadPaint(paintable);
                if (paintable.Wanted != null)
                {
                    int[] wanted = paintable.Wanted;
                    paintable.Wanted = null;
                    paintable.Known = wanted;
                    if (!Same(shown, wanted))
                    {
                        Paint(paintable, wanted);
                        paintable.SettleUntil = now + PaintSettleSeconds;
                    }

                    continue;
                }

                if (now < paintable.SettleUntil || Same(shown, paintable.Known))
                {
                    continue;
                }

                // Painted here.
                paintable.Known = shown;
                paintTallies.Set(paintable.Name, shown);
                log.Info(Category, "Car: '" + paintable.Name.Substring(PaintTally.Length) + "' painted here (type " + shown[0] + "): the others are told.");
            }
        }

        // The game's way, as on load: the type and the colour, then "Paint type".
        private void Paint(Paintable paintable, int[] values)
        {
            var color = new Color(values[1] / 255f, values[2] / 255f, values[3] / 255f, values[4] / 255f);
            PlayMakerFSM machine = paintable.Machine;
            string eventName = EventPrefix + PaintState;
            bool canState = DoorSync.HasState(machine, PaintState);
            if (canState && !DoorSync.HasGlobalTransition(machine, eventName))
            {
                // MSCLoader rebuilds the machine's data when a transition is added: its variables are found again.
                MSCLoader.PlayMakerExtensions.AddGlobalTransition(machine, eventName, PaintState);
                paintable.Type = machine.FsmVariables.FindFsmInt(PaintTypeVariable) ?? paintable.Type;
                paintable.Color = machine.FsmVariables.FindFsmColor(PaintColorVariable) ?? paintable.Color;
                paintable.Renderer = machine.FsmVariables.FindFsmObject(PaintRendererVariable) ?? paintable.Renderer;
            }

            paintable.Type.Value = values[0];
            paintable.Color.Value = color;
            Renderer renderer = paintable.Renderer == null ? null : paintable.Renderer.Value as Renderer;
            bool byType = values[0] != 0 && canState && machine.enabled && machine.gameObject.activeInHierarchy;
            if (byType)
            {
                machine.SendEvent(eventName);
            }
            else
            {
                // Not painted by type, or its machine is switched off: the colour as it is.
                if (renderer != null && renderer.material != null)
                {
                    renderer.material.color = color;
                }
            }

            log.Info(Category, "Car: '" + paintable.Name.Substring(PaintTally.Length) + "' painted as another player did (type " + values[0] + ").");
        }

        private sealed class Paintable
        {
            public PlayMakerFSM Machine;
            public string Name;
            public FsmInt Type;
            public FsmColor Color;
            public FsmObject Renderer;
            public int[] Known;
            public int[] Wanted;
            public double SettleUntil;
        }
    }
}
