using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using JXOPlayer.Features;
using Newtonsoft.Json.Linq;

public static class TestRandomPlayback
{
    static int checks;
    static string root;
    static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
    static JObject Motion(double speed)=>new JObject{["metadata"]=new JObject{["duration"]=60},["actions"]=new JArray(Enumerable.Range(0,21).Select(i=>new JObject{["at"]=i*100000.0/speed,["pos"]=i%2*100}))};
    static double ProbeFixture(string path,CancellationToken token){token.ThrowIfCancellationRequested();return 60;}
    static string Write(string path,string content){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,content);return path;}
    static IEnumerable<DependencyObject> Visuals(DependencyObject item){yield return item;for(int i=0;i<VisualTreeHelper.GetChildrenCount(item);i++)foreach(var next in Visuals(VisualTreeHelper.GetChild(item,i)))yield return next;}
    [STAThread] public static int Main()
    {
        Directory.SetCurrentDirectory(Path.GetDirectoryName(Environment.ProcessPath));root=Path.Combine(Environment.CurrentDirectory,"fixtures-random");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(s,e)=>{try{await Run();File.WriteAllText("random-results.txt",checks+" checks passed");app.Shutdown(0);}catch(Exception ex){Console.Error.WriteLine(ex);app.Shutdown(1);}};
        return app.Run();
    }
    static async Task Run()
    {
        TestAutoAdvance.Run(Check);
        var golden=JArray.Parse(File.ReadAllText("intensity-golden.json"));
        foreach(var fixture in golden)
        {
            var actual=IntensityScoring.Grade((JObject)fixture["data"]);var expected=fixture["expected"];
            Check(Math.Abs(actual.ActiveAverage-(double)expected["active_average"])<1e-8 && Math.Abs(actual.Maximum-(double)expected["maximum"])<1e-8,"ES motion metrics parity: "+fixture["name"]);
            Check(expected["score"].Type==JTokenType.Null ? actual.Error!=null : Math.Abs(actual.AbsoluteScore-(double)expected["score"])<1e-8,"ES score parity: "+fixture["name"]);
        }
        var invalidScripts=new[]{
            "{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":0,\"pos\":100}]}",
            "{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":1000,\"pos\":101}]}",
            "{\"actions\":[{\"at\":true,\"pos\":0},{\"at\":1000,\"pos\":100}]}",
            "{\"actions\":[{\"at\":-1,\"pos\":0},{\"at\":1000,\"pos\":100}]}",
            "{\"actions\":[{\"at\":0,\"pos\":50}]}",
            "{\"actions\":[{\"at\":0,\"pos\":50},{\"at\":0,\"pos\":50}]}"
        };
        foreach(var invalid in invalidScripts)
        {bool rejected=false;try{IntensityScoring.Grade(JObject.Parse(invalid));}catch(InvalidDataException){rejected=true;}Check(rejected,"invalid action data rejected");}
        for(int i=1;i<=5;i++){Write(Path.Combine(root,$"clip {i}.mp4"),"fixture");Write(Path.Combine(root,$"clip {i}.funscript"),Motion(i*50).ToString());}
        Write(Path.Combine(root,"orphan.mp4"),"fixture");Write(Path.Combine(root,"broken.mp4"),"fixture");Write(Path.Combine(root,"broken.funscript"),"not json");
        Write(Path.Combine(root,"static.mp4"),"fixture");Write(Path.Combine(root,"static.funscript"),"{\"actions\":[{\"at\":0,\"pos\":50},{\"at\":1000,\"pos\":50}]}");
        Write(Path.Combine(root,"ambiguous.mp4"),"fixture");Write(Path.Combine(root,"A","ambiguous.funscript"),Motion(60).ToString());Write(Path.Combine(root,"B","ambiguous.funscript"),Motion(160).ToString());
        // Exercise the scanner's exception boundary, not just the parser's validation.
        for(int i=0;i<invalidScripts.Length;i++){
            Write(Path.Combine(root,$"invalid-data-{i}.mp4"),"fixture");
            Write(Path.Combine(root,$"invalid-data-{i}.funscript"),invalidScripts[i]);
        }
        string cache=Path.Combine(root,"cache.json");
        var snapshot=PlaybackLibrary.Scan(new[]{root,Path.Combine(root,"A")},cache,CancellationToken.None,durationProbe:ProbeFixture);
        Check(snapshot.Videos.Count==5 && snapshot.Unmatched==1 && snapshot.Invalid==1+invalidScripts.Length && snapshot.Static==1 && snapshot.Ambiguous==1,"mixed library skips malformed and invalid action scripts while retaining valid videos");
        IntensityScoring.Apply(snapshot.Videos,true);Check(snapshot.Videos.Select(v=>v.Stats.Tier).OrderBy(x=>x).SequenceEqual(new[]{1,2,3,4,5}),"relative ranking fills five tiers in increasing speed order");
        var cached=PlaybackLibrary.Scan(new[]{root},cache,CancellationToken.None,durationProbe:ProbeFixture);Check(cached.Cached==15 && cached.Graded==0,"unchanged valid and invalid scripts reuse fingerprinted cache");
        Check(cached.Videos.Count==5 && cached.Invalid==1+invalidScripts.Length,"refresh with cached valid entries still skips invalid action scripts");
        Write(Path.Combine(root,"clip 1.funscript"),Motion(75).ToString());var changed=PlaybackLibrary.Scan(new[]{root},cache,CancellationToken.None,durationProbe:ProbeFixture);Check(changed.Graded==1,"modified script invalidates only its cache entry");
        var ties=snapshot.Videos.Select(v=>new LibraryVideo{VideoPath=v.VideoPath,ScriptPath=v.ScriptPath,Stats=IntensityScoring.Grade(Motion(100))}).ToList();
        IntensityScoring.Apply(ties,true);Check(ties.All(v=>v.Stats.Tier==3 && v.Stats.Score==50),"ties share the middle tier rather than being artificially spread");
        var selection=new ShuffleSelection();var first=selection.Pick(ties,3,null);selection.Commit(first);var second=selection.Pick(ties,3,first.VideoPath);Check(second.VideoPath!=first.VideoPath,"random selection avoids immediate repeat");
        var played=new HashSet<string>{first.VideoPath};string current=first.VideoPath;
        for(int i=1;i<5;i++){var next=selection.Pick(ties,3,current);selection.Commit(next);played.Add(next.VideoPath);current=next.VideoPath;}Check(played.Count==5,"shuffle visits the tier before recycling");
        Check(selection.Pick(ties,1,null)==null,"empty tier never falls back to a different strength");
        var deleted=ties[0];File.Delete(deleted.VideoPath);for(int i=0;i<15;i++)Check(selection.Pick(ties,3,null).VideoPath!=deleted.VideoPath,"deleted video excluded");
        Write(deleted.VideoPath,"fixture");
        var window=new Window{Width=600,Height=320,ShowInTaskbar=false};var panel=new StackPanel();window.Content=panel;
        int calls=0;var gate=new TaskCompletionSource<bool>();
        var service=new RandomPlaybackService(window,async(v,token)=>{calls++;await gate.Task;},()=>null,Path.Combine(root,"settings"),durationProbe:ProbeFixture);service.Settings.Folders=new List<string>{root};
        panel.Children.Add(service.CreateStrip(false));panel.Children.Add(service.CreateStrip(true));window.Show();await service.RefreshAsync();window.UpdateLayout();
        Check(!service.Scanning && service.Library.Videos.Count==5 && service.Library.Invalid==1+invalidScripts.Length && !service.Status.StartsWith("扫描失败"),"UI scan completes with valid videos and invalid counts in a mixed library");
        var buttons=Visuals(window).OfType<Button>().Where(b=>b.Name.StartsWith("RandomTier")).ToList();Check(buttons.Count==10,"five random buttons are available in both main and floating strips");
        var autos=Visuals(window).OfType<CheckBox>().Where(b=>b.Name=="AutoAdvance").ToArray();
        Check(autos.Length==2 && autos.All(b=>b.IsChecked==false),"both auto-next switches default off");
        autos[0].IsChecked=true;autos[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(service.Settings.AutoAdvance && autos.All(b=>b.IsChecked==true),"main and floating auto-next switches stay synchronized");
        var target=buttons.First(b=>b.Name=="RandomTier1");target.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));target.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(calls==1 && buttons.All(b=>!b.IsEnabled),"double-click starts only one request and disables both strips while opening");
        gate.SetResult(true);await Task.Delay(30);Check(!service.Busy && service.Status.StartsWith("1档"),"successful playback restores controls and reports the selected tier");
        service.Settings.Relative=false;service.Save();service.Regrade();Check(service.Library.Videos.All(v=>v.Stats.Score==v.Stats.AbsoluteScore),"absolute grading option uses the same ES absolute score");
        var reloaded=new RandomPlaybackService(window,(v,t)=>Task.CompletedTask,()=>null,Path.Combine(root,"settings"),durationProbe:ProbeFixture);Check(!reloaded.Settings.Relative&&reloaded.Settings.Folders.Contains(root),"library roots and grading mode persist");
        Check(reloaded.Settings.AutoAdvance,"auto-next preference survives reopening");
        reloaded.Dispose();service.Dispose();
        var never=new TaskCompletionSource<bool>();
        var timed=new RandomPlaybackService(window,(v,t)=>never.Task,()=>null,Path.Combine(root,"timeout-settings"),TimeSpan.FromMilliseconds(120),durationProbe:ProbeFixture);
        timed.Settings.Folders=new List<string>{root};await timed.RefreshAsync();await timed.PlayTierAsync(1);
        Check(!timed.Busy && timed.Status.Contains("超时"),"unresponsive playback times out and releases selection controls");
        timed.Dispose();
        int retryCalls=0;var late=new TaskCompletionSource<bool>();
        var cancellable=new RandomPlaybackService(window,(v,t)=>++retryCalls==1?late.Task:Task.CompletedTask,()=>null,Path.Combine(root,"cancel-settings"),durationProbe:ProbeFixture);
        cancellable.Settings.Folders=new List<string>{root};await cancellable.RefreshAsync();
        panel.Children.Clear();panel.Children.Add(cancellable.CreateStrip(false));panel.Children.Add(cancellable.CreateStrip(true));
        await Task.Delay(20);if(cancellable.Scanning)await cancellable.RefreshAsync();
        var pending=cancellable.PlayTierAsync(1);
        var cancelButtons=Visuals(window).OfType<Button>().Where(b=>b.Name=="CancelRandomOpen").ToArray();
        Check(cancelButtons.Length==2 && cancelButtons.All(b=>b.Visibility==Visibility.Visible),"both strips expose cancel while opening");
        cancelButtons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await pending;
        Check(!cancellable.Busy && cancellable.Status.Contains("取消"),"cancel releases a playback task that ignores cancellation");
        await cancellable.PlayTierAsync(5);string completedStatus=cancellable.Status;
        Check(completedStatus.StartsWith("5档"),"another selection succeeds immediately after cancellation");
        late.SetResult(true);await Task.Delay(20);
        Check(cancellable.Status==completedStatus,"late completion cannot overwrite a newer selection");
        cancellable.Dispose();window.Close();
        await BridgeRegression.Run(root,Check);
        TestDurationMatching.Run(root,Check);
        TestDurationMatching.TestFileNames(root,Check);
        await TestLocalMediaRelay.Run(root,Check);
        TestScanPerformance.Run(root,Check);
        Console.WriteLine(checks+" checks passed");
    }
}
