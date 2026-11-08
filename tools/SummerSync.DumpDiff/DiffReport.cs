using System.Globalization;
using System.Text;

namespace SummerSync.DumpDiff;

/// <summary>Renders a list of changes as text, grouped by GameObject and FSM.</summary>
internal static class DiffReport
{
    public static string Render(DumpFile before, DumpFile after, IReadOnlyList<Change> changes, bool showNoise, int maxPerObject)
    {
        var text = new StringBuilder();
        text.AppendLine("SummerSync dump diff");
        text.AppendLine("  A: " + Describe(before));
        text.AppendLine("  B: " + Describe(after));
        text.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "  between them: {0:0.0} s of game time, {1} frames",
            after.Game.Time - before.Game.Time,
            after.Game.Frame - before.Game.Frame));
        if (before.Game.Scene != after.Game.Scene)
        {
            text.AppendLine("  WARNING: the dumps come from different scenes.");
        }

        List<Change> shown = changes.Where(change => showNoise || !change.Noise).ToList();
        int noise = changes.Count(change => change.Noise);

        text.AppendLine();
        text.AppendLine("Summary");
        foreach (Section section in Enum.GetValues<Section>())
        {
            List<Change> inSection = shown.Where(change => change.Section == section).ToList();
            if (inSection.Count > 0)
            {
                int objects = inSection.Select(change => change.Object + "\n" + change.Scope).Distinct().Count();
                text.AppendLine($"  {Title(section),-12} {inSection.Count,6} change(s) in {objects} place(s)");
            }
        }

        if (shown.Count == 0)
        {
            text.AppendLine("  No differences.");
        }

        if (noise > 0 && !showNoise)
        {
            text.AppendLine($"  Hidden as noise: {noise} change(s). Use --show-noise to list them.");
        }

        foreach (Section section in Enum.GetValues<Section>())
        {
            List<Change> inSection = shown.Where(change => change.Section == section).ToList();
            if (inSection.Count == 0)
            {
                continue;
            }

            text.AppendLine();
            text.AppendLine("== " + Title(section) + " ==");
            foreach (IGrouping<string, Change> byObject in inSection.GroupBy(change => change.Object).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                if (byObject.Key.Length > 0)
                {
                    text.AppendLine(byObject.Key);
                }

                foreach (IGrouping<string, Change> byScope in byObject.GroupBy(change => change.Scope))
                {
                    string indent = "  ";
                    if (byScope.Key.Length > 0)
                    {
                        text.AppendLine("  [" + byScope.Key + "]");
                        indent = "    ";
                    }

                    int written = 0;
                    foreach (Change change in byScope)
                    {
                        if (written == maxPerObject)
                        {
                            text.AppendLine(indent + "... " + (byScope.Count() - written) + " more");
                            break;
                        }

                        text.AppendLine(indent + Line(change));
                        written++;
                    }
                }
            }
        }

        return text.ToString();
    }

    private static string Line(Change change)
    {
        string noise = change.Noise ? "  (noise)" : "";
        if (change.Before == null && change.After == null)
        {
            return change.What + noise;
        }

        if (change.Before == null)
        {
            return change.What + ": (absent) -> " + change.After + noise;
        }

        if (change.After == null)
        {
            return change.What + ": " + change.Before + " -> (absent)" + noise;
        }

        return change.What + ": " + change.Before + " -> " + change.After + noise;
    }

    private static string Describe(DumpFile dump) => string.Format(
        CultureInfo.InvariantCulture,
        "{0}  ({1}, scene {2}, t={3:0.0}s, frame {4}, {5} FSMs, {6} rigidbodies)",
        dump.Source,
        dump.Kind,
        dump.Game.Scene,
        dump.Game.Time,
        dump.Game.Frame,
        dump.Fsms.Count,
        dump.Rigidbodies.Count);

    private static string Title(Section section) => section switch
    {
        Section.Globals => "Globals",
        Section.Fsms => "FSMs",
        Section.Collections => "Collections",
        Section.Rigidbodies => "Rigidbodies",
        _ => "Vehicles",
    };
}
