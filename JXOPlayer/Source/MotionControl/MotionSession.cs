namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using JXOPlayer.Common;

    // Sampled by the native output thread, never by a DispatcherTimer. Each native
    // command therefore has a fresh position and the host's own interpolation time.
    public sealed class MotionSession : IOutputMotion
    {
        readonly object gate = new object();
        readonly Dictionary<DeviceAxis, double> positions;
        readonly DeviceAxis stroke, rotation;
        readonly int mode;
        readonly RepeatedMotion loop;
        readonly TremorPattern tremor = new TremorPattern(0x9E3779B9);
        public const double RageTempo = 1.35;
        public double StrokeSpan => mode == 1 ? .50 : Math.Max(.80, loop.Stroke);
        public double RotationSpan => mode == 1 ? .20 : .30;
        readonly double rageGain, rageLow;
        readonly double low, high, rlow, rhigh, initialL, initialR, centerL, centerR;
        double lastTime, age, strokePhase, rotationPhase, lSpeed, rSpeed, sampleInterval;
        bool stopped;
        long started;
        public double Now { get { long stamp = Stopwatch.GetTimestamp(); System.Threading.Interlocked.CompareExchange(ref started, stamp, 0); return (stamp - started) / (double)Stopwatch.Frequency; } }
        public MotionSession(int mode, RepeatedMotion loop, DeviceAxis stroke, DeviceAxis rotation,
            Dictionary<DeviceAxis, double> initial, double low, double high, double rlow, double rhigh,
            double lSpeed, double rSpeed, double sampleInterval)
        {
            this.mode = mode; this.loop = loop; this.stroke = stroke; this.rotation = rotation;
            positions = new Dictionary<DeviceAxis, double>(initial);
            this.low = low; this.high = high; this.rlow = rlow; this.rhigh = rhigh;
            initialL = positions[stroke]; initialR = positions[rotation];
            centerL = Math.Clamp(initialL, low + (high - low) * .25, high - (high - low) * .25);
            centerR = (rlow + rhigh) / 2;
            if (mode == 2)
            {
                rageGain = StrokeSpan / loop.Stroke;
                rageLow = Math.Clamp((loop.Minimum + loop.Maximum - StrokeSpan) / 2, 0, 1 - StrokeSpan);
            }
            SetLimits(lSpeed, rSpeed, sampleInterval);
        }
        public void SetLimits(double l, double r, double interval)
        {
            if (double.IsNaN(l) || double.IsNaN(r) || l < 0 || r < 0 || !double.IsFinite(interval) || interval <= 0)
                throw new InvalidOperationException("轴限速或输出间隔无效。");
            lock (gate) { lSpeed = l; rSpeed = r; sampleInterval = interval; }
        }
        public double Sample(double normal, DeviceAxis axis) => SampleAt(normal, axis, Now);
        public double SampleAt(double normal, DeviceAxis axis, double time)
        {
            lock (gate)
            {
                if (!positions.ContainsKey(axis)) return normal;
                if (!stopped && time > lastTime)
                {
                    double dt = time - lastTime; lastTime = time; age += dt;
                    double ra = (rhigh - rlow) * RotationSpan / 2;
                    double lf = mode == 1 ? Math.Min(1, Math.Min(tremor.MinimumInterval / (3 * sampleInterval), lSpeed / (tremor.PeakSpeed * (high - low))))
                        : Math.Min(RageTempo, lSpeed / (loop.PeakSpeed * rageGain * (high - low)));
                    // Independent R0 motion has time to travel each way instead of
                    // inheriting the tiny, very rapid L0 jitter. Constant-speed legs
                    // avoid lingering near the ends of the previous sine wave.
                    double rf = Math.Min(mode == 1 ? 2 : 1.5, Math.Min(1 / (8 * sampleInterval), rSpeed / (4 * ra)));
                    strokePhase += dt * lf; rotationPhase += dt * rf;
                    double blend = Math.Clamp(age / .25, 0, 1); blend = blend * blend * (3 - 2 * blend);
                    double l = mode == 1 ? centerL + (high - low) * tremor.Sample(strokePhase)
                        : low + (high - low) * (rageLow + (loop.Sample(strokePhase) - loop.Minimum) * rageGain);
                    double r = centerR + ra * Triangle(rotationPhase);
                    l = initialL + (l - initialL) * blend; r = initialR + (r - initialR) * blend;
                    positions[stroke] = Limit(positions[stroke], Math.Clamp(l, low, high), lSpeed, dt);
                    positions[rotation] = Limit(positions[rotation], Math.Clamp(r, rlow, rhigh), rSpeed, dt);
                }
                return positions[axis];
            }
        }
        static double Triangle(double cycles) => 1 - 4 * Math.Abs((cycles + .25) % 1 - .5);
        static double Limit(double current, double target, double speed, double dt) => double.IsPositiveInfinity(speed)
            ? target : current + Math.Clamp(target - current, -speed * dt, speed * dt);
        public Dictionary<DeviceAxis, double> Stop()
        {
            lock (gate) { stopped = true; return new Dictionary<DeviceAxis, double>(positions); }
        }
        public Dictionary<DeviceAxis, double> Snapshot() { lock (gate) return new Dictionary<DeviceAxis, double>(positions); }
    }
}
