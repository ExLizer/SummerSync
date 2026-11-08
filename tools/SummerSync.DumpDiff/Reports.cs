using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SummerSync.Core.Dump;

namespace SummerSync.DumpDiff;

/// <summary>Overview of a single dump.</summary>
internal static class SummaryReport
{
    public static string Render(DumpFile dump, int top)
    {
        var text = new StringBuilder();
        text.AppendLine(dump.Source);
        text.AppendLine($"  {dump.Kind} dump, format {dump.Format}, by {dump.Tool}, {dump.Created} (trigger: {dump.Trigger})");
        text.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "  Unity {0}, scene {1}, game time {2:0.0} s, frame {3}",
            dump.Game.Unity,
            dump.Game.Scene,
            dump.Game.Time,
            dump.Game.Frame));
        if (dump.Stats.TryGetValue(DumpFormat.StatMilliseconds, out double milliseconds))
        {
            text.AppendLine(string.Format(CultureInfo.InvariantCulture, "  took {0:0} ms to write in game", milliseconds));
        }

        text.AppendLine();
        text.AppendLine("FSMs: " + dump.Fsms.Count);
        text.AppendLine($"  enabled {dump.Fsms.Count(f => f.Enabled)}, on active objects {dump.Fsms.Count(f => f.Active)}, initialised {dump.Fsms.Count(f => f.Initialized)}");
        text.AppendLine($"  variables {dump.Fsms.Sum(f => f.Variables.Values.Sum(section => section.Count))}, distinct FSM names {dump.Fsms.Select(f => f.Name).Distinct().Count()}");
        text.AppendLine("  most common FSM names:");
        foreach (var group in dump.Fsms.GroupBy(f => f.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(top))
        {
            text.AppendLine($"    {group.Count(),6}  {(group.Key.Length == 0 ? "(unnamed)" : group.Key)}");
        }

        List<StateEntry> states = dump.Fsms.SelectMany(f => f.States ?? []).ToList();
        if (states.Count > 0)
        {
            List<ActionEntry> actions = states.SelectMany(s => s.Actions).ToList();
            text.AppendLine();
            text.AppendLine($"States: {states.Count} ({states.Count(s => s.Loaded)} with actions loaded in game)");
            text.AppendLine($"Actions: {actions.Count} of {actions.Select(a => a.Type).Distinct().Count()} types");
            text.AppendLine("  by category:");
            foreach (var group in actions.GroupBy(a => a.Category.Length == 0 ? "(uncategorised)" : a.Category).OrderByDescending(g => g.Count()))
            {
                text.AppendLine($"    {group.Count(),6}  {group.Key}");
            }

            text.AppendLine("  most common types:");
            foreach (var group in actions.GroupBy(a => a.Type).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(top))
            {
                text.AppendLine($"    {group.Count(),6}  {group.Key}");
            }
        }

        text.AppendLine();
        text.AppendLine($"Global variables: {dump.Globals.Values.Sum(section => section.Count)} ({string.Join(", ", dump.Globals.Select(s => s.Key + " " + s.Value.Count))})");
        text.AppendLine($"Collections (ArrayMaker): {dump.Collections.Count}");
        text.AppendLine($"Rigidbodies: {dump.Rigidbodies.Count} ({dump.Rigidbodies.Count(b => b.Active)} on active objects, {dump.Rigidbodies.Count(b => b.Kinematic)} kinematic, {dump.Rigidbodies.Count(b => b.Sleeping)} sleeping)");
        text.AppendLine($"Vehicles: {dump.Vehicles.Count}");
        foreach (VehicleEntry vehicle in dump.Vehicles.OrderBy(v => v.Path, StringComparer.Ordinal))
        {
            string colliders = vehicle.Colliders == null ? "" : $", {vehicle.Colliders.Count} colliders ({vehicle.Colliders.Count(c => c.IsTrigger)} triggers)";
            text.AppendLine($"    {vehicle.Path}  ({vehicle.Wheels} wheels{colliders}{(vehicle.Active ? "" : ", inactive")})");
        }

        if (dump.Errors.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"Items the dumper had to skip: {dump.Errors.Count}");
            foreach (string error in dump.Errors.Take(top))
            {
                text.AppendLine("    " + error);
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// Finds what changes by itself. Given dumps taken while the player does nothing, whatever still changes is
/// noise (clocks, timers, NPCs, ambient effects), and is written as an ignore list for later diffs. This derives
/// the noise from the game instead of guessing variable names.
/// </summary>
internal static class NoiseFinder
{
    public const string Header = "# SummerSync dump noise list. One regular expression per line, matched against \"path :: fsm :: what\".";

    /// <param name="dumps">Dumps in the order they were taken.</param>
    /// <param name="options">Tolerances to compare with.</param>
    /// <param name="everyPair">
    /// When true, only what changed between every consecutive pair counts (things that never stop changing).
    /// When false, anything that changed at all counts, including values that drift too slowly to cross the
    /// tolerance between two consecutive dumps but do between the first and the last.
    /// </param>
    public static List<string> Find(IReadOnlyList<DumpFile> dumps, DiffOptions options, bool everyPair)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 1; i < dumps.Count; i++)
        {
            foreach (string key in Keys(dumps[i - 1], dumps[i], options))
            {
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        IEnumerable<string> keys;
        if (everyPair)
        {
            int pairs = dumps.Count - 1;
            keys = counts.Where(pair => pair.Value == pairs).Select(pair => pair.Key);
        }
        else
        {
            keys = counts.Keys.Union(Keys(dumps[0], dumps[^1], options));
        }

        return keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> Keys(DumpFile before, DumpFile after, DiffOptions options) =>
        new Differ(options).Compare(before, after)
            .Where(change => change.Section is Section.Globals or Section.Fsms or Section.Collections or Section.Vehicles)
            .Select(change => DiffOptions.NoiseKey(change.Object, change.Scope, StableWhat(change.What)))
            .Distinct();

    public static string ToFile(IEnumerable<string> keys, IEnumerable<string> sources)
    {
        var text = new StringBuilder();
        text.AppendLine(Header);
        text.AppendLine("# Generated by 'dumpdiff noise' from: " + string.Join(", ", sources));
        text.AppendLine("# Lists what changed between those dumps while the player was doing nothing. These are not");
        text.AppendLine("# unimportant values: several are game state (clock, player needs). They are hidden only to make");
        text.AppendLine("# the effect of a deliberate action readable in a diff.");
        foreach (string key in keys)
        {
            text.AppendLine("^" + Regex.Escape(key) + "$");
        }

        return text.ToString();
    }

    public static List<Regex> Load(string path) =>
        File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => new Regex(line, RegexOptions.CultureInvariant))
            .ToList();

    // "moved 1.2 m" carries a measurement; the noise key must not depend on it.
    private static string StableWhat(string what) =>
        what.StartsWith("moved ", StringComparison.Ordinal) ? "moved" : what.StartsWith("turned ", StringComparison.Ordinal) ? "turned" : what;
}
