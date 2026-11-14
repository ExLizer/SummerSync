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
    ///
    /// Without making garbage where it can (measured on 2026-11-14: the knobs and switches asked the memory for about 12 MB
    /// a minute, and the game's garbage collector stops everything for about 160 ms each time it runs): no text is made to
    /// look an entry up, the engine's own search by name is tried first, and the vehicle's objects, each of whose names
    /// Unity makes anew when it is read, are gone through only now and then.
    /// </summary>
    internal sealed class PartFinder
    {
        // How often a part found neither way is looked for again, going through every object below the vehicle.
        private const double LookEvery = 10.0;

        // And by the engine's search, which makes nothing.
        private const double QuickLookEvery = 1.0;

        // By the vehicle's root, then by the part's name.
        private readonly Dictionary<int, Dictionary<string, Entry>> parts = new Dictionary<int, Dictionary<string, Entry>>();

        /// <summary>The part's object while it is below the root, or null.</summary>
        public Transform Find(Transform root, string name, double now)
        {
            int rootId = root.GetInstanceID();
            Dictionary<string, Entry> ofRoot;
            if (!parts.TryGetValue(rootId, out ofRoot))
            {
                ofRoot = new Dictionary<string, Entry>();
                parts[rootId] = ofRoot;
            }

            Entry entry;
            if (!ofRoot.TryGetValue(name, out entry))
            {
                entry = new Entry { NextLook = double.NegativeInfinity, NextQuickLook = double.NegativeInfinity };
                ofRoot[name] = entry;
            }

            // Unity's null check is also true for a destroyed object.
            if (entry.Found != null && entry.Found.IsChildOf(root))
            {
                return entry.Found;
            }

            entry.Found = null;
            if (now >= entry.NextQuickLook)
            {
                // A part installed is switched on and, as a rule, the one object of its name.
                entry.NextQuickLook = now + QuickLookEvery;
                GameObject named = name.IndexOf('/') < 0 ? GameObject.Find(name) : null;
                if (named != null && named.transform.IsChildOf(root))
                {
                    entry.Found = named.transform;
                    return entry.Found;
                }
            }

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
            public double NextQuickLook;
        }
    }
}
