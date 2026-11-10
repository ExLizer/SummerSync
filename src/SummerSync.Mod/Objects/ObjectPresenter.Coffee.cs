using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using SummerSync.Unity.World;
using UnityEngine;

namespace SummerSync.Unity.Objects
{
    /// <summary>
    /// The coffee pan and the coffee cup, as things whose state is told (ObjectPresenter.Items).
    ///
    /// What is assumed about the game (decoded level of 2026-10-18; docs/GAME_NOTES.md, "La cafetera y la taza"):
    /// - The pan ("coffee pan(itemx)") has no "Use" of its own. Its machine "Data" keeps "Water", "Ground" and "Coffee",
    ///   and "CapOpen". The lid is its child "Cap", machine "Use": the state "ON" takes the lid off (the sound, "CapOpen",
    ///   the lid's mesh off and "Functions" on, where the water and the ground coffee go in and their surfaces are drawn),
    ///   and "Sound" then "OFF" puts it back.
    /// - The cup ("coffee cup(itemx)") keeps "Coffee" and "Caffeine" in its "Use", and draws them (the height of
    ///   "mesh/Pivot/surface", its colour's alpha) only while it is being filled and once after loading.
    /// </summary>
    internal sealed partial class ObjectPresenter
    {
        private const string PanName = "coffee pan(itemx)";
        private const string CupName = "coffee cup(itemx)";
        private const string PanMachine = "Data";
        private const string PanPrefix = "Data:";
        private const string CapVariable = "CapOpen";
        private const string CapChild = "Cap";
        private const string CapOnState = "ON";
        private const string CapOffState = "Sound";
        private const string CapOnEvent = "SUMMERSYNC CAP ON";
        private const string CapOffEvent = "SUMMERSYNC CAP OFF";
        private const string CupSurface = "mesh/Pivot/surface";
        private const string CupCoffee = "Coffee";
        private const string CupCaffeine = "Caffeine";
        private const string CupHeight = "Pos";

        // The cup's own numbers: its coffee's surface is that much over 3.75 up, and its colour as strong as the caffeine.
        private const float CupFull = 0.3f;
        private const float CupHeightDivisor = 3.75f;
        private const float StrongestCaffeine = 1.5f;
        private static readonly Color CoffeeColour = new Color(0.463f, 0.105f, 0f, 0f);

        // The states in which the cup draws itself.
        private static readonly string[] CupBusyStates = { "Pour", "Play anim" };

        private static readonly string[] PanNumbers = { "Water", "Ground", "Coffee" };

        // On a fire the pan's "Data" is in "Cooking": every frame, in every game, the ground coffee goes down and the coffee
        // up. Told as news, two games told each other the pan about twice a second each for as long as it cooked (measured
        // on 2026-11-09), each writing its own over the other's. While it cooks those two numbers are each game's own; they
        // are told again once it is done, and a world that joins takes them as the host has them.
        private const string CookingState = "Cooking";
        private static readonly string[] CookedNumbers = { "Ground", "Coffee" };

        private PlayMakerFSM PanOf(Tracked item)
        {
            if (!item.PanSearched)
            {
                item.PanSearched = true;
                if (item.Transform.name == PanName)
                {
                    item.Pan = DoorSync.FindMachine(item.Transform, PanMachine);
                    Transform cap = item.Transform.Find(CapChild);
                    item.Cap = cap == null ? null : DoorSync.FindMachine(cap, UseMachine);
                }
            }

            // Unity's null check is also true for a destroyed component.
            return item.Pan == null ? null : item.Pan;
        }

        // The pan's lid and what it holds, added to the thing's text and values. Whole: also what cooks, while it cooks.
        private void ReadPan(PlayMakerFSM pan, List<ItemValue> values, bool whole)
        {
            bool cooking = !whole && pan.ActiveStateName == CookingState;
            FsmVariables variables = pan.FsmVariables;
            FsmBool open = variables.FindFsmBool(CapVariable);
            if (open != null)
            {
                itemText.Append('|').Append(PanPrefix).Append(CapVariable).Append(open.Value ? "=1" : "=0");
                if (values != null)
                {
                    values.Add(ItemValue.Bool(PanPrefix + CapVariable, open.Value));
                }
            }

            for (int i = 0; i < PanNumbers.Length; i++)
            {
                FsmFloat number = variables.FindFsmFloat(PanNumbers[i]);
                if (number == null || (cooking && Array.IndexOf(CookedNumbers, PanNumbers[i]) >= 0))
                {
                    continue;
                }

                itemText.Append('|').Append(PanPrefix).Append(PanNumbers[i]).Append('=').Append(number.Value.ToString("0.00", CultureInfo.InvariantCulture));
                if (values != null)
                {
                    values.Add(ItemValue.Float(PanPrefix + PanNumbers[i], number.Value));
                }
            }
        }

        // Another game's pan: its numbers written, and its lid taken off or put back the game's own way.
        private void ApplyPan(Tracked item, IList<ItemValue> values)
        {
            PlayMakerFSM pan = PanOf(item);
            if (pan == null)
            {
                return;
            }

            FsmVariables variables = pan.FsmVariables;
            for (int i = 0; i < values.Count; i++)
            {
                ItemValue value = values[i];
                if (!value.Name.StartsWith(PanPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string name = value.Name.Substring(PanPrefix.Length);
                if (value.Kind == ItemWire.KindBool && name == CapVariable)
                {
                    TurnCap(item, variables.FindFsmBool(CapVariable), value.Whole != 0);
                }
                else if (value.Kind == ItemWire.KindFloat && Array.IndexOf(PanNumbers, name) >= 0)
                {
                    FsmFloat number = variables.FindFsmFloat(name);
                    if (number != null)
                    {
                        number.Value = value.Number;
                    }
                }
            }
        }

        private void TurnCap(Tracked item, FsmBool open, bool wanted)
        {
            if (open == null || open.Value == wanted)
            {
                return;
            }

            PlayMakerFSM cap = item.Cap;

            // Unity's null check is also true for a destroyed component.
            if (cap != null && cap.enabled && cap.gameObject.activeInHierarchy)
            {
                try
                {
                    if (!item.CapHooked)
                    {
                        item.CapHooked = true;
                        if (DoorSync.HasState(cap, CapOnState) && !DoorSync.HasGlobalTransition(cap, CapOnEvent))
                        {
                            cap.AddGlobalTransition(CapOnEvent, CapOnState);
                        }

                        if (DoorSync.HasState(cap, CapOffState) && !DoorSync.HasGlobalTransition(cap, CapOffEvent))
                        {
                            cap.AddGlobalTransition(CapOffEvent, CapOffState);
                        }
                    }

                    cap.SendEvent(wanted ? CapOnEvent : CapOffEvent);
                }
                catch (Exception exception)
                {
                    if (!item.CapComplained)
                    {
                        item.CapComplained = true;
                        log.Warning(LogCategory.Fsm, "Could not turn the coffee pan's lid: " + exception.Message);
                    }
                }
            }

            if (open.Value != wanted)
            {
                // The game did not follow: the lid's state is written, and its look is right again once it is used here.
                open.Value = wanted;
                if (!item.CapComplained)
                {
                    item.CapComplained = true;
                    log.Warning(LogCategory.Fsm, "The coffee pan's lid did not " + (wanted ? "come off" : "go on") + " when told to (state '" + (cap == null ? "none" : cap.ActiveStateName) + "'); its state was set without its look.");
                }
            }
        }

        // Another game's cup: its coffee drawn as its numbers say, which the game does only while this game fills it.
        private void ShowCup(Tracked item, PlayMakerFSM use)
        {
            if (use == null || item.Transform.name != CupName || Array.IndexOf(CupBusyStates, use.ActiveStateName) >= 0)
            {
                return;
            }

            FsmVariables variables = use.FsmVariables;
            FsmFloat coffee = variables.FindFsmFloat(CupCoffee);
            FsmFloat caffeine = variables.FindFsmFloat(CupCaffeine);
            Transform surface = item.Transform.Find(CupSurface);
            if (coffee == null || caffeine == null || surface == null)
            {
                return;
            }

            float height = Mathf.Clamp(coffee.Value, 0f, CupFull) / CupHeightDivisor;
            FsmFloat drawn = variables.FindFsmFloat(CupHeight);
            if (drawn != null)
            {
                drawn.Value = height;
            }

            surface.localPosition = new Vector3(0f, 0f, height);
            Renderer renderer = surface.GetComponent<Renderer>();
            if (renderer != null)
            {
                Color colour = CoffeeColour;
                colour.a = Mathf.Clamp(caffeine.Value, 0f, StrongestCaffeine);
                renderer.material.SetColor("_Color", colour);
            }
        }
    }
}
