using SummerSync.Core.Hashing;

namespace SummerSync.Core.Net
{
    /// <summary>
    /// Names the build of the game a player is running, for the handshake: two players can only share a
    /// session when the names are equal. It is derived from the game's main assembly rather than from Steam's
    /// build id, so it also works in a copy of the game that runs without Steam, and outside the game.
    /// </summary>
    public static class GameBuild
    {
        /// <summary>The Unity version of the game this mod is written for.</summary>
        public const string ExpectedUnityVersion = "5.0.0f4";

        public const string MainAssemblyFileName = "Assembly-CSharp.dll";

        private const int HashDigits = 8;

        /// <param name="mainAssembly">Contents of the game's Assembly-CSharp.dll.</param>
        public static string Identify(string unityVersion, byte[] mainAssembly)
        {
            return unityVersion + "/" + Fnv1a.ToHex(Fnv1a.Hash64(mainAssembly)).Substring(0, HashDigits);
        }
    }
}
