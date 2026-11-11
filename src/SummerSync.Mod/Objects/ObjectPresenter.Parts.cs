using UnityEngine;

namespace SummerSync.Unity.Objects
{
    /// <summary>
    /// What the Satsuma's assembly (<see cref="SummerSync.Unity.World.PartSync"/>) needs from the loose objects: the
    /// shared number of the part a player put in, the copy here of a part another player put in, and to stop moving a
    /// part this game is about to build into the car.
    /// </summary>
    internal sealed partial class ObjectPresenter
    {
        /// <summary>The shared number of what the local player has in hand now, or 0.</summary>
        public ushort HeldId
        {
            get { return lastInHand != null && lastInHand.Shared.HasId ? lastInHand.Shared.Id : (ushort)0; }
        }

        /// <summary>The shared number of the loose object that is that game object, or 0 if it is not shared (yet).</summary>
        public ushort SharedIdOf(GameObject thing)
        {
            if (thing == null)
            {
                return 0;
            }

            Transform transform = thing.transform;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Transform == transform)
                {
                    return tracked[i].Shared.HasId ? tracked[i].Shared.Id : (ushort)0;
                }
            }

            return 0;
        }

        /// <summary>
        /// The loose things here called <paramref name="name"/>, with their shared numbers (0 for one not shared yet), into
        /// the lists (cleared first).
        /// </summary>
        public void SharedNamed(string name, System.Collections.Generic.List<GameObject> things, System.Collections.Generic.List<ushort> ids)
        {
            things.Clear();
            ids.Clear();
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked item = tracked[i];

                // Unity's null check is also true for a destroyed object.
                if (item.Transform == null || item.Shared.Name != name)
                {
                    continue;
                }

                things.Add(item.Transform.gameObject);
                ids.Add(item.Shared.HasId ? item.Shared.Id : (ushort)0);
            }
        }

        /// <summary>
        /// Whether that thing is a copy here of a loose object another machine moves (in another player's hand, or on its way
        /// where that player's game has it): a job here must not take it for its own (<see cref="World.JobSync"/>).
        /// </summary>
        public bool MovedElsewhere(GameObject thing)
        {
            if (thing == null)
            {
                return false;
            }

            Transform transform = thing.transform;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Transform == transform)
                {
                    return tracked[i].Shared.Role == SummerSync.Core.Objects.BodyRole.Follow;
                }
            }

            return false;
        }

        /// <summary>Whether that thing is a copy here of a loose object that another player has in hand.</summary>
        public bool HeldElsewhere(GameObject thing)
        {
            if (thing == null)
            {
                return false;
            }

            Transform transform = thing.transform;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Transform == transform)
                {
                    return tracked[i].Shared.Role == SummerSync.Core.Objects.BodyRole.Follow && tracked[i].Shared.Held;
                }
            }

            return false;
        }

        /// <summary>
        /// The shared number of that thing: the one it has, or, no longer loose (mounted, built in), the one it had when it
        /// stopped being. 0 if it never had one.
        /// </summary>
        public ushort IdOf(GameObject thing)
        {
            ushort id = SharedIdOf(thing);
            if (id != 0 || thing == null)
            {
                return id;
            }

            return forgottenIds.TryGetValue(thing.GetInstanceID(), out id) ? id : (ushort)0;
        }

        /// <summary>The thing here with that shared number, loose or no longer loose since; null if there is none.</summary>
        public GameObject ThingOf(ushort id)
        {
            GameObject thing = FindShared(id);
            if (thing != null)
            {
                return thing;
            }

            // Unity's null check is also true for a destroyed object.
            return forgottenThings.TryGetValue(id, out thing) && thing != null ? thing : null;
        }

        /// <summary>The copy here of the loose object with that shared number, switched off or not; null if there is none.</summary>
        public GameObject FindShared(ushort id)
        {
            if (id == 0)
            {
                return null;
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked item = tracked[i];

                // Unity's null check is also true for a destroyed object.
                if (item.Shared.HasId && item.Shared.Id == id && item.Transform != null)
                {
                    return item.Transform.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// The loose object of that name nearest to a place, switched off or not, among those shared and in nobody's
        /// hand; null if there is none. For a part whose shared number is not known.
        /// </summary>
        public GameObject FindLoose(string name, Vector3 near)
        {
            GameObject best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked item = tracked[i];

                // Unity's null check is also true for a destroyed object.
                if (item.Transform == null || item.Shared.Held || item == lastInHand || item.Transform.name != name)
                {
                    continue;
                }

                float distance = (item.Transform.position - near).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = item.Transform.gameObject;
                }
            }

            return best;
        }

        /// <summary>
        /// A part of the car that the host's world no longer has loose (built in there), kept where it was last shown for
        /// this game to build it in too, when this game will not: given back to the game, loose, solid and moved by its
        /// physics, and no longer shared. The one with that shared number, else one of that name. Returns it, or null.
        /// </summary>
        public GameObject Abandon(ushort id, string name)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < tracked.Count; i++)
                {
                    Tracked item = tracked[i];

                    // Unity's null check is also true for a destroyed object.
                    if (item.Transform == null || item.Shared.Present || item.Shared.Held)
                    {
                        continue;
                    }

                    bool match = pass == 0 ? id != 0 && item.Shared.HasId && item.Shared.Id == id : item.Transform.name == name;
                    if (match)
                    {
                        GameObject thing = item.Transform.gameObject;
                        Forget(item, i);
                        return thing;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// A job of this game is about to take that thing because another game's job took its copy (the berry box delivered,
        /// a bottle put in the kilju buyer's stash): it is given back to the game whole, switched on, solid and moved by its
        /// physics, as it was before another machine moved it, and no longer shared as loose. The game then does with it what
        /// it does (switches it off, hangs it somewhere). Returns whether it was shared.
        /// </summary>
        public bool GiveBack(GameObject thing)
        {
            if (thing == null)
            {
                return false;
            }

            Transform transform = thing.transform;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Transform == transform)
                {
                    // Seen when the box went back to its stand kinematic: switched on again, the scan left it out.
                    Forget(tracked[i], i);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// This game is about to build that object into the car: it is given back to the game (switched on, solid, on
        /// its own layer) and no longer shared as loose. Whether it is kinematic is left as it is: the game sets that
        /// as it builds the part in, and a part another machine was moving stays where it is until then. Returns
        /// whether it was shared.
        /// </summary>
        public bool Release(GameObject thing)
        {
            if (thing == null)
            {
                return false;
            }

            Transform transform = thing.transform;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked item = tracked[i];
                if (item.Transform != transform)
                {
                    continue;
                }

                item.MadeKinematic = false;
                Forget(item, i);
                return true;
            }

            return false;
        }
    }
}
