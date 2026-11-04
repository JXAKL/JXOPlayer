namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public enum DurationMatchStatus { Matched, Missing, Unknown, Mismatch, Ambiguous, Unique, Equivalent, Local }
    public sealed class DurationCandidate
    {
        public string Key, Family;
        public double Duration;
        public int Priority;
        public bool SameDirectory;
        public Func<string> Identity;
    }
    public static class DurationMatching
    {
        // Cache metadata schema; selection decisions are recalculated on scan.
        public const string Version = "duration-pair-v1";
        public static bool Known(double seconds) => double.IsFinite(seconds) && seconds > 0;
        // Closeness is evidence for resolving collisions, not a validity rule:
        // the final action is NOT necessarily the duration of the media.
        public static double Tolerance(double seconds) => Math.Max(3, Math.Min(30, seconds * .02));
        public static double TieTolerance(double seconds) => Math.Max(.5, Math.Min(2, seconds * .001));
        public static DurationCandidate Select(double video, IEnumerable<DurationCandidate> source, out DurationMatchStatus status)
        {
            var all = source.ToArray();
            if(all.Length == 0) { status=DurationMatchStatus.Missing; return null; }
            var families=new List<DurationCandidate>();
            foreach(var group in all.GroupBy(c=>c.Family,StringComparer.OrdinalIgnoreCase))
            {
                var members=group.ToArray();
                if(Known(video) && members.Any(c=>Known(c.Duration)))
                {
                    double closest=members.Where(c=>Known(c.Duration)).Min(c=>Math.Abs(c.Duration-video));
                    members=members.Where(c=>Known(c.Duration) && Math.Abs(c.Duration-video)<=closest+TieTolerance(video)).ToArray();
                }
                int priority=members.Min(c=>c.Priority);
                var primary=ResolveTie(members.Where(c=>c.Priority==priority).ToArray(),out status);
                if(primary==null)return null;
                families.Add(primary);
            }
            if(families.Count==1)
            {
                var only=families[0];status=Known(video) && Math.Abs(only.Duration-video)<=Tolerance(video)?DurationMatchStatus.Matched:DurationMatchStatus.Unique;
                return only;
            }
            var ranked=families.ToArray();
            if(Known(video) && ranked.Any(c=>Known(c.Duration)))
            {
                double closest=ranked.Where(c=>Known(c.Duration)).Min(c=>Math.Abs(c.Duration-video));
                ranked=ranked.Where(c=>Known(c.Duration) && Math.Abs(c.Duration-video)<=closest+TieTolerance(video)).ToArray();
            }
            return ResolveTie(ranked,out status);
        }
        static DurationCandidate ResolveTie(DurationCandidate[] candidates,out DurationMatchStatus status)
        {
            if(candidates.Length==1){status=DurationMatchStatus.Matched;return candidates[0];}
            var local=candidates.Where(c=>c.SameDirectory).ToArray();
            if(local.Length==1){status=DurationMatchStatus.Local;return local[0];}
            string identity=null;
            foreach(var candidate in candidates)
            {
                string value=candidate.Identity?.Invoke();
                if(string.IsNullOrEmpty(value) || (identity!=null && value!=identity)){status=DurationMatchStatus.Ambiguous;return null;}
                identity=value;
            }
            // Identical motions in duplicate folders are interchangeable; keep
            // the selected source's auxiliary axes instead of mixing folders.
            status=DurationMatchStatus.Equivalent;
            return candidates.OrderBy(c=>c.Key,StringComparer.OrdinalIgnoreCase).First();
        }
        public static double ScriptDuration(JObject data, double lastAction)
        {
            var value=(data["metadata"] as JObject)?["duration"];
            double declared=0;
            if(value!=null && !double.TryParse(value.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out declared)
                && TimeSpan.TryParse(value.ToString(),CultureInfo.InvariantCulture,out var span)) declared=span.TotalSeconds;
            return Known(declared) && declared>=lastAction ? declared : lastAction;
        }
        public static double ReadScriptDuration(TextReader reader,double lastAction,CancellationToken token)
        {
            // Skip action tokens without constructing a second action tree or
            // re-running the strength calculation when upgrading an old cache.
            using(var json=new JsonTextReader(reader){MaxDepth=64,CloseInput=false})
            {
                int count=0;double duration=lastAction;
                while(json.Read())
                {
                    if((++count&255)==0)token.ThrowIfCancellationRequested();
                    if(json.TokenType==JsonToken.PropertyName && json.Depth==1 && (string)json.Value=="metadata")
                    {
                        if(json.Read() && json.TokenType==JsonToken.StartObject)
                            duration=ScriptDuration(new JObject{["metadata"]=JObject.Load(json)},lastAction);
                    }
                }
                token.ThrowIfCancellationRequested();return duration;
            }
        }
    }

    public sealed class CachedMediaDuration
    {
        public long Length, Modified;
        public double Duration;
        public long CheckedUtc;
        // Failures are briefly cached too; a changed file always retries at once.
        public bool Valid(FileInfo info) => Length==info.Length && Modified==info.LastWriteTimeUtc.Ticks
            && (DurationMatching.Known(Duration) || (CheckedUtc>0 && DateTime.UtcNow.Ticks-CheckedUtc<TimeSpan.FromMinutes(2).Ticks));
    }
    public static class MediaDurationReader
    {
        static readonly ConcurrentDictionary<string,CachedMediaDuration> memory=new ConcurrentDictionary<string,CachedMediaDuration>(StringComparer.OrdinalIgnoreCase);
        static readonly SemaphoreSlim gate=new SemaphoreSlim(2,2);
        static readonly SemaphoreSlim[] paths=Enumerable.Range(0,64).Select(_=>new SemaphoreSlim(1,1)).ToArray();
        public static void Remember(string path,CachedMediaDuration cached) => memory[path]=cached;
        public static double Read(string path,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var info=new FileInfo(path);
            if(!info.Exists)return 0;
            if(memory.TryGetValue(info.FullName,out var cached) && cached.Valid(info))return cached.Duration;
            var pathGate=paths[StringComparer.OrdinalIgnoreCase.GetHashCode(info.FullName)&63];
            pathGate.Wait(token);
            try
            {
                // Another lookup may have populated it while this call waited.
                if(memory.TryGetValue(info.FullName,out cached) && cached.Valid(info))return cached.Duration;
                double duration=ContainerDurationReader.Read(info.FullName,token);
                if(!DurationMatching.Known(duration))
                {
                    gate.Wait(token);
                    try{duration=Probe(info.FullName,token);}finally{gate.Release();}
                }
                token.ThrowIfCancellationRequested();
                var after=new FileInfo(info.FullName);
                if(!after.Exists || after.Length!=info.Length || after.LastWriteTimeUtc.Ticks!=info.LastWriteTimeUtc.Ticks)return 0;
                memory[info.FullName]=new CachedMediaDuration{Length=info.Length,Modified=info.LastWriteTimeUtc.Ticks,Duration=duration,CheckedUtc=DateTime.UtcNow.Ticks};
                return duration;
            }
            finally { pathGate.Release(); }
        }
        static double Probe(string path,CancellationToken token)
        {
            string root=Path.GetDirectoryName(Environment.ProcessPath),tool=Path.Combine(root,"Bin","mpv.exe");
            if(!File.Exists(tool))return 0;
            try
            {
                string lua=Path.Combine(root,"Bin","DurationProbe.lua");
                if(!File.Exists(lua))return 0;
                var start=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
                // Metadata-only fallback for other containers. Quit at file-loaded
                // with playback paused, without decoding a frame or opening a UI.
                foreach(string arg in new[]{"--no-config","--load-scripts=no","--script="+lua,"--ytdl=no","--idle=no","--vo=null","--ao=null","--pause=yes","--terminal=yes","--msg-level=all=warn","--",path})start.ArgumentList.Add(arg);
                using(var process=Process.Start(start))
                {
                    var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
                    var clock=Stopwatch.StartNew();
                    try
                    {
                        while(!process.WaitForExit(50))
                        {
                            token.ThrowIfCancellationRequested();
                            if(clock.Elapsed.TotalSeconds>=8)return 0;
                        }
                        token.ThrowIfCancellationRequested();
                        string text=output.GetAwaiter().GetResult(); error.GetAwaiter().GetResult();
                        foreach(string line in text.Split('\n'))
                            if(line.Trim().StartsWith("JXO_DURATION=",StringComparison.Ordinal) && double.TryParse(line.Trim().Substring(13),NumberStyles.Float,CultureInfo.InvariantCulture,out var seconds) && DurationMatching.Known(seconds))
                                return seconds;
                    }
                    finally { if(!process.HasExited) { process.Kill(true); process.WaitForExit(1000); } }
                }
            }
            catch(OperationCanceledException){throw;}
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { }
            return 0;
        }
    }
}
