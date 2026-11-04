namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using JXOPlayer.Common;
    using JXOPlayer.Script;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    // Runs inside native local repository lookup, before it picks the first name.
    // Keep all auxiliary axes with the chosen primary's folder/archive, never
    // fill missing axes with a different cut from another same-named folder.
    public sealed class LocalDurationMatcher : IDisposable
    {
        readonly Func<string,CancellationToken,double> probe;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly Func<string,string,List<ScriptReaderResult>,List<ScriptReaderResult>> filter;
        readonly FieldInfo hook;
        readonly FieldInfo pathHook;
        readonly Func<string,string> resolvePath=PlaybackLibrary.ResolveFilePath;
        readonly FieldInfo openHook;
        readonly Func<string,string> preparePath=PlaybackLibrary.PreparePlayerPath;
        readonly NLog.Logger log=NLog.LogManager.GetLogger("JXOPlayer.DurationMatching");
        public LocalDurationMatcher(Func<string,CancellationToken,double> probe=null)
        {
            this.probe=probe??MediaDurationReader.Read;
            filter=Filter;
            hook=typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Common.LocalScriptMatchHook",true).GetField("Filter");
            hook.SetValue(null,filter);
            pathHook=typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Common.MediaPathResolutionHook",true).GetField("Resolve");
            pathHook.SetValue(null,resolvePath);
            openHook=pathHook.DeclaringType.GetField("Prepare");openHook.SetValue(null,preparePath);
        }
        public List<ScriptReaderResult> Filter(string mediaName,string mediaSource,List<ScriptReaderResult> candidates)
        {
            if(lifetime.IsCancellationRequested)return new List<ScriptReaderResult>();
            try
            {
                string video=Path.Combine(mediaSource??"",mediaName??"");
                if(!File.Exists(video))return new List<ScriptReaderResult>();
                var archives=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var valid=candidates.Where(c=>c!=null && c.IsSuccess && !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.Source))
                    .GroupBy(c=>c.Source+"\0"+c.Name,StringComparer.OrdinalIgnoreCase).Select(g=>g.First())
                    .Where(c=>UniqueArchiveMember(c,archives)).ToArray();
                string basename=Path.GetFileNameWithoutExtension(mediaName);
                var primaries=new List<DurationCandidate>();
                var map=new Dictionary<string,ScriptReaderResult>();
                foreach(var candidate in valid)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    int priority=string.Equals(candidate.Name,basename+".funscript",StringComparison.OrdinalIgnoreCase)?0
                        :string.Equals(candidate.Name,basename+".stroke.funscript",StringComparison.OrdinalIgnoreCase)?1
                        :string.Equals(candidate.Name,basename+".L0.funscript",StringComparison.OrdinalIgnoreCase)?2:-1;
                    if(priority<0)continue;
                    var resource=candidate.IsMultiAxis ? candidate.Resources.GetValueOrDefault(DeviceAxis.Parse("L0")) : candidate.Resource;
                    if(resource?.Keyframes==null || resource.Keyframes.Count<2)continue;
                    double last=resource.Keyframes.Last().Position;
                    if(!DurationMatching.Known(last))continue;
                    double duration=ReadDuration(candidate,last);
                    string key=candidate.Source+"\0"+candidate.Name;
                    map[key]=candidate;primaries.Add(new DurationCandidate{Key=key,Family=candidate.Source,Priority=priority,Duration=duration,
                        SameDirectory=PlaybackLibrary.SamePath(candidate.Source.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(candidate.Source):candidate.Source,mediaSource),
                        Identity=()=>MotionIdentity.Compute(resource.Keyframes.Select(p=>new KeyValuePair<double,double>(p.Position,p.Value)))});
                }
                double videoDuration=primaries.Count>0 ? probe(video,lifetime.Token) : 0;
                var chosen=DurationMatching.Select(videoDuration,primaries,out var status);
                if(status==DurationMatchStatus.Unique && candidates.Any(c=>c!=null && c.Source!=null && c.Source.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(c.Name,basename+".funscript",StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name,basename+".L0.funscript",StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name,basename+".stroke.funscript",StringComparison.OrdinalIgnoreCase))
                    && !UniqueArchiveMember(c,archives)))
                {chosen=null;status=DurationMatchStatus.Ambiguous;}
                log.Info("Duration pairing [{0}, Video: \"{1}\", Duration: {2:0.000}s, Candidates: {3}, Script: \"{4}\"]",status,video,videoDuration,primaries.Count,chosen?.Key?.Replace('\0','/'));
                if(chosen==null)return new List<ScriptReaderResult>();
                var primary=map[chosen.Key];
                var primaryKeys=new HashSet<string>(primaries.Select(c=>c.Key),StringComparer.OrdinalIgnoreCase);
                return new[]{primary}.Concat(valid.Where(c=>string.Equals(c.Source,primary.Source,StringComparison.OrdinalIgnoreCase)
                    && !primaryKeys.Contains(c.Source+"\0"+c.Name))).ToList();
            }
            catch(OperationCanceledException){return new List<ScriptReaderResult>();}
            catch(Exception ex) { log.Warn(ex,"Duration pairing failed; no local script selected");return new List<ScriptReaderResult>(); }
        }
        static bool UniqueArchiveMember(ScriptReaderResult candidate,Dictionary<string,HashSet<string>> archives)
        {
            if(!candidate.Source.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))return true;
            // Native resources retain only the entry basename. Different zip
            // subfolders can therefore look identical; never choose one blindly.
            try
            {
                if(!archives.TryGetValue(candidate.Source,out var unique))
                {
                    using(var stream=File.OpenRead(candidate.Source))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                        archives[candidate.Source]=unique=new HashSet<string>(zip.Entries.GroupBy(e=>e.Name,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).Select(g=>g.Key),StringComparer.OrdinalIgnoreCase);
                }
                return unique.Contains(candidate.Name);
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }
        double ReadDuration(ScriptReaderResult candidate,double fallback)
        {
            try
            {
                if(candidate.Source.EndsWith(".zip",StringComparison.OrdinalIgnoreCase) && File.Exists(candidate.Source))
                {
                    using(var stream=File.OpenRead(candidate.Source))
                    using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                    {
                        var entry=zip.Entries.FirstOrDefault(e=>string.Equals(e.Name,candidate.Name,StringComparison.OrdinalIgnoreCase));
                        if(entry==null)return fallback;
                        using(var content=entry.Open())using(var reader=new StreamReader(content))
                            return DurationMatching.ReadScriptDuration(reader,fallback,lifetime.Token);
                    }
                }
                string path=Path.Combine(candidate.Source,candidate.Name);
                if(File.Exists(path))using(var reader=new StreamReader(path))
                    return DurationMatching.ReadScriptDuration(reader,fallback,lifetime.Token);
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { }
            return fallback;
        }
        public void Dispose()
        {
            lifetime.Cancel();
            if(ReferenceEquals(hook.GetValue(null),filter))hook.SetValue(null,null);
            if(ReferenceEquals(pathHook.GetValue(null),resolvePath))pathHook.SetValue(null,null);
            if(ReferenceEquals(openHook.GetValue(null),preparePath))openHook.SetValue(null,null);
        }
    }
}
