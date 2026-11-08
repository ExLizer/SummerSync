using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SummerSync.Dumper
{
    /// <summary>Builds hierarchy paths for transforms, caching them for the duration of one dump.</summary>
    internal sealed class PathCache
    {
        private readonly Dictionary<int, string> namePaths = new Dictionary<int, string>();
        private readonly Dictionary<int, string> indexPaths = new Dictionary<int, string>();

        /// <summary>
        /// Object names from the root down, joined with '/'. Readable but not unique: siblings can share a name,
        /// and names in this game can contain '/' themselves.
        /// </summary>
        public string NamePath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            int id = transform.GetInstanceID();
            string path;
            if (!namePaths.TryGetValue(id, out path))
            {
                Transform parent = transform.parent;
                path = parent == null ? transform.name : NamePath(parent) + "/" + transform.name;
                namePaths[id] = path;
            }

            return path;
        }

        /// <summary>Sibling indices from the root down ("0/3/1"). Tells apart objects that share a name path.</summary>
        public string IndexPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            int id = transform.GetInstanceID();
            string path;
            if (!indexPaths.TryGetValue(id, out path))
            {
                string index = transform.GetSiblingIndex().ToString(CultureInfo.InvariantCulture);
                Transform parent = transform.parent;
                path = parent == null ? index : IndexPath(parent) + "/" + index;
                indexPaths[id] = path;
            }

            return path;
        }

        /// <summary>Name path of <paramref name="descendant"/> below <paramref name="root"/>; empty for the root itself.</summary>
        public string RelativePath(Transform root, Transform descendant)
        {
            string rootPath = NamePath(root);
            string path = NamePath(descendant);
            return path.Length > rootPath.Length ? path.Substring(rootPath.Length + 1) : string.Empty;
        }
    }
}
