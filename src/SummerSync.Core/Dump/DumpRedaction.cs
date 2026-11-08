using System;

namespace SummerSync.Core.Dump
{
    /// <summary>
    /// Decides which values never go into a dump. Dumps get passed around to diagnose problems, so anything that
    /// looks like a credential is replaced by a placeholder at the source.
    /// </summary>
    public static class DumpRedaction
    {
        public const string Placeholder = "<redacted>";

        private static readonly string[] SecretMarkers = { "password", "passwd", "secret", "token", "apikey", "api_key" };

        /// <summary>Whether a variable with this name is assumed to hold a credential.</summary>
        public static bool IsSecret(string variableName)
        {
            if (string.IsNullOrEmpty(variableName))
            {
                return false;
            }

            for (int i = 0; i < SecretMarkers.Length; i++)
            {
                if (variableName.IndexOf(SecretMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
