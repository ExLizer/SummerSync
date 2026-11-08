using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The police of the host's game: whether there is a checkpoint today and where is
    /// what the host's game drew. What the police do with a player (stop them, test them, fine them, chase them) is done
    /// by that player's own game, with its own cops at the same place; a fine is paid out of the money everyone shares,
    /// and being wanted is each player's own.
    ///
    /// What is assumed about the game, from the dump of 2026-10-14 (docs/GAME_NOTES.md, "La policía"): the state machine
    /// "TRAFFIC/Police :: Police" draws each day, by day (in the week one in ten, at the weekend one in two), whether there
    /// is a checkpoint and at which of three places ("Airport", "Skihill", "Fields"; "RatsiaCurrent" is then
    /// "Checkpoints/Ratsia1Airport", "Ratsia2Skihill" or "Ratsia3Fields"); "Parenting" moves the cops and their two cars
    /// there, "Cop1" switches "TRAFFIC/Police/Checkpoints" on, and at night, once the player is near home, "State 3"
    /// switches it off.
    ///
    /// The host says ("police/checkpoint": place, on) what its game has. A client's machine is paused for the session and
    /// taken, the game's way, through the same states: to the place's state (which moves the cops there and switches the
    /// checkpoint on) or to "State 3" (which switches it off).
    /// </summary>
    internal sealed class PoliceSync
    {
        private const string PolicePath = "TRAFFIC/Police";
        private const string Key = "police/checkpoint";
        private const string DriveEvent = "SSYNC POLICE";
        private const string OffState = "State 3";
        private const double SearchInterval = 2.0;

        // The three places, by their state and by the name of the object "RatsiaCurrent" points to, 1 up.
        private static readonly string[] Places = { "Airport", "Skihill", "Fields" };
        private static readonly string[] PlaceObjects = { "Ratsia1Airport", "Ratsia2Skihill", "Ratsia3Fields" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<int[]> heard = new List<int[]>();
        private TallyReplication current;
        private bool host;
        private bool found;
        private bool searchReported;
        private double nextSearch;
        private PlayMakerFSM police;
        private FsmGameObject place;
        private GameObject checkpoints;
        private PausedMachine paused;
        private int toldPlace = -1;
        private int toldOn = -1;
        private int shownPlace = -1;
        private int shownOn = -1;

        public PoliceSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's root object,
        /// or null while there is no game world.
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

                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }

                heard.Clear();
                found = false;
                toldPlace = -1;
                toldOn = -1;
                shownPlace = -1;
                shownOn = -1;
                nextSearch = now;
            }

            if (current == null)
            {
                return;
            }

            if (!found && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            // Unity's null check is also true for a destroyed object.
            if (!found || police == null || checkpoints == null)
            {
                return;
            }

            if (host)
            {
                Tell();
                return;
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Show(heard[i][0], heard[i][1] != 0);
            }

            heard.Clear();
        }

        /// <summary>A client's machine goes on as its own, from where it is.</summary>
        public void Release()
        {
            if (paused != null)
            {
                paused.Resume();
                paused = null;
            }

            found = false;
        }

        private void Search()
        {
            int slash = PolicePath.IndexOf('/');
            GameObject root = GameObject.Find("/" + PolicePath.Substring(0, slash));
            Transform at = root == null ? null : root.transform.Find(PolicePath.Substring(slash + 1));
            police = at == null ? null : DoorSync.FindMachine(at, "Police");
            place = police == null ? null : police.FsmVariables.FindFsmGameObject("RatsiaCurrent");
            Transform checkpointsAt = at == null ? null : at.Find("Checkpoints");
            checkpoints = checkpointsAt == null ? null : checkpointsAt.gameObject;
            bool states = police != null && DoorSync.HasState(police, OffState);
            for (int i = 0; states && i < Places.Length; i++)
            {
                states = DoorSync.HasState(police, Places[i]);
            }

            if (police == null || place == null || checkpoints == null || !states)
            {
                if (!searchReported)
                {
                    searchReported = true;
                    log.Warning(LogCategory.Fsm, "Police: '" + PolicePath + " :: Police' (its places, 'RatsiaCurrent', 'Checkpoints') is not as this build expects: each game draws its own checkpoints.");
                }

                police = null;
                return;
            }

            found = true;
            if (!host)
            {
                paused = new PausedMachine(police);
            }

            log.Info(LogCategory.Fsm, "Police: " + (host ? "this game draws the checkpoints for everyone." : "the host's game draws the checkpoints; this game's draw is off for the session."));
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key == Key && values.Length >= 2)
            {
                heard.Add(values);
            }
        }

        // The host: where its checkpoint is, and whether it is on.
        private void Tell()
        {
            int at = 0;
            GameObject placeObject = place.Value;

            // Unity's null check is also true for a destroyed object.
            if (placeObject != null)
            {
                at = Array.IndexOf(PlaceObjects, placeObject.name) + 1;
            }

            int on = checkpoints.activeSelf ? 1 : 0;
            if ((at != toldPlace || on != toldOn) && manager.Session.State == SessionState.Connected)
            {
                toldPlace = at;
                toldOn = on;
                current.Set(Key, new[] { at, on });
                log.Info(LogCategory.Fsm, "Police: " + (on == 1 && at > 0 ? "a checkpoint at " + Places[at - 1] : "no checkpoint") + " in this game; told.");
            }
        }

        // A client: its machine through the host's states, then paused again where they leave it.
        private void Show(int at, bool on)
        {
            if (on && at >= 1 && at <= Places.Length)
            {
                if (at != shownPlace || shownOn != 1 || !checkpoints.activeSelf)
                {
                    Drive(Places[at - 1]);
                    log.Info(LogCategory.Fsm, "Police: the host's checkpoint is at " + Places[at - 1] + "; here too.");
                }
            }
            else if (shownOn != 0 || checkpoints.activeSelf)
            {
                Drive(OffState);
                log.Info(LogCategory.Fsm, "Police: no checkpoint in the host's game; none here.");
            }

            shownPlace = at;
            shownOn = on ? 1 : 0;
        }

        private void Drive(string state)
        {
            if (paused != null)
            {
                paused.Resume();
            }

            if (!DoorSync.HasGlobalTransition(police, DriveEvent + " " + state))
            {
                police.AddGlobalTransition(DriveEvent + " " + state, state);
            }

            police.SendEvent(DriveEvent + " " + state);
            paused = new PausedMachine(police);
        }
    }
}
