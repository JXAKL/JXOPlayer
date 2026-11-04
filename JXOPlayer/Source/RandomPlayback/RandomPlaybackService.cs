namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Controls.Primitives;
    using System.Windows.Media;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Stylet;

    public sealed class RandomPlaybackSettings
    {
        public List<string> Folders=new List<string>();
        public bool Relative=true;
        public bool AutoAdvance=false;
    }
    public sealed class RandomPlaybackService : IDisposable
    {
        public static readonly string[] Labels={"低","较低","中","较高","高"};
        readonly string dataDirectory;
        readonly Window main;
        readonly ShuffleSelection shuffle=new ShuffleSelection();
        readonly Dictionary<string,LibraryVideo> autoIndex=new Dictionary<string,LibraryVideo>(StringComparer.OrdinalIgnoreCase);
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        CancellationTokenSource scan;
        CancellationTokenSource opening;
        readonly TimeSpan playbackTimeout;
        bool cancelledByUser;
        readonly Func<LibraryVideo,CancellationToken,Task> play;
        readonly Func<string> currentPath;
        PotPlayerBridge bridge;
        LocalDurationMatcher durationMatcher;
        readonly Func<string,CancellationToken,double> durationProbe;
        Window libraryWindow;
        bool started,disposed,playing;
        string lastVideo;
        readonly AutoAdvanceGate autoGate=new AutoAdvanceGate();
        readonly PotPlayerPlaybackReader autoReader=new PotPlayerPlaybackReader();
        readonly System.Windows.Threading.DispatcherTimer autoTimer;
        bool autoPolling,automaticOpening;
        int autoRevision;
        readonly NLog.Logger log=NLog.LogManager.GetLogger("JXOPlayer.RandomPlayback");
        public event Action Changed;
        public RandomPlaybackSettings Settings {get;private set;}
        public LibrarySnapshot Library {get;private set;}=new LibrarySnapshot();
        public bool Scanning {get;private set;}
        public bool Busy=>Scanning||playing;
        public string Status {get;private set;}="选择视频库后按强度随机播放 · PotPlayer";
        public RandomPlaybackService(Window main,Func<LibraryVideo,CancellationToken,Task> play=null,Func<string> currentPath=null,string directory=null,TimeSpan? playbackTimeout=null,Func<string,CancellationToken,double> durationProbe=null)
        {
            this.main=main;this.play=play;this.currentPath=currentPath;
            this.durationProbe=durationProbe;
            this.playbackTimeout=playbackTimeout??TimeSpan.FromSeconds(30);
            dataDirectory=directory??Path.Combine(Path.GetDirectoryName(Environment.ProcessPath),"Data");
            try{Settings=JsonConvert.DeserializeObject<RandomPlaybackSettings>(File.ReadAllText(Path.Combine(dataDirectory,"RandomPlayback.settings.json")));}catch{}
            if(Settings==null)
            {
                Settings=new RandomPlaybackSettings();
                try
                {
                    var config=JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath),"JXOPlayer.config.json")));
                    Settings.Folders=PlaybackLibrary.NormalizeRoots(config.Descendants().OfType<JProperty>().Where(p=>p.Name=="Directory"&&p.Value.Type==JTokenType.String).Select(p=>(string)p.Value)).ToList();
                }catch{}
            }
            Settings.Folders=Settings.Folders??new List<string>();
            if(Application.Current!=null)Application.Current.Exit+=OnExit;
            if(main?.DataContext!=null)
            {
                autoTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
                autoTimer.Tick+=AutoTick;autoTimer.Start();
                durationMatcher=new LocalDurationMatcher(durationProbe);
                // A player may already be open while the plugin is loading.
                // Re-run its initial lookup after the duration filter is ready.
                main.Dispatcher.BeginInvoke(new Action(()=>
                {
                    if(disposed)return;
                    var field=main.DataContext.GetType().GetField("_eventAggregator",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    (field?.GetValue(main.DataContext) as Stylet.IEventAggregator)?.Publish(new JXOPlayer.Common.ReloadScriptsRequestMessage());
                }));
            }
        }
        void OnExit(object sender,ExitEventArgs e)=>Dispose();
        void Notify(){if(!disposed)Changed?.Invoke();}
        void ResetAuto(){autoRevision++;autoGate.Reset();}
        public void SetAutoAdvance(bool enabled)
        {
            Settings.AutoAdvance=enabled;ResetAuto();
            log.Info("Auto advance setting changed [Enabled: {0}]",enabled);
            if(!enabled && automaticOpening){cancelledByUser=true;opening?.Cancel();}
            try{Save();}catch(Exception ex){Status="保存联播设置失败："+ex.Message;}
            Notify();
        }
        async void AutoTick(object sender,EventArgs e)
        {
            if(autoPolling || disposed)return;
            if(!Settings.AutoAdvance || Busy){autoGate.Reset();return;}
            autoPolling=true;
            try
            {
                string path=PotPlayerPlaybackReader.ConnectedPath(main.DataContext);
                LibraryVideo entry=null;string pathKey=PlaybackLibrary.PathIdentity(path);if(pathKey!=null)autoIndex.TryGetValue(pathKey,out entry);
                if(entry==null || !PlaybackLibrary.IsVideo(path)){autoGate.Reset();return;}
                int revision=autoRevision;
                var sample=await autoReader.ReadAsync(path,entry.Stats.Tier);
                if(disposed || !Settings.AutoAdvance || Busy || revision!=autoRevision || !PlaybackLibrary.SamePath(path,PotPlayerPlaybackReader.ConnectedPath(main.DataContext)))
                {autoGate.Reset();return;}
                var ended=autoGate.Observe(sample,System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency);
                if(ended!=null)
                {
                    log.Info("Auto advance EOF confirmed [Video: {0}, Tier: {1}]",path,ended.Tier);
                    // Script loading is asynchronous: recheck immediately before
                    // sending the path so a native playlist advance wins that race.
                    async Task<bool> StillEnded(CancellationToken token)
                    {
                        token.ThrowIfCancellationRequested();
                        if(!Settings.AutoAdvance || revision!=autoRevision || !PlaybackLibrary.SamePath(path,PotPlayerPlaybackReader.ConnectedPath(main.DataContext)))return false;
                        PlaybackObservation latest=null;
                        for(int attempt=0;attempt<3;attempt++)
                        {
                            latest=await autoReader.ReadAsync(path,ended.Tier);
                            if(latest!=null)break;
                            await Task.Delay(100,token);
                            if(!Settings.AutoAdvance || revision!=autoRevision || !PlaybackLibrary.SamePath(path,PotPlayerPlaybackReader.ConnectedPath(main.DataContext)))return false;
                        }
                        token.ThrowIfCancellationRequested();
                        return !disposed && Settings.AutoAdvance && revision==autoRevision && AutoAdvanceGate.Same(ended,latest) && latest.State==0
                            && Math.Abs(latest.Duration-ended.Duration)<.5 && PlaybackLibrary.SamePath(path,PotPlayerPlaybackReader.ConnectedPath(main.DataContext));
                    }
                    await PlayTierCoreAsync(ended.Tier,StillEnded);
                }
            }
            catch(Exception ex){log.Warn(ex,"Auto advance observation failed");autoGate.Reset();} // Never restart a missing player.
            finally{autoPolling=false;}
        }
        public void Save()
        {
            Directory.CreateDirectory(dataDirectory);
            string path=Path.Combine(dataDirectory,"RandomPlayback.settings.json"),temp=path+".tmp";
            File.WriteAllText(temp,JsonConvert.SerializeObject(Settings,Formatting.Indented));File.Move(temp,path,true);
        }
        public async Task RefreshAsync()
        {
            if(disposed||playing)return;
            ResetAuto();
            scan?.Cancel();var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);scan=request;
            Scanning=true;Status="正在建立视频与脚本索引…";Notify();
            var roots=Settings.Folders.ToArray();
            try
            {
                Save();
                var progress=new Progress<string>(message=>{if(ReferenceEquals(scan,request)&&!disposed){Status=message;Notify();}});
                var snapshot=await Task.Run(()=>PlaybackLibrary.Scan(roots,Path.Combine(dataDirectory,"RandomPlayback.cache.json"),request.Token,message=>((IProgress<string>)progress).Report(message),durationProbe),request.Token);
                request.Token.ThrowIfCancellationRequested();
                if(!ReferenceEquals(scan,request))return;
                Library=snapshot;autoIndex.Clear();foreach(var video in snapshot.Videos)autoIndex[PlaybackLibrary.PathIdentity(video.VideoPath)]=video;shuffle.Reset();Regrade();
                Status=Library.Videos.Count>0 ? $"{Library.Videos.Count} 部可播放 · 点击档位随机换片" : "没有找到可配对的视频与脚本，请检查视频库";
            }
            catch(OperationCanceledException){if(ReferenceEquals(scan,request))Status="扫描已停止，已保存扫描进度；刷新可继续，保留上次视频库";}
            catch(Exception ex){if(ReferenceEquals(scan,request))Status="扫描失败："+ex.GetBaseException().Message;}
            finally{if(ReferenceEquals(scan,request)){Scanning=false;scan=null;Notify();}request.Dispose();}
        }
        public void Regrade(){ResetAuto();IntensityScoring.Apply(Library.Videos,Settings.Relative);shuffle.Reset();Notify();}
        public void CancelPlayback(){ResetAuto();if(!playing)return;cancelledByUser=true;opening?.Cancel();}
        public Task PlayTierAsync(int tier){ResetAuto();return PlayTierCoreAsync(tier,null);}
        async Task PlayTierCoreAsync(int tier,Func<CancellationToken,Task<bool>> beforeOpen)
        {
            if(disposed||Busy||tier<1||tier>5)return;
            var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);opening=request;
            cancelledByUser=false;request.CancelAfter(playbackTimeout);
            automaticOpening=beforeOpen!=null;playing=true;Status=$"正在选择{Labels[tier-1]}强度视频…";Notify();
            try
            {
                if(play==null && bridge==null)bridge=new PotPlayerBridge(main.DataContext);
                string current=currentPath?.Invoke()??bridge?.CurrentPath??lastVideo;
                var selected=shuffle.Pick(Library.Videos,tier,current);
                if(selected==null){Status="这个档位暂无可播放视频，请刷新视频库";return;}
                var stamp=new FileInfo(selected.ScriptPath);
                if(stamp.Length!=selected.ScriptLength || stamp.LastWriteTimeUtc.Ticks!=selected.ScriptModified)
                {Status="脚本已改变，请刷新视频库后再按强度播放";return;}
                Status="PotPlayer 正在打开："+Path.GetFileNameWithoutExtension(selected.VideoPath);Notify();
                await (play!=null ? play(selected,request.Token) : bridge.Play(selected,request.Token,message=>
                {if(!disposed && ReferenceEquals(opening,request) && !request.IsCancellationRequested){Status=message;Notify();}},beforeOpen)).WaitAsync(request.Token);
                request.Token.ThrowIfCancellationRequested();
                shuffle.Commit(selected);lastVideo=selected.VideoPath;
                Status=$"{tier}档 {Labels[tier-1]} · {(automaticOpening?"自动联播 · ":"")}{Path.GetFileNameWithoutExtension(selected.VideoPath)}";
            }
            catch(OperationCanceledException){Status=automaticOpening?"自动联播已取消，保留播放器当前状态":cancelledByUser||lifetime.IsCancellationRequested?"已取消等待，可以重新选择视频":"打开视频或确认脚本超时，可以重试";}
            catch(Exception ex){Status=ex.GetBaseException().Message;}
            finally{opening=null;request.Dispose();playing=false;automaticOpening=false;Notify();}
        }
        public FrameworkElement CreateStrip(bool compact)
        {
            var panel=new StackPanel{Margin=new Thickness(compact?3:0,4,compact?3:0,2)};
            var header=new DockPanel{LastChildFill=true};
            var manage=new Button{Content="视频库",Padding=new Thickness(7,2,7,2),MinHeight=24,Margin=new Thickness(2,0,0,2),FontSize=11};
            manage.Click+=(s,e)=>ShowLibrary();DockPanel.SetDock(manage,Dock.Right);header.Children.Add(manage);
            var cancel=new Button{Name="CancelRandomOpen",Content="取消打开",Padding=new Thickness(6,2,6,2),MinHeight=24,Margin=new Thickness(2,0,0,2),FontSize=11,ToolTip="取消等待并恢复选片按钮，不停止已在播放的视频"};
            cancel.Click+=(s,e)=>CancelPlayback();DockPanel.SetDock(cancel,Dock.Right);header.Children.Add(cancel);
            var label=new TextBlock{Foreground=Brushes.LightSteelBlue,FontSize=11,VerticalAlignment=VerticalAlignment.Center};header.Children.Add(label);panel.Children.Add(header);
            var row=new UniformGrid{Columns=5};var buttons=new List<Button>();
            string[] colors={"#267C6A","#3278A6","#6651AA","#A57327","#AB475A"};
            for(int tier=1;tier<=5;tier++)
            {
                int selectedTier=tier;
                var button=new Button{Name="RandomTier"+tier,FontSize=compact?11:12,MinHeight=37,Padding=new Thickness(2,3,2,3),Margin=new Thickness(2),Background=(Brush)new BrushConverter().ConvertFromString(colors[tier-1]),Foreground=Brushes.White};
                button.Click+=async(s,e)=>await PlayTierAsync(selectedTier);buttons.Add(button);row.Children.Add(button);
            }
            panel.Children.Add(row);
            var auto=new CheckBox{Name="AutoAdvance",Content="自动联播同档视频",Foreground=Brushes.LightSteelBlue,FontSize=11,Margin=new Thickness(3,4,2,1),ToolTip="当前库内的视频播完后等待 2 秒；PotPlayer 没有继续播放时，随机播放同一强度档。音频不参与。"};
            auto.Click+=(s,e)=>SetAutoAdvance(auto.IsChecked==true);panel.Children.Add(auto);
            var status=new TextBlock{FontSize=10,Foreground=Brushes.LightSteelBlue,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(2,3,2,0)};panel.Children.Add(status);
            Action update=()=>
            {
                label.Text=Settings.Relative?"随机播放 · 库内相对强度":"随机播放 · 固定强度";
                auto.IsChecked=Settings.AutoAdvance;
                cancel.Visibility=playing?Visibility.Visible:Visibility.Collapsed;
                status.Text=Status;status.ToolTip=Status;
                for(int i=0;i<buttons.Count;i++)
                {
                    int count=Library.Videos.Count(v=>v.Stats.Tier==i+1);
                    buttons[i].Content=$"{i+1} {Labels[i]}\n{count} 部";
                    buttons[i].IsEnabled=!Busy&&count>0;
                    buttons[i].Opacity=buttons[i].IsEnabled?1:.5;
                    buttons[i].ToolTip=$"在{Labels[i]}强度的 {count} 部视频中随机选择，使用 PotPlayer 播放";
                }
            };
            panel.Loaded+=(s,e)=>{Changed-=update;Changed+=update;update();if(!started){started=true;_ = RefreshAsync();}};
            panel.Unloaded+=(s,e)=>Changed-=update;
            update();return panel;
        }
        public void ShowLibrary()
        {
            if(libraryWindow!=null){libraryWindow.Activate();return;}
            var window=new Window{Title="JXOPlayer 2.0 · 随机播放视频库",Width=600,Height=510,MinWidth=490,MinHeight=430,Owner=main,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=(Brush)new BrushConverter().ConvertFromString("#172131"),Foreground=Brushes.White};
            libraryWindow=window;
            var grid=new Grid{Margin=new Thickness(20)};foreach(var h in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto})grid.RowDefinitions.Add(new RowDefinition{Height=h});window.Content=grid;
            void Add(UIElement control,int row){Grid.SetRow(control,row);grid.Children.Add(control);}
            Add(new TextBlock{Text="视频库文件夹（包含子文件夹）",FontSize=17,Margin=new Thickness(0,0,0,12)},0);
            var folders=new ListBox{Background=(Brush)new BrushConverter().ConvertFromString("#243248"),Foreground=Brushes.White,BorderThickness=new Thickness(0)};Add(folders,1);
            void ReloadFolders(){folders.ItemsSource=null;folders.ItemsSource=Settings.Folders.ToArray();}ReloadFolders();
            var actions=new WrapPanel{Margin=new Thickness(0,10,0,12)};Add(actions,2);
            Button Button(string text,Action action){var b=new Button{Content=text,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,8,0)};b.Click+=(s,e)=>{try{action();}catch(Exception ex){Status=ex.GetBaseException().Message;Notify();}};actions.Children.Add(b);return b;}
            Button("添加文件夹",()=>{var dialog=new Microsoft.Win32.OpenFolderDialog{Title="选择视频或脚本所在文件夹",Multiselect=true};if(dialog.ShowDialog(window)==true){Settings.Folders=PlaybackLibrary.NormalizeRoots(Settings.Folders.Concat(dialog.FolderNames)).ToList();Save();ReloadFolders();_ = RefreshAsync();}});
            Button("移除",()=>{if(folders.SelectedItem is string folder){Settings.Folders.Remove(folder);Save();ReloadFolders();_ = RefreshAsync();}});
            var refresh=Button("刷新视频库",()=>{if(playing)CancelPlayback();else if(Scanning)scan?.Cancel();else _ = RefreshAsync();});
            var mode=new CheckBox{Content="按当前视频库相对分档（ES 默认算法）",IsChecked=Settings.Relative,Foreground=Brushes.White,Margin=new Thickness(0,0,0,10)};
            mode.Click+=(s,e)=>{Settings.Relative=mode.IsChecked==true;try{Save();Regrade();}catch(Exception ex){Status=ex.Message;Notify();}};Add(mode,3);
            Add(new TextBlock{Text="随机库仅包含视频；音频正常播放、匹配脚本，不参与随机分档和联播。\n唯一同名主脚本可直接配对，允许脚本只覆盖部分内容。多个同名候选比较时长；相近时优先同目录，相同动作副本不算冲突。\n平均速度占 80%，峰值占 20%；默认 PotPlayer。自动联播开关位于主界面和悬浮窗的档位按钮下方。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightSteelBlue,FontSize=12,Margin=new Thickness(0,0,0,10)},4);
            var detail=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightSteelBlue,FontSize=11};Add(detail,5);
            Action update=()=>{detail.Text=Status+"\n"+Library.Summary;refresh.Content=playing?"取消打开":Scanning?"停止扫描":"刷新视频库";foreach(var b in actions.Children.OfType<Button>())b.IsEnabled=!playing || ReferenceEquals(b,refresh);mode.IsEnabled=!Busy;};
            Changed+=update;update();window.Closed+=(s,e)=>{Changed-=update;libraryWindow=null;};window.Show();
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;autoTimer?.Stop();if(autoTimer!=null)autoTimer.Tick-=AutoTick;ResetAuto();lifetime.Cancel();scan?.Cancel();bridge?.Dispose();durationMatcher?.Dispose();libraryWindow?.Close();LocalMediaRelay.Stop();
            if(Application.Current!=null)Application.Current.Exit-=OnExit;Changed=null;
        }
    }
}
