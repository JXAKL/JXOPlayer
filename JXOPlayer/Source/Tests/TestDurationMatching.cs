using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using JXOPlayer.Common;
using JXOPlayer.Features;
using JXOPlayer.Script;
using Newtonsoft.Json.Linq;
using Stylet;

public static class TestDurationMatching
{
    public static void TestFileNames(string parent,Action<bool,string> check)
    {
        string root=Path.Combine(parent,"filename-cases"),folder=Path.Combine(root,new string('深',90),new string('階',90));
        Directory.CreateDirectory(folder);
        string[] names={"中文_日本語_English_😀👩‍💻❤️", "[字幕](完整版) 100% #1 & + ; ' $` %23 %20",new string('長',220)+"日本語😀", "café_が"};
        var assembly=typeof(DeviceAxis).Assembly;
        var repositoryType=assembly.GetType("JXOPlayer.Script.Repository.ViewModels.LocalScriptRepository",true);
        var repository=Activator.CreateInstance(repositoryType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{new EventAggregator()},null);
        var builderType=assembly.GetType("JXOPlayer.MediaSource.MediaResource.MediaResourceInfoBuilder",true);
        var failures=new List<string>();
        void Verify(bool ok,string label){if(!ok){failures.Add(label);Console.WriteLine("FAIL "+label);}else check(true,label);}
        check(PlaybackLibrary.SamePath(@"C:\目录\😀%23.mp4",@"\\?\C:\目录\😀%23.mp4"),"extended drive prefix does not break playback acknowledgement");
        check(PlaybackLibrary.SamePath(@"\\server\share\日本語.mp4",@"\\?\UNC\server\share\日本語.mp4"),"extended UNC prefix identifies the same network media");
        check(!PlaybackLibrary.SamePath(@"C:\a%23.mp4",@"C:\a#.mp4") && !PlaybackLibrary.SamePath("bad\0path","bad\0path"),"path identity neither decodes literal percent sequences nor throws on malformed paths");
        var uriBuilder=Activator.CreateInstance(builderType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{"file:///C:/%E7%9B%AE%E5%BD%95/a%2520%23.mp4"},null);
        var uriResource=builderType.GetMethod("Build",Type.EmptyTypes).Invoke(uriBuilder,null);
        check((string)uriResource.GetType().GetProperty("Name").GetValue(uriResource)=="a%20#.mp4","explicit file URI still decodes exactly once");
        using(var matcher=new LocalDurationMatcher())
        {
            foreach(string name in names)
            {
                string video=Video(folder,name+".mp4");string scriptPath=WriteScript(folder,name+".funscript",12);
                var builder=Activator.CreateInstance(builderType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{video},null);
                var resource=builderType.GetMethod("Build",Type.EmptyTypes).Invoke(builder,null);
                string source=(string)resource?.GetType().GetProperty("Source").GetValue(resource);
                string mediaName=(string)resource?.GetType().GetProperty("Name").GetValue(resource);
                Verify(mediaName==Path.GetFileName(video) && source==folder,"native path parser preserves literal Unicode/punctuation, length "+video.Length);
                var loaded=FunscriptReader.Default.FromPath(scriptPath);
                Verify(loaded.IsSuccess && loaded.Resource!=null,"native script reader opens long Unicode path, length "+scriptPath.Length);
                try
                {
                    var found=(Dictionary<DeviceAxis,IScriptResource>)repositoryType.GetMethod("SearchForScripts").Invoke(repository,new object[]{mediaName,source,DeviceAxis.All});
                    Verify(found.ContainsKey(DeviceAxis.Parse("L0")),"native lookup matches exact special filename, length "+video.Length);
                }catch(Exception ex){Verify(false,"native lookup threw "+ex.GetBaseException().GetType().Name+" at length "+video.Length);}
            }
            var snapshot=PlaybackLibrary.Scan(new[]{root},Path.Combine(root,"cache.json"),CancellationToken.None);
            Verify(snapshot.Videos.Count==names.Length,"random library retains every exact long mixed-language filename");
        }
        if(failures.Count>0)throw new Exception(string.Join("; ",failures));
    }
    static string WriteScript(string folder,string name,double duration,double? metadata=null,int peak=100)
    {
        Directory.CreateDirectory(folder);
        int steps=(int)Math.Ceiling(duration*2);
        var data=new JObject { ["actions"]=new JArray(Enumerable.Range(0,steps+1).Select(i=>new JObject{["at"]=i*duration*1000/steps,["pos"]=i%2*peak})) };
        if(metadata.HasValue)data["metadata"]=new JObject{["duration"]=metadata.Value};
        string path=Path.Combine(folder,name);File.WriteAllText(path,data.ToString());return path;
    }
    static string Video(string folder,string name)
    {
        Directory.CreateDirectory(folder);string path=Path.Combine(folder,name);
        File.Copy(Path.Combine(Environment.CurrentDirectory,"Fixtures","duration-12s.mp4"),path,true);return path;
    }
    public static void Run(string parent,Action<bool,string> check)
    {
        var a=new DurationCandidate{Key="wrong",Family="A",Duration=60};
        var b=new DurationCandidate{Key="right",Family="B",Duration=12};
        var chosen=DurationMatching.Select(12,new[]{a,b},out var status);
        check(chosen==b && status==DurationMatchStatus.Matched,"duration picks correct same-named script across folders instead of first directory");
        chosen=DurationMatching.Select(12,new[]{a},out status);
        check(chosen==a && status==DurationMatchStatus.Unique,"unique same-named script remains usable even when final action differs from media duration");
        chosen=DurationMatching.Select(0,new[]{b},out status);
        check(chosen==b && status==DurationMatchStatus.Unique,"unknown duration does not block the only same-named script");
        a.Duration=12.2;chosen=DurationMatching.Select(12,new[]{a,b},out status);
        check(chosen==null && status==DurationMatchStatus.Ambiguous,"equally plausible same-named scripts in different folders remain ambiguous");
        a.Duration=14.9;chosen=DurationMatching.Select(12,new[]{a,b},out status);
        check(chosen==b,"closest duration wins even when both candidates fall inside tolerance");
        a.Duration=12;a.Family="B";a.Priority=1;chosen=DurationMatching.Select(12,new[]{a,b},out status);
        check(chosen==b,"plain primary retains priority over alternate stroke suffix within same family");
        check(DurationMatching.Tolerance(7200)==30 && DurationMatching.Tolerance(12)==3,"duration tolerance allows small tails and remains capped for long videos");
        check(DurationMatching.ScriptDuration(JObject.Parse("{\"metadata\":{\"duration\":\"00:00:12\"}}"),10)==12,"time-formatted declared script duration is supported");
        check(DurationMatching.ScriptDuration(JObject.Parse("{\"metadata\":{\"duration\":5}}"),10)==10,"declared duration cannot hide actions extending beyond video");

        string root=Path.Combine(parent,"duration-cases"),videos=Path.Combine(root,"videos");
        string same=Video(videos,"同名 重复.mp4");
        WriteScript(videos,"同名 重复.funscript",50);
        string correct=WriteScript(Path.Combine(root,"correct"),"同名 重复.funscript",12);
        Video(videos,"末尾差异.mp4");WriteScript(videos,"末尾差异.funscript",10);
        Video(videos,"声明时长.mp4");WriteScript(videos,"声明时长.funscript",3,12);
        Video(videos,"时长不符.mp4");WriteScript(videos,"时长不符.funscript",60);
        Video(videos,"仍然歧义.mp4");WriteScript(Path.Combine(root,"A"),"仍然歧义.funscript",12);WriteScript(Path.Combine(root,"B"),"仍然歧义.funscript",12,peak:60);
        string unknown=Path.Combine(videos,"损坏媒体.mp4");File.WriteAllText(unknown,"not a video");WriteScript(videos,"损坏媒体.funscript",12);
        double actual=MediaDurationReader.Read(same,CancellationToken.None);
        check(Math.Abs(actual-12)<.05,"header reader reads actual Unicode video duration without starting PotPlayer");
        int calls=0;
        double Probe(string path,CancellationToken token){calls++;return MediaDurationReader.Read(path,token);}
        string cache=Path.Combine(root,"cache.json");
        var first=PlaybackLibrary.Scan(new[]{root},cache,CancellationToken.None,durationProbe:Probe);
        check(first.Videos.Count==5 && first.Ambiguous==1,"scanner restores unique partial/unknown-duration pairs and still excludes genuinely conflicting duplicates: "+first.Summary);
        check(PlaybackLibrary.SamePath(first.Videos.Single(v=>Path.GetFileName(v.VideoPath)=="同名 重复.mp4").ScriptPath,correct),"real scan overrides wrong same-folder script with duration-matched script in another folder");
        check(first.Videos.Single(v=>Path.GetFileName(v.VideoPath)=="声明时长.mp4").DurationDifference<.05,"script metadata takes priority over incomplete action coverage");
        int before=calls;
        var second=PlaybackLibrary.Scan(new[]{root},cache,CancellationToken.None,durationProbe:Probe);
        check(second.MediaCached==6 && calls==before,"refresh reuses known durations and briefly caches unreadable media");
        using(var append=new FileStream(same,FileMode.Append,FileAccess.Write))append.WriteByte(0);
        before=calls;PlaybackLibrary.Scan(new[]{root},cache,CancellationToken.None,durationProbe:Probe);
        check(calls==before+1,"changed video invalidates its own duration cache without probing all other files");
        var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;
        try{MediaDurationReader.Read(same,canceled.Token);}catch(OperationCanceledException){stopped=true;}
        check(stopped,"duration probing honors cancellation before opening files or processes");

        using(var matcher=new LocalDurationMatcher())
        {
            string rotation=WriteScript(Path.GetDirectoryName(correct),"同名 重复.twist.funscript",8);
            WriteScript(videos,"同名 重复.twist.funscript",12);
            var repositoryType=typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Script.Repository.ViewModels.LocalScriptRepository",true);
            var repository=Activator.CreateInstance(repositoryType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{new EventAggregator()},null);
            var libraries=repositoryType.GetProperty("ScriptLibraries").GetValue(repository);
            var libraryType=typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Script.Repository.ViewModels.ScriptLibrary",true);
            var library=Activator.CreateInstance(libraryType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{new DirectoryInfo(root)},null);
            libraryType.GetProperty("Recursive").SetValue(library,true);
            libraries.GetType().GetMethod("Add").Invoke(libraries,new[]{library});
            var search=repositoryType.GetMethod("SearchForScripts");
            Dictionary<DeviceAxis,IScriptResource> Search(string name)=>(Dictionary<DeviceAxis,IScriptResource>)search.Invoke(repository,new object[]{name,videos,DeviceAxis.All});
            var native=Search("同名 重复.mp4");
            check(native.TryGetValue(DeviceAxis.Parse("L0"),out var script) && PlaybackLibrary.SamePath(script.Source,Path.GetDirectoryName(correct)),"patched native repository selects duration-matched L0 before first-name-wins lookup");
            check(native.TryGetValue(DeviceAxis.Parse("R0"),out var twist) && PlaybackLibrary.SamePath(twist.Source,Path.GetDirectoryName(rotation)),"native auxiliary axes stay with chosen primary folder instead of mixing cuts");
            check(Search("仍然歧义.mp4").Count==0 && Search("时长不符.mp4").Count>0 && Search("损坏媒体.mp4").Count>0,"native matching preserves unique scripts without requiring exact/available duration and rejects unresolved collisions");
            Video(videos,"压缩包.mp4");WriteScript(videos,"压缩包.funscript",60);
            string archive=Path.Combine(root,"compressed","压缩包.zip");Directory.CreateDirectory(Path.GetDirectoryName(archive));
            using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))
            {
                var entry=zip.CreateEntry("压缩包.funscript");
                using(var writer=new StreamWriter(entry.Open()))writer.Write(File.ReadAllText(correct));
            }
            native=Search("压缩包.mp4");
            check(native.TryGetValue(DeviceAxis.Parse("L0"),out script) && PlaybackLibrary.SamePath(script.Source,archive),"duration matching also chooses correct primary script inside a zip library");
            using(var zip=ZipFile.Open(archive,ZipArchiveMode.Update))
            {
                var entry=zip.CreateEntry("other-cut/压缩包.funscript");
                using(var writer=new StreamWriter(entry.Open()))writer.Write(File.ReadAllText(correct));
            }
            check(Search("压缩包.mp4").Count==0,"duplicate zip member basenames in different subfolders are not silently treated as the same script");
            File.Delete(rotation);native=Search("同名 重复.mp4");
            check(!native.ContainsKey(DeviceAxis.Parse("R0")),"missing auxiliary axis does not borrow same-named script from a different duration family");

            // Both actual audio probing and native lookup must work for pure audio.
            string audio=Path.Combine(videos,"音频.mp3");File.Copy(Path.Combine(Environment.CurrentDirectory,"Fixtures","duration-12s.mp3"),audio);
            WriteScript(videos,"音频.funscript",4);
            check(Math.Abs(MediaDurationReader.Read(audio,CancellationToken.None)-12)<.2,"pure MP3 probe keeps audio stream enabled while output stays muted");
            check(Search("音频.mp3").ContainsKey(DeviceAxis.Parse("L0")),"manually opened pure audio matches its partial script through native repository");

            Video(videos,"副本.mp4");string copy=WriteScript(Path.Combine(root,"copy-a"),"副本.funscript",12);
            Directory.CreateDirectory(Path.Combine(root,"copy-b"));File.Copy(copy,Path.Combine(root,"copy-b","副本.funscript"));
            check(Search("副本.mp4").ContainsKey(DeviceAxis.Parse("L0")),"identical script copies are not treated as conflicting native candidates");
            var restored=PlaybackLibrary.Scan(new[]{root},cache,CancellationToken.None);
            check(!restored.Videos.Any(v=>v.VideoPath==audio) && restored.Videos.Any(v=>Path.GetFileName(v.VideoPath)=="副本.mp4"),"random library excludes audio while native audio matching and equivalent video copies remain supported");

            a.Family="A";a.Priority=0;a.Duration=3;b.Duration=12;chosen=DurationMatching.Select(100,new[]{a,b},out status);
            check(chosen==b,"multiple partial scripts compare relative duration even beyond the old two-percent cutoff");
            a.Duration=12;a.SameDirectory=true;chosen=DurationMatching.Select(12,new[]{a,b},out status);
            check(chosen==a && status==DurationMatchStatus.Local,"duration tie respects an explicit same-directory pairing");
            a.SameDirectory=false;chosen=DurationMatching.Select(0,new[]{a,b},out status);
            check(chosen==null && status==DurationMatchStatus.Ambiguous,"unknown duration cannot arbitrarily choose among distinct remote scripts");
        }
        check(typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Common.LocalScriptMatchHook").GetField("Filter").GetValue(null)==null,"disposing duration matcher clears only its own native callback");
    }
}
