using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using JXOPlayer.Common;
using JXOPlayer.Features;
using JXOPlayer.Script;

public class StimulusAxisOptions
{
    public bool SpeedLimitEnabled { get; set; }
    public double SpeedLimitUnitsPerSecond { get; set; } = 10;
    public bool AutoHomeEnabled { get; set; } = true;
    public double AutoHomeTargetValue { get; set; } = .5;
    public double AutoHomeDuration { get; set; } = .5;
}
public class StimulusAxisModel { public IScriptResource Script { get; set; } }
public class StimulusMedia { public string OriginalPath { get; set; } = "synthetic.mp4"; }
public class StimulusScript
{
    public Dictionary<DeviceAxis, StimulusAxisOptions> AxisSettings { get; } = DeviceAxis.All.ToDictionary(a => a, a => new StimulusAxisOptions());
    public Dictionary<DeviceAxis, StimulusAxisModel> AxisModels { get; } = DeviceAxis.All.ToDictionary(a => a, a => new StimulusAxisModel());
    public StimulusMedia MediaResource { get; set; } = new StimulusMedia();
}

public static partial class TestFloatingAxis
{
    static Keyframe[] MotionFixture()
    {
        var points = new List<Keyframe> { new Keyframe(0, .2) }; double time = 0;
        void Cycles(int count, double duration, double high)
        {
            for (int i = 0; i < count; i++) { points.Add(new Keyframe(time + duration / 2, high)); time += duration; points.Add(new Keyframe(time, .2)); }
        }
        Cycles(4, 2, .6); Cycles(5, .5, .8); Cycles(1, .2, 1);
        return points.ToArray();
    }
    static ScriptResource Resource(Keyframe[] frames)
    {
        var collection = new KeyframeCollection(); foreach (var p in frames) collection.Add(p.Position, p.Value);
        return new ScriptResource { Name = "synthetic.funscript", Source = "test", Keyframes = collection };
    }
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    static void AwaitAnalysis(object plugin)
    {
        var clock = Stopwatch.StartNew();
        while ((bool)Field(plugin, "_analyzing") && clock.Elapsed.TotalSeconds < 5) { Pump(); Thread.Sleep(10); }
        Pump();
    }
    static void TestStimulus(FloatingAxisControl plugin, FakeOutput output, object original, Window window)
    {
        var fixture = MotionFixture();
        var loop = RepeatedMotion.Find(fixture, CancellationToken.None);
        Assert(Near(loop.Start, 8) && Near(loop.End, 10.5) && loop.Repetitions == 5, "adaptive range selects fastest repeated family and ignores isolated faster spike");
        Assert(Near(loop.Duration, .5) && Near(loop.AverageSpeed, 2.4), "representative cycle retains source timing and distance-weighted mean speed");
        Assert(Near(loop.Sample(0), .2) && Near(loop.Sample(.25), .8) && Near(loop.Sample(.5), .2) && Near(loop.Sample(1000.25), .8), "cycle samples original shape and wraps without endpoint jumps");
        bool Reject(Keyframe[] p) { try { RepeatedMotion.Find(p, CancellationToken.None); return false; } catch (InvalidOperationException) { return true; } }
        Assert(Reject(Enumerable.Range(0, 20).Select(i => new Keyframe(i, .5)).ToArray()), "static scripts cannot start rage mode");
        Assert(Reject(fixture.Take(5).ToArray()), "non-repeating or insufficient cycles cannot start rage mode");
        var invalid = fixture.ToArray(); invalid[3] = new Keyframe(double.NaN, .5);
        Assert(Reject(invalid), "non-finite action times rejected");
        invalid = fixture.ToArray(); invalid[3] = new Keyframe(invalid[2].Position, .5);
        Assert(Reject(invalid), "duplicate action times rejected");
        invalid = fixture.ToArray(); invalid[3] = new Keyframe(3, 1.1);
        Assert(Reject(invalid), "out-of-range script positions rejected");
        var canceled = new CancellationTokenSource(); canceled.Cancel(); bool canceledOk = false;
        try { RepeatedMotion.Find(fixture, canceled.Token); } catch (OperationCanceledException) { canceledOk = true; }
        Assert(canceledOk, "analysis honors cancellation");

        var axis = DeviceAxis.Parse("L0"); var rotation = DeviceAxis.Parse("R0");
        var script = new StimulusScript(); script.AxisModels[axis].Script = Resource(fixture);
        Field(plugin, "_script", script);
        Field(plugin, "_readAxisValue", new Func<DeviceAxis, double>(a => .5));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Action<string> click = name => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var hookType = typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Common.MotionOutputHook", true);
        var filter = (Func<double, DeviceAxis, double>)hookType.GetMethod("Apply").CreateDelegate(typeof(Func<double, DeviceAxis, double>));
        Func<DeviceAxis, double> pos = a => filter((double)Get(At(output.Items[0].AxisSettings, a), "Minimum"), a);
        Assert(Near(filter(.123, axis), .123), "native hook is a pass-through when no mode owns it");
        TestContinuousSession(axis, rotation, fixture);
        click("Tremor");
        Assert((int)Field(plugin, "_stimulusMode") == 1 && plugin.PauseButtonText.Contains("暂停刺激"), "visible tremor button takes control and offers an explicit pause");
        Assert(Math.Abs(pos(axis) - .5) < .001 && Math.Abs(pos(rotation) - .5) < .001, "takeover starts at current positions without a jump");
        Assert(ReferenceEquals(output.Items[0].AxisSettings, original), "stimulus never substitutes native range maps while running");
        var l = new List<double>(); var r = new List<double>();
        for (int i = 0; i < 50; i++) { Advance(plugin, 20); l.Add(pos(axis)); r.Add(pos(rotation)); }
        Assert(l.Max() - l.Min() > .18 && l.All(x => x >= .35 - 1e-6 && x <= .65 + 1e-6), "stronger irregular tremor travels beyond the old envelope within configured bounds");
        Assert(r.Max() - r.Min() > .10 && r.All(x => x >= .44 - 1e-6 && x <= .56 + 1e-6), "tremor produces independent twenty-percent R0 rotation");
        Assert(DeviceAxis.All.Where(a => a != axis && a != rotation).All(a => Near(pos(a), .5)), "other axes stay held during takeover");
        script.AxisSettings[axis].SpeedLimitEnabled = true; script.AxisSettings[axis].SpeedLimitUnitsPerSecond = .02;
        Call(plugin, "Tick", null, EventArgs.Empty);
        double prior = pos(axis); Advance(plugin, 25);
        Assert(Math.Abs(pos(axis) - prior) <= .02 * .6 * .04 + 1e-8, "configured native axis speed limit is reapplied downstream of range maps");
        script.AxisSettings[axis].SpeedLimitEnabled = false;
        click("Pause"); double stopped = pos(axis); Advance(plugin, 80);
        Assert((int)Field(plugin, "_stimulusMode") == 0 && (bool)Field(plugin, "_held") && Near(pos(axis), stopped), "stop stimulus holds position instead of resuming the script");
        click("Pause"); Advance(plugin, 750);
        Assert(ReferenceEquals(output.Items[0].AxisSettings, original), "second pause-button click smoothly returns to original output mapping");

        click("Rage"); AwaitAnalysis(plugin);
        Assert((int)Field(plugin, "_stimulusMode") == 2, "rage button asynchronously extracts current script and takes over");
        l.Clear(); r.Clear();
        for (int i = 0; i < 120; i++) { Advance(plugin, 20); l.Add(pos(axis)); r.Add(pos(rotation)); }
        Assert(l.Max() - l.Min() > .43 && l.All(x => x >= .26 - 1e-6 && x <= .74 + 1e-6), "rage expands the repeated source stroke to eighty percent of configured range");
        Assert(r.Max() - r.Min() > .16 && r.All(x => x >= .41 - 1e-6 && x <= .59 + 1e-6), "rage sends thirty-percent reciprocal R0 rotation");
        script.MediaResource.OriginalPath = "different.mp4"; Advance(plugin, 20);
        Assert((int)Field(plugin, "_stimulusMode") == 0 && (bool)Field(plugin, "_held"), "video changes stop old takeover even before new script is loaded");
        click("Rage"); click("Pause"); AwaitAnalysis(plugin);
        Assert((int)Field(plugin, "_stimulusMode") == 0 && !(bool)Field(plugin, "_analyzing"), "cancel prevents late asynchronous completion from starting motion");
        click("Tremor"); Call(plugin, "HidePanel");
        Assert((int)Field(plugin, "_stimulusMode") == 0, "hiding panel stops active stimulus");
        click("Tremor");
        var task = System.Threading.Tasks.Task.Run(() => { var trace = new List<double>(); for (int i = 0; i < 32; i++) { trace.Add(pos(axis)); Thread.Sleep(10); } return trace; });
        Thread.Sleep(360); var continuous = task.GetAwaiter().GetResult();
        Assert((int)Field(plugin, "_stimulusMode") == 1 && continuous.Distinct().Count() > 25, "native output samples keep changing while UI thread is blocked");
        Call(plugin, "SetCollapsed", true, false); Advance(plugin, 20);
        Assert((int)Field(plugin, "_stimulusMode") == 1 && (string)((Button)window.FindName("Bubble")).Content == "颤", "collapsed bubble keeps mode visible while continuing control");
        click("Tremor");
        Advance(plugin, 750);
        Assert((int)Field(plugin, "_stimulusMode") == 0 && !(bool)Field(plugin, "_held") && hookType.GetField("Filter").GetValue(null) == null, "clicking active mode again releases both hook and hold so automatic homing works");
        for (int i = 0; i < 20; i++) { click("Tremor"); pos(axis); click("Tremor"); }
        Advance(plugin, 750);
        Assert(hookType.GetField("Filter").GetValue(null) == null && ReferenceEquals(output.Items[0].AxisSettings, original) && !(bool)Field(plugin, "_held"), "twenty rapid toggles leave no held map or motion callback behind");
        Assert(Near(filter(.37, axis), .37), "native home position passes through unchanged after repeated toggles");
        Set(At(original, rotation), "Enabled", false); click("Tremor");
        Assert((int)Field(plugin, "_stimulusMode") == 0 && !(bool)Get(At(original, rotation), "Enabled"), "disabled rotation refuses takeover and is never enabled implicitly");
        Set(At(original, rotation), "Enabled", true);
        click("Tremor"); Call(plugin, "ChangeRange", true, -1);
        Assert((int)Field(plugin, "_stimulusMode") == 0, "range edits stop active stimulus");
        Set(At(original, axis), "Maximum", .8);
        script.AxisModels[axis].Script = Resource(Enumerable.Range(0, 10).Select(i => new Keyframe(i, .5)).ToArray());
        click("Rage"); AwaitAnalysis(plugin);
        Assert((int)Field(plugin, "_stimulusMode") == 0 && !(bool)Field(plugin, "_analyzing"), "invalid script failure clears busy state and leaves takeover stopped");
        TestHome(plugin, output, original, window, script);
        Field(plugin, "_script", null);
        Call(plugin, "ResumeMotion"); Advance(plugin, 750);
    }

    static void TestStrongerMotion(DeviceAxis axis, DeviceAxis rotation, Dictionary<DeviceAxis, double> initial, RepeatedMotion loop)
    {
        var variable = new List<Keyframe> { new Keyframe(0, .2) };
        double t = 0;
        foreach (double duration in new[] { .5, .55, .4, .5, .5 })
        { variable.Add(new Keyframe(t + duration / 2, .8)); t += duration; variable.Add(new Keyframe(t, .2)); }
        var fastest = RepeatedMotion.Find(variable.ToArray(), CancellationToken.None);
        Assert(Near(fastest.Duration, .4) && fastest.Repetitions == 5, "rage selects fastest complete cycle instead of slower median within repeated family");
        for (int i = 0; i < 4; i++) { variable.Add(new Keyframe(t+.04,.8)); t+=.08; variable.Add(new Keyframe(t,.2)); }
        fastest = RepeatedMotion.Find(variable.ToArray(), CancellationToken.None);
        Assert(Near(fastest.Duration,.08) && fastest.Repetitions == 4, "dense eighty-millisecond repeat family wins over slower cycles instead of being excluded");
        foreach (int mode in new[] { 1, 2 })
        {
            var session = new MotionSession(mode, loop, axis, rotation, initial, .2, .8, .2, .8, double.PositiveInfinity, double.PositiveInfinity, .01);
            var strokeTrace = new List<double>(); var rotationTrace = new List<double>();
            var commands = new List<string>();
            var lastSent = DeviceAxis.All.ToDictionary(a => a, a => double.NaN);
            for (int i = 0; i <= 500; i++)
            {
                var values = DeviceAxis.All.ToDictionary(a => a, a => session.SampleAt(.5, a, i / 100.0));
                var changed = values.Where(p => DeviceAxis.IsValueDirty(p.Value, lastSent[p.Key])).ToArray();
                string command = DeviceAxis.ToString(changed, 10);
                foreach (var pair in changed) lastSent[pair.Key] = pair.Value;
                if (i >= 100) { strokeTrace.Add(values[axis]); rotationTrace.Add(values[rotation]); commands.Add(command); }
            }
            double meanSpeed = strokeTrace.Zip(strokeTrace.Skip(1), (a,b) => Math.Abs(b-a)).Sum() / 4;
            Assert(meanSpeed > (mode == 1 ? 3 : loop.AverageSpeed * .6 * 1.5), "mode " + mode + " measured output travel per second exceeds previous motion");
            Assert(commands.Count(c => c.Contains("R0") && c.Contains("I10")) > 380,
                "mode " + mode + " R0 survives native dirty filtering and TCode formatting in nearly every frame");
            Assert(rotationTrace.Max() - rotationTrace.Min() >= (mode == 1 ? .114 : .175), "mode " + mode + " sampled rotation reaches both sweep ends within one output interval");
            System.IO.File.WriteAllLines("motion-mode-" + mode + ".txt", new[] { "mean-speed=" + meanSpeed.ToString("F4"), "L0-span=" + (strokeTrace.Max()-strokeTrace.Min()).ToString("F4"), "R0-span=" + (rotationTrace.Max()-rotationTrace.Min()).ToString("F4") }.Concat(commands.Take(20)));
            session = new MotionSession(mode, loop, axis, rotation, initial, .2, .8, .2, .8, .012, .018, .01);
            double lastL = .5, lastR = .5; bool bounded = true;
            for (int i = 1; i <= 500; i++)
            {
                double l = session.SampleAt(.5, axis, i / 100.0), r = session.SampleAt(.5, rotation, i / 100.0);
                bounded &= Math.Abs(l-lastL) <= .000120001 && Math.Abs(r-lastR) <= .000180001;
                lastL=l; lastR=r;
            }
            Assert(bounded, "mode " + mode + " preserves explicit limits on both axes including entry");
            session = new MotionSession(mode, loop, axis, rotation, initial, .2, .8, .2, .8, double.PositiveInfinity, double.PositiveInfinity, .01);
            Thread.Sleep(40);
            Assert(Near(session.Sample(.9, axis), .5), "mode " + mode + " starts from held position even when first device sample is delayed");
        }
        var full = RepeatedMotion.Find(Enumerable.Range(0, 9).Select(i => new Keyframe(i*.25, i%2)).ToArray(), CancellationToken.None);
        var fullSession = new MotionSession(2, full, axis, rotation, initial, .2, .8, .2, .8, double.PositiveInfinity, double.PositiveInfinity, .01);
        Assert(Near(fullSession.StrokeSpan, 1) && Near(fullSession.SampleAt(.5, axis, .75/1.35), .8), "full-range source strokes are never reduced to eighty percent");
    }

    static void TestContinuousSession(DeviceAxis axis, DeviceAxis rotation, Keyframe[] fixture)
    {
        var initial = DeviceAxis.All.ToDictionary(a => a, a => .5);
        var session = new MotionSession(1, null, axis, rotation, initial, .2, .8, .2, .8, double.PositiveInfinity, double.PositiveInfinity, .01);
        var samples = Enumerable.Range(0, 1201).Select(i => session.SampleAt(.5, axis, i / 100.0)).ToArray();
        var turns = Enumerable.Range(101, 1099).Where(i => (samples[i] - samples[i - 1]) * (samples[i + 1] - samples[i]) < 0).ToArray();
        Assert(turns.Length >= 220 && turns.Length <= 320, "stronger tremor delivers twenty-to-thirty reversals per second after entry");
        Assert(turns.Zip(turns.Skip(1), (a,b) => b-a).Distinct().Count() >= 4 && turns.Select(i => Math.Round(samples[i], 3)).Distinct().Count() > 30,
            "tremor peaks have unequal timing and amplitudes instead of a repeating sine wave");
        var pattern = new TremorPattern(7);
        double slope1 = (pattern.Sample(.012) - pattern.Sample(.01)) / .002, slope2 = (pattern.Sample(.022) - pattern.Sample(.02)) / .002;
        Assert(Near(slope1, slope2) && Math.Abs(slope1) > .1, "individual tremor strokes are straight script-style segments");
        System.IO.File.WriteAllLines("tremor-samples.csv", new[] { "seconds,position" }.Concat(samples.Select((v,i) => (i / 100.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))));
        Assert(Enumerable.Range(31, 169).All(i => Math.Abs(samples[i] - samples[i - 1]) > 1e-6), "there are no repeated every-other-frame positions that introduce interpolation gaps");
        var loop = RepeatedMotion.Find(fixture, CancellationToken.None);
        session = new MotionSession(2, loop, axis, rotation, initial, .2, .8, .2, .8, double.PositiveInfinity, double.PositiveInfinity, .01);
        session.SampleAt(.5, axis, .5 / 1.35);
        Assert(Near(session.SampleAt(.5, axis, .75 / 1.35), .74) && Near(session.SampleAt(.5, axis, 1 / 1.35), .26), "rage expands source amplitude and completes each cycle at 1.35 times source tempo");
        TestStrongerMotion(axis, rotation, initial, loop);
        session = new MotionSession(1, null, axis, rotation, initial, .2, .8, .2, .8, .012, .012, .01);
        double previous = .5; bool limited = true;
        for (int i = 1; i <= 200; i++) { double value = session.SampleAt(.5, axis, i / 100.0); limited &= Math.Abs(value - previous) <= .000120001; previous = value; }
        Assert(limited, "explicit user speed limits also bound entry transition at every output frame");
    }
}
