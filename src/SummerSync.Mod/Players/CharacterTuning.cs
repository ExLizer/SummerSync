using System;
using System.IO;
using SummerSync.Core.Logging;
using SummerSync.Core.Players;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// How the people move and where their heads look, as tuned in the character window: one set for the whole run
    /// of the game, read from "poses.txt" in the mod's folder and written back there when the player saves. Every
    /// model made from then on, and every one already shown, follows it (<see cref="PoseSettings.Serial"/>).
    /// </summary>
    internal static class CharacterTuning
    {
        private const string FileName = "poses.txt";

        private static PoseSettings settings;

        public static PoseSettings Settings
        {
            get
            {
                if (settings == null)
                {
                    Load();
                }

                return settings;
            }
        }

        public static string FilePath
        {
            get { return ModRuntime.SettingsFolder == null ? null : Path.Combine(ModRuntime.SettingsFolder, FileName); }
        }

        /// <summary>Writes the values to the file, and to the log, so that they can be made everyone's standard.</summary>
        public static string Save()
        {
            string text = Settings.Write();
            string path = FilePath;
            ModRuntime.Log.Info(LogCategory.Player, "Tuned poses and heads:\n" + text.TrimEnd('\n'));
            if (path == null)
            {
                return "No hay carpeta del mod: los valores quedaron solo en el log.";
            }

            try
            {
                File.WriteAllText(path, text);
                return "Guardado en " + FileName + ".";
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Warning(LogCategory.Player, "The tuned poses could not be written to '" + path + "': " + exception.Message);
                return "No se pudo guardar: " + exception.Message;
            }
        }

        private static void Load()
        {
            string path = FilePath;
            string text = null;
            try
            {
                if (path != null && File.Exists(path))
                {
                    text = File.ReadAllText(path);
                }
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Warning(LogCategory.Player, "The tuned poses could not be read from '" + path + "' (" + exception.Message + "); the standard ones are used.");
            }

            settings = PoseSettings.Parse(text);
            if (text != null)
            {
                ModRuntime.Log.Info(LogCategory.Player, "Tuned poses and heads read from '" + path + "':\n" + settings.Write().TrimEnd('\n'));
            }
        }
    }
}
