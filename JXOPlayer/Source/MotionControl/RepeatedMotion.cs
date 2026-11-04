namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using JXOPlayer.Common;

    // Pure analysis: seconds and normalized script positions, no device access.
    public sealed class RepeatedMotion
    {
        public Keyframe[] Points { get; private set; }
        public double Start { get; private set; }
        public double End { get; private set; }
        public double AverageSpeed { get; private set; }
        public int Repetitions { get; private set; }
        public double PeakSpeed { get; private set; }
        public double Minimum { get; private set; }
        public double Maximum { get; private set; }
        public double Stroke => Maximum - Minimum;
        public double Duration => Points[Points.Length - 1].Position;

        sealed class Cycle
        {
            public int First, Last;
            public double Low, High, Duration, Distance;
        }

        public static RepeatedMotion Find(Keyframe[] points, CancellationToken token)
        {
            if (points == null || points.Length < 7) throw new InvalidOperationException("当前上下轴脚本没有足够的重复往复动作。");
            var turns = new List<int> { 0 };
            var distance = new double[points.Length];
            int direction = 0;
            for (int i = 0; i < points.Length; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                var p = points[i];
                if (!double.IsFinite(p.Position) || !double.IsFinite(p.Value) || p.Position < 0 || p.Value < 0 || p.Value > 1 || (i > 0 && p.Position <= points[i - 1].Position))
                    throw new InvalidOperationException("当前脚本含有无效时间或位置，无法提取动作。");
                if (i == 0) continue;
                double delta = p.Value - points[i - 1].Value;
                distance[i] = distance[i - 1] + Math.Abs(delta);
                int sign = Math.Sign(delta);
                if (sign == 0) continue; // Dwells remain in the elapsed cycle time.
                if (direction != 0 && sign != direction) turns.Add(i - 1);
                direction = sign;
            }
            turns.Add(points.Length - 1);
            var cycles = new List<Cycle>();
            // Trough-to-trough cycles. Monotone sample density does not affect matching.
            for (int i = 0; i + 2 < turns.Count; i++)
            {
                int a = turns[i], b = turns[i + 1], c = turns[i + 2];
                if (points[b].Value <= points[a].Value || points[b].Value <= points[c].Value) continue;
                cycles.Add(new Cycle { First = a, Last = c, Low = (points[a].Value + points[c].Value) / 2,
                    High = points[b].Value, Duration = points[c].Position - points[a].Position, Distance = distance[c] - distance[a] });
            }
            // Do not discard dense 40-120 ms complete cycles: that made rage
            // select a slower family even when the script contained fast repeats.
            bool Valid(Cycle c) => c.Duration >= .04 - 1e-9 && c.High - c.Low >= .02 && Math.Abs(points[c.First].Value - points[c.Last].Value) <= .04;
            bool Similar(Cycle a, Cycle b) => Valid(b) && Math.Abs(a.Low - b.Low) <= .08 && Math.Abs(a.High - b.High) <= .08
                && Math.Abs((a.High - a.Low) - (b.High - b.Low)) <= .25 * (a.High - a.Low)
                && Math.Abs(a.Duration - b.Duration) <= .3 * a.Duration;
            int bestStart = -1, bestCount = 0;
            double bestSpeed = 0;
            // Maximal consecutive families provide elastic windows. Compare with
            // the first cycle to avoid tolerance drift across an entire script.
            for (int first = 0; first < cycles.Count;)
            {
                token.ThrowIfCancellationRequested();
                var seed = cycles[first];
                if (!Valid(seed)) { first++; continue; }
                int end = first + 1;
                double seconds = seed.Duration, travel = seed.Distance;
                while (end < cycles.Count && cycles[end - 1].Last == cycles[end].First && Similar(seed, cycles[end]))
                {
                    if ((end & 1023) == 0) token.ThrowIfCancellationRequested();
                    seconds += cycles[end].Duration; travel += cycles[end].Distance; end++;
                }
                int count = end - first;
                double speed = travel / seconds;
                if (count >= 3 && (speed > bestSpeed + 1e-9 || (Math.Abs(speed - bestSpeed) <= 1e-9 && count > bestCount)))
                { bestStart = first; bestCount = count; bestSpeed = speed; }
                first = end;
            }
            if (bestStart < 0) throw new InvalidOperationException("未找到连续至少 3 次的相似往复动作；当前运动未被接管。");
            // The median-duration cycle can be slower than the selected family.
            // Use its fastest complete cycle; isolated spikes outside it still lose.
            var chosen = cycles.Skip(bestStart).Take(bestCount)
                .OrderByDescending(c => c.Distance / c.Duration).ThenByDescending(c => c.High - c.Low).First();
            var loop = new Keyframe[chosen.Last - chosen.First + 1];
            double origin = points[chosen.First].Position;
            double drift = points[chosen.Last].Value - points[chosen.First].Value;
            for (int i = 0; i < loop.Length; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                var p = points[chosen.First + i];
                loop[i] = new Keyframe(p.Position - origin, Math.Clamp(p.Value - drift * (p.Position - origin) / chosen.Duration, 0, 1));
            }
            double peak = 0;
            for (int i = 1; i < loop.Length; i++) peak = Math.Max(peak, Math.Abs(loop[i].Value - loop[i - 1].Value) / (loop[i].Position - loop[i - 1].Position));
            return new RepeatedMotion { Points = loop, PeakSpeed = peak, Minimum = loop.Min(p => p.Value), Maximum = loop.Max(p => p.Value), Start = points[cycles[bestStart].First].Position,
                End = points[cycles[bestStart + bestCount - 1].Last].Position, AverageSpeed = bestSpeed, Repetitions = bestCount };
        }

        public double Sample(double seconds)
        {
            double t = Math.Max(0, seconds) % Duration;
            int lo = 0, hi = Points.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Points[mid].Position <= t) lo = mid; else hi = mid; }
            var a = Points[lo]; var b = Points[hi];
            return a.Value + (b.Value - a.Value) * (t - a.Position) / (b.Position - a.Position);
        }

    }
}
