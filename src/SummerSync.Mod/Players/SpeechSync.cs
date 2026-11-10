using System;
using System.Globalization;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// What a player says with the game's keys, heard by the others where that player is: swearing, the drunk mumbling,
    /// the short answers, and the insult that goes with the middle finger, which the others also see.
    ///
    /// What is assumed about the game (decoded level of 2026-10-18; docs/GAME_NOTES.md, "Insultar, balbucear y el dedo"):
    /// - Under the player's camera, "SpeakDatabase :: Speech" says something in the states "Swear" (the sound group
    ///   "Swearing", variations "1" to "16"), "Drunk speech" ("Drunk", "1" to "13") and "Yes gestures" ("Yes", "1" to
    ///   "27"); the camera's own "PlayerFunctions" does in "Finger" ("Fuck", "1" to "11"), where it also switches on the
    ///   hand "FPSCamera/MiddleFinger". Each draws the variation into its integer "RandomInt" on entering the state.
    /// - A sound group is an object "MasterAudio/(group)", and each variation a child with that name and an AudioSource.
    ///
    /// A game whose player says something tells the tally "say/(player)": which kind, which variation, and a number that
    /// makes it news. The others play that variation at the player's head, and for the finger hold the hand up.
    /// </summary>
    internal sealed class SpeechSync
    {
        /// <summary>The kinds, as they travel: never renumbered.</summary>
        public const int Finger = 4;

        private const string CameraPath = "Pivot/AnimPivot/Camera/FPSCamera";
        private const string SpeechPath = "Pivot/AnimPivot/Camera/FPSCamera/SpeakDatabase";
        private const string SpeechMachine = "Speech";
        private const string FunctionsMachine = "PlayerFunctions";
        private const string DrawnVariable = "RandomInt";
        private const string Key = "say/";
        private const double FindSeconds = 3.0;
        private const LogCategory Category = LogCategory.Player;

        private static readonly Kind[] Kinds =
        {
            new Kind(1, false, "Swear", "Swearing", 16, 1f),
            new Kind(2, false, "Drunk speech", "Drunk", 13, 1f),
            new Kind(3, false, "Yes gestures", "Yes", 27, 0.8f),
            new Kind(Finger, true, "Finger", "Fuck", 11, 1f),
        };

        // The one the game's machines tell, and the machines that already do: they live as long as the world.
        private static SpeechSync listening;
        private static PlayMakerFSM hookedSpeech;
        private static PlayMakerFSM hookedFunctions;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly PlayerPresenter players;
        private readonly int[] lastToken = new int[256];
        private TallyReplication current;
        private Transform player;
        private PlayMakerFSM speech;
        private PlayMakerFSM functions;
        private bool found;
        private bool missingReported;
        private double nextFind;

        // The kinds the local player started saying in this frame, as bits by their place in the list.
        private int entered;
        private int said;
        private int heard;

        public SpeechSync(Logger log, NetworkManager manager, PlayerPresenter players)
        {
            this.log = log;
            this.manager = manager;
            this.players = players;
        }

        /// <summary>The sound group and the loudness of a kind, and how many variations it has; false for an unknown one.</summary>
        public static bool Describe(int kind, out string group, out float volume, out int variations)
        {
            for (int i = 0; i < Kinds.Length; i++)
            {
                if (Kinds[i].Number == kind)
                {
                    group = Kinds[i].Group;
                    volume = Kinds[i].Volume;
                    variations = Kinds[i].Variations;
                    return true;
                }
            }

            group = null;
            volume = 0f;
            variations = 0;
            return false;
        }

        /// <summary>Once per frame, after the game's machines. <paramref name="localPlayer"/> is the player's root object, or null without a world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current || localPlayer != player)
            {
                Release();
                current = tallies;
                player = localPlayer;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                    listening = this;
                }
            }

            int started = entered;
            entered = 0;
            NetSession session = manager.Session;
            if (current == null || session == null || session.State != SessionState.Connected)
            {
                return;
            }

            if (!found)
            {
                if (now < nextFind)
                {
                    return;
                }

                nextFind = now + FindSeconds;
                if (!Find())
                {
                    return;
                }
            }

            // Unity's null check is also true for a destroyed component.
            if (speech == null || functions == null)
            {
                found = false;
                return;
            }

            for (int i = 0; started != 0 && i < Kinds.Length; i++)
            {
                if ((started & (1 << i)) == 0)
                {
                    continue;
                }

                Kind kind = Kinds[i];
                FsmInt drawn = (kind.OfFunctions ? functions : speech).FsmVariables.FindFsmInt(DrawnVariable);
                int variation = drawn == null ? 0 : drawn.Value;
                if (variation < 1 || variation > kind.Variations)
                {
                    continue;
                }

                int token = (int)((long)(now * 1000.0) & 0x7FFFFFFF);
                current.Set(Key + session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { kind.Number, variation, token });
                said++;
                if (said == 1)
                {
                    log.Info(Category, "Speech: this player said '" + kind.Group + "' " + variation + "; the others are told, now and from here on.");
                }
            }
        }

        /// <summary>Stops listening; the game's machines keep telling whoever listens next.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
                if (said > 0 || heard > 0)
                {
                    log.Info(Category, "Speech: this player was heard " + said + " time(s) by the others, and they " + heard + " time(s) here.");
                }
            }

            if (listening == this)
            {
                listening = null;
            }

            current = null;
            player = null;
            speech = null;
            functions = null;
            found = false;
            entered = 0;
            said = 0;
            heard = 0;
            nextFind = 0.0;
            Array.Clear(lastToken, 0, lastToken.Length);
        }

        private bool Find()
        {
            Transform camera = player.Find(CameraPath);
            Transform database = player.Find(SpeechPath);
            speech = database == null ? null : World.DoorSync.FindMachine(database, SpeechMachine);
            functions = camera == null ? null : World.DoorSync.FindMachine(camera, FunctionsMachine);
            string problem = speech == null ? "the player's '" + SpeechMachine + "' was not found"
                : functions == null ? "the player's '" + FunctionsMachine + "' was not found" : null;
            for (int i = 0; problem == null && i < Kinds.Length; i++)
            {
                PlayMakerFSM machine = Kinds[i].OfFunctions ? functions : speech;
                if (!World.DoorSync.HasState(machine, Kinds[i].State) || machine.FsmVariables.FindFsmInt(DrawnVariable) == null)
                {
                    problem = "'" + machine.FsmName + "' has no state '" + Kinds[i].State + "' that draws into '" + DrawnVariable + "'";
                }
            }

            if (problem == null)
            {
                try
                {
                    problem = Hook();
                }
                catch (Exception exception)
                {
                    problem = "its states could not be listened to (" + exception.Message + ")";
                }
            }

            if (problem != null)
            {
                speech = null;
                functions = null;
                if (!missingReported)
                {
                    missingReported = true;
                    log.Warning(Category, "Speech: " + problem + ": what this player says is not heard by the others.");
                }

                return false;
            }

            found = true;
            log.Info(Category, "Speech: listening to what this player says (swearing, mumbling, answers) and to the middle finger.");
            return true;
        }

        // Each state tells as it is entered, once per machine however many sessions the world sees.
        private string Hook()
        {
            for (int i = 0; i < Kinds.Length; i++)
            {
                bool ofFunctions = Kinds[i].OfFunctions;
                PlayMakerFSM machine = ofFunctions ? functions : speech;
                if (ReferenceEquals(machine, ofFunctions ? hookedFunctions : hookedSpeech))
                {
                    continue;
                }

                int bit = 1 << i;
                if (!machine.FsmInject(Kinds[i].State, () => OnEntered(bit), false, 0, false))
                {
                    return "'" + machine.FsmName + "' would not take a listener in '" + Kinds[i].State + "'";
                }
            }

            hookedSpeech = speech;
            hookedFunctions = functions;
            return null;
        }

        // From the game's machine, as the state is entered: its number is drawn after this, so it is read later in the frame.
        private static void OnEntered(int bit)
        {
            SpeechSync told = listening;
            if (told != null)
            {
                told.entered |= bit;
            }
        }

        private void OnStateWanted(string name, int[] values)
        {
            NetSession session = manager.Session;
            if (!name.StartsWith(Key, StringComparison.Ordinal) || values.Length < 3 || session == null)
            {
                return;
            }

            byte who;
            if (!byte.TryParse(name.Substring(Key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out who))
            {
                return;
            }

            // What was said before this game listened is not said again; nor this player's own words coming back.
            bool news = !current.CatchingUp && values[2] != lastToken[who] && who != session.LocalPlayerId;
            lastToken[who] = values[2];
            if (!news)
            {
                return;
            }

            string group;
            float volume;
            int variations;
            if (!Describe(values[0], out group, out volume, out variations) || values[1] < 1 || values[1] > variations)
            {
                return;
            }

            heard++;
            string result = players.Say(who, values[0], values[1]);
            if (heard == 1 || result != null)
            {
                log.Info(Category, "Speech: player " + who + " says '" + group + "' " + values[1] + (result == null ? "; it is played where they are." : "; " + result));
            }
        }

        private struct Kind
        {
            public Kind(int number, bool ofFunctions, string state, string group, int variations, float volume)
            {
                Number = number;
                OfFunctions = ofFunctions;
                State = state;
                Group = group;
                Variations = variations;
                Volume = volume;
            }

            public int Number;
            public bool OfFunctions;
            public string State;
            public string Group;
            public int Variations;
            public float Volume;
        }
    }
}
