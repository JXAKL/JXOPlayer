namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public sealed class MotionStats
    {
        public double ActiveAverage, Maximum, ActiveDuration, Duration, Distance, AbsoluteScore, Score;
        public int Tier;
        public string Error;
        public string Identity;
        public bool Eligible => Error == null && ActiveDuration > 0 && Distance > 0;
    }
    public static class IntensityScoring
    {
        public const string Version = "motion-v3-batch/active-v2-200-600-gap10";
        public static MotionStats Read(string path)
        {
            using(var reader = new StreamReader(path)) using(var json = new JsonTextReader(reader) { MaxDepth = 64 })
                return Grade(JObject.Load(json));
        }
        public static MotionStats Grade(JObject data)
        {
            var actions = data["actions"] as JArray;
            if(actions == null || actions.Count < 2) throw new InvalidDataException("动作点不足");
            var points = new SortedDictionary<double,double>();
            foreach(var action in actions)
            {
                var at = action is JObject ? action["at"] : null;
                var pos = action is JObject ? action["pos"] : null;
                bool Number(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);
                if(!Number(at) || !Number(pos)) throw new InvalidDataException("动作时间/位置无效");
                double time = (double)at, value = (double)pos;
                if(!double.IsFinite(time) || !double.IsFinite(value) || time < 0 || value < 0 || value > 100)
                    throw new InvalidDataException("动作时间/位置超出范围");
                if(points.TryGetValue(time,out var existing) && existing != value) throw new InvalidDataException("同一时间有不同位置");
                points[time]=value;
            }
            if(points.Count < 2 || points.Last().Key <= 0) throw new InvalidDataException("有效时间点不足");
            var stats=new MotionStats { Duration=DurationMatching.ScriptDuration(data,points.Last().Key / 1000),Identity=MotionIdentity.Compute(points.Select(p=>new KeyValuePair<double,double>(p.Key/1000,p.Value/100))) };
            double activeDistance=0; KeyValuePair<double,double>? previous=null;
            foreach(var point in points)
            {
                if(previous.HasValue)
                {
                    double delta=Math.Abs(point.Value-previous.Value.Value), seconds=(point.Key-previous.Value.Key)/1000;
                    stats.Distance+=delta;
                    stats.Maximum=Math.Max(stats.Maximum,delta/seconds);
                    if(delta > 0 && seconds <= 10) { activeDistance+=delta; stats.ActiveDuration+=seconds; }
                }
                previous=point;
            }
            stats.ActiveAverage=stats.ActiveDuration > 0 ? activeDistance/stats.ActiveDuration : 0;
            stats.Score=stats.AbsoluteScore=80*Math.Min(stats.ActiveAverage/200,1)+20*Math.Min(stats.Maximum/600,1);
            stats.Tier=stats.Eligible ? Tier(stats.Score) : 0;
            if(stats.Distance > 0 && stats.ActiveDuration == 0) stats.Error="移动段全部超过 10 秒间隔";
            return stats;
        }
        public static int Tier(double score) => Math.Min(5,Math.Max(1,(int)Math.Floor(score/20)+1));
        static double Percentile(double value,double[] sorted)
        {
            value=Math.Round(value,9,MidpointRounding.ToEven);
            int lo=0,hi=sorted.Length;
            while(lo<hi){int mid=(lo+hi)/2;if(sorted[mid]<value)lo=mid+1;else hi=mid;}
            int first=lo; hi=sorted.Length;
            while(lo<hi){int mid=(lo+hi)/2;if(sorted[mid]<=value)lo=mid+1;else hi=mid;}
            double rank=(first+lo-1)/2.0;
            return sorted.Length<2 ? 50 : 100*rank/(sorted.Length-1);
        }
        public static int Apply(IEnumerable<LibraryVideo> videos,bool relative)
        {
            // A script gets one vote even when several videos share it.
            var unique=videos.GroupBy(v=>v.ScriptPath,StringComparer.OrdinalIgnoreCase).Select(g=>g.First().Stats).Where(s=>s.Eligible).ToArray();
            var averages=unique.Select(s=>Math.Round(s.ActiveAverage,9,MidpointRounding.ToEven)).OrderBy(x=>x).ToArray();
            var peaks=unique.Select(s=>Math.Round(s.Maximum,9,MidpointRounding.ToEven)).OrderBy(x=>x).ToArray();
            foreach(var video in videos)
            {
                var s=video.Stats;
                s.Score=relative && unique.Length>=2 ? .8*Percentile(s.ActiveAverage,averages)+.2*Percentile(s.Maximum,peaks) : s.AbsoluteScore;
                s.Tier=s.Eligible ? Tier(s.Score) : 0;
            }
            return unique.Length;
        }
    }
    public sealed class LibraryVideo
    {
        public string VideoPath, ScriptPath;
        public MotionStats Stats;
        public long ScriptLength, ScriptModified;
        public double VideoDuration, DurationDifference;
        public long VideoLength, VideoModified;
    }
    public sealed class LibrarySnapshot
    {
        public List<LibraryVideo> Videos=new List<LibraryVideo>();
        public int VideoCount, Unmatched, Ambiguous, Invalid, Static, ReadErrors, Cached, Graded, ScriptCount;
        public int DurationMismatch, DurationUnknown, MediaCached;
        public string Summary => $"可播放 {Videos.Count} 部 · 未配对 {Unmatched} · 时长不符 {DurationMismatch} · 时长未知 {DurationUnknown} · 歧义 {Ambiguous} · 无效/静止 {Invalid+Static} · 读取失败 {ReadErrors}";
    }
    public sealed class CachedMotion
    {
        public long Length, Modified;
        public MotionStats Stats;
        public bool NeedsDurationRefresh;
    }
    public sealed class LibraryCache
    {
        public string Version=IntensityScoring.Version+"/"+DurationMatching.Version;
        public Dictionary<string,CachedMotion> Files=new Dictionary<string,CachedMotion>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,CachedMediaDuration> Media=new Dictionary<string,CachedMediaDuration>(StringComparer.OrdinalIgnoreCase);
    }
    public static class PlaybackLibrary
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
        static extern uint GetShortPathName(string path,System.Text.StringBuilder output,uint capacity);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
        static extern uint GetLongPathName(string path,System.Text.StringBuilder output,uint capacity);
        public static string ResolveFilePath(string path)
        {
            path=LocalMediaRelay.Resolve(path);
            if(string.IsNullOrEmpty(path) || path.IndexOf('~')<0 || !Path.IsPathFullyQualified(path))return path;
            var buffer=new System.Text.StringBuilder(32768);
            uint length=GetLongPathName(path,buffer,(uint)buffer.Capacity);
            return length>0 && length<buffer.Capacity?buffer.ToString():path;
        }
        static readonly HashSet<string> VideoExtensions=new HashSet<string>(new[]{".mp4",".mkv",".avi",".mov",".wmv",".webm",".m4v",".flv",".ts",".mts",".m2ts",".mpg",".mpeg"},StringComparer.OrdinalIgnoreCase);
        public static bool IsVideo(string path)=>!string.IsNullOrEmpty(path) && VideoExtensions.Contains(Path.GetExtension(path));
        // Comparison only: preserve the original path for all file operations and
        // player messages. Extended Windows paths can be reported without their prefix.
        public static string PathIdentity(string path)
        {
            if(string.IsNullOrEmpty(path))return null;
            try
            {
                string full=Path.GetFullPath(ResolveFilePath(path));
                if(full.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))return @"\\"+full.Substring(8);
                if(full.StartsWith(@"\\?\",StringComparison.Ordinal) && full.Length>6 && full[5]==':')return full.Substring(4);
                return full;
            }
            catch(Exception ex) when(ex is ArgumentException || ex is NotSupportedException || ex is IOException){return null;}
        }
        public static bool SamePath(string a,string b)
        {
            string left=PathIdentity(a),right=PathIdentity(b);
            return left!=null && right!=null && string.Equals(left,right,StringComparison.OrdinalIgnoreCase);
        }
        public static string PlayerPath(string path)
        {
            string full=PathIdentity(path)??throw new ArgumentException("无效的媒体路径",nameof(path));
            if(full.Length<260)return full;
            string extended=full.StartsWith(@"\\",StringComparison.Ordinal)?@"\\?\UNC\"+full.Substring(2):@"\\?\"+full;
            var buffer=new System.Text.StringBuilder(32768);
            uint length=GetShortPathName(extended,buffer,(uint)buffer.Capacity);
            if(length>0 && length<buffer.Capacity)
            {
                string shortened=buffer.ToString();
                if(shortened.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))shortened=@"\\"+shortened.Substring(8);
                else if(shortened.StartsWith(@"\\?\",StringComparison.Ordinal))shortened=shortened.Substring(4);
                if(shortened.Length<260)return shortened;
            }
            return LocalMediaRelay.Register(full);
        }
        public static string PreparePlayerPath(string path)=>!string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && IsVideo(path) && File.Exists(path)?PlayerPath(path):path;
        public static string[] NormalizeRoots(IEnumerable<string> roots)
        {
            var unique=roots.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(x))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x.Length).ToArray();
            return unique.Where(path=>!unique.Any(parent=>parent.Length<path.Length && path.StartsWith(Path.EndsInDirectorySeparator(parent)?parent:parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        static string ScriptKey(string path)
        {
            string name=Path.GetFileNameWithoutExtension(path);
            foreach(var suffix in new[]{".stroke",".L0"}) if(name.EndsWith(suffix,StringComparison.OrdinalIgnoreCase)) return name.Substring(0,name.Length-suffix.Length);
            foreach(var suffix in new[]{".L1",".L2",".R0",".R1",".R2",".V0",".A0",".A1",".A2",".roll",".pitch",".twist",".surge",".sway",".vib",".suck",".valve"})
                if(name.EndsWith(suffix,StringComparison.OrdinalIgnoreCase)) return null;
            return name;
        }
        public static LibrarySnapshot Scan(IEnumerable<string> roots,string cachePath,CancellationToken token,Action<string> progress=null,Func<string,CancellationToken,double> durationProbe=null)
        {
            var result=new LibrarySnapshot();
            using var session=new LibraryCacheSession(cachePath,token,()=>result.ReadErrors++);
            var cache=session.Cache;
            if(durationProbe==null)foreach(var item in cache.Media)if(item.Value!=null)MediaDurationReader.Remember(item.Key,item.Value);
            durationProbe=durationProbe??MediaDurationReader.Read;
            var videoPaths=new Dictionary<string,FileInfo>(StringComparer.OrdinalIgnoreCase);
            var scriptFiles=new Dictionary<string,FileInfo>(StringComparer.OrdinalIgnoreCase);
            var scripts=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
            var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var pending=new Stack<string>(NormalizeRoots(roots));
            var clock=System.Diagnostics.Stopwatch.StartNew();
            while(pending.Count>0)
            {
                token.ThrowIfCancellationRequested();string dir=pending.Pop();if(!visited.Add(dir))continue;
                try
                {
                    // Enumeration already carries length/time/attributes on
                    // Windows. Avoid a separate metadata request for every file.
                    foreach(var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            string path=entry.FullName;var attr=entry.Attributes;
                            if((attr&FileAttributes.ReparsePoint)!=0)continue;
                            if((attr&FileAttributes.Directory)!=0){pending.Push(path);continue;}
                            if(VideoExtensions.Contains(entry.Extension))videoPaths[path]=(FileInfo)entry;
                            else if(string.Equals(Path.GetExtension(path),".funscript",StringComparison.OrdinalIgnoreCase))
                            {
                                var key=ScriptKey(path);if(key==null)continue;
                                if(!scripts.TryGetValue(key,out var list))scripts[key]=list=new List<string>();list.Add(path);scriptFiles[path]=(FileInfo)entry;
                            }
                        } catch(IOException){result.ReadErrors++;} catch(UnauthorizedAccessException){result.ReadErrors++;}
                    }
                }catch(IOException){result.ReadErrors++;}catch(UnauthorizedAccessException){result.ReadErrors++;}
                if(clock.ElapsedMilliseconds>=250){progress?.Invoke($"扫描中：{videoPaths.Count} 个视频，{scripts.Count} 组脚本");clock.Restart();}
            }
            result.VideoCount=videoPaths.Count;
            var computed=new Dictionary<string,MotionStats>(StringComparer.OrdinalIgnoreCase);
            MotionStats StatsFor(string script)
            {
                if(!computed.TryGetValue(script,out var stats))
                {
                    FileInfo info=scriptFiles[script];
                    try
                    {
                        if(cache.Files.TryGetValue(script,out var stored) && stored!=null && stored.Length==info.Length && stored.Modified==info.LastWriteTimeUtc.Ticks && stored.Stats!=null)
                        {
                            stats=stored.Stats;
                            if(stored.NeedsDurationRefresh)
                            {
                                using(var reader=File.OpenText(script))stats.Duration=DurationMatching.ReadScriptDuration(reader,stats.Duration,token);
                                stored.NeedsDurationRefresh=false;session.Changed();
                            }
                            result.Cached++;
                        }
                        else{stats=IntensityScoring.Read(script);result.Graded++;cache.Files[script]=new CachedMotion{Length=info.Length,Modified=info.LastWriteTimeUtc.Ticks,Stats=stats};session.Changed();}
                    // InvalidDataException derives from SystemException, not IOException.
                    // Bad action data must exclude this script, not abort the whole library.
                    }catch(Exception ex) when(ex is InvalidDataException || ex is JsonException || ex is ArgumentException || ex is OverflowException)
                    {stats=new MotionStats{Error=ex.Message};cache.Files[script]=new CachedMotion{Length=info.Length,Modified=info.LastWriteTimeUtc.Ticks,Stats=stats};session.Changed();}
                    catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException){stats=new MotionStats{Error=ex.Message};}
                    computed[script]=stats;
                }
                return stats;
            }
            int processed=0;
            string IdentityFor(string path)
            {
                var stats=StatsFor(path);
                if(stats.Identity==null)
                {
                    try{stats.Identity=IntensityScoring.Read(path).Identity;session.Changed();}
                    catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidDataException){return null;}
                }
                return stats.Identity;
            }
            foreach(var video in videoPaths.Keys.OrderBy(p=>p,StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();processed++;
                if(clock.ElapsedMilliseconds>=250){progress?.Invoke($"配对 {processed}/{videoPaths.Count} · 可播放 {result.Videos.Count} · 时长缓存 {result.MediaCached} · 脚本缓存 {result.Cached}");clock.Restart();}
                scripts.TryGetValue(Path.GetFileNameWithoutExtension(video),out var candidates);
                if(candidates==null || candidates.Count==0){result.Unmatched++;continue;}
                var eligible=new List<DurationCandidate>();
                foreach(string candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();var candidateStats=StatsFor(candidate);
                    if(candidateStats.Eligible)eligible.Add(new DurationCandidate{Key=candidate,Family=Path.GetDirectoryName(candidate),Duration=candidateStats.Duration,
                        SameDirectory=SamePath(Path.GetDirectoryName(candidate),Path.GetDirectoryName(video)),Identity=()=>IdentityFor(candidate),
                        Priority=string.Equals(Path.GetFileNameWithoutExtension(candidate),Path.GetFileNameWithoutExtension(video),StringComparison.OrdinalIgnoreCase)?0:candidate.EndsWith(".stroke.funscript",StringComparison.OrdinalIgnoreCase)?1:2});
                }
                if(eligible.Count==0){if(candidates.Any(p=>StatsFor(p).Error!=null))result.Invalid++;else result.Static++;continue;}
                double videoDuration=0;FileInfo videoInfo;
                try
                {
                    videoInfo=videoPaths[video];
                    if(cache.Media.TryGetValue(video,out var media) && media!=null && media.Valid(videoInfo))
                    {videoDuration=media.Duration;result.MediaCached++;}
                    else
                    {
                        videoDuration=durationProbe(video,token);
                        token.ThrowIfCancellationRequested();
                        cache.Media[video]=new CachedMediaDuration{Length=videoInfo.Length,Modified=videoInfo.LastWriteTimeUtc.Ticks,Duration=videoDuration,CheckedUtc=DateTime.UtcNow.Ticks};
                        session.Changed();
                    }
                }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException){result.ReadErrors++;result.DurationUnknown++;continue;}
                var selected=DurationMatching.Select(videoDuration,eligible,out var matchStatus);
                if(selected==null)
                {
                    if(matchStatus==DurationMatchStatus.Ambiguous)result.Ambiguous++;
                    else if(matchStatus==DurationMatchStatus.Mismatch)result.DurationMismatch++;
                    else result.DurationUnknown++;
                    continue;
                }
                string script=selected.Key;var stats=StatsFor(script);
                var stamp=new FileInfo(script);
                if(!cache.Files.TryGetValue(script,out var fingerprint) || stamp.Length!=fingerprint.Length || stamp.LastWriteTimeUtc.Ticks!=fingerprint.Modified)
                {result.ReadErrors++;continue;}
                result.Videos.Add(new LibraryVideo{VideoPath=video,ScriptPath=script,Stats=stats,ScriptLength=stamp.Length,ScriptModified=stamp.LastWriteTimeUtc.Ticks,
                    VideoDuration=videoDuration,DurationDifference=Math.Abs(videoDuration-stats.Duration),VideoLength=videoInfo.Length,VideoModified=videoInfo.LastWriteTimeUtc.Ticks});
            }
            token.ThrowIfCancellationRequested(); result.ScriptCount=computed.Count;
            return result;
        }
    }
    public sealed class ShuffleSelection
    {
        readonly Random random=new Random();
        readonly Dictionary<int,HashSet<string>> played=new Dictionary<int,HashSet<string>>();
        public void Reset()=>played.Clear();
        public LibraryVideo Pick(IEnumerable<LibraryVideo> videos,int tier,string current)
        {
            var available=videos.Where(v=>v.Stats.Tier==tier && File.Exists(v.VideoPath) && File.Exists(v.ScriptPath)).ToList();
            if(available.Count==0)return null;
            if(!played.TryGetValue(tier,out var used))played[tier]=used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var next=available.Where(v=>!used.Contains(v.VideoPath) && !PlaybackLibrary.SamePath(v.VideoPath,current)).ToList();
            if(next.Count==0){used.Clear();next=available.Where(v=>!PlaybackLibrary.SamePath(v.VideoPath,current)).ToList();}
            if(next.Count==0)next=available;
            return next[random.Next(next.Count)];
        }
        public void Commit(LibraryVideo video){if(!played.TryGetValue(video.Stats.Tier,out var used))played[video.Stats.Tier]=used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);used.Add(video.VideoPath);}
    }
}
