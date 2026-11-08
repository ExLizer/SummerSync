using System;
using MSCLoader;
using SummerSync.Core.Logging;

namespace SummerSync.Unity
{
    /// <summary>Log level and per-category switches shown in the MSCLoader settings menu.</summary>
    internal sealed class LogSettings
    {
        private const int DefaultLevelIndex = 2;

        private static readonly LogLevel[] Levels =
        {
            LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error,
        };

        private static readonly string[] LevelNames = { "Trace (everything)", "Debug", "Info", "Warning", "Error" };

        // General is not listed: it carries startup and version information and is always on.
        private static readonly LogCategory[] Categories =
        {
            LogCategory.Net, LogCategory.Player, LogCategory.Authority, LogCategory.Vehicle, LogCategory.Fsm, LogCategory.Passenger,
        };

        // Static: what the log was last said to record must survive MSCLoader re-creating the mod.
        private static bool reported;

        private SettingsDropDownList level;
        private SettingsCheckBox[] categories;

        /// <summary>Creates the settings. Must be called from the mod's ModSettings function.</summary>
        public void Create(Action onChanged)
        {
            Settings.AddHeader("Logging", false, true);
            level = Settings.AddDropDownList("logLevel", "Minimum log level", LevelNames, DefaultLevelIndex, onChanged, true);

            categories = new SettingsCheckBox[Categories.Length];
            for (int i = 0; i < Categories.Length; i++)
            {
                string name = LogFormatter.CategoryName(Categories[i]);
                categories[i] = Settings.AddCheckBox("logCategory_" + name, "Log category: " + name, true, onChanged, true);
            }
        }

        /// <summary>Copies the current setting values to the logger. Does nothing before <see cref="Create"/>.</summary>
        public void ApplyTo(Logger logger)
        {
            if (level == null)
            {
                return;
            }

            LogLevel levelBefore = logger.MinimumLevel;
            LogCategory categoriesBefore = logger.EnabledCategories;

            int index = level.GetSelectedItemIndex();
            logger.MinimumLevel = index >= 0 && index < Levels.Length ? Levels[index] : Levels[DefaultLevelIndex];

            LogCategory enabled = LogCategory.General;
            for (int i = 0; i < Categories.Length; i++)
            {
                if (categories[i].GetValue())
                {
                    enabled |= Categories[i];
                }
            }

            logger.EnabledCategories = enabled;

            // Written whatever the level is: a log that goes quiet has to say that it was told to.
            if (!reported || levelBefore != logger.MinimumLevel || categoriesBefore != enabled)
            {
                reported = true;
                logger.Always(LogCategory.General, "Log settings: minimum level " + logger.MinimumLevel + ", categories " + enabled + ".");
            }
        }
    }
}
