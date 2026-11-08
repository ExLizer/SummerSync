using SummerSync.Core.Replication;

namespace SummerSync.Core.Players
{
    /// <summary>
    /// Snapshot interpolation for one remote player: what pose to show now, given the states that arrived.
    /// All of it is in <see cref="SnapshotBuffer{T}"/>; a player's pose is one of the things it interpolates.
    /// </summary>
    public sealed class PoseBuffer : SnapshotBuffer<PlayerPose>
    {
        public PoseBuffer()
        {
        }

        public PoseBuffer(SnapshotBufferConfig config)
            : base(config)
        {
        }
    }
}
