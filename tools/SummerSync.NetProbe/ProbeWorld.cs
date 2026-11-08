using System.Globalization;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;

namespace SummerSync.NetProbe;

/// <summary>
/// The loose objects, as far as a program without a game can have them. As a client it copies the host's
/// list, so that every object of the host's world has a counterpart here; as a host it can make up a few
/// crates. Its "physics" is a straight throw under gravity onto a flat floor.
///
/// With a bot it also plays the other player of the object tests: it picks up the nearest object, carries it
/// along for a while, throws it, lets it come to rest, and starts again.
/// </summary>
internal sealed class ProbeWorld
{
    private const double IdleSeconds = 4.0;
    private const double CarrySeconds = 8.0;
    private const double ReachMetres = 12.0;
    private const float Gravity = 9.81f;

    private readonly List<Item> items = new();
    private readonly bool carries;
    private bool listRequested;
    private Item? inHand;
    private double phaseStarted = double.NaN;
    private float floor;

    public ProbeWorld(bool carries)
    {
        this.carries = carries;
    }

    /// <summary>Times the bot picked an object up.</summary>
    public int PickedUp { get; private set; }

    /// <summary>Times an object the bot threw came to rest and went back to the host.</summary>
    public int HandedBack { get; private set; }

    public int Count => items.Count;

    /// <summary>As a host: puts a ring of crates on the floor around a point.</summary>
    public void AddCrates(ObjectReplication objects, PlayerPose centre, int count)
    {
        for (int i = 0; i < count; i++)
        {
            double angle = 2.0 * Math.PI * i / count;
            var pose = BodyPose.At(centre.X + (float)(4.0 * Math.Cos(angle)), centre.Y + 0.15f, centre.Z + (float)(4.0 * Math.Sin(angle)));
            items.Add(new Item(objects.AddLocal("crate(probe)", pose), pose));
        }
    }

    /// <summary>
    /// As a host: the objects of a file that <see cref="Save"/> wrote, so that a game whose world they come from pairs
    /// every one of them with its own. Returns how many.
    /// </summary>
    public int Load(ObjectReplication objects, string path)
    {
        int count = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 8)
            {
                continue;
            }

            var numbers = new float[7];
            for (int i = 0; i < 7; i++)
            {
                numbers[i] = float.Parse(fields[i + 1], CultureInfo.InvariantCulture);
            }

            var pose = new BodyPose(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5], numbers[6]);
            items.Add(new Item(objects.AddLocal(fields[0], pose), pose));
            count++;
        }

        return count;
    }

    private readonly HashSet<ushort> shown = new();

    /// <summary>Prints, once each, the shared numbers of the objects of that name (for a test that names one).</summary>
    public void ShowIds(string name)
    {
        foreach (Item item in items)
        {
            if (item.Shared.HasId && item.Shared.Name == name && shown.Add(item.Shared.Id))
            {
                Console.WriteLine("Object #" + item.Shared.Id + " '" + name + "' at (" + item.Pose.X.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                    + item.Pose.Y.ToString("0.0", CultureInfo.InvariantCulture) + ", " + item.Pose.Z.ToString("0.0", CultureInfo.InvariantCulture) + ").");
            }
        }
    }

    /// <summary>The newest shared number of an object of that name, or 0 if there is none.</summary>
    public int NewestId(string name)
    {
        int newest = 0;
        foreach (Item item in items)
        {
            if (item.Shared.HasId && item.Shared.Name == name && item.Shared.Id > newest)
            {
                newest = item.Shared.Id;
            }
        }

        return newest;
    }

    /// <summary>The objects here, name and place, one to a line, for <see cref="Load"/>. Returns how many.</summary>
    public int Save(string path)
    {
        var lines = new List<string>(items.Count);
        foreach (Item item in items)
        {
            BodyPose pose = item.Pose;
            lines.Add(string.Join(
                "\t",
                item.Shared.Name,
                F(pose.X),
                F(pose.Y),
                F(pose.Z),
                F(pose.QX),
                F(pose.QY),
                F(pose.QZ),
                F(pose.QW)));
        }

        File.WriteAllLines(path, lines);
        return lines.Count;
    }

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>One frame: follow what others simulate, simulate what is ours, and let the bot do its round.</summary>
    public void Step(NetworkManager manager, double now, double frameSeconds, PlayerPose? bot)
    {
        ObjectReplication? objects = manager.Objects;
        if (objects == null || manager.Session!.State != SessionState.Connected)
        {
            return;
        }

        if (manager.Mode == NetworkMode.Client)
        {
            if (!listRequested)
            {
                listRequested = true;
                objects.RequestList();
            }

            // Whatever the host has and this world lacks, this world now has too.
            foreach (KeyValuePair<string, BodyPose> missing in objects.Missing)
            {
                items.Add(new Item(objects.AddLocal(missing.Key, missing.Value), missing.Value));
            }
        }

        objects.Sample(now);
        if (carries && bot != null)
        {
            Round(objects, now, bot.Value);
        }

        foreach (Item item in items)
        {
            switch (item.Shared.Role)
            {
                case BodyRole.Simulate:
                    if (item.Shared.TakeHandOff(out BodyPose handed, out BodyMotion motion))
                    {
                        item.Pose = handed;
                        item.Motion = motion;
                        item.Flying = false;
                    }

                    if (item == inHand && bot != null)
                    {
                        item.Pose = InFrontOf(bot.Value, 0.7f, 1.0f);
                        item.Motion = default;
                    }
                    else if (item.Flying)
                    {
                        Fly(item, (float)frameSeconds);
                    }

                    item.Shared.Report(item.Pose, item.Motion, item == inHand || item.Flying);
                    break;

                case BodyRole.Follow:
                    if (item.Shared.HasTarget)
                    {
                        item.Pose = item.Shared.Target;
                    }

                    item.Flying = false;
                    break;
            }
        }
    }

    public string Describe()
    {
        return "Objects here: " + items.Count + ", picked up " + PickedUp + " time(s), handed back " + HandedBack + " time(s)"
            + (inHand != null ? ", carrying '" + inHand.Shared.Name + "'" : string.Empty);
    }

    // Wait, pick the nearest object up, carry it, throw it, wait for it to land and go back to the host.
    private void Round(ObjectReplication objects, double now, PlayerPose bot)
    {
        if (double.IsNaN(phaseStarted))
        {
            phaseStarted = now;
        }

        if (inHand == null)
        {
            if (now - phaseStarted < IdleSeconds)
            {
                return;
            }

            Item? nearest = null;
            double nearestDistance = ReachMetres;
            foreach (Item item in items)
            {
                SharedBody shared = item.Shared;
                double distance = Distance(item.Pose, bot);

                // Not one that someone has in hand, and not one still in the air from our own throw.
                if (shared.HasId && shared.Present && !shared.Held && !item.Flying && distance < nearestDistance)
                {
                    nearest = item;
                    nearestDistance = distance;
                }
            }

            if (nearest == null)
            {
                phaseStarted = now;
                return;
            }

            inHand = nearest;
            floor = Math.Min(bot.Y + 0.15f, nearest.Pose.Y);
            phaseStarted = now;
            objects.SetHeld(nearest.Shared);
            PickedUp++;
            Console.WriteLine("The bot picks up '" + nearest.Shared.Name + "' (object " + nearest.Shared.Id + "), "
                + nearestDistance.ToString("0.0", CultureInfo.InvariantCulture) + " m away.");
            return;
        }

        if (inHand.Shared.Role != BodyRole.Simulate)
        {
            // Someone else had it in hand after all.
            Console.WriteLine("The bot could not have '" + inHand.Shared.Name + "': player " + inHand.Shared.Owner + " is holding it.");
            objects.SetHeld(null);
            inHand = null;
            phaseStarted = now;
            return;
        }

        if (now - phaseStarted >= CarrySeconds)
        {
            // Thrown the way the bot looks, a bit upwards.
            double yaw = bot.Yaw * Math.PI / 180.0;
            Item thrown = inHand;
            thrown.Motion = new BodyMotion { VX = (float)(4.0 * Math.Sin(yaw)), VY = 3f, VZ = (float)(4.0 * Math.Cos(yaw)), AX = 2f };
            thrown.Flying = true;
            thrown.Counted = false;
            inHand = null;
            phaseStarted = now;
            objects.SetHeld(null);
            Console.WriteLine("The bot throws '" + thrown.Shared.Name + "'.");
        }
    }

    private void Fly(Item item, float seconds)
    {
        BodyMotion motion = item.Motion;
        BodyPose pose = item.Pose;
        motion.VY -= Gravity * seconds;
        pose.X += motion.VX * seconds;
        pose.Y += motion.VY * seconds;
        pose.Z += motion.VZ * seconds;
        if (pose.Y <= floor)
        {
            pose.Y = floor;
            motion = default;
            item.Flying = false;
            if (!item.Counted)
            {
                item.Counted = true;
                HandedBack++;
            }
        }

        item.Pose = pose;
        item.Motion = motion;
    }

    private static BodyPose InFrontOf(PlayerPose player, float ahead, float height)
    {
        double yaw = player.Yaw * Math.PI / 180.0;
        float half = (float)(yaw / 2.0);
        return new BodyPose(
            player.X + (float)(Math.Sin(yaw) * ahead),
            player.Y + height,
            player.Z + (float)(Math.Cos(yaw) * ahead),
            0,
            (float)Math.Sin(half),
            0,
            (float)Math.Cos(half));
    }

    private static double Distance(BodyPose body, PlayerPose player)
    {
        double x = body.X - player.X;
        double y = body.Y - player.Y;
        double z = body.Z - player.Z;
        return Math.Sqrt((x * x) + (y * y) + (z * z));
    }

    private sealed class Item(SharedBody shared, BodyPose pose)
    {
        public SharedBody Shared { get; } = shared;

        public BodyPose Pose { get; set; } = pose;

        public BodyMotion Motion { get; set; }

        public bool Flying { get; set; }

        public bool Counted { get; set; } = true;
    }
}
