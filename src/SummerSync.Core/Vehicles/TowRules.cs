namespace SummerSync.Core.Vehicles
{
    /// <summary>One end of a towing rope, as the rules below need to know it.</summary>
    public struct RopeEnd
    {
        /// <summary>Whether it is tied to a vehicle that is shared as one.</summary>
        public bool OnVehicle;

        /// <summary>Whether it is tied to a vehicle's rear hook ("HookRear"), or to its front one ("HookFront").</summary>
        public bool Rear;

        public bool Front;

        /// <summary>Whether what it is tied to runs its physics on this machine.</summary>
        public bool RunsHere;

        /// <summary>Whether what it is tied to is held to the world by a joint (the video poker before it breaks off).</summary>
        public bool Anchored;
    }

    /// <summary>What a towing rope does in one game, worked out from its two ends.</summary>
    public struct RopePull
    {
        /// <summary>Whether the first end is the towing one.</summary>
        public bool FirstTows;

        /// <summary>Whether the rope's joint pulls in this game; otherwise it is slack and only drawn.</summary>
        public bool Pulls;

        /// <summary>Whether this machine holds what is towed: it runs the towing vehicle's physics.</summary>
        public bool HoldsTowed;
    }

    /// <summary>
    /// The rules for a towing rope between two things, the same in every game (docs/ARCHITECTURE.md "Remolques, la cuerda y
    /// la máquina de póker"). A joint between two bodies only does what it does in the game whose physics moves both: tied to
    /// a copy that another machine moves, a vehicle could not pull it and would be held where the copy is.
    /// </summary>
    public static class TowRules
    {
        /// <summary>
        /// Which end tows: a vehicle against something that is not one; between two vehicles the one tied at its rear, or
        /// the other of one tied at its front; the first tied otherwise.
        /// </summary>
        public static bool FirstTows(RopeEnd first, RopeEnd second)
        {
            if (first.OnVehicle != second.OnVehicle)
            {
                return first.OnVehicle;
            }

            if (first.Rear != second.Rear)
            {
                return first.Rear;
            }

            if (first.Front != second.Front)
            {
                return second.Front;
            }

            return true;
        }

        /// <summary>
        /// What the rope does here. It pulls where what is towed runs its physics, if the towing vehicle does too, or if
        /// what is towed is not held to the world (then it is dragged by the copy of the towing one: a junk car in every game,
        /// or a towed vehicle on its driver's machine). The machine that runs the towing vehicle holds what it tows.
        /// </summary>
        public static RopePull Decide(RopeEnd first, RopeEnd second)
        {
            bool firstTows = FirstTows(first, second);
            RopeEnd tower = firstTows ? first : second;
            RopeEnd towed = firstTows ? second : first;
            return new RopePull
            {
                FirstTows = firstTows,
                Pulls = towed.RunsHere && (tower.RunsHere || !towed.Anchored),
                HoldsTowed = tower.RunsHere,
            };
        }
    }
}
