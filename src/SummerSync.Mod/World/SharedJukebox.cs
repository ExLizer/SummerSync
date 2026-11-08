using System.Collections;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// One of the game's jukeboxes (the pub's, Fleetari's office): the host's is the jukebox. Each has a state machine
    /// "Logic" that picks a random clip of "RADIO/Paikallisradio"'s "Music" through its list "Jukebox", and plays it on its
    /// own <see cref="AudioSource"/> (docs/GAME_NOTES.md, "El pub"; Fleetari's, the dump of 2026-10-15). The host tells which
    /// clip plays and how far in, when it changes and every few seconds; a client whose copy is there stops its own pick
    /// and plays the host's. While the host's is quiet (its player far away, its bar closed), each game's picks its own.
    /// </summary>
    internal sealed class SharedJukebox
    {
        private const double TellInterval = 5.0;
        private const float Slack = 2f;

        private readonly Logger log;
        private readonly string key;
        private readonly string path;
        private readonly string name;
        private PlayMakerFSM logic;
        private AudioSource source;
        private FsmFloat volume;
        private bool off;
        private AudioClip toldClip;
        private int serial;
        private float lastTime;
        private double nextTell;
        private int[] hostValues;
        private bool hostNew;
        private double hostAt;

        public SharedJukebox(Logger log, string key, string path, string name)
        {
            this.log = log;
            this.key = key;
            this.path = path;
            this.name = name;
        }

        /// <summary>The tally that carries this jukebox.</summary>
        public string Key => key;

        /// <summary>Whether this game has the jukebox.</summary>
        public bool Found => source != null && logic != null;

        /// <summary>Finds the jukebox in this game's world; <paramref name="at"/> is its object, or null.</summary>
        public void Find(Transform at)
        {
            logic = at == null ? null : DoorSync.FindMachine(at, "Logic");
            source = at == null ? null : at.GetComponent<AudioSource>();
            volume = logic == null ? null : logic.FsmVariables.FindFsmFloat("Volume");
            if (!Found)
            {
                log.Info(LogCategory.Fsm, name + ": the jukebox '" + path + "' was not found; it is each game's own.");
            }
        }

        /// <summary>The path of the jukebox's object, from a root of the scene.</summary>
        public string Path => path;

        /// <summary>What the host said its jukebox plays (a client).</summary>
        public void Heard(int[] values)
        {
            if (values != null && values.Length >= 4)
            {
                hostValues = values;
                hostNew = true;
            }
        }

        /// <summary>A new session: nothing heard yet.</summary>
        public void Forget()
        {
            hostValues = null;
            hostNew = false;
            toldClip = null;
            nextTell = 0;
        }

        /// <summary>Once per frame, with the songs of the jukeboxes.</summary>
        public void Update(double now, bool host, TallyReplication tallies, ArrayList songs)
        {
            // Unity's null check is also true for a destroyed object.
            if (!Found || songs == null || tallies == null)
            {
                return;
            }

            if (hostNew)
            {
                hostNew = false;
                hostAt = now;
            }

            if (host)
            {
                Tell(now, tallies, songs);
            }
            else
            {
                Follow(now, songs);
            }
        }

        /// <summary>The jukebox here goes back to how the game had it.</summary>
        public void Release()
        {
            // Unity's null check is also true for a destroyed object.
            if (off && logic != null)
            {
                logic.enabled = true;
            }

            off = false;
        }

        // The host: which clip its jukebox plays, and how far in, when it changes and every few seconds.
        private void Tell(double now, TallyReplication tallies, ArrayList songs)
        {
            bool on = source.gameObject.activeInHierarchy && source.isPlaying && source.clip != null;
            AudioClip clip = on ? source.clip : null;
            float time = on ? source.time : 0f;
            bool changed = clip != toldClip || (on && time + 1f < lastTime);
            lastTime = time;
            if (!changed && now < nextTell)
            {
                return;
            }

            if (changed)
            {
                serial++;
            }

            toldClip = clip;
            nextTell = now + TellInterval;
            int index = clip == null ? -1 : songs.IndexOf(clip);
            tallies.Set(key, new[] { on && index >= 0 ? 1 : 0, index, serial, Mathf.RoundToInt(time * 10f) });
            if (changed)
            {
                log.Info(LogCategory.Fsm, on && index >= 0 ? name + ": the jukebox plays '" + clip.name + "' here; the others hear it." : name + ": the jukebox is quiet here; the others' play their own.");
            }
        }

        // A client: while the host's plays, its clip is this one's.
        private void Follow(double now, ArrayList songs)
        {
            bool hostPlays = hostValues != null && hostValues[0] != 0 && hostValues[1] >= 0 && hostValues[1] < songs.Count;
            if (!hostPlays)
            {
                if (off)
                {
                    logic.enabled = true;
                    off = false;
                    log.Info(LogCategory.Fsm, name + ": the host's jukebox is quiet; this game's picks its own.");
                }

                return;
            }

            if (!source.gameObject.activeInHierarchy)
            {
                return;
            }

            if (!off)
            {
                logic.enabled = false;
                off = true;
            }

            var clip = songs[hostValues[1]] as AudioClip;
            if (clip == null)
            {
                return;
            }

            float wanted = hostValues[3] / 10f + (float)(now - hostAt);
            if (wanted >= clip.length - 0.1f)
            {
                // Over here; the host's next clip is on its way.
                return;
            }

            if (source.clip != clip || !source.isPlaying)
            {
                source.clip = clip;
                source.enabled = true;
                if (volume != null)
                {
                    source.volume = volume.Value;
                }

                source.time = Mathf.Clamp(wanted, 0f, Mathf.Max(0f, clip.length - 0.1f));
                source.Play();
                log.Info(LogCategory.Fsm, name + ": the jukebox plays the host's '" + clip.name + "' here.");
            }
            else if (Mathf.Abs(source.time - wanted) > Slack)
            {
                source.time = wanted;
            }
        }
    }
}
