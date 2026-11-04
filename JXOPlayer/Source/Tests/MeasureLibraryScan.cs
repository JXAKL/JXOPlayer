using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JXOPlayer.Features;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Standalone scanner: no UI, main application, player control or device code.
public static class MeasureLibraryScan
{
    public static int Main(string[] args)
    {
        try
        {
            string app=args[0],cache=args[1],report=args[2];
            var roots=JObject.Parse(File.ReadAllText(Path.Combine(app,"Data","RandomPlayback.settings.json")))["Folders"].Values<string>().ToArray();
            var measurements=new JArray();
            for(int run=1;run<=2;run++)
            {
                var timer=Stopwatch.StartNew();var next=TimeSpan.Zero;
                var result=PlaybackLibrary.Scan(roots,cache,CancellationToken.None,message=>
                {
                    if(timer.Elapsed>=next){Console.WriteLine($"Run {run}, {timer.Elapsed.TotalSeconds:0.0}s: {message}");Console.Out.Flush();next=timer.Elapsed+TimeSpan.FromSeconds(3);}
                });
                measurements.Add(new JObject{["Run"]=run,["Seconds"]=timer.Elapsed.TotalSeconds,["Videos"]=result.VideoCount,["Playable"]=result.Videos.Count,
                    ["DurationCached"]=result.MediaCached,["ScriptCached"]=result.Cached,["Graded"]=result.Graded,["UnknownDuration"]=result.DurationUnknown,
                    ["DurationMismatch"]=result.DurationMismatch,["Ambiguous"]=result.Ambiguous,["ReadErrors"]=result.ReadErrors,
                    ["AudioPlayable"]=result.Videos.Count(v=>new[]{".mp3",".wav",".flac",".m4a",".m4b",".aac",".ogg",".opus",".wma",".aif",".aiff",".ape"}.Contains(Path.GetExtension(v.VideoPath).ToLowerInvariant()))});
                File.WriteAllText(report,measurements.ToString(Formatting.Indented));Console.WriteLine(measurements.Last.ToString(Formatting.None));Console.Out.Flush();
            }
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
