using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using SummerSync.Unity.Objects;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The rockets of the fireworks bag, lit in every game. A rocket taken out of the bag is made in every game with its
    /// machines on (<see cref="Players.ThrowSync"/>), and moved like any loose thing by the game that simulates it; lit in
    /// one game, it flew and blew up only there.
    ///
    /// What is assumed about the game (dump of 2026-10-15, the prefab's machines; the actions read in the game with the
    /// bridge, docs/GAME_NOTES.md): a rocket ("rocket(Clone)") has a child "Trigger" with the machine "Use", which waits in
    /// "State 4", "Wait player" and "Wait button" until it is lit, by a click ("USE") or by the global event
    /// "EXPLOSION" that its machine "Explosion" sends when it lies in a fire; from "ON 3" on, it burns, flies and blows up
    /// by itself.
    ///
    /// The game where a rocket is lit says so ("rocket/&lt;its shared number&gt;", with the player), once the rocket has a
    /// shared number; the others send their copy "EXPLOSION", the game's own way of lighting it, if it is not lit there.
    /// Only as news: a word heard while catching up is of a rocket that has flown already.
    /// </summary>
    internal sealed class FireworksSync
    {
        private const string RocketName = "rocket(Clone)";
        private const string TriggerChild = "Trigger";
        private const string TriggerMachine = "Use";
        private const string LightEvent = "EXPLOSION";
        private const string KeyPrefix = "rocket/";
        private const double LookSeconds = 0.25;

        // A word about a rocket not here (yet) is kept this long.
        private const double WaitSeconds = 10.0;
        private const LogCategory Category = LogCategory.Fsm;

        // Before its machine's first frame its state has no name: not lit either.
        private static readonly string[] Unlit = { string.Empty, "State 4", "Wait player", "Wait button" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly ObjectPresenter objects;

        // By the game object's instance number: a rocket is followed from before it has a shared number.
        private readonly Dictionary<int, Rocket> rockets = new Dictionary<int, Rocket>();
        private readonly List<GameObject> found = new List<GameObject>();
        private readonly List<ushort> foundIds = new List<ushort>();
        private readonly List<int> gone = new List<int>();
        private readonly List<Word> words = new List<Word>();
        private TallyReplication current;
        private double nextLook;

        public FireworksSync(Logger log, NetworkManager manager, ObjectPresenter objects)
        {
            this.log = log;
            this.manager = manager;
            this.objects = objects;
        }

        /// <summary>
        /// Once per frame, after the loose objects. <paramref name="localPlayer"/> is the player's root object, or null while
        /// there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                current = tallies;
                rockets.Clear();
                words.Clear();
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }
            }

            if (current == null || now < nextLook)
            {
                return;
            }

            nextLook = now + LookSeconds;
            Look();
            Hear(now);
        }

        // Heard while the session reads its messages: done in this class's turn in the frame.
        private void OnStateWanted(string key, int[] values)
        {
            if (values.Length < 2 || values[0] == 0 || !key.StartsWith(KeyPrefix, StringComparison.Ordinal) || current.CatchingUp)
            {
                return;
            }

            // This game's own word, coming back.
            if (values[1] == manager.Session.LocalPlayerId)
            {
                return;
            }

            ushort id;
            if (!ushort.TryParse(key.Substring(KeyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0)
            {
                return;
            }

            words.Add(new Word { Id = id, Player = (byte)Mathf.Clamp(values[1], 0, byte.MaxValue), Since = double.NaN });
        }

        // The rockets here, and which of them this game has just lit.
        private void Look()
        {
            objects.SharedNamed(RocketName, found, foundIds);
            for (int i = 0; i < found.Count; i++)
            {
                int key = found[i].GetInstanceID();
                Rocket rocket;
                if (!rockets.TryGetValue(key, out rocket))
                {
                    rocket = new Rocket { Thing = found[i], Machine = MachineOf(found[i]) };

                    // Lit before this class saw it: by a fire, or in a frame before the loose objects found it. Not told.
                    rocket.Lit = rocket.Machine != null && !IsUnlit(rocket.Machine);
                    rocket.Told = rocket.Lit;
                    rockets[key] = rocket;
                }

                rocket.Id = foundIds[i];

                // Unity's null check is also true for a destroyed component.
                if (!rocket.Lit && rocket.Machine != null && !IsUnlit(rocket.Machine))
                {
                    rocket.Lit = true;
                }

                if (rocket.Lit && !rocket.Told && rocket.Id != 0)
                {
                    rocket.Told = true;
                    current.Set(KeyPrefix + rocket.Id.ToString(CultureInfo.InvariantCulture), new[] { 1, (int)manager.Session.LocalPlayerId });
                    log.Info(Category, "Fireworks: rocket #" + rocket.Id + " was lit in this game (now '" + rocket.Machine.ActiveStateName + "'): the others are told.");
                }
            }

            // Blown up and gone: forgotten.
            gone.Clear();
            foreach (KeyValuePair<int, Rocket> entry in rockets)
            {
                if (entry.Value.Thing == null)
                {
                    gone.Add(entry.Key);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                rockets.Remove(gone[i]);
            }
        }

        // What the others lit, lit here the game's way.
        private void Hear(double now)
        {
            for (int i = words.Count - 1; i >= 0; i--)
            {
                Word word = words[i];
                if (double.IsNaN(word.Since))
                {
                    word.Since = now;
                }

                Rocket rocket = Find(word.Id);
                if (rocket == null)
                {
                    if (now - word.Since > WaitSeconds)
                    {
                        words.RemoveAt(i);
                        log.Info(Category, "Fireworks: player " + word.Player + " lit rocket #" + word.Id + ", which is not in this world.");
                    }

                    continue;
                }

                words.RemoveAt(i);
                if (rocket.Lit || !IsUnlit(rocket.Machine))
                {
                    rocket.Lit = true;
                    rocket.Told = true;
                    continue;
                }

                if (!rocket.Machine.enabled || !rocket.Machine.gameObject.activeInHierarchy)
                {
                    log.Info(Category, "Fireworks: player " + word.Player + " lit rocket #" + word.Id + "; its machine is switched off here, so it is not.");
                    continue;
                }

                // Lit by the other's word: not this game's news.
                rocket.Lit = true;
                rocket.Told = true;
                rocket.Machine.SendEvent(LightEvent);
                log.Info(Category, "Fireworks: rocket #" + word.Id + " lit here, as player " + word.Player + "'s game did (now '" + rocket.Machine.ActiveStateName + "').");
            }
        }

        // The rocket here with that shared number, with its machine; null if there is none.
        private Rocket Find(ushort id)
        {
            foreach (KeyValuePair<int, Rocket> entry in rockets)
            {
                Rocket rocket = entry.Value;

                // Unity's null check is also true for a destroyed object.
                if (rocket.Id == id && rocket.Thing != null && rocket.Machine != null)
                {
                    return rocket;
                }
            }

            return null;
        }

        private static PlayMakerFSM MachineOf(GameObject rocket)
        {
            Transform trigger = rocket.transform.Find(TriggerChild);
            return trigger == null ? null : DoorSync.FindMachine(trigger, TriggerMachine);
        }

        private static bool IsUnlit(PlayMakerFSM machine)
        {
            return Array.IndexOf(Unlit, machine.ActiveStateName ?? string.Empty) >= 0;
        }

        private sealed class Rocket
        {
            public GameObject Thing;
            public PlayMakerFSM Machine;
            public ushort Id;
            public bool Lit;

            // Said to the others, or lit by their word: not to be said (again).
            public bool Told;
        }

        private sealed class Word
        {
            public ushort Id;
            public byte Player;
            public double Since;
        }
    }
}
