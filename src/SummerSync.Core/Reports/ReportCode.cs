namespace SummerSync.Core.Reports
{
    /// <summary>
    /// The code the reports' server gives a report: "R-" and six characters of an alphabet without the ones that read
    /// alike (no 0, 1, I, L, O, U). It is what the player sees, what Discord shows and what another player's report names
    /// to be filed with it.
    /// </summary>
    public static class ReportCode
    {
        public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

        public const string Prefix = "R-";

        public const int Characters = 6;

        public const int Length = 8;

        /// <summary>Whether that is a code as the server makes them, exactly (upper case).</summary>
        public static bool IsValid(string code)
        {
            if (code == null || code.Length != Length || !code.StartsWith(Prefix, System.StringComparison.Ordinal))
            {
                return false;
            }

            for (int i = Prefix.Length; i < Length; i++)
            {
                if (Alphabet.IndexOf(code[i]) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A code as a player may type it ("r-4f2k7q", " R-4F2K7Q "), as the server writes it; null if it is not one.</summary>
        public static string Normalize(string typed)
        {
            if (typed == null)
            {
                return null;
            }

            string code = typed.Trim().ToUpperInvariant();
            return IsValid(code) ? code : null;
        }
    }
}
