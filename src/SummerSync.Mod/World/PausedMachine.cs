namespace SummerSync.Unity.World
{
    /// <summary>
    /// A state machine of the game switched off for a while and back on where it was. PlayMaker restarts a machine from
    /// its first state when it is switched on again if it has "RestartOnEnable" (most do), and switching it off then
    /// leaves its state too: the bus's setup moved the bus back to where it starts, and a seat forgot its rider. Off, the
    /// machine here keeps its state; on again, it goes on from it, and keeps its own setting.
    /// </summary>
    internal sealed class PausedMachine
    {
        private readonly bool restart;

        /// <summary>Switches the machine off, keeping where it is.</summary>
        public PausedMachine(PlayMakerFSM machine)
        {
            Machine = machine;
            restart = machine.Fsm != null && machine.Fsm.RestartOnEnable;
            if (machine.Fsm != null)
            {
                machine.Fsm.RestartOnEnable = false;
            }

            machine.enabled = false;
        }

        public PlayMakerFSM Machine { get; }

        /// <summary>Switches it back on, where it was.</summary>
        public void Resume()
        {
            // Unity's null check is also true for a destroyed object.
            if (Machine == null)
            {
                return;
            }

            Machine.enabled = true;
            if (Machine.Fsm != null)
            {
                Machine.Fsm.RestartOnEnable = restart;
            }
        }
    }
}
