using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;

namespace SummerSync.Unity.World
{
    /// <summary>"ss satsuma build": a test aid that puts the car together here, the game's way.</summary>
    internal sealed partial class PartSync
    {
        /// <summary>
        /// Puts the stock Satsuma together in this game as a player would, one part at a time and in the order the car
        /// goes together: a part lying around goes into its slot through its trigger and its bolts are tightened before
        /// the next one (a part on a hinge left loose is shaken off by the next one going in); what could not go in yet
        /// is tried again after the others, then every wire is made and every bolt still loose is tightened. Nothing is
        /// told by it: the game makes the steps and <see cref="PartSync"/> tells them as this game's. Bought parts, and
        /// what is not in this world (a battery, spark plugs), are left out. Only for testing.
        /// </summary>
        private sealed class Builder
        {
            private const double InstallWait = 3.0;
            private const double TightenWait = 4.0;
            private const int MostPasses = 6;
            private const int TurnsWithoutChange = 3;
            private const double SweepSeconds = 90.0;

            private readonly PartSync sync;
            private readonly List<int> order = new List<int>();
            private readonly List<int> later = new List<int>();
            private readonly Dictionary<int, BoltTry> tries = new Dictionary<int, BoltTry>();
            private readonly List<string> skippedNames = new List<string>();
            private int next;
            private int pass = 1;
            private int installedThisPass;
            private int working = -1;
            private bool tightening;
            private double waitUntil;
            private int phase;
            private int installed;
            private int wires;
            private int turned;
            private int wireAt;
            private double sweepUntil;
            private double engineUntil;
            private const double EngineSeconds = 15.0;

            public Builder(PartSync sync)
            {
                this.sync = sync;
                for (int i = 0; i < sync.slots.Length; i++)
                {
                    if (sync.slots[i].Steps && sync.slots[i].Entry.Group != "Orders")
                    {
                        order.Add(i);
                    }
                }

                AssemblyCatalog catalog = sync.catalog;
                order.Sort(delegate(int a, int b)
                {
                    int byRank = catalog.Rank(a).CompareTo(catalog.Rank(b));
                    return byRank != 0 ? byRank : a.CompareTo(b);
                });
                sync.log.Info(Category, "Car: putting the Satsuma together here (test aid): " + order.Count + " slot(s) to look at.");
            }

            public bool Done { get; private set; }

            public string Describe()
            {
                return "Building: " + (phase == 0 ? "parts, pass " + pass + ", " + next + " of " + order.Count : phase == 1 ? "the engine" : phase == 2 ? "wires" : "bolts") + "; " + installed + " installed, "
                    + wires + " wire(s), " + turned + " turn(s)"
                    + (skippedNames.Count == 0 ? string.Empty : "; left out: " + string.Join(", ", skippedNames.ToArray()));
            }

            public void Step(double now)
            {
                switch (phase)
                {
                    case 0:
                        Parts(now);
                        break;
                    case 1:
                        Engine(now);
                        break;
                    case 2:
                        WireNext();
                        if (phase == 3)
                        {
                            sweepUntil = now + SweepSeconds;
                        }

                        break;
                    default:
                        Sweep(now);
                        break;
                }
            }

            private void Parts(double now)
            {
                if (working >= 0)
                {
                    Slot slot = sync.slots[working];
                    if (!tightening)
                    {
                        if (sync.GameInstalled(slot))
                        {
                            installed++;
                            installedThisPass++;
                            tightening = true;
                            waitUntil = now + TightenWait;
                        }
                        else if (now >= waitUntil)
                        {
                            later.Add(working);
                            working = -1;
                        }

                        return;
                    }

                    // Its bolts, before the next part goes in.
                    if (TightenAll(slot) && now < waitUntil)
                    {
                        return;
                    }

                    working = -1;
                    tightening = false;
                }

                for (int looked = 0; looked < 8 && next < order.Count; looked++)
                {
                    Slot slot = sync.slots[order[next++]];
                    if (sync.GameInstalled(slot) || slot.Driving)
                    {
                        continue;
                    }

                    string why = "it has no trigger";
                    bool sent = false;
                    for (int t = 0; t < slot.Entry.Triggers.Length && !sent; t++)
                    {
                        int corner = slot.Entry.Triggers[t].Place.Kind == PartPlaceKind.Wheel ? CornerFor(slot.Entry) : 0;
                        sent = sync.Install(slot, t + (corner * SatsumaAssembly.CornerFactor), 0, out why);
                    }

                    if (sent)
                    {
                        working = slot.Index;
                        waitUntil = now + InstallWait;
                        return;
                    }

                    later.Add(slot.Index);
                    slot.BuildWhy = why;
                }

                if (next < order.Count)
                {
                    return;
                }

                // A pass is over: what could not go in is tried again while passes still install something.
                if (later.Count > 0 && installedThisPass > 0 && pass < MostPasses)
                {
                    order.Clear();
                    order.AddRange(later);
                    later.Clear();
                    next = 0;
                    pass++;
                    installedThisPass = 0;
                    return;
                }

                for (int i = 0; i < later.Count; i++)
                {
                    Slot slot = sync.slots[later[i]];
                    if (skippedNames.Count < 16)
                    {
                        skippedNames.Add(slot.Entry.Name);
                    }

                    sync.log.Info(Category, "Car: building here, '" + slot.Entry.Name + "' left out: " + (slot.BuildWhy ?? "it did not go in") + ".");
                }

                phase = 1;
                engineUntil = now + EngineSeconds;
                sync.log.Info(Category, "Car: building here, parts done in " + pass + " pass(es): " + installed + " installed, " + later.Count + " left out. The engine next.");
            }

            // The block goes in by bolting it to its mounts: their bolts are shown the game's way, then tightened, and
            // the game puts the block in its place with whatever is already on it.
            private void Engine(double now)
            {
                Slot block = null;
                for (int i = 0; i < sync.slots.Length && block == null; i++)
                {
                    if (sync.slots[i].Entry.Family == PartFamily.Hoist)
                    {
                        block = sync.slots[i];
                    }
                }

                bool done = block == null || (block.Installed != null && block.Installed.Value);
                if (!done && now < engineUntil)
                {
                    sync.BringBlock(block);
                    TightenAll(block);
                    return;
                }

                sync.log.Info(Category, "Car: building here, the engine " + (block == null ? "is not in the catalogue" : done ? "is in" : "did not go in") + ". Wires next.");
                phase = 2;
            }

            // Hubcap1 goes on the front left wheel, Hubcap4 on the rear right one.
            private static int CornerFor(PartEntry entry)
            {
                char last = entry.Name[entry.Name.Length - 1];
                return last >= '1' && last <= '4' ? last - '1' : 0;
            }

            // One turn for every bolt of the entry not at its last stage. True while some bolt still has to turn.
            private bool TightenAll(Slot slot)
            {
                bool more = false;
                for (int b = 0; b < slot.Bolts.Length; b++)
                {
                    int stage = sync.GameStage(slot, b, true);
                    int key = (slot.Index * AssemblyCatalog.MaxBolts) + b;
                    BoltTry attempt;
                    if (!tries.TryGetValue(key, out attempt))
                    {
                        attempt = new BoltTry { Stage = -2 };
                        tries[key] = attempt;
                    }

                    if (stage < 0)
                    {
                        // Off, or in the middle of a turn.
                        more |= slot.Bolts[b].Object != null && slot.Bolts[b].Object.activeInHierarchy && attempt.Turns < TurnsWithoutChange;
                        continue;
                    }

                    if (stage != attempt.Stage)
                    {
                        attempt.Stage = stage;
                        attempt.Turns = 0;
                    }

                    if (stage >= slot.Entry.Bolts[b].Stages || attempt.Turns >= TurnsWithoutChange)
                    {
                        continue;
                    }

                    if (sync.Turn(slot, b, 1))
                    {
                        attempt.Turns++;
                        turned++;
                    }

                    more = true;
                }

                return more;
            }

            private void WireNext()
            {
                for (; wireAt < sync.slots.Length; wireAt++)
                {
                    Slot slot = sync.slots[wireAt];
                    if (!slot.Wire || sync.GameWired(slot))
                    {
                        continue;
                    }

                    string why;
                    if (sync.Connect(slot, out why))
                    {
                        wires++;
                    }
                    else
                    {
                        sync.log.Info(Category, "Car: building here, the wire '" + slot.Entry.Name + "' left out: " + why + ".");
                    }

                    wireAt++;
                    return;
                }

                phase = 3;
                sync.log.Info(Category, "Car: building here, " + wires + " wire(s) made. The bolts still loose next.");
            }

            private void Sweep(double now)
            {
                bool more = false;
                for (int i = 0; i < sync.slots.Length; i++)
                {
                    Slot slot = sync.slots[i];
                    if (slot.Bolts.Length > 0 && !slot.Driving && !SatsumaAssembly.IsTool(slot.Entry))
                    {
                        more |= TightenAll(slot);
                    }
                }

                if (!more || now >= sweepUntil)
                {
                    Done = true;
                    sync.log.Info(Category, "Car: building here done: " + installed + " part(s) installed, " + wires + " wire(s), " + turned.ToString(CultureInfo.InvariantCulture) + " bolt turn(s).");
                }
            }

            private sealed class BoltTry
            {
                public int Stage;
                public int Turns;
            }
        }
    }
}
