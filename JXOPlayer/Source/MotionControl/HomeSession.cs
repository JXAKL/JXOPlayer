namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using JXOPlayer.Common;

    public interface IOutputMotion
    {
        double Sample(double normal, DeviceAxis axis);
        Dictionary<DeviceAxis, double> Stop();
        Dictionary<DeviceAxis, double> Snapshot();
    }

    // All enabled axes return together; completed motion stays at the requested
    // home position until the controller installs a hold or explicitly resumes.
    public sealed class HomeSession : IOutputMotion
    {
        readonly object gate = new object();
        readonly Dictionary<DeviceAxis, double> initial, targets, durations, positions;
        long started;
        bool stopped;
        double elapsed;
        public HomeSession(Dictionary<DeviceAxis, double> initial, Dictionary<DeviceAxis, double> targets,
            Dictionary<DeviceAxis, double> durations, Dictionary<DeviceAxis, double> speedLimits)
        {
            this.initial = new Dictionary<DeviceAxis, double>(initial);
            this.targets = new Dictionary<DeviceAxis, double>(targets);
            this.durations = new Dictionary<DeviceAxis, double>(durations);
            positions = new Dictionary<DeviceAxis, double>(initial);
            foreach (var axis in targets.Keys)
            {
                double distance = Math.Abs(targets[axis] - initial[axis]), speed = speedLimits[axis];
                if (!double.IsFinite(targets[axis]) || targets[axis] < 0 || targets[axis] > 1 || !double.IsFinite(durations[axis]) || double.IsNaN(speed) || speed < 0)
                    throw new InvalidOperationException("回零位置、时长或轴限速无效。");
                if (distance > 1e-6 && speed == 0) throw new InvalidOperationException(axis + " 的限速为零，无法移动到零位。");
                // Smoothstep has peak derivative 1.5. Extend the whole trajectory
                // if a configured speed limit needs more time, instead of clipping.
                this.durations[axis] = distance < 1e-6 ? 0 : Math.Max(.5, Math.Max(durations[axis], double.IsPositiveInfinity(speed) ? 0 : 1.5 * distance / speed));
            }
        }
        public bool Complete { get { lock (gate) return targets.Keys.All(a => Math.Abs(positions[a] - targets[a]) < 1e-7); } }
        public double Sample(double normal, DeviceAxis axis)
        {
            // A reset requested before connection must not skip straight to its
            // endpoint when the first real output sample finally arrives.
            long now = Stopwatch.GetTimestamp();
            long origin = Interlocked.CompareExchange(ref started, now, 0);
            return SampleAt(normal, axis, (now - (origin == 0 ? now : origin)) / (double)Stopwatch.Frequency);
        }
        public double SampleAt(double normal, DeviceAxis axis, double seconds)
        {
            lock (gate)
            {
                if (!stopped && seconds >= elapsed)
                {
                    elapsed = seconds;
                    foreach (var a in targets.Keys)
                    {
                        double t = durations[a] <= 0 ? 1 : Math.Clamp(seconds / durations[a], 0, 1);
                        positions[a] = initial[a] + (targets[a] - initial[a]) * t * t * (3 - 2 * t);
                    }
                }
                return positions.TryGetValue(axis, out var value) ? value : normal;
            }
        }
        public Dictionary<DeviceAxis, double> Stop() { lock (gate) { stopped = true; return new Dictionary<DeviceAxis, double>(positions); } }
        public Dictionary<DeviceAxis, double> Snapshot() { lock (gate) return new Dictionary<DeviceAxis, double>(positions); }
    }
}
