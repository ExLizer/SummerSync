using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>A player who smokes now: where they are in the world (the origin of their object), and whether they breathe the smoke out.</summary>
    internal struct Smoker
    {
        public readonly Vector3 Position;
        public readonly bool Breathing;

        public Smoker(Vector3 position, bool breathing)
        {
            Position = position;
            Breathing = breathing;
        }
    }
}
