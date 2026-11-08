using System;
using System.IO;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// The catalogue of the Satsuma's parts that travels inside the mod (data/satsuma/parts.json), and the game's own
    /// record of each part: the machine "Data" of "Database/&lt;entry&gt;", which says whether it is installed and which
    /// object the part is (docs/GAME_NOTES.md, "El armado del Satsuma").
    /// </summary>
    internal static class SatsumaParts
    {
        public const string Resource = "SummerSync.satsuma.parts.json";
        public const string DatabaseRoot = "Database";
        public const string DataMachine = "Data";

        private static SatsumaCatalog catalog;
        private static bool loaded;

        /// <summary>The catalogue, read the first time it is asked for; null if the build's copy cannot be read (logged once).</summary>
        public static SatsumaCatalog Load(Logger log)
        {
            if (loaded)
            {
                return catalog;
            }

            loaded = true;
            try
            {
                using (Stream stream = typeof(SatsumaParts).Assembly.GetManifestResourceStream(Resource))
                {
                    if (stream == null)
                    {
                        log.Error(LogCategory.Vehicle, "This build has no catalogue of the Satsuma's parts: putting it together is not shared.");
                        return null;
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        catalog = SatsumaCatalog.Parse(reader.ReadToEnd());
                    }
                }

                log.Info(
                    LogCategory.Vehicle,
                    "Catalogue of the Satsuma's parts: " + catalog.Entries.Length + " entries, " + catalog.BoltCount + " bolts, shape "
                    + catalog.Shape.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ".");
            }
            catch (FormatException exception)
            {
                log.Error(LogCategory.Vehicle, "The catalogue of the Satsuma's parts cannot be read; putting it together is not shared. " + exception.Message);
                catalog = null;
            }

            return catalog;
        }

        /// <summary>The entry's machine "Data", or null if the scene has none (no world, or another version of the game).</summary>
        public static PlayMakerFSM DataOf(PartEntry entry)
        {
            if (entry == null || entry.Group == "Wheels")
            {
                return null;
            }

            GameObject record = GameObject.Find("/" + DatabaseRoot + "/" + entry.Name);
            if (record == null)
            {
                return null;
            }

            PlayMakerFSM[] machines = record.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == DataMachine)
                {
                    return machines[i];
                }
            }

            return null;
        }

        /// <summary>The object the game's record names as the part (its variable "ThisPart" or the like), or null.</summary>
        public static GameObject PartOf(PartEntry entry, PlayMakerFSM data)
        {
            if (entry == null || data == null || entry.PartVariable == null)
            {
                return null;
            }

            FsmGameObject part = data.FsmVariables.FindFsmGameObject(entry.PartVariable);
            return part == null ? null : part.Value;
        }
    }
}
