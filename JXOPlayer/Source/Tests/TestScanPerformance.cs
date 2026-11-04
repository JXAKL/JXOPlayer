using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JXOPlayer.Features;
using Newtonsoft.Json;

public static class TestScanPerformance
{
    static byte[] Be(ulong value,int bytes)=>Enumerable.Range(0,bytes).Select(i=>(byte)(value>>(8*(bytes-1-i)))).ToArray();
    static byte[] Box(string type,byte[] payload,bool extended=false)=>
        Be(extended?1:(ulong)(payload.Length+8),4).Concat(System.Text.Encoding.ASCII.GetBytes(type))
        .Concat(extended?Be((ulong)payload.Length+16,8):Array.Empty<byte>()).Concat(payload).ToArray();
    static byte[] Movie(byte version,ulong duration,bool fragmented=false)
    {
        var data=new[]{version,(byte)0,(byte)0,(byte)0}.Concat(new byte[version==1?16:8]).Concat(Be(1000,4)).Concat(Be(duration,version==1?8:4)).ToArray();
        return Box("moov",Box("mvhd",data).Concat(fragmented?Box("mvex",Array.Empty<byte>()):Array.Empty<byte>()).ToArray(),true);
    }
    public static void Run(string parent,Action<bool,string> check)
    {
        string root=Path.Combine(parent,"scan-performance");Directory.CreateDirectory(root);
        string file=Path.Combine(root,"header.mp4");
        File.WriteAllBytes(file,Box("free",new byte[128]).Concat(Movie(0,12345)).ToArray());
        check(Math.Abs(ContainerDurationReader.Read(file,CancellationToken.None)-12.345)<.00001,"MP4 v0 duration, skipped boxes and extended box size");
        File.WriteAllBytes(file,Movie(1,4294969000));
        check(Math.Abs(ContainerDurationReader.Read(file,CancellationToken.None)-4294969)<.00001,"MP4 v1 uses 64-bit duration without truncation");
        File.WriteAllBytes(file,Movie(0,12000,true));
        check(ContainerDurationReader.Read(file,CancellationToken.None)==0,"fragmented MP4 delegates to demuxer instead of trusting incomplete movie header");
        File.WriteAllBytes(file,Movie(0,uint.MaxValue));
        check(ContainerDurationReader.Read(file,CancellationToken.None)==0,"unknown MP4 duration sentinel is not accepted");
        File.WriteAllBytes(file,Be(ulong.MaxValue,4).Concat(System.Text.Encoding.ASCII.GetBytes("moov")).ToArray());
        check(ContainerDurationReader.Read(file,CancellationToken.None)==0,"oversized/truncated MP4 atom is rejected within header bounds");
        string fixture=Path.Combine(Environment.CurrentDirectory,"Fixtures","duration-12s.mp4");
        check(Math.Abs(ContainerDurationReader.Read(fixture,CancellationToken.None)-12)<.05,"actual MP4 with movie header after media data uses direct reader");
        string mkv=Path.Combine(Environment.CurrentDirectory,"Fixtures","duration-12s.mkv");
        check(Math.Abs(ContainerDurationReader.Read(mkv,CancellationToken.None)-12)<.05,"actual MKV duration and timestamp scale use direct reader");
        string avi=Path.Combine(Environment.CurrentDirectory,"Fixtures","duration-12s.avi");
        check(Math.Abs(MediaDurationReader.Read(avi,CancellationToken.None)-12)<.05,"metadata-only MPV fallback reads AVI duration while paused");

        string library=Path.Combine(root,"resume");Directory.CreateDirectory(library);
        for(int i=0;i<4;i++)
        {
            File.Copy(fixture,Path.Combine(library,i+".mp4"));
            File.WriteAllText(Path.Combine(library,i+".funscript"),"{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":3000,\"pos\":100}],\"metadata\":{\"duration\":12}}");
        }
        string cachePath=Path.Combine(library,"cache.json");int probes=0;bool canceled=false;
        using(var cancel=new CancellationTokenSource())
        {
            try{PlaybackLibrary.Scan(new[]{library},cachePath,cancel.Token,durationProbe:(p,t)=>{if(++probes==3)cancel.Cancel();t.ThrowIfCancellationRequested();return 12;});}
            catch(OperationCanceledException){canceled=true;}
        }
        var partial=JsonConvert.DeserializeObject<LibraryCache>(File.ReadAllText(cachePath));
        check(canceled && partial.Media.Count==2 && partial.Files.Count==3,"cancel saves completed video durations and grades instead of discarding entire scan");
        probes=0;
        var resumed=PlaybackLibrary.Scan(new[]{library},cachePath,CancellationToken.None,durationProbe:(p,t)=>{probes++;return 12;});
        check(resumed.Videos.Count==4 && resumed.MediaCached==2 && resumed.Graded==1 && probes==2,"resumed scan processes only unfinished files");
        DateTime marker=DateTime.UtcNow.AddDays(-1);File.SetLastWriteTimeUtc(cachePath,marker);
        PlaybackLibrary.Scan(new[]{library},cachePath,CancellationToken.None,durationProbe:(p,t)=>throw new Exception("Unexpected probe"));
        check(File.GetLastWriteTimeUtc(cachePath)==marker,"unchanged scan does not rewrite the entire cache");

        var legacy=JsonConvert.DeserializeObject<LibraryCache>(File.ReadAllText(cachePath));
        legacy.Version=IntensityScoring.Version;legacy.Media.Clear();
        foreach(var value in legacy.Files.Values){value.Stats.Duration=3;value.Stats.ActiveAverage=123.456;}
        File.WriteAllText(cachePath,JsonConvert.SerializeObject(legacy));
        var migrated=PlaybackLibrary.Scan(new[]{library},cachePath,CancellationToken.None,durationProbe:(p,t)=>12);
        check(migrated.Cached==4 && migrated.Graded==0 && migrated.Videos.All(v=>v.Stats.Duration==12 && v.Stats.ActiveAverage==123.456),"legacy scoring cache migrates without regrading and still reads declared duration after actions");

        var unknownCache=JsonConvert.DeserializeObject<LibraryCache>(File.ReadAllText(cachePath));
        string first=unknownCache.Media.Keys.First();unknownCache.Media[first].Duration=0;unknownCache.Media[first].CheckedUtc=DateTime.UtcNow.AddMinutes(-3).Ticks;
        File.WriteAllText(cachePath,JsonConvert.SerializeObject(unknownCache));probes=0;
        PlaybackLibrary.Scan(new[]{library},cachePath,CancellationToken.None,durationProbe:(p,t)=>{probes++;return 12;});
        check(probes==1,"expired failure cache retries only the failed video");

        // Compare the previous per-file decode subprocess with the real new
        // metadata reader on identical independent files; no hardware involved.
        const int count=8;var watch=Stopwatch.StartNew();
        for(int i=0;i<count;i++)OldProbe(fixture);
        double oldMs=watch.Elapsed.TotalMilliseconds;
        for(int i=0;i<count;i++)File.Copy(fixture,Path.Combine(root,"bench-"+i+".mp4"));
        watch.Restart();
        for(int i=0;i<count;i++)check(Math.Abs(MediaDurationReader.Read(Path.Combine(root,"bench-"+i+".mp4"),CancellationToken.None)-12)<.05,"cold header benchmark duration "+i);
        double newMs=watch.Elapsed.TotalMilliseconds;
        string report=$"Identical synthetic 12-second MP4s: {count}\nPrevious MPV-per-file path: {oldMs:0.0} ms\nNew cold header path: {newMs:0.0} ms\nObserved ratio: {oldMs/Math.Max(.001,newMs):0.0}x\nNo machine motion or production application startup.\n";
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory,"scan-performance.txt"),report);Console.WriteLine(report);
    }
    static void OldProbe(string path)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.CurrentDirectory,"Bin","mpv.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"--no-config","--load-scripts=no","--ytdl=no","--idle=no","--vo=null","--ao=null","--frames=1","--no-audio","--terminal=yes","--msg-level=all=info","--term-playing-msg=JXO_DURATION=${=duration}","--",path})start.ArgumentList.Add(arg);
        using(var process=Process.Start(start))
        {
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(8000)){process.Kill(true);throw new Exception("Old probe timeout");}
            if(!output.GetAwaiter().GetResult().Contains("JXO_DURATION=12"))throw new Exception("Old probe failed");error.GetAwaiter().GetResult();
        }
    }
}
