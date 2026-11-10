using System;
using System.Globalization;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The house's television, the same in every game: which film it shows, whether its teletext is on, and the page.
    ///
    /// What is assumed about the game (decoded level of 2026-10-18; docs/GAME_NOTES.md, "El teletexto"):
    /// - "YARD/Building/LIVINGROOM/TV/Switch :: Use" names the remote control in its variable "Remote", and switches the
    ///   remote's "Use" on only while the television is on. Switched on again, that machine starts over in "Close".
    /// - The remote's "Use" turns the teletext on in "Open" and off in "Close": the object "Systems/Teletext" (its variable
    ///   "Teletext") and the screen's material. Turning the television off switches the object off as well.
    /// - "Systems/Teletext :: Pages" starts over each time the object is switched on, at page 100, and then goes to
    ///   whatever page its integer "PageNumber" is changed to. The remote's "Input" writes the keys typed into it.
    ///
    /// - By day the television shows "…/ElectricAppliances/TV_Programs/VideoProgram", whose machine "Program" plays one
    ///   film in "Topless" for 122 seconds and the other in "Marjatta" for 175, in turns, from the moment the object was
    ///   switched on. A game that joins with the television on starts at the first film while the others are further on.
    ///
    /// The tally "tv/teletext" is on or off, the page, and who said so. A game whose player turned the teletext or typed a
    /// page says it; the others send their remote to the same state and write the page. The tally "tv/film" is the film
    /// the host's game shows: the others go to the same one when theirs is another.
    /// </summary>
    internal sealed class TelevisionSync
    {
        private const string HouseRoot = "/YARD";
        private const string TvPath = "Building/LIVINGROOM/TV/Switch";
        private const string MachineName = "Use";
        private const string RemoteVariable = "Remote";
        private const string TeletextVariable = "Teletext";
        private const string PagesMachine = "Pages";
        private const string PageVariable = "PageNumber";
        private const string OpenState = "Open";
        private const string CloseState = "Close";
        private const string Key = "tv/teletext";
        private const string VideoPath = "Building/Dynamics/HouseElectricity/ElectricAppliances/TV_Programs/VideoProgram";
        private const string VideoMachine = "Program";
        private const string FilmKey = "tv/film";

        // The states of the two films, and the events of the mod's that lead to them.
        private static readonly string[] Films = { "Topless", "Marjatta" };
        private static readonly string[] FilmEvents = { "SUMMERSYNC TV FILM 1", "SUMMERSYNC TV FILM 2" };
        private const string OnEvent = "SUMMERSYNC TELETEXT ON";
        private const string OffEvent = "SUMMERSYNC TELETEXT OFF";
        private const double FindSeconds = 3.0;
        private const double RetrySeconds = 0.5;

        // The pages' machine starts over at page 100 a moment after the teletext goes on: a page is written after that.
        private const double SettleSeconds = 0.5;
        private const int MostTries = 6;
        private const LogCategory Category = LogCategory.Fsm;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private TallyReplication current;
        private Transform player;
        private GameObject remote;
        private PlayMakerFSM remoteUse;
        private GameObject teletext;
        private PlayMakerFSM pages;
        private PlayMakerFSM video;
        private bool filmHooked;

        // The film this game told last (the host) and the one the host said that is not shown here yet; -1 for none.
        private int toldFilm = -1;
        private int heardFilm = -1;
        private bool found;
        private bool missingReported;
        private bool hooked;
        private double nextFind;
        private double nextTry;
        private double settleUntil;
        private int tries;

        // What this game had at the last look.
        private bool lastOn;
        private int lastPage;

        // What another game said and this one does not show yet.
        private bool pending;
        private bool heardOn;
        private int heardPage;

        public TelevisionSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null without a world.</summary>
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
                }
            }

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

            // Unity's null check is also true for a destroyed object.
            if (remote == null || remoteUse == null || teletext == null || pages == null)
            {
                found = false;
                return;
            }

            WatchFilm(session);
            bool on = teletext.activeSelf;
            FsmInt number = pages.FsmVariables.FindFsmInt(PageVariable);
            int page = on && number != null ? number.Value : lastPage;
            if (pending)
            {
                Follow(now, on, number);
                return;
            }

            if (now < settleUntil || (on == lastOn && page == lastPage))
            {
                return;
            }

            // Turning it on and typing a page are the player's at the home remote; the television going off turns it off
            // in every game by itself, and that is said too so that whoever joins later hears it is off.
            if (on && !RemoteWorks())
            {
                return;
            }

            lastOn = on;
            lastPage = page;
            current.Set(Key, new[] { on ? 1 : 0, page, session.LocalPlayerId });
            log.Info(Category, "Teletext: " + (on ? "on at page " + page.ToString(CultureInfo.InvariantCulture) : "off") + " in this game; the others are told.");
        }

        /// <summary>Lets go of the session; the television stays as it is.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            current = null;
            found = false;
            hooked = false;
            filmHooked = false;
            pending = false;
            toldFilm = -1;
            heardFilm = -1;
            nextFind = 0.0;
            settleUntil = 0.0;
        }

        private bool RemoteWorks()
        {
            return remoteUse.enabled && remote.activeInHierarchy;
        }

        private bool Find()
        {
            GameObject house = GameObject.Find(HouseRoot);
            Transform tv = house == null ? null : house.transform.Find(TvPath);
            PlayMakerFSM tvUse = tv == null ? null : DoorSync.FindMachine(tv, MachineName);
            FsmGameObject remoteVariable = tvUse == null ? null : tvUse.FsmVariables.FindFsmGameObject(RemoteVariable);
            remote = remoteVariable == null ? null : remoteVariable.Value;
            remoteUse = remote == null ? null : DoorSync.FindMachine(remote.transform, MachineName);
            FsmGameObject teletextVariable = remoteUse == null ? null : remoteUse.FsmVariables.FindFsmGameObject(TeletextVariable);
            teletext = teletextVariable == null ? null : teletextVariable.Value;
            pages = teletext == null ? null : DoorSync.FindMachine(teletext.transform, PagesMachine);
            if (pages == null || pages.FsmVariables.FindFsmInt(PageVariable) == null || !DoorSync.HasState(remoteUse, OpenState) || !DoorSync.HasState(remoteUse, CloseState))
            {
                if (!missingReported && house != null)
                {
                    missingReported = true;
                    log.Warning(Category, "Teletext: the television, its remote or the teletext is not made as this build expects; the teletext is each game's own.");
                }

                return false;
            }

            Transform videoObject = house.transform.Find(VideoPath);
            video = videoObject == null ? null : DoorSync.FindMachine(videoObject, VideoMachine);
            if (video == null || !DoorSync.HasState(video, Films[0]) || !DoorSync.HasState(video, Films[1]))
            {
                video = null;
                log.Warning(Category, "Television: its films are not made as this build expects; which one it shows is each game's own.");
            }

            found = true;
            hooked = false;
            filmHooked = false;
            lastOn = teletext.activeSelf;
            lastPage = pages.FsmVariables.FindFsmInt(PageVariable).Value;
            log.Info(Category, "Teletext: found, " + (lastOn ? "on at page " + lastPage.ToString(CultureInfo.InvariantCulture) : "off") + " here; shared from now on.");
            return true;
        }

        // Another game's teletext, shown here the game's own way: the remote's machine sent to the state that turns it, and
        // the page written for the pages' machine to go to.
        private void Follow(double now, bool on, FsmInt number)
        {
            if (on == heardOn && now >= settleUntil && (!on || number == null || number.Value == heardPage))
            {
                // As this game has it already: the host's answer to whoever asked says it again.
                pending = false;
                lastOn = on;
                lastPage = on ? heardPage : lastPage;
                return;
            }

            if (on != heardOn)
            {
                if (now < nextTry)
                {
                    return;
                }

                nextTry = now + RetrySeconds;
                if (!RemoteWorks())
                {
                    // The television is off here: its remote does nothing until it is on, and starts with the teletext off.
                    return;
                }

                try
                {
                    if (!hooked)
                    {
                        hooked = true;
                        if (!DoorSync.HasGlobalTransition(remoteUse, OnEvent))
                        {
                            remoteUse.AddGlobalTransition(OnEvent, OpenState);
                        }

                        if (!DoorSync.HasGlobalTransition(remoteUse, OffEvent))
                        {
                            remoteUse.AddGlobalTransition(OffEvent, CloseState);
                        }
                    }

                    remoteUse.SendEvent(heardOn ? OnEvent : OffEvent);
                }
                catch (Exception exception)
                {
                    pending = false;
                    log.Warning(Category, "Teletext: could not turn it " + (heardOn ? "on" : "off") + " as another game has it: " + exception.Message);
                    return;
                }

                on = teletext.activeSelf;
                if (on != heardOn)
                {
                    tries++;
                    if (tries >= MostTries)
                    {
                        pending = false;
                        lastOn = on;
                        log.Warning(Category, "Teletext: it does not go " + (heardOn ? "on" : "off") + " when told to (the remote's state '" + remoteUse.ActiveStateName + "'); it stays as it is here.");
                    }

                    return;
                }

                lastOn = on;
                if (on)
                {
                    settleUntil = now + SettleSeconds;
                }
            }

            if (!on)
            {
                pending = false;
                lastOn = false;
                log.Info(Category, "Teletext: off, as another game has it.");
                return;
            }

            if (now < settleUntil)
            {
                return;
            }

            if (number != null && number.Value != heardPage)
            {
                number.Value = heardPage;
            }

            pending = false;
            lastOn = true;
            lastPage = heardPage;
            log.Info(Category, "Teletext: on at page " + heardPage.ToString(CultureInfo.InvariantCulture) + ", as another game has it.");
        }

        // The host says which film its television shows each time one starts; another game showing the other goes to it.
        private void WatchFilm(NetSession session)
        {
            // Unity's null check is also true for a destroyed component.
            if (video == null)
            {
                return;
            }

            bool showing = video.enabled && video.gameObject.activeInHierarchy;
            int film = showing ? Array.IndexOf(Films, video.ActiveStateName) : -1;
            if (session.Role == SessionRole.Host)
            {
                if (!showing)
                {
                    toldFilm = -1;
                }
                else if (film >= 0 && film != toldFilm)
                {
                    toldFilm = film;
                    current.Set(FilmKey, new[] { film });
                    log.Info(Category, "Television: the film '" + Films[film] + "' starts in this game; the others are told.");
                }

                return;
            }

            if (heardFilm < 0 || film < 0)
            {
                return;
            }

            int wanted = heardFilm;
            heardFilm = -1;
            if (film == wanted)
            {
                return;
            }

            try
            {
                if (!filmHooked)
                {
                    filmHooked = true;
                    for (int i = 0; i < Films.Length; i++)
                    {
                        if (!DoorSync.HasGlobalTransition(video, FilmEvents[i]))
                        {
                            video.AddGlobalTransition(FilmEvents[i], Films[i]);
                        }
                    }
                }

                video.SendEvent(FilmEvents[wanted]);
                log.Info(Category, "Television: the film '" + Films[wanted] + "' started, as the host's game shows it (this game was at '" + Films[film] + "').");
            }
            catch (Exception exception)
            {
                log.Warning(Category, "Television: could not start the film the host's game shows: " + exception.Message);
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key == FilmKey && values.Length >= 1 && values[0] >= 0 && values[0] < Films.Length)
            {
                heardFilm = values[0];
                return;
            }

            if (key != Key || values.Length < 3)
            {
                return;
            }

            // What this game said itself comes back from the host; a catch-up is how the house is, whoever said it.
            NetSession session = manager.Session;
            if (current != null && !current.CatchingUp && session != null && values[2] == session.LocalPlayerId)
            {
                return;
            }

            pending = true;
            heardOn = values[0] != 0;
            heardPage = values[1];
            tries = 0;
            nextTry = 0.0;
        }
    }
}
