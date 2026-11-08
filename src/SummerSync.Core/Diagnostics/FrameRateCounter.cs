using System;

namespace SummerSync.Core.Diagnostics
{
    /// <summary>
    /// Averages the frame rate over fixed time windows so the displayed number is stable enough to read.
    /// </summary>
    public sealed class FrameRateCounter
    {
        private readonly float windowSeconds;
        private float elapsed;
        private int frames;

        public FrameRateCounter()
            : this(0.5f)
        {
        }

        public FrameRateCounter(float windowSeconds)
        {
            if (!(windowSeconds > 0f))
            {
                throw new ArgumentOutOfRangeException("windowSeconds", "The averaging window must be positive.");
            }

            this.windowSeconds = windowSeconds;
        }

        /// <summary>Frames per second over the last completed window; 0 until the first one completes.</summary>
        public float FramesPerSecond { get; private set; }

        /// <summary>
        /// Registers one frame. Returns true when a window completed and <see cref="FramesPerSecond"/> changed.
        /// </summary>
        public bool Tick(float deltaSeconds)
        {
            if (!(deltaSeconds >= 0f))
            {
                // Negative or NaN frame times would poison the average; skip the frame.
                return false;
            }

            frames++;
            elapsed += deltaSeconds;
            if (elapsed < windowSeconds)
            {
                return false;
            }

            FramesPerSecond = frames / elapsed;
            frames = 0;
            elapsed = 0f;
            return true;
        }
    }
}
