namespace JXOPlayer.Features
{
    using System;
    using System.Diagnostics;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;

    public sealed class PlaybackObservation
    {
        public string Path;
        public IntPtr Window;
        public int State, Tier;
        public double Position, Duration;
    }

    // PotPlayer reports EOF as stopped, then resets position to zero. Retain the
    // last advancing position, rather than using the application's extrapolated clock.
    public sealed class AutoAdvanceGate
    {
        PlaybackObservation previous;
        double previousAt, lastAdvanceAt, due;
        int advances;
        bool pending, consumed;
        public void Reset(){previous=null;pending=false;consumed=false;advances=0;due=0;}
        public static bool Same(PlaybackObservation a,PlaybackObservation b)=>a!=null && b!=null && a.Window==b.Window && PlaybackLibrary.SamePath(a.Path,b.Path);
        public PlaybackObservation Observe(PlaybackObservation value,double now)
        {
            // One bounded query can straddle the playing -> stopped transition.
            // Keep the last reliable sample briefly; never launch from missing data.
            if(value==null){if(previous!=null && now-previousAt>1.5)Reset();return null;}
            if(!PlaybackLibrary.IsVideo(value.Path) || value.Duration<=0 || value.Tier<1 || value.Tier>5 || value.State<0)
            {Reset();return null;}
            if(!Same(previous,value)){Reset();previous=value;previousAt=now;lastAdvanceAt=now;return null;}
            if(now-previousAt>1.5 || Math.Abs(value.Duration-previous.Duration)>.5){Reset();previous=value;previousAt=now;return null;}
            if(value.State==2)
            {
                pending=false;consumed=false;
                if(previous.State!=2 || value.Position<previous.Position-.2 || value.Position-previous.Position>2)
                {advances=0;lastAdvanceAt=now;}
                else if(value.Position>previous.Position+.02){advances++;lastAdvanceAt=now;}
            }
            else if(value.State==1){pending=false;advances=0;}
            else if(value.State==0 && previous.State==2 && !consumed)
            {
                // Require actual playback advancing into the final polling interval.
                // A paused frame, a seek straight to EOF, or a mid-video stop is not EOF.
                double remaining=previous.Duration-previous.Position;
                double allowance=Math.Min(1.5,Math.Max(.65,now-previousAt+.5));
                if(advances>=2 && remaining>=-.1 && remaining<=allowance && now-lastAdvanceAt<=1.5)
                {pending=true;due=now+2;}
            }
            PlaybackObservation ready=null;
            if(pending && value.State==0 && now>=due){pending=false;consumed=true;ready=value;}
            previous=value;previousAt=now;
            return ready;
        }
    }

    // Never wait for a player window on the WPF thread. All three native queries
    // are bounded, and overlapping reads are prevented by the service.
    public sealed class PotPlayerPlaybackReader
    {
        [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,UIntPtr wparam,IntPtr lparam,uint flags,uint timeout,out UIntPtr result);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        IntPtr window;
        double nextDiscovery;
        static object Get(object target,string name)=>target?.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(target);
        public static string ConnectedPath(object root)
        {
            var manager=Get(root,"MediaSource");
            var player=manager?.GetType().GetField("_currentSource",BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(manager);
            if((string)Get(player,"Name")!="PotPlayer" || Get(player,"Status")?.ToString()!="Connected")return null;
            return (string)Get(Get(Get(root,"Script"),"MediaResource"),"OriginalPath");
        }
        static double Clock=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        public Task<PlaybackObservation> ReadAsync(string path,int tier)=>Task.Run(()=>Read(path,tier));
        PlaybackObservation Read(string path,int tier)
        {
            if(window==IntPtr.Zero || !IsWindow(window) || Clock>=nextDiscovery)
            {
                window=IntPtr.Zero;nextDiscovery=Clock+2;
                // Multiple player instances cannot safely be associated with the
                // application's native source. Wait until there is a unique window.
                foreach(var process in Process.GetProcesses())
                using(process)
                {
                    try
                    {
                        if(process.ProcessName.IndexOf("potplayer",StringComparison.OrdinalIgnoreCase)<0)continue;
                        var candidate=process.MainWindowHandle;if(candidate==IntPtr.Zero)continue;
                        if(window!=IntPtr.Zero){window=IntPtr.Zero;return null;}
                        window=candidate;
                    }catch{}
                }
            }
            if(window==IntPtr.Zero)return null;
            long? Query(uint command)
            {
                if(SendMessageTimeout(window,1024,new UIntPtr(command),IntPtr.Zero,2,150,out var value)==IntPtr.Zero)return null;
                return unchecked((int)value.ToUInt64());
            }
            var state=Query(20486);var position=Query(20484);var duration=Query(20482);var finalState=Query(20486);
            if(!state.HasValue || state!=finalState || !position.HasValue || !duration.HasValue || position<0 || duration<=0)return null;
            return new PlaybackObservation{Path=path,Window=window,State=(int)state.Value,Position=position.Value/1000.0,Duration=duration.Value/1000.0,Tier=tier};
        }
    }
}
