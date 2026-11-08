using MSCLoader;
using SummerSync.Core;
using SummerSync.Core.Logging;

namespace SummerSync.Unity
{
    /// <summary>
    /// Mirrors the important entries to the in-game MSCLoader console: every warning and error, plus general
    /// information. Verbose per-subsystem output stays in the log file so the console remains readable.
    ///
    /// MSCLoader opens its console for every warning and error it is given (<c>ModConsole.Warning</c> and
    /// <c>ModConsole.Error</c>), which in a session covers the screen whenever another player's log reports something. Unless
    /// the player asks for that (<see cref="OpensConsole"/>), they are printed in the same colours without opening it: they
    /// are there when the console is opened with its key, and in the log file.
    /// </summary>
    internal sealed class ModConsoleLogSink : ILogSink
    {
        private const string Prefix = "[" + BuildInfo.ProductName + "] ";

        /// <summary>Whether a warning or an error opens MSCLoader's console. Off unless the mod's setting says so.</summary>
        public static bool OpensConsole { get; set; }

        public void Write(LogEntry entry)
        {
            if (entry.Level >= LogLevel.Error)
            {
                if (OpensConsole)
                {
                    ModConsole.Error(Prefix + entry.Message);
                }
                else
                {
                    ModConsole.Print("<color=red><b>Error: </b>" + Prefix + entry.Message + "</color>");
                }
            }
            else if (entry.Level == LogLevel.Warning)
            {
                if (OpensConsole)
                {
                    ModConsole.Warning(Prefix + entry.Message);
                }
                else
                {
                    ModConsole.Print("<color=yellow><b>Warning: </b>" + Prefix + entry.Message + "</color>");
                }
            }
            else if (entry.Level == LogLevel.Info && entry.Category == LogCategory.General)
            {
                ModConsole.Print(Prefix + entry.Message);
            }
        }
    }
}
