namespace JXOPlayer.Features
{
    using System;
    using JXOPlayer.Common;

    // Dense script-style linear reversals, with unequal peaks and timing.
    // Stronger than the reference tails: 24-48% travel, 30-60 ms per leg.
    public sealed class TremorPattern
    {
        readonly Keyframe[] points;
        public double Duration => points[points.Length - 1].Position;
        public double PeakSpeed { get; }
        public double MinimumInterval { get; } = .030;
        public TremorPattern(uint seed)
        {
            uint state = seed == 0 ? 1 : seed;
            double Random() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state / (double)uint.MaxValue; }
            points = new Keyframe[2049]; points[0] = new Keyframe(0, 0);
            double time = 0, bias = 0, burst = .5, peak = 0;
            for (int i = 1; i < points.Length; i++)
            {
                if (i % 13 == 1) { burst = Random(); bias = (Random() - .5) * .02; }
                double interval = .030 + .020 * Random() + .010 * (1 - burst);
                double amplitude = .12 + .10 * Random() + .02 * burst;
                double value = Math.Clamp(bias + (i % 2 == 1 ? amplitude : -amplitude), -.25, .25);
                if (i == points.Length - 1) value = 0; // continuous loop seam
                time += interval;
                points[i] = new Keyframe(time, value);
                peak = Math.Max(peak, Math.Abs(value - points[i - 1].Value) / interval);
            }
            PeakSpeed = peak;
        }
        public double Sample(double seconds)
        {
            double t = Math.Max(0, seconds) % Duration;
            int lo = 0, hi = points.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (points[mid].Position <= t) lo = mid; else hi = mid; }
            var a = points[lo]; var b = points[hi];
            return a.Value + (b.Value - a.Value) * (t - a.Position) / (b.Position - a.Position);
        }
    }
}
