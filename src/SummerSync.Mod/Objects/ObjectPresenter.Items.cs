using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using UnityEngine;

namespace SummerSync.Unity.Objects
{
    /// <summary>
    /// What happens to a shared object besides moving. The game keeps it in the variables of the thing's state
    /// machine "Use" and in its name (dump of 2026-10-12, docs/GAME_NOTES.md): a juice can drunk empty has
    /// "ContainsJuice" false and is renamed "empty plastic can(itemx)" a few seconds later; a beer case counts the
    /// bottles taken out in "DestroyedBottles" and destroys one of its children for each. A can of fuel or of any
    /// other fluid keeps how much it holds in the numbers called "Fluid…" of the machine "Data" of its child
    /// "FluidTrigger" (or "TwoStrokeTrigger" and the like), and is renamed after it ("gasoline 40%(itemx)"). Whichever game changes them (a can can be
    /// drunk off a table, without being picked up) tells the whole, numbers and truth values and name; the others
    /// write them into their copy, and take out as many bottles as were taken.
    /// </summary>
    internal sealed partial class ObjectPresenter
    {
        // About four times a second each, at sixty frames a second; and not told more than once a second.
        private const int ItemLookEvery = 15;
        private const double ItemTellInterval = 1.0;

        // News of a thing not paired here yet is kept this long: a world that joins pairs some things only once it has made
        // what the host's game made since the save.
        private const double ItemNewsKeep = 45.0;

        private const string BottlesVariable = "DestroyedBottles";
        private const string ContentsMachine = "Data";

        // The child that holds a can's fluid. The other cans keep it the same way under their own name (dump of 2026-10-15),
        // and copy it into the "Fluid" of their "Use" as they pour: with only that copy told, each game wrote its own back
        // over the other's once a second, and a two-stroke can poured into a moped was full again in the other game.
        private static readonly string[] ContentsChildren =
        {
            "FluidTrigger", "TwoStrokeTrigger", "MotorOilTrigger", "CoolantTrigger", "BrakeFluidTrigger",
        };
        private const string ContentsPrefix = "FluidTrigger:";
        private const string FluidPrefix = "Fluid";

        // Numbers the game changes by itself, on every machine, all the time: not news. Told as news, each game tells its
        // own every second and writes the other's over it, and with them what a player changed: the kilju bucket brews in
        // every game (its "Use" counts "Time", adds "Vinegar" with the lid off and works out the rest from them each loop;
        // dump of 2026-10-15), and a lit lantern counts "Burning" up to a "Burntime" each game rolls, with "Pos" its height.
        // The bucket's lid went back off in a game that had it on (log of 2026-11-08: "LidOn" from the other game's last
        // brewing news). Of the coffee cup, "Pos" is its coffee over 3.75, worked out each frame.
        //
        // A car battery, the same (decoded level of 2026-10-18, "battery0 :: Use"): once a second its "Use" takes
        // "DischargeRate" off "Charge", and under 109 also wears it out ("ChargeMax" down, "DischargeRate" up), with nothing to
        // stop "Charge" below nothing; the charger adds to "Charge" every frame. All of it runs in every game, near the
        // battery or not (measured on 2026-11-09 with a player at the shop), and no player changes those numbers by hand.
        // Told, a worn battery had both games telling each other its three numbers every few seconds (294 times in a session
        // of 72 minutes).
        private static readonly string[] Unshared =
        {
            "Condition", "Distance", "FridgeDistance", "SpoilingRate", "SpoilingRateFridge", "DestroyProgress",
            "Time", "Vinegar", "Alcohol", "Sweetness", "SugarWaterRatio", "Math0", "Math1", "Math2",
            "Burning", "Burntime", "Pos",
            "Charge", "ChargeMax", "DischargeRate",
        };

        // Of those, what a world that joins takes from the host once, as it is there (food's spoiling, how far the bucket
        // has brewed, how a battery is: its "DischargeRate" is saved and never loaded, so a game that has just loaded has the
        // prefab's).
        private static readonly string[] Joining = { "Condition", "Time", "Vinegar", "Alcohol", "Charge", "ChargeMax", "DischargeRate" };

        private readonly List<ItemNews> itemNews = new List<ItemNews>();
        private readonly List<ItemValue> itemValues = new List<ItemValue>();
        private readonly StringBuilder itemText = new StringBuilder();
        private ItemReplication items;

        private void AttachItems(ItemReplication next)
        {
            if (next == items)
            {
                return;
            }

            if (items != null)
            {
                items.StateWanted -= OnItemStateWanted;
            }

            items = next;
            itemNews.Clear();
            if (items != null)
            {
                items.StateWanted += OnItemStateWanted;
            }
        }

        private void OnItemStateWanted(ushort id, string name, IList<ItemValue> values)
        {
            itemNews.Add(new ItemNews { Id = id, Name = name, Values = new List<ItemValue>(values), Since = frameNow });
        }

        // What the others' games did to things, done to the copies here.
        private void ApplyItemNews()
        {
            for (int n = itemNews.Count - 1; n >= 0; n--)
            {
                ItemNews news = itemNews[n];
                Tracked item = FindTracked(news.Id);
                if (item == null)
                {
                    if (frameNow - news.Since > ItemNewsKeep)
                    {
                        itemNews.RemoveAt(n);
                    }

                    continue;
                }

                itemNews.RemoveAt(n);

                // Unity's null check is also true for a destroyed object.
                if (item.Transform == null || item.Body == null)
                {
                    continue;
                }

                ApplyItem(item, news);
            }
        }

        private Tracked FindTracked(ushort id)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Shared.HasId && tracked[i].Shared.Id == id)
                {
                    return tracked[i];
                }
            }

            return null;
        }

        private void ApplyItem(Tracked item, ItemNews news)
        {
            GameObject thing = item.Transform.gameObject;
            PlayMakerFSM use = UseOf(item);
            string before = ReadItem(item, null);
            FsmInt bottles = use == null ? null : use.FsmVariables.FindFsmInt(BottlesVariable);
            int bottlesBefore = bottles == null ? 0 : bottles.Value;
            string nameBefore = thing.name;
            PlayMakerFSM contents = ContentsOf(item);
            if (contents != null)
            {
                for (int i = 0; i < news.Values.Count; i++)
                {
                    ItemValue value = news.Values[i];
                    if (value.Kind == ItemWire.KindFloat && value.Name.StartsWith(ContentsPrefix, System.StringComparison.Ordinal))
                    {
                        FsmFloat variable = contents.FsmVariables.FindFsmFloat(value.Name.Substring(ContentsPrefix.Length));
                        if (variable != null)
                        {
                            variable.Value = value.Number;
                        }
                    }
                }
            }

            if (use != null)
            {
                FsmVariables variables = use.FsmVariables;
                for (int i = 0; i < news.Values.Count; i++)
                {
                    ItemValue value = news.Values[i];
                    if (value.Name.StartsWith(ContentsPrefix, System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (value.Kind == ItemWire.KindBool)
                    {
                        FsmBool variable = variables.FindFsmBool(value.Name);
                        if (variable != null)
                        {
                            variable.Value = value.Whole != 0;
                        }
                    }
                    else if (value.Kind == ItemWire.KindInt)
                    {
                        FsmInt variable = variables.FindFsmInt(value.Name);
                        if (variable != null)
                        {
                            variable.Value = value.Whole;
                        }
                    }
                    else
                    {
                        FsmFloat variable = variables.FindFsmFloat(value.Name);
                        if (variable != null)
                        {
                            variable.Value = value.Number;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(news.Name) && thing.name != news.Name)
            {
                thing.name = news.Name;
            }

            // A beer case: the bottles taken out in the other game come out of this one too.
            int taken = bottles == null ? 0 : bottles.Value - bottlesBefore;
            Transform holder = thing.transform;
            for (int i = 0; i < taken && holder.childCount - i > 0; i++)
            {
                Object.Destroy(holder.GetChild(holder.childCount - 1 - i).gameObject);
            }

            item.Signature = ReadItem(item, null);
            if (item.Signature == before)
            {
                // What this game had already: its own news coming back, or the same done here too.
                return;
            }

            log.Info(
                LogCategory.Authority,
                "'" + nameBefore + "' changed in another game" + (nameBefore != thing.name ? ", now '" + thing.name + "'" : string.Empty)
                + (taken > 0 ? ", " + taken + " bottle(s) taken out" : string.Empty) + ": " + Describe(news.Values) + ".");
        }

        // What this machine's game did to the things it simulates, told.
        private void WatchItems()
        {
            if (items == null)
            {
                return;
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked item = tracked[i];
                if ((frame + item.Stagger) % ItemLookEvery != 0 || !item.Shared.HasId || item.Body == null || item.Transform == null)
                {
                    continue;
                }

                string now = ReadItem(item, itemValues);
                if (now == null)
                {
                    continue;
                }

                if (item.Signature == null)
                {
                    // The first look: what it has now is where it starts from.
                    item.Signature = now;
                    continue;
                }

                if (now == item.Signature || frameNow - item.LastTold < ItemTellInterval)
                {
                    continue;
                }

                if (items.TellState(item.Shared.Id, item.Transform.gameObject.name, itemValues))
                {
                    item.LastTold = frameNow;
                    log.Info(LogCategory.Authority, "'" + item.Transform.gameObject.name + "' changed in this game; the others are told: " + Describe(itemValues) + ".");
                }

                item.Signature = now;
            }
        }

        // The thing's name and the values of its "Use" and of a can's fluid, as text to compare, and as values if a
        // list is given. Null if it has neither. Whole: also what only a world that joins takes (Joining).
        private string ReadItem(Tracked item, List<ItemValue> values)
        {
            return ReadItem(item, values, false);
        }

        private string ReadItem(Tracked item, List<ItemValue> values, bool whole)
        {
            PlayMakerFSM use = UseOf(item);
            PlayMakerFSM contents = ContentsOf(item);
            if (use == null && contents == null)
            {
                return null;
            }

            if (values != null)
            {
                values.Clear();
            }

            itemText.Length = 0;
            itemText.Append(item.Transform.gameObject.name);
            if (contents != null)
            {
                FsmFloat[] fluids = contents.FsmVariables.FloatVariables;
                for (int i = 0; fluids != null && i < fluids.Length; i++)
                {
                    if (fluids[i].Name != null && fluids[i].Name.StartsWith(FluidPrefix, System.StringComparison.Ordinal))
                    {
                        itemText.Append('|').Append(ContentsPrefix).Append(fluids[i].Name).Append('=').Append(fluids[i].Value.ToString("0.00", CultureInfo.InvariantCulture));
                        if (values != null)
                        {
                            values.Add(ItemValue.Float(ContentsPrefix + fluids[i].Name, fluids[i].Value));
                        }
                    }
                }
            }

            if (use == null)
            {
                return itemText.ToString();
            }

            FsmVariables variables = use.FsmVariables;
            FsmBool[] bools = variables.BoolVariables;
            for (int i = 0; bools != null && i < bools.Length; i++)
            {
                if (IsShared(bools[i].Name))
                {
                    itemText.Append('|').Append(bools[i].Name).Append(bools[i].Value ? "=1" : "=0");
                    if (values != null)
                    {
                        values.Add(ItemValue.Bool(bools[i].Name, bools[i].Value));
                    }
                }
            }

            FsmInt[] ints = variables.IntVariables;
            for (int i = 0; ints != null && i < ints.Length; i++)
            {
                if (IsShared(ints[i].Name))
                {
                    itemText.Append('|').Append(ints[i].Name).Append('=').Append(ints[i].Value.ToString(CultureInfo.InvariantCulture));
                    if (values != null)
                    {
                        values.Add(ItemValue.Int(ints[i].Name, ints[i].Value));
                    }
                }
            }

            FsmFloat[] floats = variables.FloatVariables;
            for (int i = 0; floats != null && i < floats.Length; i++)
            {
                if (IsShared(floats[i].Name) || (whole && System.Array.IndexOf(Joining, floats[i].Name) >= 0))
                {
                    itemText.Append('|').Append(floats[i].Name).Append('=').Append(floats[i].Value.ToString("0.00", CultureInfo.InvariantCulture));
                    if (values != null)
                    {
                        values.Add(ItemValue.Float(floats[i].Name, floats[i].Value));
                    }
                }
            }

            return itemText.ToString();
        }

        private static bool IsShared(string name)
        {
            return !string.IsNullOrEmpty(name) && System.Array.IndexOf(Unshared, name) < 0;
        }

        private const string FuseHolderName = "fuse holder(Clone)";

        private PlayMakerFSM UseOf(Tracked item)
        {
            if (!item.UseSearched)
            {
                item.UseSearched = true;

                // A fuse holder's fuse and screw are the house's electricity's (ElectricitySync), shown the game's way.
                if (item.Transform.name == FuseHolderName)
                {
                    return null;
                }

                PlayMakerFSM[] machines = item.Transform.GetComponents<PlayMakerFSM>();
                for (int i = 0; i < machines.Length; i++)
                {
                    if (machines[i].FsmName == UseMachine)
                    {
                        item.Use = machines[i];
                        break;
                    }
                }
            }

            // Unity's null check is also true for a destroyed component: thrown away, its "Use" may go.
            return item.Use == null ? null : item.Use;
        }

        private PlayMakerFSM ContentsOf(Tracked item)
        {
            if (!item.ContentsSearched)
            {
                item.ContentsSearched = true;
                for (int c = 0; c < ContentsChildren.Length && item.Contents == null; c++)
                {
                    Transform child = item.Transform.Find(ContentsChildren[c]);
                    PlayMakerFSM[] machines = child == null ? new PlayMakerFSM[0] : child.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < machines.Length; i++)
                    {
                        if (machines[i].FsmName == ContentsMachine)
                        {
                            item.Contents = machines[i];
                            break;
                        }
                    }
                }
            }

            // Unity's null check is also true for a destroyed component.
            return item.Contents == null ? null : item.Contents;
        }

        private static string Describe(IList<ItemValue> values)
        {
            var text = new StringBuilder();
            for (int i = 0; i < values.Count && i < 8; i++)
            {
                ItemValue value = values[i];
                text.Append(i == 0 ? string.Empty : ", ").Append(value.Name).Append('=')
                    .Append(value.Kind == ItemWire.KindFloat ? value.Number.ToString("0.##", CultureInfo.InvariantCulture) : value.Whole.ToString(CultureInfo.InvariantCulture));
            }

            if (values.Count > 8)
            {
                text.Append(", …");
            }

            return text.ToString();
        }

        private sealed class ItemNews
        {
            public ushort Id;
            public string Name;
            public List<ItemValue> Values;
            public double Since;
        }
    }
}
