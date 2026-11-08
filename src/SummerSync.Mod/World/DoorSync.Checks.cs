using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.World;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The doors' section of the checksums (Fase 8): each door and click switch as each game shows it, by its name. A door
    /// this game shows otherwise than the session says is taken there again; if this machine's word of the session is what
    /// differs, the host says the states again.
    /// </summary>
    internal sealed partial class DoorSync
    {
        private SwitchesSection check;
        private double checkNow;

        /// <summary>The section of the doors and click switches.</summary>
        public ICheckSection Check
        {
            get { return check ?? (check = new SwitchesSection(this)); }
        }

        private bool CheckReady
        {
            get { return current != null && found && current.Shared; }
        }

        private void ReadCheck(List<CheckEntry> entries)
        {
            for (int i = 0; i < doors.Count; i++)
            {
                Door door = doors[i];

                // Unity's null check is also true for a destroyed object.
                if (door.Machine == null || door.Open == null || current.IsOnlyHere(i))
                {
                    continue;
                }

                entries.Add(new CheckEntry(ChecksumReplication.KeyOf(door.Name), door.Open.Value ? 1UL : 0UL));
            }
        }

        // A client: the doors shown otherwise than the session says go there.
        private void FixCheckHere(IList<ulong> keys)
        {
            int shown = 0;
            for (int i = 0; i < doors.Count; i++)
            {
                Door door = doors[i];
                if (door.Machine == null || door.Open == null || !keys.Contains(ChecksumReplication.KeyOf(door.Name)))
                {
                    continue;
                }

                bool wanted = current.Get(i);
                if (door.Open.Value != wanted)
                {
                    Show(door, wanted, checkNow);
                    shown++;
                }
            }

            if (shown > 0)
            {
                log.Info(LogCategory.Fsm, shown + " door(s) or switch(es) were shown otherwise than the session says: taken there again.");
            }
        }

        private sealed class SwitchesSection : ICheckSection
        {
            private readonly DoorSync owner;

            public SwitchesSection(DoorSync owner)
            {
                this.owner = owner;
            }

            public byte Id
            {
                get { return CheckSections.Switches; }
            }

            public string Name
            {
                get { return "doors and switches"; }
            }

            public bool Ready
            {
                get { return owner.CheckReady; }
            }

            public bool CountsMissing
            {
                get { return false; }
            }

            public bool SendsEntries
            {
                get { return false; }
            }

            public void Read(List<CheckEntry> entries)
            {
                owner.ReadCheck(entries);
            }

            public ulong Coarse(ulong value)
            {
                return value;
            }

            public bool Same(ulong host, ulong here)
            {
                return host == here;
            }

            public void FixHere(IList<ulong> different, IList<ulong> onlyHost, IList<ulong> onlyHere)
            {
                owner.FixCheckHere(different);
            }

            public void FixFor(byte player, IList<ulong> keys)
            {
                owner.current.Resend(player);
            }
        }
    }
}
