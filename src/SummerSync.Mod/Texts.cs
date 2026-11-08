namespace SummerSync.Unity
{
    /// <summary>
    /// What players read, in their language: English unless the mod's setting "Language" says Spanish
    /// (English by default). Each text is written next to where it is shown, in both languages. The log and the
    /// console stay in English.
    /// </summary>
    internal static class Texts
    {
        /// <summary>The languages of the setting, in its order.</summary>
        public static readonly string[] Languages = { "English", "Español" };

        /// <summary>Whether the player reads Spanish.</summary>
        public static bool Spanish { get; set; }

        /// <summary>The text in the player's language.</summary>
        public static string Pick(string english, string spanish)
        {
            return Spanish ? spanish : english;
        }
    }
}
