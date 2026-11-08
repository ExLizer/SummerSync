using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// Which of the game's people each player looks like. The local player's choice is kept in a file of the mod's
    /// settings and said to the others as a tally ("look/" and the player's number, set by them: see
    /// <see cref="TallyReplication.Set"/>), once they are in a session, in the main menu's lobby or in the game world. A
    /// player who has not said looks like one of the passers-by, by their number.
    /// </summary>
    internal sealed class LookSync
    {
        private const string KeyPrefix = "look/";
        private const string FileName = "character.txt";
        private const int Unknown = int.MinValue;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly string file;
        private readonly Dictionary<byte, int> looks = new Dictionary<byte, int>();
        private TallyReplication current;
        private int mine = Unknown;
        private bool told;
        private bool askedInMenu;

        public LookSync(Logger log, NetworkManager manager, string settingsFolder)
        {
            this.log = log;
            this.manager = manager;
            file = string.IsNullOrEmpty(settingsFolder) ? null : Path.Combine(settingsFolder, FileName);
            mine = Load();
        }

        /// <summary>
        /// While the character window is open in the main menu: the look being looked at, which the lobby shows on the local
        /// player as it moves (nothing is said to the others until it is chosen). Null otherwise.
        /// </summary>
        public int? Previewing { get; set; }

        /// <summary>Whether the local player has ever chosen.</summary>
        public bool HasChosen
        {
            get { return mine != Unknown; }
        }

        /// <summary>The local player's look: an index of <see cref="Characters.All"/>, or -1 for a capsule.</summary>
        public int Mine
        {
            get { return mine == Unknown ? 0 : mine; }
        }

        /// <summary>The look of another player, as they said, or a passer-by by their number.</summary>
        public int LookOf(byte player)
        {
            int look;
            return looks.TryGetValue(player, out look) ? look : (player + 5) % 6;
        }

        /// <summary>The local player chooses. It is kept, and said to the others.</summary>
        public void Choose(int look)
        {
            mine = look;
            told = false;
            Save();
            Characters.Look chosen = Characters.At(look);
            log.Info(LogCategory.Player, "The local player looks like " + (chosen == null ? "a capsule" : chosen.Name) + " from now on.");
        }

        /// <summary>
        /// Once per frame. <paramref name="localPlayer"/> is the player's root object, or null with no game world;
        /// <paramref name="inMenu"/>, whether the main menu is the scene (its lobby shows the players as they look).
        /// </summary>
        public void LateUpdate(Transform localPlayer, bool inMenu)
        {
            // Listened to for as long as the session lasts, loading screens included: what is said while a world loads is
            // not lost.
            TallyReplication tallies = manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                current = tallies;
                looks.Clear();
                told = false;
                askedInMenu = false;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }
            }

            NetSession session = manager.Session;
            if (current == null || session == null || session.State != SessionState.Connected)
            {
                return;
            }

            // A guest in the main menu's lobby asks for what the others said before it came. Never in a world: there the
            // world's parts ask, and a question already asked would not be asked again for them.
            if (inMenu && localPlayer == null && !askedInMenu && manager.Mode == NetworkMode.Client)
            {
                askedInMenu = true;
                current.RequestAll();
            }

            if (told)
            {
                return;
            }

            told = true;
            current.Set(KeyPrefix + session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { Mine + 1 });
        }

        private void OnStateWanted(string key, int[] values)
        {
            byte player;
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) || values.Length < 1
                || !byte.TryParse(key.Substring(KeyPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out player))
            {
                return;
            }

            int look = values[0] - 1;
            looks[player] = look >= -1 && look < Characters.All.Length ? look : -1;
        }

        private int Load()
        {
            try
            {
                if (file == null || !File.Exists(file))
                {
                    return Unknown;
                }

                string text = File.ReadAllText(file).Trim();
                if (text == "capsule")
                {
                    return -1;
                }

                for (int i = 0; i < Characters.All.Length; i++)
                {
                    if (Characters.All[i].Name == text)
                    {
                        return i;
                    }
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Player, "Could not read the chosen character: " + exception.Message);
            }

            return Unknown;
        }

        private void Save()
        {
            try
            {
                if (file != null)
                {
                    Characters.Look look = Characters.At(mine);
                    File.WriteAllText(file, look == null ? "capsule" : look.Name);
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Player, "Could not keep the chosen character: " + exception.Message);
            }
        }
    }
}
