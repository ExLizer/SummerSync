using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using SummerSync.Unity.Objects;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// What the game mounts a loose thing onto that is not the Satsuma (data/mounts/*.json): the lid on the bucket, a diskette
    /// in the computer, the charger's clamps on a battery. Mounted, the thing is no longer loose and the loose objects stop
    /// sharing it, so in the other games it stayed where it was last seen, or went away.
    ///
    /// The game where it was mounted or taken off says which thing is mounted ("mount/&lt;name&gt;": mounted or not, the
    /// thing's shared number, and the player). The others take their game there the game's own way, as it does on a click or
    /// when it loads the save: a state machine to a state, or an event after one of its object variables is given the thing;
    /// never by writing the result. A thing mounted here because of another game's word keeps its looks where it was until
    /// then (<see cref="ObjectPresenter"/> keeps it), and a thing taken off is expected as the player's who took it off.
    ///
    /// What is assumed about the game (dump of 2026-10-14; docs/GAME_NOTES.md, "Lo que se encastra fuera del auto"): the
    /// paths, machines, states and events in the data files, read from the dump.
    /// </summary>
    internal sealed class MountSync
    {
        private const string DataPrefix = "SummerSync.mounts.";
        private const string EventPrefix = "SUMMERSYNC MOUNT ";
        private const double FirstLookSeconds = 2.0;
        private const double BindSeconds = 5.0;
        private const double LookSeconds = 0.25;
        private const double RetrySeconds = 1.0;
        private const int MostTries = 6;

        // Waiting for what makes a step possible (the thing on its way to the charger, the computer's drive switched on):
        // not a try, for this long at most.
        private const double WaitSeconds = 30.0;
        private const LogCategory Category = LogCategory.Fsm;

        private static MountDefinition[] catalog;
        private static HashSet<string> partNames;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly ObjectPresenter objects;
        private readonly List<Mount> mounts = new List<Mount>();
        private TallyReplication current;
        private bool bound;
        private double bindAt;
        private double nextLook;

        public MountSync(Logger log, NetworkManager manager, ObjectPresenter objects)
        {
            this.log = log;
            this.manager = manager;
            this.objects = objects;
        }

        /// <summary>Whether things of that name can be mounted onto something here: their copies are kept when the host's world no longer has them loose.</summary>
        public static bool IsMountable(string name, Logger log)
        {
            if (partNames == null)
            {
                Load(log);
            }

            return name != null && partNames.Contains(name);
        }

        /// <summary>
        /// Once per frame, after the loose objects. <paramref name="localPlayer"/> is the player's root object, or null while
        /// there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                current = tallies;
                mounts.Clear();
                bound = false;
                bindAt = now + FirstLookSeconds;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }
            }

            if (current == null)
            {
                return;
            }

            if (!bound)
            {
                if (now < bindAt)
                {
                    return;
                }

                bindAt = now + BindSeconds;
                Bind();
                return;
            }

            if (now < nextLook)
            {
                return;
            }

            nextLook = now + LookSeconds;
            for (int i = 0; i < mounts.Count; i++)
            {
                Work(mounts[i], now);
            }
        }

        private static void Load(Logger log)
        {
            var found = new List<MountDefinition>();
            partNames = new HashSet<string>();
            Assembly assembly = typeof(MountSync).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            for (int i = 0; i < resources.Length; i++)
            {
                if (!resources[i].StartsWith(DataPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resources[i]))
                    using (var reader = new StreamReader(stream))
                    {
                        MountDefinition definition = MountDefinition.Parse(reader.ReadToEnd());
                        found.Add(definition);
                        partNames.Add(definition.Part);
                    }
                }
                catch (FormatException exception)
                {
                    log.Error(Category, "The mount file '" + resources[i] + "' cannot be read; that mount is each game's own. " + exception.Message);
                }
            }

            catalog = found.ToArray();
        }

        private void Bind()
        {
            if (catalog == null)
            {
                Load(log);
            }

            var names = new List<string>();
            var missing = new List<string>();
            int told = 0;
            for (int i = 0; i < catalog.Length; i++)
            {
                MountDefinition definition = catalog[i];
                Transform place = Scene(definition.Into ?? definition.Onto);
                if (place == null)
                {
                    missing.Add(definition.Name);
                    continue;
                }

                var mount = new Mount { Definition = definition, Place = place, Home = definition.Onto != null ? place.parent : null };
                GameObject thing;
                mount.Known = Mounted(mount, out thing);
                mount.KnownId = mount.Known ? objects.IdOf(thing) : (ushort)0;
                mounts.Add(mount);
                names.Add(definition.Name);

                // What the host's game has is the session's from the start (it may have changed since the save the guests
                // loaded), all of it: what the session remembers may be of an earlier world. A thing mounted since the
                // save started has no shared number; it is told as mounted, by no number, and a game that has something
                // of that name mounted there keeps it.
                if (manager.Mode == NetworkMode.Host)
                {
                    Tell(mount);
                    told++;
                }
            }

            bound = true;
            current.RequestAll();
            log.Info(
                Category,
                "Mounts shared: " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())) + (missing.Count == 0 ? string.Empty : "; not in this world: " + string.Join(", ", missing.ToArray()))
                + (told > 0 ? "; " + told + " told as this game has them, for whoever joins." : "."));
        }

        // Heard while the session reads its messages: done in this class's turn in the frame.
        private void OnStateWanted(string key, int[] values)
        {
            if (values.Length < 3 || !key.StartsWith("mount/", StringComparison.Ordinal))
            {
                return;
            }

            // This game's own word, coming back: what it has now is newer. Not while catching up: what comes then under this
            // player's number was said before this world was loaded, by this game or by a player who had the number before
            // (a guest who joined late did not put on the charger the battery an earlier guest had put there).
            if (values[2] == manager.Session.LocalPlayerId && !current.CatchingUp)
            {
                return;
            }

            for (int i = 0; i < mounts.Count; i++)
            {
                Mount mount = mounts[i];
                if (mount.Definition.Key != key)
                {
                    continue;
                }

                mount.Wanted = true;
                mount.WantMounted = values[0] != 0;
                mount.WantId = (ushort)Mathf.Clamp(values[1], 0, ushort.MaxValue);
                mount.WantBy = (byte)Mathf.Clamp(values[2], 0, byte.MaxValue);
                mount.Tries = 0;
                mount.NextTry = 0.0;
                mount.WaitUntil = 0.0;
                mount.WaitSaid = false;
            }
        }

        private void Work(Mount mount, double now)
        {
            GameObject thing;
            bool mounted = Mounted(mount, out thing);
            ushort id = mounted ? objects.IdOf(thing) : (ushort)0;
            if (mount.Wanted)
            {
                if (mounted == mount.WantMounted && (!mounted || mount.WantId == 0 || id == mount.WantId))
                {
                    mount.Wanted = false;
                    mount.Known = mounted;
                    mount.KnownId = id;
                    if (mount.Tries > 0)
                    {
                        log.Info(Category, "Mounts: '" + mount.Definition.Name + "' " + (mounted ? "has " + Describe(thing, id) + " mounted" : "has nothing mounted") + " here now, as player " + mount.WantBy + "'s game.");
                    }

                    return;
                }

                if (now < mount.NextTry)
                {
                    return;
                }

                if (mount.Tries >= MostTries || (mount.WaitUntil != 0.0 && now >= mount.WaitUntil))
                {
                    GiveUp(mount, mounted, id);
                    return;
                }

                mount.Tries++;
                mount.NextTry = now + RetrySeconds;
                bool waits;
                string why;
                if (!Step(mount, mounted, thing, id, now, out waits, out why))
                {
                    if (waits)
                    {
                        // Not a try: what it waits for may be on its way.
                        mount.Tries--;
                        if (mount.WaitUntil == 0.0)
                        {
                            mount.WaitUntil = now + WaitSeconds;
                        }
                    }

                    if (!waits || !mount.WaitSaid)
                    {
                        mount.WaitSaid = waits;
                        log.Info(Category, "Mounts: '" + mount.Definition.Name + "' cannot be " + (mount.WantMounted ? "mounted" : "taken off") + " here yet: " + why + (waits ? "; waited for" : string.Empty) + ".");
                    }
                }

                return;
            }

            if (mounted == mount.Known && id == mount.KnownId)
            {
                return;
            }

            // This game did it: its player, or the game by itself (the charger lets go of a battery taken away).
            mount.Known = mounted;
            mount.KnownId = id;
            Tell(mount);
            log.Info(Category, "Mounts: '" + mount.Definition.Name + "' " + (mounted ? "has " + Describe(thing, id) + " mounted" : "has nothing mounted") + " here: the others are told.");
        }

        private void Tell(Mount mount)
        {
            current.Set(mount.Definition.Key, new[] { mount.Known ? 1 : 0, mount.KnownId, manager.Session.LocalPlayerId });
        }

        // One step towards what the others said. False if it could not be taken now; waits if what it needs may still come.
        private bool Step(Mount mount, bool mounted, GameObject thing, ushort id, double now, out bool waits, out string why)
        {
            waits = false;
            if (!mount.WantMounted || (mounted && mount.WantId != 0 && id != mount.WantId))
            {
                // Off, or another thing is in its place: off first.
                return Unmount(mount, thing, now, out waits, out why);
            }

            if (mounted)
            {
                why = null;
                return true;
            }

            GameObject part = mount.WantId == 0 ? null : objects.ThingOf(mount.WantId);
            if (part == null)
            {
                waits = mount.WantId != 0;
                why = mount.WantId == 0 ? "the other game did not say which " + mount.Definition.Part : "#" + mount.WantId + " is not in this world (yet)";
                return false;
            }

            MountStep step = mount.Definition.Mount;
            PlayMakerFSM machine = Machine(step, part);
            if (machine == null || !machine.gameObject.activeInHierarchy)
            {
                waits = true;
                why = "its machine '" + step.Machine + "' at " + (step.Path ?? "the thing") + " is " + (machine == null ? "not there" : "switched off");
                return false;
            }

            if (step.Within > 0f && (machine.transform.position - part.transform.position).sqrMagnitude > step.Within * step.Within)
            {
                waits = true;
                why = Describe(part, mount.WantId) + " is not within " + step.Within.ToString("0.##", CultureInfo.InvariantCulture) + " m of it yet";
                return false;
            }

            if (mount.Definition.Into != null)
            {
                // Built into something here, it is no longer a loose object of the session's.
                objects.Release(part);
            }

            if (!machine.enabled)
            {
                machine.enabled = true;
            }

            if (!Fire(machine, step, part))
            {
                why = "its machine '" + step.Machine + "' has no " + (step.State != null ? "state '" + step.State + "'" : "global event '" + step.Event + "'");
                return false;
            }

            log.Info(Category, "Mounts: '" + mount.Definition.Name + "': mounting " + Describe(part, mount.WantId) + " here, as player " + mount.WantBy + "'s game did.");
            why = null;
            return true;
        }

        private bool Unmount(Mount mount, GameObject thing, double now, out bool waits, out string why)
        {
            waits = false;
            MountStep step = mount.Definition.Unmount;
            if (step == null)
            {
                // The game lets go by itself (the charger, once the battery is moved away), and the thing moves here as
                // the other game moves it.
                waits = true;
                why = "the game takes it off by itself";
                return false;
            }

            PlayMakerFSM machine = Machine(step, thing);
            if (machine == null || !machine.gameObject.activeInHierarchy)
            {
                waits = true;
                why = "its machine '" + step.Machine + "' is " + (machine == null ? "not there" : "switched off");
                return false;
            }

            if (!machine.enabled)
            {
                machine.enabled = true;
            }

            // Loose again, it is the player's who took it off, until they let go of it.
            if (mount.Definition.Into != null)
            {
                objects.Expect(thing, mount.WantBy, now);
            }

            if (!Fire(machine, step, thing))
            {
                why = "its machine '" + step.Machine + "' has no " + (step.State != null ? "state '" + step.State + "'" : "global event '" + step.Event + "'");
                return false;
            }

            log.Info(Category, "Mounts: '" + mount.Definition.Name + "': taking " + Describe(thing, objects.IdOf(thing)) + " off here, as player " + mount.WantBy + "'s game did.");
            why = null;
            return true;
        }

        private void GiveUp(Mount mount, bool mounted, ushort id)
        {
            mount.Wanted = false;
            mount.Known = mounted;
            mount.KnownId = id;
            log.Warning(
                Category,
                "Mounts: '" + mount.Definition.Name + "' did not get " + (mount.WantMounted ? "#" + mount.WantId + " mounted" : "taken off") + " here; this game differs from player "
                + mount.WantBy + "'s there until one of them changes it.");
            if (mount.WantMounted && mount.WantId != 0 && mount.Definition.Into != null)
            {
                // Kept where it was last seen for this game to mount it: given back to this game's physics.
                GameObject left = objects.Abandon(mount.WantId, mount.Definition.Part);
                if (left != null)
                {
                    log.Info(Category, "Mounts: '" + left.name + "' is left loose here.");
                }
            }
        }

        // What is mounted now: with "into", a thing of that name below the place; with "onto", the thing the place hangs
        // from, if it is one of that name.
        private static bool Mounted(Mount mount, out GameObject thing)
        {
            thing = null;
            Transform place = mount.Place;

            // Unity's null check is also true for a destroyed object.
            if (place == null)
            {
                return false;
            }

            if (mount.Definition.Into != null)
            {
                for (int i = 0; i < place.childCount; i++)
                {
                    Transform child = place.GetChild(i);
                    if (child.name == mount.Definition.Part)
                    {
                        thing = child.gameObject;
                        return true;
                    }
                }

                return false;
            }

            Transform parent = place.parent;
            if (parent != null && parent != mount.Home && parent.name == mount.Definition.Part)
            {
                thing = parent.gameObject;
                return true;
            }

            return false;
        }

        // The machine of a step: on the object of its path, or on the thing itself.
        private static PlayMakerFSM Machine(MountStep step, GameObject thing)
        {
            Transform at = step.Path != null ? Scene(step.Path) : thing == null ? null : thing.transform;
            return at == null ? null : DoorSync.FindMachine(at, step.Machine);
        }

        // Takes the machine to the step's state, or sends it the step's event, after giving its variable the thing.
        private static bool Fire(PlayMakerFSM machine, MountStep step, GameObject thing)
        {
            if (step.State != null && !DoorSync.HasState(machine, step.State))
            {
                return false;
            }

            if (step.Event != null && !DoorSync.HasGlobalTransition(machine, step.Event))
            {
                return false;
            }

            string eventName = step.Event;
            if (step.State != null)
            {
                eventName = EventPrefix + step.State;
                if (!DoorSync.HasGlobalTransition(machine, eventName))
                {
                    machine.AddGlobalTransition(eventName, step.State);
                }
            }

            if (step.Variable != null)
            {
                // MSCLoader rebuilds a machine's data when a transition is added: the variable is found after that.
                FsmGameObject variable = machine.FsmVariables.FindFsmGameObject(step.Variable);
                if (variable != null)
                {
                    variable.Value = thing;
                }
            }

            machine.SendEvent(eventName);
            return true;
        }

        // An object from the scene's top, switched on or not; or, with '*' first, below an object of that name anywhere that has
        // the rest of the path below it (a part that hangs from wherever it is installed: the CD player).
        private static Transform Scene(string path)
        {
            if (path[0] == '*')
            {
                return Anywhere(path.Substring(1));
            }

            string plain = path.Substring(1);
            int slash = plain.IndexOf('/');
            GameObject root = GameObject.Find("/" + (slash < 0 ? plain : plain.Substring(0, slash)));
            return root == null ? null : slash < 0 ? root.transform : root.transform.Find(plain.Substring(slash + 1));
        }

        private static Transform Anywhere(string path)
        {
            int slash = path.IndexOf('/');
            string name = slash < 0 ? path : path.Substring(0, slash);
            string rest = slash < 0 ? null : path.Substring(slash + 1);
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(Transform));
            Transform best = null;
            for (int i = 0; i < all.Length; i++)
            {
                var named = all[i] as Transform;
                if (named == null || named.name != name || named.gameObject.hideFlags != HideFlags.None)
                {
                    continue;
                }

                Transform found = rest == null ? named : named.Find(rest);
                if (found != null && (best == null || (found.gameObject.activeInHierarchy && !best.gameObject.activeInHierarchy)))
                {
                    best = found;
                }
            }

            return best;
        }

        private static string Describe(GameObject thing, ushort id)
        {
            return thing == null ? "#" + id : "'" + thing.name + "'" + (id == 0 ? string.Empty : " (#" + id + ")");
        }

        private sealed class Mount
        {
            public MountDefinition Definition;

            /// <summary>Where the thing sits ("into"), or what goes onto it ("onto").</summary>
            public Transform Place;

            /// <summary>With "onto": where that is when it is on nothing.</summary>
            public Transform Home;

            /// <summary>What this game had at the last look, or was last made to have.</summary>
            public bool Known;
            public ushort KnownId;

            /// <summary>Another game's word, being done here.</summary>
            public bool Wanted;
            public bool WantMounted;
            public ushort WantId;
            public byte WantBy;
            public int Tries;
            public double NextTry;
            public double WaitUntil;
            public bool WaitSaid;
        }
    }
}
