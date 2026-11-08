namespace SummerSync.DumpDiff;

/// <summary>An entry of the first dump paired with its counterpart in the second; one side is null when it has none.</summary>
internal readonly record struct EntryPair<T>(T? Before, T? After)
    where T : DumpEntry;

/// <summary>
/// Pairs the entries of two dumps.
///
/// Objects are identified by a readable key (path, plus FSM name). That key is not unique: the game has many
/// identically named siblings. Within a group that shares a key, entries are paired by instance id, which is
/// reliable when both dumps come from the same game session, and otherwise by position in the hierarchy.
/// Sibling positions shift when an earlier sibling is destroyed, so they are only the fallback.
/// </summary>
internal static class EntryMatcher
{
    public static List<EntryPair<T>> Pair<T>(IEnumerable<T> before, IEnumerable<T> after, Func<T, string> key, Func<T, string> position)
        where T : DumpEntry
    {
        Dictionary<string, List<T>> groupsBefore = Group(before, key);
        Dictionary<string, List<T>> groupsAfter = Group(after, key);
        var pairs = new List<EntryPair<T>>();

        foreach (string groupKey in groupsBefore.Keys.Union(groupsAfter.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            List<T> first = groupsBefore.GetValueOrDefault(groupKey) ?? [];
            List<T> second = groupsAfter.GetValueOrDefault(groupKey) ?? [];

            if (first.Count == 1 && second.Count == 1)
            {
                pairs.Add(new EntryPair<T>(first[0], second[0]));
                continue;
            }

            PairWithinGroup(first, second, position, pairs);
        }

        return pairs;
    }

    private static Dictionary<string, List<T>> Group<T>(IEnumerable<T> entries, Func<T, string> key)
    {
        var groups = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (T entry in entries)
        {
            string groupKey = key(entry);
            if (!groups.TryGetValue(groupKey, out List<T>? list))
            {
                groups[groupKey] = list = [];
            }

            list.Add(entry);
        }

        return groups;
    }

    private static void PairWithinGroup<T>(List<T> first, List<T> second, Func<T, string> position, List<EntryPair<T>> pairs)
        where T : DumpEntry
    {
        var remaining = new List<T>(second);

        var unmatched = new List<T>();
        foreach (T entry in first)
        {
            int index = remaining.FindIndex(candidate => candidate.Id == entry.Id);
            if (index >= 0)
            {
                pairs.Add(new EntryPair<T>(entry, remaining[index]));
                remaining.RemoveAt(index);
            }
            else
            {
                unmatched.Add(entry);
            }
        }

        foreach (T entry in unmatched)
        {
            string where = position(entry);
            int index = remaining.FindIndex(candidate => position(candidate) == where);
            if (index >= 0)
            {
                pairs.Add(new EntryPair<T>(entry, remaining[index]));
                remaining.RemoveAt(index);
            }
            else
            {
                pairs.Add(new EntryPair<T>(entry, null));
            }
        }

        foreach (T entry in remaining)
        {
            pairs.Add(new EntryPair<T>(null, entry));
        }
    }
}
