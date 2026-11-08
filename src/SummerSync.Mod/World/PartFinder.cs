using System.Collections.Generic;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Finds, below a vehicle, a part that is installed on it and taken off again, by the name the game gives the
    /// part's object: the Satsuma's instrument panel ("dashboard meters(Clone)") and its radio carry knobs that are
    /// shared. Installed, the part hangs somewhere below the vehicle's root (from another part, as the radio from the
    /// instrument panel); taken off, it lies loose in the world and is not the vehicle's. While it is not below the
    /// root it is looked for again now and then, not on every frame: the Satsuma has thousands of objects.
    /// </summary>
    internal sealed class PartFinder
    {
        private const double LookEvery = 2.0;

        private readonly Dictionary<string, Entry> parts = new Dictionary<string, Entry>();

        /// <summary>The part's object while it is below the root, or null.</summary>
        public Transform Find(Transform root, string name, double now)
        {
            string key = root.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + name;
            Entry entry;
            if (!parts.TryGetValue(key, out entry))
            {
                entry = new Entry { NextLook = double.NegativeInfinity };
                parts[key] = entry;
            }

            // Unity's null check is also true for a destroyed object.
            if (entry.Found != null && entry.Found.IsChildOf(root))
            {
                return entry.Found;
            }

            entry.Found = null;
            if (now < entry.NextLook)
            {
                return null;
            }

            entry.NextLook = now + LookEvery;
            entry.Found = Below(root, name);
            return entry.Found;
        }

        public void Clear()
        {
            parts.Clear();
        }

        /// <summary>The first object of that name below the root, switched off or not, nearest to it first; null if none.</summary>
        public static Transform Below(Transform root, string name)
        {
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                Transform at = queue.Dequeue();
                int children = at.childCount;
                for (int i = 0; i < children; i++)
                {
                    Transform child = at.GetChild(i);
                    if (child.name == name)
                    {
                        return child;
                    }

                    queue.Enqueue(child);
                }
            }

            return null;
        }

        private sealed class Entry
        {
            public Transform Found;
            public double NextLook;
        }
    }
}
