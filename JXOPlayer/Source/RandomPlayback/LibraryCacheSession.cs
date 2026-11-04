namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using Newtonsoft.Json;

    // Serialize scans of the same cache. A replacement scan waits for the old
    // one's cancellation checkpoint rather than overwriting newer results.
    public sealed class LibraryCacheSession : IDisposable
    {
        static readonly ConcurrentDictionary<string,SemaphoreSlim> gates=new ConcurrentDictionary<string,SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        readonly SemaphoreSlim gate;
        readonly string path;
        readonly Action failed;
        readonly System.Diagnostics.Stopwatch checkpoint=System.Diagnostics.Stopwatch.StartNew();
        bool dirty;
        public readonly LibraryCache Cache;
        public LibraryCacheSession(string path,CancellationToken token,Action failed)
        {
            this.path=Path.GetFullPath(path);this.failed=failed;
            gate=gates.GetOrAdd(this.path,_=>new SemaphoreSlim(1,1));gate.Wait(token);
            try
            {
                LibraryCache cache=null;
                try
                {
                    using(var reader=File.OpenText(this.path))using(var json=new JsonTextReader(reader))
                        cache=new JsonSerializer().Deserialize<LibraryCache>(json);
                }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is JsonException){ }
                if(cache?.Version!=IntensityScoring.Version && cache?.Version!=IntensityScoring.Version+"/"+DurationMatching.Version)cache=null;
                cache=cache??new LibraryCache();
                cache.Files=new Dictionary<string,CachedMotion>(cache.Files??new Dictionary<string,CachedMotion>(),StringComparer.OrdinalIgnoreCase);
                cache.Media=new Dictionary<string,CachedMediaDuration>(cache.Media??new Dictionary<string,CachedMediaDuration>(),StringComparer.OrdinalIgnoreCase);
                if(cache.Version==IntensityScoring.Version)
                {
                    // Scoring did not change. Only metadata duration is new.
                    foreach(var value in cache.Files.Values)if(value!=null)value.NeedsDurationRefresh=true;
                    cache.Version=IntensityScoring.Version+"/"+DurationMatching.Version;dirty=true;
                }
                Cache=cache;
            }
            catch {gate.Release();throw;}
        }
        public void Changed(){dirty=true;if(checkpoint.Elapsed.TotalSeconds>=5)Save();}
        void Save()
        {
            if(!dirty)return;
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using(var writer=File.CreateText(temp))using(var json=new JsonTextWriter(writer))new JsonSerializer().Serialize(json,Cache);
                File.Move(temp,path,true);dirty=false;
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException){failed?.Invoke();}
            finally
            {
                checkpoint.Restart();
                try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
        public void Dispose(){try{Save();}finally{gate.Release();}}
    }
}
