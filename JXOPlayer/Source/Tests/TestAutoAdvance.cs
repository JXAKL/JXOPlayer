using System;
using JXOPlayer.Features;

public static class TestAutoAdvance
{
    public static void Run(Action<bool,string> check)
    {
        PlaybackObservation Sample(int state,double position,string path="C:\\clip.mp4",int tier=3,int window=1)=>new PlaybackObservation{Path=path,Tier=tier,Window=new IntPtr(window),Duration=12,Position=position,State=state};
        AutoAdvanceGate End()
        {
            var gate=new AutoAdvanceGate();
            gate.Observe(Sample(2,10.5),0);gate.Observe(Sample(2,11),.5);gate.Observe(Sample(2,11.6),1);
            check(gate.Observe(Sample(0,11.6),1.3)==null,"EOF leaves time for PotPlayer's own playlist");
            return gate;
        }
        PlaybackObservation Wait(AutoAdvanceGate gate,int state=0,string path="C:\\clip.mp4",int tier=3,int window=1)
        {
            gate.Observe(Sample(state,0,path,tier,window),1.8);gate.Observe(Sample(state,0,path,tier,window),2.6);
            return gate.Observe(Sample(state,0,path,tier,window),3.4);
        }
        var end=End();var next=Wait(end);check(next?.Tier==3,"natural EOF survives position reset and retains the same tier");
        check(end.Observe(Sample(0,0),4)==null,"one finished video produces only one auto-open attempt");
        check(Wait(End(),2,"C:\\next.mp4")==null,"native next video takes precedence");
        check(Wait(End(),2)==null,"native same-video repeat takes precedence");
        check(Wait(End(),1)==null,"pause during the grace period cancels auto-next");
        check(Wait(End(),0,"C:\\clip.mp3")==null,"audio never auto-advances");
        check(Wait(End(),0,window:2)==null,"a replaced player window cancels auto-next");
        check(Wait(End(),0,tier:0)==null,"unclassified media never falls back to an arbitrary tier");
        var reset=End();reset.Reset();check(Wait(reset)==null,"disabling or refreshing cancels pending auto-next");
        var closed=End();closed.Observe(Sample(-1,0),1.5);check(Wait(closed)==null,"disconnect cancels pending auto-next");
        var pause=new AutoAdvanceGate();pause.Observe(Sample(2,11),0);pause.Observe(Sample(2,11.4),.5);pause.Observe(Sample(2,11.8),1);pause.Observe(Sample(1,11.8),1.3);
        check(Wait(pause)==null,"pause at the last frame followed by stop is not EOF");
        var stop=new AutoAdvanceGate();stop.Observe(Sample(2,3),0);stop.Observe(Sample(2,3.5),.5);stop.Observe(Sample(2,4),1);stop.Observe(Sample(0,0),1.3);
        check(Wait(stop)==null,"stopping mid-video does not start another video");
        var seek=new AutoAdvanceGate();seek.Observe(Sample(2,3),0);seek.Observe(Sample(2,11.9),.5);seek.Observe(Sample(0,0),1.3);
        check(Wait(seek)==null,"seeking straight to EOF does not fabricate natural playback");
        var stalled=End();check(stalled.Observe(Sample(0,0),10)==null,"stale observations after sleep do not auto-open");
        var failed=End();failed.Observe(null,1.6);failed.Observe(null,3);check(Wait(failed)==null,"prolonged unresponsive player cannot trigger a restart");
        var transient=End();transient.Observe(null,1.6);check(Wait(transient)!=null,"a single transient read failure during grace does not lose EOF");
        var boundary=new AutoAdvanceGate();boundary.Observe(Sample(2,10.5),0);boundary.Observe(Sample(2,11),.5);boundary.Observe(Sample(2,11.6),1);
        boundary.Observe(null,1.2);boundary.Observe(Sample(0,0),1.5);boundary.Observe(Sample(0,0),2.5);
        check(boundary.Observe(Sample(0,0),3.6)!=null,"read straddling playing/stopped transition still triggers exactly one auto-next");
        var lag=new AutoAdvanceGate();lag.Observe(Sample(2,10),0);lag.Observe(Sample(2,10.5),.5);lag.Observe(Sample(2,11.1),1);lag.Observe(Sample(0,0),1.8);lag.Observe(Sample(0,0),2.8);
        check(lag.Observe(Sample(0,0),3.9)!=null,"moderately delayed final poll uses elapsed time instead of a fixed 650ms cutoff");
    }
}
