using System.Globalization;
using SummerSync.Core.Net;
using SummerSync.Core.World;

namespace SummerSync.NetProbe;

/// <summary>
/// The doors and switches of a game's world, as far as a program without a game can have them: the list a game wrote with
/// "ss doors" (one switch to a line, its name and its state), so that the two lists agree. It prints every switch someone
/// else flips, and flips its own at the seconds it is told.
/// </summary>
internal sealed class ProbeSwitches
{
    private readonly List<string> names = new();
    private readonly List<bool> states = new();
    private readonly List<(double At, string Part, bool Open)> flips = new();
    private SwitchReplication? current;
    private bool sharedSaid;
    private bool mismatchSaid;

    /// <param name="path">The list a game wrote with "ss doors".</param>
    /// <param name="script">Flips at their seconds: "&lt;s&gt;|&lt;part of the name&gt;|&lt;0|1&gt;", separated by ';'.</param>
    public ProbeSwitches(string path, string? script)
    {
        foreach (string line in File.ReadAllLines(path))
        {
            string[] fields = line.Split('\t');
            if (fields.Length >= 2)
            {
                names.Add(fields[0]);
                states.Add(fields[1] == "1");
            }
        }

        foreach (string part in (script ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = part.Split('|');
            flips.Add((double.Parse(fields[0], CultureInfo.InvariantCulture), fields[1], fields[2] == "1"));
        }

        Console.WriteLine("Switches: " + names.Count + " loaded from " + path + ".");
    }

    public void Step(NetworkManager manager, double now)
    {
        SwitchReplication? switches = manager.Switches;
        if (switches == null)
        {
            return;
        }

        if (switches != current)
        {
            current = switches;
            switches.SetWorld(names, states);
            sharedSaid = false;
            mismatchSaid = false;
        }

        if (switches.Shared && !sharedSaid)
        {
            sharedSaid = true;
            Console.WriteLine("Switches: shared with the host.");
        }

        if (switches.Mismatch && !mismatchSaid)
        {
            mismatchSaid = true;
            Console.WriteLine("Switches: this list is not the host's; " + (switches.Shared
                ? switches.SharedCount + " shared by name, " + switches.OnlyHere + " only here, " + switches.OnlyHost + " only the host's."
                : "nothing is shared."));
        }

        while (switches.TryTakeChange(out int index, out bool open))
        {
            Console.WriteLine("Switch heard: " + switches.NameOf(index) + " is " + (open ? "open" : "closed") + ".");
        }

        for (int f = 0; f < flips.Count; f++)
        {
            if (now < flips[f].At)
            {
                continue;
            }

            int found = -1;
            for (int i = 0; i < switches.Count && found < 0; i++)
            {
                if (switches.NameOf(i).Contains(flips[f].Part, StringComparison.Ordinal))
                {
                    found = i;
                }
            }

            Console.WriteLine(found < 0
                ? "Switch: nothing named like '" + flips[f].Part + "'."
                : "Switch: " + switches.NameOf(found) + " " + (flips[f].Open ? "opened" : "closed") + " here.");
            if (found >= 0)
            {
                switches.SetLocal(found, flips[f].Open);
            }

            flips.RemoveAt(f);
            f--;
        }
    }
}
