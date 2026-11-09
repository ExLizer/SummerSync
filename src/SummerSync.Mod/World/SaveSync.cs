using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>The toilets' "SAVE AND QUIT TO MENU" buttons (docs/GAME_NOTES.md, "Guardar sin salir al menú").</summary>
    internal static class SaveButtons
    {
        public const string Machine = "Button";
        public const string WaitState = "Wait";
        public const string ClickState = "Wait for click";
        public const string MuteState = "Mute audio";
        public const string SaveAllState = "Save all";

        /// <summary>Whether a machine is one of those buttons.</summary>
        public static bool IsButton(PlayMakerFSM machine)
        {
            return machine.FsmName == Machine && DoorSync.HasState(machine, WaitState) && DoorSync.HasState(machine, ClickState)
                && DoorSync.HasState(machine, MuteState) && DoorSync.HasState(machine, SaveAllState);
        }
    }

    /// <summary>
    /// Saving in a session without anyone leaving it (the game saves and loads the menu, and that ended the
    /// session for everyone).
    ///
    /// In a session the toilets' button says "SAVE GAME". Pressed on the host, the host's world is saved where it is
    /// (<see cref="WorldSaver"/>) and the button waits again; pressed on a guest, the guest asks the host (the tally
    /// "save/request/(player)", a number that goes up), and the host saves the same way: the world is the host's, and a guest's
    /// copy is never saved. When it is written the host says so (the tally "save/done") and everyone sees it. Without a session
    /// the button is the game's: save and quit to the menu.
    /// </summary>
    internal sealed class SaveSync
    {
        private const string RequestPrefix = "save/request/";
        private const string DoneTally = "save/done";
        private const string BackEvent = "SUMMERSYNC SAVE BACK";
        private const string AfterClickState = "SummerSync after click";
        private const string AfterClickEvent = "SUMMERSYNC SAVE CLICKED";
        private const string QuitText = "SAVE AND QUIT TO MENU";
        private const string SessionText = "SAVE GAME";
        private const double FindEvery = 5.0;
        private const double AnswerSeconds = 20.0;
        private const double NoticeSeconds = 6.0;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly WorldSaver saver;
        private readonly Dictionary<int, Fsm> hooked = new Dictionary<int, Fsm>();
        private readonly List<SetStringValue> labels = new List<SetStringValue>();
        private readonly Dictionary<byte, int> requests = new Dictionary<byte, int>();
        private TallyReplication current;
        private double clock;
        private double nextFind;
        private bool inSession;
        private bool labelled;
        private int asked;
        private double askedAt = double.NaN;
        private int saves;

        public SaveSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
            saver = new WorldSaver(log);
        }

        /// <summary>Once per frame, in the world (<paramref name="inWorld"/>) or not.</summary>
        public void LateUpdate(double now, bool inWorld)
        {
            clock = now;
            saver.Update();
            TallyReplication tallies = inWorld ? manager.Tallies : null;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                current = tallies;
                requests.Clear();
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }
            }

            if (!inWorld)
            {
                return;
            }

            if (now >= nextFind)
            {
                nextFind = now + FindEvery;
                Find();
            }

            NetSession session = manager.Session;
            bool connected = current != null && session != null && session.State == SessionState.Connected;
            if (connected != inSession || !labelled)
            {
                inSession = connected;
                Label();
            }

            if (!double.IsNaN(askedAt) && now - askedAt > AnswerSeconds)
            {
                askedAt = double.NaN;
                Notify("The host did not save the world.", "El host no guardó la partida.");
                log.Info(LogCategory.Fsm, "Save: the host did not answer this player's request to save in " + AnswerSeconds.ToString("0", CultureInfo.InvariantCulture) + " s.");
            }
        }

        /// <summary>Lets go of the session and the world; the buttons say what the game says again.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            current = null;
            saver.Release();
            inSession = false;
            Label();
            labels.Clear();
            hooked.Clear();
            requests.Clear();
            askedAt = double.NaN;
        }

        /// <summary>
        /// The "ss save" test aid: saves the world where it is now, on the host or in a game alone; a guest asks the host.
        /// </summary>
        public string SaveFromConsole()
        {
            NetSession session = manager.Session;
            if (inSession && session != null && session.Role != SessionRole.Host)
            {
                Ask();
                return "The host was asked to save its world.";
            }

            return Save(null, "the console") ? "Saving the world where it is." : "A save is already on its way, or this game cannot save without leaving.";
        }

        // The buttons of the scene, the ones switched off too: each gets the mod's action first in "Mute audio", and a way back
        // to waiting.
        private void Find()
        {
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM));
            int found = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var machine = all[i] as PlayMakerFSM;
                if (machine == null || machine.gameObject.hideFlags != HideFlags.None || !SaveButtons.IsButton(machine))
                {
                    continue;
                }

                int id = machine.GetInstanceID();
                Fsm was;
                if (hooked.TryGetValue(id, out was) && was == machine.Fsm)
                {
                    continue;
                }

                try
                {
                    if (machine.GetGlobalTransition(BackEvent) == null)
                    {
                        machine.AddGlobalTransition(BackEvent, SaveButtons.WaitState);
                    }

                    if (!DoorSync.HasState(machine, AfterClickState))
                    {
                        machine.AddState(AfterClickState);
                        if (!machine.FsmInject(AfterClickState, new AfterClick(machine), -1, false))
                        {
                            throw new InvalidOperationException("the mod's state '" + AfterClickState + "' could not take its action");
                        }
                    }

                    if (machine.GetGlobalTransition(AfterClickEvent) == null)
                    {
                        machine.AddGlobalTransition(AfterClickEvent, AfterClickState);
                    }

                    PlayMakerFSM pressed = machine;
                    if (!machine.FsmInject(SaveButtons.MuteState, () => Pressed(pressed), false, 0, false))
                    {
                        throw new InvalidOperationException("its '" + SaveButtons.MuteState + "' could not take the mod's action");
                    }

                    hooked[id] = machine.Fsm;
                    found++;
                    SetStringValue label = Label(machine);
                    if (label != null && !labels.Contains(label))
                    {
                        labels.Add(label);
                    }
                }
                catch (Exception exception)
                {
                    hooked[id] = machine.Fsm;
                    log.Warning(LogCategory.Fsm, "Save: the button on '" + machine.name + "' is not as this build expects (" + exception.Message + "): it saves and quits to the menu, as the game does.");
                }
            }

            if (found > 0)
            {
                labelled = false;
                log.Info(LogCategory.Fsm, "Save: " + found + " toilet button(s) found; in a session they save the host's world without leaving it.");
            }
        }

        // What the button shows while it is pointed at.
        private static SetStringValue Label(PlayMakerFSM machine)
        {
            FsmState[] states = machine.Fsm.States;
            for (int s = 0; s < states.Length; s++)
            {
                if (states[s].Name != SaveButtons.ClickState)
                {
                    continue;
                }

                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; a < actions.Length; a++)
                {
                    var set = actions[a] as SetStringValue;
                    if (set != null && set.stringValue != null && (set.stringValue.Value == QuitText || set.stringValue.Value == SessionText))
                    {
                        return set;
                    }
                }
            }

            return null;
        }

        private void Label()
        {
            labelled = true;
            for (int i = 0; i < labels.Count; i++)
            {
                labels[i].stringValue.Value = inSession ? SessionText : QuitText;
            }
        }

        // The game's "Mute audio", before anything of its own: in a session the button leaves it at once for the mod's state,
        // which takes it back to waiting, so the rest of the state (the boxes, the version, the muting) and the menu do not
        // happen, and the world is saved here or by the host.
        private void Pressed(PlayMakerFSM machine)
        {
            // Not while the mod runs the save: that is not the player pressing it.
            if (!inSession || WorldSaver.Running)
            {
                return;
            }

            machine.SendEvent(AfterClickEvent);
            NetSession session = manager.Session;
            if (session != null && session.Role == SessionRole.Host)
            {
                Save(machine, "the host");
            }
            else
            {
                Ask();
            }
        }

        private void Ask()
        {
            NetSession session = manager.Session;
            if (current == null || session == null)
            {
                return;
            }

            // A number the host has not had from this player, also after this player's world loaded again: the session's clock.
            int next = unchecked((int)(clock * 1000.0));
            asked = next == asked ? next + 1 : next;
            askedAt = clock;
            current.Set(RequestPrefix + session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { asked });
            Notify("Asking the host to save the world...", "Pidiéndole al host que guarde la partida...");
            log.Info(LogCategory.Fsm, "Save: this player asked the host to save (request " + asked + ").");
        }

        private bool Save(PlayMakerFSM button, string who)
        {
            if (!saver.Begin(button, Saved))
            {
                if (saver.Busy)
                {
                    return false;
                }

                Notify("This game cannot save without leaving the world (the log says why).", "Este juego no puede guardar sin salir (el log dice por qué).");
                return false;
            }

            Notify("Saving...", "Guardando...");
            log.Info(LogCategory.Fsm, "Save: asked by " + who + ".");
            return true;
        }

        private void Saved(WorldSaver.Report report)
        {
            saves++;
            if (report.Errors == 0)
            {
                Notify("World saved.", "Partida guardada.");
            }
            else
            {
                Notify(
                    "World saved, with " + report.Errors + " error(s): the log says which.",
                    "Partida guardada, con " + report.Errors + " error(es): el log dice cuáles.");
            }

            NetSession session = manager.Session;
            if (current != null && session != null && session.Role == SessionRole.Host && session.State == SessionState.Connected)
            {
                current.Set(DoneTally, new[] { saves, report.Errors });
            }
        }

        private void OnStateWanted(string name, int[] values)
        {
            NetSession session = manager.Session;
            if (session == null || values == null || values.Length == 0)
            {
                return;
            }

            if (name == DoneTally)
            {
                if (session.Role != SessionRole.Host && !current.CatchingUp)
                {
                    askedAt = double.NaN;
                    Notify("The host saved the world.", "El host guardó la partida.");
                    log.Info(LogCategory.Fsm, "Save: the host saved its world (save " + values[0] + ").");
                }

                return;
            }

            byte player;
            if (session.Role != SessionRole.Host || !name.StartsWith(RequestPrefix, StringComparison.Ordinal)
                || !byte.TryParse(name.Substring(RequestPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out player)
                || player == session.LocalPlayerId)
            {
                return;
            }

            int was;
            if (requests.TryGetValue(player, out was) && was == values[0])
            {
                return;
            }

            requests[player] = values[0];
            if (!Save(null, "player " + player) && saver.Busy)
            {
                log.Info(LogCategory.Fsm, "Save: player " + player + " asked while a save was on its way: that one counts.");
            }
        }

        private static void Notify(string english, string spanish)
        {
            HostWorld.Notify(english, spanish, NoticeSeconds);
        }

        // The mod's state after a click it took: the button goes back to waiting only in a later frame with the mouse's button
        // up. Sent back in the frame of the click, it is pointed at and clicked again in that same frame, a thousand times,
        // and PlayMaker switches it off ("Loop count exceeded"): that toilet saved once and never again until the world
        // loaded again (2026-11-09).
        private sealed class AfterClick : FsmStateAction
        {
            private readonly PlayMakerFSM machine;
            private int frame;

            public AfterClick(PlayMakerFSM machine)
            {
                this.machine = machine;
            }

            public override void OnEnter()
            {
                frame = Time.frameCount;
            }

            public override void OnUpdate()
            {
                if (Time.frameCount > frame && !Input.GetMouseButton(0))
                {
                    machine.SendEvent(BackEvent);
                }
            }
        }
    }
}
