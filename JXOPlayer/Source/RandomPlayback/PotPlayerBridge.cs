namespace JXOPlayer.Features
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using JXOPlayer.Common;
    using JXOPlayer.Script;
    using Stylet;

    // Route playback through the application's existing PotPlayer connection.
    // The chosen script is already known: repository lookup must not gate loading.
    public sealed class PotPlayerBridge : IHandle<PostScriptSearchMessage>, IDisposable
    {
        readonly object root;
        readonly IEventAggregator events;
        sealed class Selection
        {
            public string VideoPath;
            public IScriptResource Script;
            public bool ForceApply;
            public CancellationToken Token;
            public readonly TaskCompletionSource<bool> Loaded=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        volatile Selection selected;
        readonly SemaphoreSlim applyGate=new SemaphoreSlim(1,1);
        readonly NLog.Logger log=NLog.LogManager.GetLogger("JXOPlayer.RandomPlayback");
        bool disposed;
        static object Get(object target,string name) => target?.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(target);
        static object Field(object target,string name) => target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(target);
        public string CurrentPath => (string)Get(Get(Get(root,"Script"),"MediaResource"),"OriginalPath");
        public PotPlayerBridge(object root)
        {
            this.root=root;
            events=(IEventAggregator)Field(root,"_eventAggregator");
            events.Subscribe(this);
        }
        public async Task Play(LibraryVideo video,CancellationToken token,Action<string> progress=null,Func<CancellationToken,Task<bool>> beforeOpen=null)
        {
            selected=null;
            log.Info("Random open requested [Video: {0}, Script: {1}, Automatic: {2}]",video.VideoPath,video.ScriptPath,beforeOpen!=null);
            token.ThrowIfCancellationRequested();
            if(video.VideoModified>0)
            {
                var media=new System.IO.FileInfo(video.VideoPath);var file=new System.IO.FileInfo(video.ScriptPath);
                if(!media.Exists || !file.Exists || media.Length!=video.VideoLength || media.LastWriteTimeUtc.Ticks!=video.VideoModified || file.Length!=video.ScriptLength || file.LastWriteTimeUtc.Ticks!=video.ScriptModified)
                    throw new InvalidOperationException("媒体或脚本已变化，请刷新媒体库重新配对");
            }
            var script=Get(root,"Script");var axis=DeviceAxis.Parse("L0");
            if(axis==null)throw new InvalidOperationException("当前设备没有上下轴 L0");
            var settings=Get(script,"AxisSettings");
            var axisSettings=settings.GetType().GetProperty("Item").GetValue(settings,new object[]{axis});
            if((bool)Get(axisSettings,"LockScript"))throw new InvalidOperationException("请先解除上下轴的脚本锁定，再随机播放");
            progress?.Invoke("正在读取所选脚本…");
            var reader=await Task.Run(()=>FunscriptReader.Default.FromPath(video.ScriptPath),token).WaitAsync(token);
            if(!reader.IsSuccess || reader.Resource?.Keyframes==null || reader.Resource.Keyframes.Count<2)throw new InvalidOperationException("所选脚本已无法读取，请刷新视频库");
            var manager=Get(root,"MediaSource");
            var player=((IEnumerable)Get(manager,"AvailableSources")).Cast<object>().FirstOrDefault(p=>(string)Get(p,"Name")=="PotPlayer");
            if(player==null)throw new InvalidOperationException("未找到 PotPlayer 连接组件");
            if(beforeOpen!=null && (!ReferenceEquals(Field(manager,"_currentSource"),player) || Get(player,"Status").ToString()!="Connected"))throw new OperationCanceledException(token);
            var items=(IList)Get(manager,"Items");if(!items.Contains(player))items.Add(player);
            manager.GetType().GetProperty("ActiveItem").SetValue(manager,player);
            progress?.Invoke("正在连接 PotPlayer…");
            if(!ReferenceEquals(Field(manager,"_currentSource"),player) || Get(player,"Status").ToString()!="Connected")
            {
                var connection=(Task)manager.GetType().GetMethod("ToggleConnectAsync").Invoke(manager,new[]{player});
                try { await connection.WaitAsync(TimeSpan.FromSeconds(15),token); }
                catch(TimeoutException){throw new TimeoutException("连接 PotPlayer 超时，请检查播放器状态");}
                // Connected means the process was found, not that its startup media is
                // ready. Let the first native polling result arrive before replacing it.
                progress?.Invoke("正在等待 PotPlayer 就绪…");
                var readyDeadline=DateTime.UtcNow.AddSeconds(2);
                while(string.IsNullOrEmpty(CurrentPath) && Get(player,"Status").ToString()=="Connected" && DateTime.UtcNow<readyDeadline)
                    await Task.Delay(100,token);
            }
            if(Get(player,"Status").ToString()!="Connected")throw new InvalidOperationException("无法连接 PotPlayer，请先打开 PotPlayer 后重试");
            if(beforeOpen!=null && !await beforeOpen(token))throw new OperationCanceledException("PotPlayer 已继续播放或退出",token);
            token.ThrowIfCancellationRequested();
            bool sameVideo=PlaybackLibrary.SamePath(CurrentPath,video.VideoPath);
            var request=new Selection{VideoPath=video.VideoPath,Script=reader.Resource,Token=token,ForceApply=sameVideo};
            selected=request;
            try
            {
                progress?.Invoke("PotPlayer 正在打开："+System.IO.Path.GetFileNameWithoutExtension(video.VideoPath));
                if(sameVideo)Send(player,new MediaSeekMessage(TimeSpan.Zero));
                else Send(player,new MediaChangePathMessage(PlaybackLibrary.PlayerPath(video.VideoPath)));
                var deadline=DateTime.UtcNow.AddSeconds(15);
                while(!PlaybackLibrary.SamePath(CurrentPath,video.VideoPath))
                {
                    CheckConnection(manager,player);
                    if(DateTime.UtcNow>=deadline)throw new TimeoutException("PotPlayer 未确认打开所选视频");
                    await Task.Delay(150,token);
                }
                progress?.Invoke("视频已打开，正在确认脚本…");
                // Apply the preloaded selection immediately, including when native
                // lookup is slow, fails, or has no matching script library configured.
                // Post-search callbacks below reconcile late native results as well.
                _ = CompleteAsync(request);
                deadline=DateTime.UtcNow.AddSeconds(15);
                while(!request.Loaded.Task.IsCompleted)
                {
                    CheckConnection(manager,player);
                    if(!PlaybackLibrary.SamePath(CurrentPath,video.VideoPath))throw new InvalidOperationException("视频已停止或切换，已结束本次打开操作");
                    if(DateTime.UtcNow>=deadline)throw new TimeoutException("脚本确认超时，请检查脚本库路径后重试");
                    await Task.Delay(100,token);
                }
                await request.Loaded.Task.WaitAsync(token);
                await Task.Delay(200,token);
                await CompleteAsync(request).WaitAsync(token);
                if(!await Task.Run(()=>MatchesScript(CurrentScript(),request),token))throw new InvalidOperationException("所选脚本未能保持加载，请检查脚本锁定或手动切换");
                token.ThrowIfCancellationRequested();
                CheckConnection(manager,player);
                if(!PlaybackLibrary.SamePath(CurrentPath,video.VideoPath))throw new InvalidOperationException("视频已停止或切换，已结束本次打开操作");
                Send(player,new MediaPlayPauseMessage(true));
                log.Info("Random open completed [Video: {0}, Script: {1}]",video.VideoPath,request.Script.Name);
            }
            catch(Exception ex) { if(ReferenceEquals(selected,request))selected=null;log.Warn(ex,"Random open ended before confirmation [Video: {0}]",video.VideoPath);throw; }
        }
        static void CheckConnection(object manager,object player)
        {
            if(!ReferenceEquals(Field(manager,"_currentSource"),player) || Get(player,"Status").ToString()!="Connected")
                throw new InvalidOperationException("PotPlayer 已关闭或断开连接，可以重新选择视频");
        }
        static bool MatchesScript(IScriptResource actual,Selection request)=>actual!=null && string.Equals(actual.Name,request.Script.Name,StringComparison.OrdinalIgnoreCase) && PlaybackLibrary.SamePath(actual.Source,request.Script.Source);
        IScriptResource CurrentScript()
        {
            var models=Get(Get(root,"Script"),"AxisModels");
            return Get(models.GetType().GetProperty("Item").GetValue(models,new object[]{DeviceAxis.Parse("L0")}),"Script") as IScriptResource;
        }
        public void Handle(PostScriptSearchMessage message)
        {
            var request=selected;
            // A previous video's slow callback can clear/overwrite the current L0.
            // Reconcile against the active video, not the callback's obsolete path.
            if(disposed || request==null || request.Token.IsCancellationRequested || !PlaybackLibrary.SamePath(CurrentPath,request.VideoPath))return;
            _ = CompleteAsync(request);
        }
        async Task CompleteAsync(Selection request)
        {
            try
            {
                await applyGate.WaitAsync(request.Token).ConfigureAwait(false);
                try
                {
                    // Native script setters may wait for UI notifications while holding axis locks.
                    // Never acquire those locks on the UI thread; keep cancellation and UI responsive.
                    await Task.Run(()=>
                    {
                        if(disposed || !ReferenceEquals(selected,request) || request.Token.IsCancellationRequested || !PlaybackLibrary.SamePath(CurrentPath,request.VideoPath))return;
                        var axis=DeviceAxis.Parse("L0");var script=Get(root,"Script");
                        var models=Get(script,"AxisModels");var model=models.GetType().GetProperty("Item").GetValue(models,new object[]{axis});
                        var existing=Get(model,"Script") as IScriptResource;
                        if(request.ForceApply || !MatchesScript(existing,request))
                        {
                            log.Info("Applying selected random script [Video: {0}, Script: {1}, Repair: {2}]",request.VideoPath,request.Script.Name,request.Loaded.Task.IsCompleted);
                            var change=new ChangeScriptMessage(new Dictionary<DeviceAxis,IScriptResource>{{axis,request.Script}});
                            script.GetType().GetMethod("Handle",new[]{typeof(ChangeScriptMessage)}).Invoke(script,new object[]{change});
                        }
                        var actual=Get(model,"Script") as IScriptResource;
                        if(!MatchesScript(actual,request))throw new InvalidOperationException("上下轴脚本未切换，请检查脚本锁定");
                        request.ForceApply=false;
                        request.Token.ThrowIfCancellationRequested();
                        if(ReferenceEquals(selected,request))request.Loaded.TrySetResult(true);
                    },request.Token).ConfigureAwait(false);
                }
                finally{applyGate.Release();}
            }
            catch(OperationCanceledException){request.Loaded.TrySetCanceled();}
            catch(Exception ex){log.Warn(ex,"Random script apply failed [Video: {0}]",request.VideoPath);request.Loaded.TrySetException(ex.GetBaseException());}
        }
        static void Send(object player,IMediaSourceControlMessage message) => player.GetType().GetMethod("Handle",new[]{typeof(IMediaSourceControlMessage)}).Invoke(player,new object[]{message});
        public void Dispose(){if(disposed)return;disposed=true;events.Unsubscribe(this);selected?.Loaded.TrySetCanceled();selected=null;}
    }
}
