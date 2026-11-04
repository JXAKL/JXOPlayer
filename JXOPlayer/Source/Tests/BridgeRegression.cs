using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using JXOPlayer.Common;
using JXOPlayer.Features;
using JXOPlayer.MediaSource.MediaResource;
using JXOPlayer.Script;
using Newtonsoft.Json.Linq;
using Stylet;

public static class BridgeRegression
{
    static MediaResourceInfo Resource(string path)=>new MediaResourceInfo(MediaResourcePathType.File,null,path,Path.GetDirectoryName(path),Path.GetFileName(path),null);
    public sealed class AxisOptions { public bool LockScript {get;set;} }
    public sealed class AxisModel { public IScriptResource Script {get;set;} }
    public sealed class ScriptModel
    {
        public MediaResourceInfo MediaResource {get;set;}
        public Dictionary<DeviceAxis,AxisOptions> AxisSettings {get;}=new Dictionary<DeviceAxis,AxisOptions>();
        public Dictionary<DeviceAxis,AxisModel> AxisModels {get;}=new Dictionary<DeviceAxis,AxisModel>();
        public int Reloads, Changes;
        public Action BeforeChange;
        public void Handle(ReloadScriptsRequestMessage message){Reloads++;}
        public void Handle(ChangeScriptMessage message)
        {
            if(Application.Current.Dispatcher.CheckAccess())throw new Exception("Script replacement must not wait on axis locks from the UI thread");
            BeforeChange?.Invoke();
            foreach(var pair in message.Scripts)AxisModels[pair.Key].Script=pair.Value;
            Changes++;
        }
    }
    public sealed class Player
    {
        public string Name=>"PotPlayer";
        public string Status {get;set;}="Connected";
        public int Opens, Plays;
        public Action<string> Open;
        public void Handle(IMediaSourceControlMessage message)
        {
            if(message is MediaChangePathMessage path){Opens++;Open?.Invoke(path.Path);}
            if(message is MediaPlayPauseMessage pause && pause.ShouldBePlaying)Plays++;
        }
    }
    public sealed class Manager
    {
        public object _currentSource;
        public List<object> AvailableSources {get;}=new List<object>();
        public List<object> Items {get;}=new List<object>();
        public object ActiveItem {get;set;}
        public Task ToggleConnectAsync(object player){_currentSource=player;return Task.CompletedTask;}
    }
    public sealed class Root
    {
        public IEventAggregator _eventAggregator=new EventAggregator();
        public ScriptModel Script {get;}=new ScriptModel();
        public Manager MediaSource {get;}=new Manager();
    }
    public static async Task Run(string folder,Action<bool,string> check)
    {
        var config=JObject.Parse(File.ReadAllText("JXOPlayer.config.json"));
        var deviceType=typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Common.DeviceSettings");
        var device=config["Devices"].First(x=>(string)x["Name"]=="TCode-0.3").ToObject(deviceType);
        typeof(DeviceAxis).GetMethod("InitializeFromDevice",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{device});
        var axis=DeviceAxis.Parse("L0");var root=new Root();var player=new Player();
        root.MediaSource.AvailableSources.Add(player);root.MediaSource.Items.Add(player);root.MediaSource._currentSource=player;
        root.Script.AxisSettings[axis]=new AxisOptions();root.Script.AxisModels[axis]=new AxisModel();
        var video=new LibraryVideo{VideoPath=Path.Combine(folder,"clip 1.mp4"),ScriptPath=Path.Combine(folder,"clip 1.funscript")};
        using(var bridge=new PotPlayerBridge(root))
        {
            var finalCheck=new TaskCompletionSource<bool>();
            var raced=bridge.Play(video,CancellationToken.None,beforeOpen:token=>finalCheck.Task);
            await Task.Delay(50);root.Script.MediaResource=Resource("C:\\native-next.mp4");finalCheck.SetResult(false);
            bool yielded=false;try{await raced;}catch(OperationCanceledException){yielded=true;}
            check(yielded && player.Opens==0 && player.Plays==0 && root.Script.MediaResource.OriginalPath=="C:\\native-next.mp4","automatic open rechecks after script preload and preserves native playlist continuation");
            player.Status="Disconnected";
            bool noRestart=false;try{await bridge.Play(video,CancellationToken.None,beforeOpen:token=>Task.FromResult(true));}catch(OperationCanceledException){noRestart=true;}
            check(noRestart && player.Opens==0,"automatic open never reconnects or launches a disconnected player");
            player.Status="Connected";root.Script.MediaResource=null;
            var mediaInfo=new FileInfo(video.VideoPath);var scriptInfo=new FileInfo(video.ScriptPath);
            var guarded=new LibraryVideo{VideoPath=video.VideoPath,ScriptPath=video.ScriptPath,VideoDuration=60,Stats=new MotionStats{Duration=120},
                VideoLength=mediaInfo.Length,VideoModified=mediaInfo.LastWriteTimeUtc.Ticks,ScriptLength=scriptInfo.Length,ScriptModified=scriptInfo.LastWriteTimeUtc.Ticks};
            guarded.VideoLength++;
            bool modified=false;try{await bridge.Play(guarded,CancellationToken.None);}catch(InvalidOperationException ex){modified=ex.Message.Contains("变化");}
            check(modified && player.Opens==0,"playback requests rescan when media fingerprint changed after duration pairing");
            guarded.VideoLength--;
            void Post()=>bridge.Handle(new PostScriptSearchMessage(root.Script.MediaResource,new Dictionary<DeviceAxis,IScriptResource>()));
            player.Open=path=>{root.Script.MediaResource=Resource(path);Post();};
            using(var release=new ManualResetEventSlim(false))
            {
                root.Script.BeforeChange=()=>{if(!release.Wait(2000))throw new Exception("UI did not release script lock fixture");};
                var opening=bridge.Play(guarded,CancellationToken.None);
                await Task.Delay(80);
                check(!opening.IsCompleted,"UI continues while a background script replacement waits for an axis lock");
                release.Set();await opening.WaitAsync(TimeSpan.FromSeconds(3));root.Script.BeforeChange=null;
                check(player.Opens==1,"confirmed unique partial script is not vetoed by a second duration gate in PotPlayer bridge");
            }
            check(root.Script.Reloads==0 && root.Script.Changes==1,"new video performs no duplicate native reload and applies the selected script");
            int changes=root.Script.Changes;
            root.Script.MediaResource=null;
            await bridge.Play(video,CancellationToken.None);
            check(root.Script.Changes==changes,"already matched native script is not redundantly replaced");
            player.Open=path=>root.Script.MediaResource=Resource(path);
            await bridge.Play(video,CancellationToken.None);
            check(root.Script.Reloads==0 && root.Script.Changes==changes+1,"same-video replay loads the known script without waiting for a missing path event");
            root.Script.MediaResource=null;root.Script.AxisModels[axis].Script=null;
            await bridge.Play(video,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            check(root.Script.AxisModels[axis].Script!=null,"known random script loads even when native repository completion never arrives");
            root.Script.AxisModels[axis].Script=null;
            bridge.Handle(new PostScriptSearchMessage(Resource("C:\\previous-video.mp4"),new Dictionary<DeviceAxis,IScriptResource>()));
            await Task.Delay(100);
            check(root.Script.AxisModels[axis].Script?.Name=="clip 1.funscript","late callback from a previous video cannot leave current random script cleared");
            root.Script.MediaResource=Resource("C:\\manually-selected.mp4");root.Script.AxisModels[axis].Script=null;
            bridge.Handle(new PostScriptSearchMessage(Resource(video.VideoPath),new Dictionary<DeviceAxis,IScriptResource>()));await Task.Delay(80);
            check(root.Script.AxisModels[axis].Script==null,"late callback never applies random script to a different current video");
            root.Script.MediaResource=null;
            using(var cancel=new CancellationTokenSource())
            {
                var opening=bridge.Play(video,cancel.Token);await Task.Delay(80);cancel.Cancel();
                bool canceled=false;try{await opening;}catch(OperationCanceledException){canceled=true;}
                int before=root.Script.Changes;Post();await Task.Delay(50);
                check(canceled && root.Script.Changes==before,"canceled open ignores late repository callbacks");
            }
            root.Script.MediaResource=null;
            var disconnected=bridge.Play(video,CancellationToken.None);await Task.Delay(80);player.Status="Disconnected";
            bool closed=false;try{await disconnected.WaitAsync(TimeSpan.FromSeconds(2));}catch(InvalidOperationException){closed=true;}
            check(closed,"closing PotPlayer ends pending script confirmation promptly");
            player.Status="Connected";root.Script.MediaResource=null;
            var stopped=bridge.Play(video,CancellationToken.None);await Task.Delay(80);root.Script.MediaResource=null;
            bool ended=false;try{await stopped.WaitAsync(TimeSpan.FromSeconds(2));}catch(InvalidOperationException){ended=true;}
            check(ended,"stopping the media ends pending confirmation promptly");
        }
    }
}
