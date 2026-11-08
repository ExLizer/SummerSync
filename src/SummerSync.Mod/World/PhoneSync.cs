using System;
using System.Collections.Generic;
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
    /// The house's telephone, one for everyone: the host's game decides who calls, the
    /// phone rings in every game, and whoever picks it up first takes the call.
    ///
    /// What is assumed about the game, from the dump of 2026-10-14 and the level file (docs/GAME_NOTES.md, "El
    /// teléfono"): under "YARD/Building/LIVINGROOM/Telephone/Logic", the state machine "Ring" of "PhoneLogic" decides
    /// the calls from flags that the jobs and the story set (a sewage well full, the car repaired, the grandmother...):
    /// it writes the call's "Topic" into "Ring :: Ring" and switches "Ring" on. That machine rings ("phone_ring") a
    /// random number of times, unless the bill is unpaid; picking up the handset ("UseHandle :: Use", "Pick phone")
    /// sets its "Answer", and it goes to "State 2", which sends the topic as an event: the topic's state plays the
    /// caller's voice (sound group "Callers", or "Teimo"), shows the subtitle, and changes the world (the job's flags, the
    /// decider's flag cleared, events to the jobs). Then it hangs up and switches itself off.
    ///
    /// The host's decider is the decider: a client's is switched off for the session. When the host's phone starts
    /// ringing, the topic is said ("phone/ring": number, topic, ringing) and every client's phone rings with it; when it
    /// stops, theirs stop. A game whose player picks up says so ("phone/answer": number, player); the others stop
    /// ringing. If a client answered, the host's own phone takes the same call silently (its voices muted, its
    /// subtitles cleared), so that what the call changes is changed in the host's world, the game's way.
    /// </summary>
    internal sealed class PhoneSync
    {
        private const string LogicPath = "YARD/Building/LIVINGROOM/Telephone/Logic";
        private const string RingKey = "phone/ring";
        private const string AnswerKey = "phone/answer";
        private const double SearchInterval = 2.0;
        private const double SilentAtMost = 60.0;
        private const string HungUpState = "Beep beep";
        private const string ThunderState = "Thunder calls";
        private const string DisableState = "Disable phone";
        private const string DisableEvent = "SSYNC PHONE OFF";

        // The topics the decider writes, in the order they travel (1 and up; 0 is none).
        private static readonly string[] Topics =
        {
            "ORDER", "FUEL", "DRUNK", "SHIT1", "SHIT2", "SHIT3", "SHIT4", "SHIT5", "WOOD1", "REPAIR", "FLEETARISHIT", "FLEETARIRALLY",
            "FERNDALE", "DRUNKANGRY", "MOVING", "MUMMO1", "MUMMO2", "MUMMO3", "SUSKI1", "SUSKI2", "UNCLE", "TOHVAKKA1", "TOHVAKKA2",
            "JOKE1", "JOKE2", "JOKE3",
        };

        private static readonly string[] Voices = { "Callers", "Teimo" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Heard> heard = new List<Heard>();
        private TallyReplication current;
        private bool host;
        private double nextSearch;
        private bool found;
        private bool searchReported;

        private GameObject ringObject;
        private PlayMakerFSM ring;
        private PlayMakerFSM decider;
        private FsmString topic;
        private FsmBool answer;
        private FsmString subtitle;
        private bool deciderOff;
        private bool wasRinging;
        private int serial;
        private int ringTopic;
        private bool answeredHere;
        private bool answeredElsewhere;
        private bool silent;
        private double silentSince;

        // A client: the host's phone rings in its answer to this game's request; rung here once the answer is over, unless
        // the same call was taken already (the answer says so too).
        private bool ringPending;
        private int caughtAnswer = -1;

        public PhoneSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
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

                // Calls are numbered per session: the next host starts again from one.
                found = false;
                heard.Clear();
                serial = 0;
                ringTopic = 0;
                answeredHere = false;
                answeredElsewhere = false;
                ringPending = false;
                caughtAnswer = -1;
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
            if (!found || ring == null || ringObject == null)
            {
                return;
            }

            if (!host && !deciderOff && decider != null && decider.enabled)
            {
                // A client's phone never decides a call: the host's does.
                decider.enabled = false;
                deciderOff = true;
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Values, heard[i].CatchUp, now);
            }

            heard.Clear();
            if (ringPending && !current.CatchingUp)
            {
                RingCaughtUp();
            }

            Watch(now);
        }

        /// <summary>The phone here goes back to how the game had it.</summary>
        public void Release()
        {
            // Unity's null check is also true for a destroyed object.
            if (deciderOff && decider != null)
            {
                decider.enabled = true;
            }

            deciderOff = false;
            Unmute();
        }

        private void Search()
        {
            int slash = LogicPath.IndexOf('/');
            GameObject root = GameObject.Find("/" + LogicPath.Substring(0, slash));
            Transform logic = root == null ? null : root.transform.Find(LogicPath.Substring(slash + 1));
            Transform ringAt = logic == null ? null : logic.Find("Ring");
            Transform deciderAt = logic == null ? null : logic.Find("PhoneLogic");
            ring = ringAt == null ? null : DoorSync.FindMachine(ringAt, "Ring");
            decider = deciderAt == null ? null : DoorSync.FindMachine(deciderAt, "Ring");
            topic = ring == null ? null : ring.FsmVariables.FindFsmString("Topic");
            answer = ring == null ? null : ring.FsmVariables.FindFsmBool("Answer");
            subtitle = FsmVariables.GlobalVariables.FindFsmString("GUIsubtitle");
            if (ring == null || decider == null || topic == null || answer == null || !DoorSync.HasState(ring, "State 2"))
            {
                if (!searchReported)
                {
                    searchReported = true;
                    log.Warning(LogCategory.Fsm, "Phone: '" + LogicPath + "' (state machines 'Ring' of 'Ring' and of 'PhoneLogic') is not as this build expects: each game's phone is its own.");
                }

                ring = null;
                return;
            }

            ringObject = ringAt.gameObject;
            wasRinging = ringObject.activeSelf;
            found = true;
            log.Info(LogCategory.Fsm, "Phone: shared; " + (host ? "this game decides the calls." : "the host decides the calls; this game's phone rings with it."));
            if (host)
            {
                TellRing();
            }
        }

        // The host: its phone as it is now is the session's from the start. A call that was already ringing when this was
        // found would never be told otherwise, and what the session remembers may be a call of an earlier world. If this
        // game's player has the handset, that is told next, by Watch.
        private void TellRing()
        {
            bool ringing = ringObject.activeSelf;
            if (ringing)
            {
                serial++;
                ringTopic = TopicCode(topic.Value);
                answeredHere = false;
                answeredElsewhere = false;
            }

            current.Set(RingKey, new[] { serial, ringing ? ringTopic : 0, ringing ? 1 : 0 });
            log.Info(LogCategory.Fsm, ringing ? "Phone: rings here already (" + topic.Value + "); told as it is now, for whoever joins." : "Phone: does not ring here; told as it is now, for whoever joins.");
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key == RingKey || key == AnswerKey)
            {
                heard.Add(new Heard { Key = key, Values = values, CatchUp = current.CatchingUp });
            }
        }

        private void Hear(string key, int[] values, bool catchUp, double now)
        {
            if (key == RingKey && !host && values.Length >= 3 && catchUp)
            {
                CatchUpRing(values[0], values[1], values[2] != 0);
            }
            else if (key == RingKey && !host && values.Length >= 3)
            {
                int said = values[0];
                if (values[2] != 0 && said != serial)
                {
                    // The host's phone rings: this one too, with the same call.
                    serial = said;
                    ringTopic = values[1];
                    answeredHere = false;
                    answeredElsewhere = false;
                    ringPending = false;
                    Ring("; this one too.");
                }
                else if (values[2] == 0 && said == serial && !answeredHere)
                {
                    StopRinging("the host's phone stopped ringing");
                }
            }
            else if (key == AnswerKey && !host && values.Length >= 2 && catchUp)
            {
                // The last call someone took, whoever it was (a player who had this number before too): not to ring here.
                caughtAnswer = values[0];
            }
            else if (key == AnswerKey && values.Length >= 2)
            {
                int player = values[1] - 1;
                if (values[0] != serial || player == manager.Session.LocalPlayerId || answeredHere || answeredElsewhere)
                {
                    return;
                }

                answeredElsewhere = true;
                if (host && ringObject.activeSelf && !answer.Value && ring.ActiveStateName == ThunderState)
                {
                    // In a thunderstorm a call is the lightning's: whoever picks up may die. It changes nothing in
                    // the world, and the host's player did not pick up.
                    StopRinging("player " + player + " took a call in a thunderstorm");
                }
                else if (host && ringObject.activeSelf && !answer.Value)
                {
                    // A client took the call: the host's phone takes it too, unheard, for what it changes here.
                    silent = true;
                    silentSince = now;
                    for (int i = 0; i < Voices.Length; i++)
                    {
                        MasterAudio.MuteGroup(Voices[i]);
                    }

                    answer.Value = true;
                    log.Info(LogCategory.Fsm, "Phone: player " + player + " took the call (" + (TopicName(ringTopic) ?? "?") + "); this phone takes it too, silently, for the world.");
                }
                else
                {
                    StopRinging("player " + player + " took the call");
                }
            }
        }

        // A client: the host's phone in its answer to this game's request. Whether it rings is how it is now; it rings here
        // once the whole answer is in, unless that call was taken already. One ringing here of this game's own stops: the
        // host's game decides the calls.
        private void CatchUpRing(int said, int code, bool ringing)
        {
            // The call this game knows already (heard as it rang, or in an earlier answer) stays as it is here.
            if (said != serial)
            {
                serial = said;
                ringTopic = code;
                answeredHere = false;
                answeredElsewhere = false;
                ringPending = ringing;
            }

            if (!ringing)
            {
                ringPending = false;
                StopRinging("the host's phone does not ring (a catch-up on joining)");
            }
        }

        private void RingCaughtUp()
        {
            ringPending = false;
            if (caughtAnswer == serial)
            {
                answeredElsewhere = true;
                log.Info(LogCategory.Fsm, "Phone: the host's call (" + (TopicName(ringTopic) ?? "?") + ") was taken already; it does not ring here (a catch-up on joining).");
                return;
            }

            Ring(", as it does now; this one too (a catch-up on joining).");
        }

        private void Ring(string why)
        {
            string name = TopicName(ringTopic);
            if (name != null && !ringObject.activeSelf)
            {
                topic.Value = name;
                answer.Value = false;
                ringObject.SetActive(true);
                wasRinging = true;
                log.Info(LogCategory.Fsm, "Phone: the host's phone rings (" + name + ")" + why);
            }
        }

        private void Watch(double now)
        {
            bool ringing = ringObject.activeSelf;
            if (host && ringing && !wasRinging)
            {
                serial++;
                ringTopic = TopicCode(topic.Value);
                answeredHere = false;
                answeredElsewhere = false;
                current.Set(RingKey, new[] { serial, ringTopic, 1 });
                log.Info(LogCategory.Fsm, "Phone: rings here (" + topic.Value + "); told.");
            }
            else if (!host && ringing && !wasRinging)
            {
                // A call this game made by itself (the bed makes some when the day changes): the host's decides.
                StopRinging("a call of this game's own (" + topic.Value + "); the host's game decides the calls");
                ringing = ringObject.activeSelf;
            }
            else if (!ringing && wasRinging)
            {
                if (host)
                {
                    current.Set(RingKey, new[] { serial, ringTopic, 0 });
                }

                if (silent)
                {
                    Unmute();
                    log.Info(LogCategory.Fsm, "Phone: the silent call is over.");
                }
            }

            wasRinging = ringing;
            if (ringing && answer.Value && !answeredHere && !answeredElsewhere && !silent)
            {
                answeredHere = true;
                current.Set(AnswerKey, new[] { serial, manager.Session.LocalPlayerId + 1 });
                log.Info(LogCategory.Fsm, "Phone: picked up here; told.");
            }

            if (silent)
            {
                // Whatever the silent call says is not shown here.
                if (subtitle != null && !string.IsNullOrEmpty(subtitle.Value))
                {
                    subtitle.Value = string.Empty;
                }

                // The caller has hung up and the line beeps until someone puts the handset down: nobody holds it here.
                if (ring.ActiveStateName == HungUpState || now - silentSince > SilentAtMost)
                {
                    HangUp();
                }
            }
        }

        // Someone else has the call, or the host's phone stopped: this one stops ringing, as the game's own does when
        // nobody picks up ("Disable phone" also clears the thunderstorm's call, then switches the phone off).
        private void StopRinging(string why)
        {
            if (ringObject.activeSelf && !answer.Value)
            {
                if (DoorSync.HasState(ring, DisableState))
                {
                    if (!DoorSync.HasGlobalTransition(ring, DisableEvent))
                    {
                        ring.AddGlobalTransition(DisableEvent, DisableState);
                    }

                    ring.SendEvent(DisableEvent);
                }

                if (ringObject.activeSelf)
                {
                    ringObject.SetActive(false);
                }

                wasRinging = false;
                log.Info(LogCategory.Fsm, "Phone: stops ringing here: " + why + ".");
            }
        }

        // As putting the handset down does ("UseHandle :: Use", "Close phone"): the call over, the voices stopped.
        private void HangUp()
        {
            answer.Value = false;
            for (int i = 0; i < Voices.Length; i++)
            {
                MasterAudio.StopAllOfSound(Voices[i]);
            }

            if (subtitle != null)
            {
                subtitle.Value = string.Empty;
            }

            ringObject.SetActive(false);
            Unmute();
            log.Info(LogCategory.Fsm, "Phone: the silent call is over; hung up.");
        }

        private void Unmute()
        {
            if (!silent)
            {
                return;
            }

            silent = false;
            for (int i = 0; i < Voices.Length; i++)
            {
                MasterAudio.UnmuteGroup(Voices[i]);
            }
        }

        private static int TopicCode(string name)
        {
            int index = name == null ? -1 : Array.IndexOf(Topics, name);
            return index + 1;
        }

        private static string TopicName(int code)
        {
            return code >= 1 && code <= Topics.Length ? Topics[code - 1] : null;
        }

        private struct Heard
        {
            public string Key;
            public int[] Values;

            /// <summary>Part of the host's answer to this game's request: how things are, not what just happened.</summary>
            public bool CatchUp;
        }
    }
}
