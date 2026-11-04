using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JXOPlayer.Common;
using JXOPlayer.Features;

public static partial class TestFloatingAxis
{
    static void TestHome(FloatingAxisControl plugin, FakeOutput output, object original, Window window, StimulusScript script)
    {
        Call(plugin, "ResumeMotion"); Advance(plugin, 750);
        var axes = DeviceAxis.All.ToArray(); var axis = DeviceAxis.Parse("L0");
        var disabled = axes.Last();
        var before = axes.Select((a,i) => (a, v: .8 - i * .08)).ToDictionary(x => x.a, x => x.v);
        Field(plugin, "_readAxisValue", new Func<DeviceAxis, double>(a => before[a]));
        foreach (var a in axes) { script.AxisSettings[a].AutoHomeTargetValue = .15 + Array.IndexOf(axes,a) * .10; script.AxisSettings[a].AutoHomeDuration = .5; }
        script.AxisSettings[axis].AutoHomeEnabled = false; // Explicit reset still works.
        Set(At(original, disabled), "Enabled", false);
        Action<string> click = name => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Func<DeviceAxis,double> held = a => (double)Get(At(output.Items[0].AxisSettings,a),"Minimum");
        click("Home");
        Assert((int)Field(plugin,"_stimulusMode") == 3 && plugin.PauseButtonText.Contains("复位"), "home button starts all-axis reset independently of automatic-home enable flag");
        var home = (HomeSession)Field(plugin,"_motionSession");
        Assert(axes.All(a => Near(home.SampleAt(.5,a,0), .2 + .6 * before[a])), "all-axis reset starts from current output without a position jump");
        home.SampleAt(.5,axis,.2);
        Assert(home.Snapshot().Where(p => p.Key != disabled).All(p => Math.Abs(p.Value - (.2 + .6 * before[p.Key])) > 1e-5), "reset moves every enabled axis rather than just L0 and R0");
        Assert(Near(home.Snapshot()[disabled], .2 + .6 * before[disabled]), "disabled axis retains its original position during reset");
        home.SampleAt(.5,axis,1); Call(plugin,"Tick",null,EventArgs.Empty);
        Assert((bool)Field(plugin,"_atHome") && (int)Field(plugin,"_stimulusMode") == 0 && (bool)Field(plugin,"_held"), "completed reset transitions into an explicit home hold");
        Assert(axes.Where(a => a != disabled).All(a => Near(held(a),.2 + .6 * script.AxisSettings[a].AutoHomeTargetValue)), "each axis uses its own configured home target mapped through its range");
        Assert(!(bool)Get(At(output.Items[0].AxisSettings,disabled),"Enabled"), "reset never enables a disabled output axis");
        Advance(plugin,180);
        Assert(Near(held(axis),.29), "home hold does not fall back to the still-playing script");
        click("Pause"); Advance(plugin,750);
        Assert(ReferenceEquals(output.Items[0].AxisSettings,original) && !(bool)Field(plugin,"_held"), "explicit resume after reset fully restores native output maps");

        Set(At(original,disabled),"Enabled",true);
        script.AxisModels[axis].Script = Resource(MotionFixture());
        click("Rage"); click("Home");
        for (int i=0;i<10;i++) { Pump(); Thread.Sleep(10); }
        Assert((int)Field(plugin,"_stimulusMode") == 3 && Field(plugin,"_motionSession") is HomeSession, "reset cancels pending rage analysis so late completion cannot reclaim output");
        home = (HomeSession)Field(plugin,"_motionSession"); home.SampleAt(.5,axis,.1);
        click("Pause");
        Assert((int)Field(plugin,"_stimulusMode") == 0 && (bool)Field(plugin,"_held") && !(bool)Field(plugin,"_atHome"), "pause during reset holds the intermediate position without claiming completion");
        click("Home");
        home = (HomeSession)Field(plugin,"_motionSession");
        var worker = System.Threading.Tasks.Task.Run(() => { for (int i=0;i<65;i++) { foreach(var a in axes) home.Sample(.5,a); Thread.Sleep(10); } });
        Thread.Sleep(720); worker.GetAwaiter().GetResult(); Call(plugin,"Tick",null,EventArgs.Empty);
        Assert((bool)Field(plugin,"_atHome") && axes.All(a => Near(held(a),.2 + .6 * script.AxisSettings[a].AutoHomeTargetValue)), "reset keeps interpolating on output thread while the UI is blocked");
        click("Tremor"); click("Home");
        Assert(Field(plugin,"_motionSession") is HomeSession && (int)Field(plugin,"_stimulusMode") == 3, "reset also takes control directly from active tremor");
        home = (HomeSession)Field(plugin,"_motionSession"); home.SampleAt(.5,axis,1); Call(plugin,"Tick",null,EventArgs.Empty);

        var initial = axes.ToDictionary(a => a, a => .8);
        var targets = axes.ToDictionary(a => a, a => .2);
        var duration = axes.ToDictionary(a => a, a => .5);
        var speed = axes.ToDictionary(a => a, a => .1);
        home = new HomeSession(initial,targets,duration,speed);
        double last=.8; bool bounded=true;
        for(int i=1;i<=1000;i++){double v=home.SampleAt(0,axis,i/100.0);bounded &= Math.Abs(v-last)<=.00100001;last=v;}
        Assert(bounded && home.Complete, "all-axis return extends duration to respect configured speed limits");
        speed[axis]=0; bool rejected=false;
        try { new HomeSession(initial,targets,duration,speed); } catch(InvalidOperationException){rejected=true;}
        Assert(rejected,"zero speed limit reports an impossible reset instead of hanging forever");
        speed[axis]=.1;
        home = new HomeSession(initial,targets,duration,speed);
        Thread.Sleep(60);
        Assert(Near(home.Sample(0,axis),.8), "reset clock begins at first device sample so delayed connection cannot skip the return trajectory");
        foreach(var a in axes){script.AxisSettings[a].AutoHomeTargetValue=.5;script.AxisSettings[a].AutoHomeEnabled=true;}
        Field(plugin,"_readAxisValue",new Func<DeviceAxis,double>(a=>.5));
        Call(plugin,"ResumeMotion");Advance(plugin,750);
    }
}
