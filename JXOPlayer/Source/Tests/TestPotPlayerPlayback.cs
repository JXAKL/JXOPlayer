using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Interop;
using JXOPlayer.Common;
using Stylet;

public static class TestPotPlayerPlayback
{
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,UIntPtr wparam,IntPtr lparam,uint flags,uint timeout,out UIntPtr result);
    [StructLayout(LayoutKind.Sequential)] struct CopyData {public IntPtr Tag;public int Size;public IntPtr Bytes;}
    static int checks;
    static DispatcherTimer timer;
    static DateTime started;
    static object Get(object obj,string name)=>obj?.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(obj);
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;File.AppendAllText("potplayer-results.txt","PASS "+text+"\n");}
    [STAThread] public static int Main()
    {
        Directory.SetCurrentDirectory(Path.GetDirectoryName(Environment.ProcessPath));File.WriteAllText("potplayer-results.txt","");
        timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};timer.Tick+=Tick;started=DateTime.UtcNow;timer.Start();
        try
        {
            var assembly=Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"JXOPlayer.dll"));
            typeof(Assembly).GetMethod("SetEntryAssembly",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{assembly});
            typeof(Application).GetField("_resourceAssembly",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,assembly);
            assembly.EntryPoint.Invoke(null,null);
            return File.ReadAllText("potplayer-results.txt").Contains("COMPLETE")?0:1;
        }catch(Exception ex){File.AppendAllText("potplayer-results.txt","FAIL "+ex);return 1;}
    }
    static async void Tick(object sender,EventArgs args)
    {
        var main=Application.Current?.MainWindow;
        if(!(main?.Content is DockPanel host) || !Equals(host.Tag,"JXOPlayer.FloatingAxisControl.MainHost"))
        {if((DateTime.UtcNow-started).TotalSeconds>20){timer.Stop();Application.Current?.Shutdown(1);}return;}
        timer.Stop();
        try
        {
            var nativePlayer=((IEnumerable)Get(Get(main.DataContext,"MediaSource"),"AvailableSources")).Cast<object>().Single(p=>(string)Get(p,"Name")=="PotPlayer");
            nativePlayer.GetType().GetProperty("AutoStartEnabled").SetValue(nativePlayer,false);
            var sendFilename=nativePlayer.GetType().GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Single(m=>m.Name.Contains("SetValueString"));
            string packetName=Path.Combine(Environment.CurrentDirectory,"videos","文件名 边界.mp4");byte[] packet=null;
            var hwnd=new WindowInteropHelper(main).Handle;var source=HwndSource.FromHwnd(hwnd);
            HwndSourceHook capture=(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled)=>
            {
                if(message==74){var data=Marshal.PtrToStructure<CopyData>(lParam);packet=new byte[data.Size];Marshal.Copy(data.Bytes,packet,0,data.Size);handled=true;return new IntPtr(1);}return IntPtr.Zero;
            };
            source.AddHook(capture);
            try{sendFilename.Invoke(nativePlayer,new object[]{hwnd,Enum.Parse(sendFilename.GetParameters()[1].ParameterType,"SetFilename"),packetName,Encoding.Unicode});}
            finally{source.RemoveHook(capture);}
            Check(packet!=null && packet.SequenceEqual(Encoding.Unicode.GetBytes(packetName+"\0")),"native filename message contains the complete UTF-16 terminator and byte count");
            var settings=((Border)host.Children[0]).Tag;var service=Get(settings,"RandomPlayback");
            var pathTools=service.GetType().Assembly.GetType("JXOPlayer.Features.PlaybackLibrary");
            bool SameMedia(string a,string b)=>(bool)pathTools.GetMethod("SamePath").Invoke(null,new object[]{a,b});
            var scriptView=Get(main.DataContext,"Script");
            var repository=((IEnumerable)Get(Get(scriptView,"ScriptRepositoryManager"),"Repositories")).Cast<object>().Single(r=>r.GetType().Name=="LocalScriptRepository");
            var libraryType=typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Script.Repository.ViewModels.ScriptLibrary",true);
            var library=Activator.CreateInstance(libraryType,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{new DirectoryInfo(Path.Combine(Environment.CurrentDirectory,"videos"))},null);
            libraryType.GetProperty("Recursive").SetValue(library,true);
            var libraries=Get(repository,"ScriptLibraries");libraries.GetType().GetMethod("Add").Invoke(libraries,new[]{library});
            var prefs=Get(service,"Settings");var folders=(IList)prefs.GetType().GetField("Folders").GetValue(prefs);folders.Clear();folders.Add(Path.Combine(Environment.CurrentDirectory,"videos"));
            await (Task)service.GetType().GetMethod("RefreshAsync").Invoke(service,null);
            Check(!((bool)Get(service,"Scanning")),"synthetic five-tier library finishes scanning");
            foreach(int tier in new[]{1,5,5,2,4})
            {
                await (Task)service.GetType().GetMethod("PlayTierAsync").Invoke(service,new object[]{tier});
                string status=(string)Get(service,"Status");Check(status.StartsWith(tier+"档"),"real PotPlayer playback succeeds for tier "+tier+" ("+status+")");
                var script=Get(main.DataContext,"Script");var path=(string)Get(Get(script,"MediaResource"),"OriginalPath");
                Check(Path.GetFileName(path)==$"强度 {tier}.mp4","PotPlayer reports the selected Unicode video filename");
                var models=Get(script,"AxisModels");var model=models.GetType().GetProperty("Item").GetValue(models,new object[]{DeviceAxis.Parse("L0")});
                Check((string)Get(Get(model,"Script"),"Name")==$"强度 {tier}.funscript","motion script matches the selected intensity tier");
                if(tier==1)Check(((string)Get(Get(model,"Script"),"Source")).EndsWith("correct-cut"),"random playback chooses correct duration across duplicate same-named scripts");
                // Polling may report the old position once after a same-video
                // seek. Observe advancement after any pending reset to zero.
                double before=(double)Get(script,"MediaPosition");bool advancing=false;
                var advanceDeadline=DateTime.UtcNow.AddSeconds(3);
                do
                {
                    await Task.Delay(200);double after=(double)Get(script,"MediaPosition");
                    if(after<before)before=after;
                    else if(after>before+.05)advancing=true;
                }while(!advancing && DateTime.UtcNow<advanceDeadline);
                Check(advancing,"PotPlayer playback position continues advancing");
                var outputs=(IEnumerable)Get(Get(main.DataContext,"OutputTarget"),"Items");
                Check(outputs.Cast<object>().All(o=>Get(o,"Status").ToString()=="Disconnected"),"test hardware output transports stay disconnected");
            }
            // Real IPC round trips must preserve complete long Unicode paths and
            // literal URL-looking punctuation, not just parse a short test name.
            string nameCases=Path.Combine(Environment.CurrentDirectory,"name-cases");
            var complexPaths=new System.Collections.Generic.List<string>();
            string[] complexNames={"中文_日本語_English_😀👩‍💻❤️", "[字幕] 100% #1 & + ; ' $` %23 %20",new string('長',220)+"日本語😀", "cafe\u0301_か\u3099"};
            for(int caseIndex=0;caseIndex<complexNames.Length;caseIndex++)
            {
                string caseFolder=Path.Combine(nameCases,caseIndex.ToString(),new string('深',75),new string('階',75));Directory.CreateDirectory(caseFolder);
                string caseVideo=Path.Combine(caseFolder,complexNames[caseIndex]+".mp4");
                complexPaths.Add(caseVideo);
                File.Copy(Path.Combine(Environment.CurrentDirectory,"Fixtures","duration-12s.mp4"),caseVideo);
                File.WriteAllText(Path.ChangeExtension(caseVideo,".funscript"),"{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":6000,\"pos\":100},{\"at\":12000,\"pos\":0}]}");
                folders.Clear();folders.Add(caseFolder);await (Task)service.GetType().GetMethod("RefreshAsync").Invoke(service,null);
                var snapshot=Get(service,"Library");var candidate=((IEnumerable)snapshot.GetType().GetField("Videos").GetValue(snapshot)).Cast<object>().Single();
                var stats=candidate.GetType().GetField("Stats").GetValue(candidate);int caseTier=(int)stats.GetType().GetField("Tier").GetValue(stats);
                await (Task)service.GetType().GetMethod("PlayTierAsync").Invoke(service,new object[]{caseTier});
                Check(((string)Get(service,"Status")).StartsWith(caseTier+"档"),"real random open succeeds for complex filename case "+caseIndex+", path length "+caseVideo.Length+" ("+Get(service,"Status")+")");
                string returnedPath=(string)Get(Get(scriptView,"MediaResource"),"OriginalPath");
                Check((bool)pathTools.GetMethod("SamePath").Invoke(null,new object[]{returnedPath,caseVideo}),"PotPlayer returned path resolves to original complex file "+caseIndex);
                var caseModels=Get(scriptView,"AxisModels");var caseModel=caseModels.GetType().GetProperty("Item").GetValue(caseModels,new object[]{DeviceAxis.Parse("L0")});
                Check((string)Get(Get(caseModel,"Script"),"Name")==complexNames[caseIndex]+".funscript","complex filename displays its exact paired script "+caseIndex);
                nativePlayer.GetType().GetMethod("Handle",new[]{typeof(IMediaSourceControlMessage)}).Invoke(nativePlayer,new object[]{new MediaSeekMessage(TimeSpan.FromSeconds(8))});
                var seekDeadline=DateTime.UtcNow.AddSeconds(4);
                while((double)Get(scriptView,"MediaPosition")<5 && DateTime.UtcNow<seekDeadline)await Task.Delay(100);
                Check((double)Get(scriptView,"MediaPosition")>=5,"complex filename supports real seeking "+caseIndex);
            }
            folders.Clear();folders.Add(nameCases);await (Task)service.GetType().GetMethod("RefreshAsync").Invoke(service,null);
            nativePlayer.GetType().GetMethod("Handle",new[]{typeof(IMediaSourceControlMessage)}).Invoke(nativePlayer,new object[]{new MediaChangePathMessage(complexPaths[2])});
            Check(await WaitForScript(complexPaths[2],complexNames[2]+".funscript"),"native non-random open also resolves long path and its original script");
            service.GetType().GetMethod("SetAutoAdvance").Invoke(service,new object[]{true});
            await Task.Delay(500);
            nativePlayer.GetType().GetMethod("Handle",new[]{typeof(IMediaSourceControlMessage)}).Invoke(nativePlayer,new object[]{new MediaSeekMessage(TimeSpan.FromSeconds(8.5))});
            var longAutoDeadline=DateTime.UtcNow.AddSeconds(12);
            while((!((string)Get(service,"Status")).Contains("自动联播") || (bool)Get(service,"Busy")) && DateTime.UtcNow<longAutoDeadline)await Task.Delay(100);
            Check(((string)Get(service,"Status")).Contains("自动联播") && !(bool)Get(service,"Busy") && !SameMedia((string)Get(Get(scriptView,"MediaResource"),"OriginalPath"),complexPaths[2]),"long-path media reaches EOF and automatically continues in its original tier");
            service.GetType().GetMethod("SetAutoAdvance").Invoke(service,new object[]{false});
            folders.Clear();folders.Add(Path.Combine(Environment.CurrentDirectory,"videos"));await (Task)service.GetType().GetMethod("RefreshAsync").Invoke(service,null);
            // Direct player opening does not use the random bridge's chosen L0.
            // It must independently resolve the duplicate through native lookup.
            string manual=Path.Combine(Environment.CurrentDirectory,"videos","强度 1.mp4");
            nativePlayer.GetType().GetMethod("Handle",new[]{typeof(IMediaSourceControlMessage)}).Invoke(nativePlayer,new object[]{new MediaChangePathMessage(manual)});
            var manualDeadline=DateTime.UtcNow.AddSeconds(6);bool matched=false;
            do
            {
                await Task.Delay(100);
                var models=Get(scriptView,"AxisModels");var model=models.GetType().GetProperty("Item").GetValue(models,new object[]{DeviceAxis.Parse("L0")});
                matched=string.Equals((string)Get(Get(scriptView,"MediaResource"),"OriginalPath"),manual,StringComparison.OrdinalIgnoreCase)
                    && ((string)Get(Get(model,"Script"),"Source"))?.EndsWith("correct-cut")==true;
            }while(!matched && DateTime.UtcNow<manualDeadline);
            Check(matched,"manual PotPlayer path change uses duration to bypass wrong same-folder script without random override");
            // The compatibility regression must include a pure audio stream and
            // scripts that deliberately stop well before the end of the media.
            string audioFolder=Path.Combine(Environment.CurrentDirectory,"audio-cases");Directory.CreateDirectory(audioFolder);
            string audioPath=Path.Combine(audioFolder,"音频局部脚本.mp3");
            File.Copy(Path.Combine(Environment.CurrentDirectory,"Fixtures","duration-12s.mp3"),audioPath);
            string partial="{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":2000,\"pos\":100},{\"at\":4000,\"pos\":0}]}";
            File.WriteAllText(Path.Combine(audioFolder,"音频局部脚本.funscript"),partial);
            var audioLibrary=Activator.CreateInstance(libraryType,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{new DirectoryInfo(audioFolder)},null);
            libraries.GetType().GetMethod("Add").Invoke(libraries,new[]{audioLibrary});
            async Task<bool> WaitForScript(string path,string expected)
            {
                var end=DateTime.UtcNow.AddSeconds(6);
                do
                {
                    await Task.Delay(100);
                    var models=Get(scriptView,"AxisModels");var model=models.GetType().GetProperty("Item").GetValue(models,new object[]{DeviceAxis.Parse("L0")});
                    if(SameMedia((string)Get(Get(scriptView,"MediaResource"),"OriginalPath"),path)
                        && (string)Get(Get(model,"Script"),"Name")==expected)return true;
                }while(DateTime.UtcNow<end);
                return false;
            }
            nativePlayer.GetType().GetMethod("Handle",new[]{typeof(IMediaSourceControlMessage)}).Invoke(nativePlayer,new object[]{new MediaChangePathMessage(audioPath)});
            Check(await WaitForScript(audioPath,"音频局部脚本.funscript"),"manually opened pure MP3 loads a four-second script for twelve-second audio");
            double audioPosition=(double)Get(scriptView,"MediaPosition");await Task.Delay(400);
            Check((double)Get(scriptView,"MediaPosition")>audioPosition,"actual MP3 playback advances after native script matching");
            folders.Clear();folders.Add(audioFolder);await (Task)service.GetType().GetMethod("RefreshAsync").Invoke(service,null);
            var audioSnapshot=Get(service,"Library");var entries=((IEnumerable)audioSnapshot.GetType().GetField("Videos").GetValue(audioSnapshot)).Cast<object>().ToArray();
            Check(entries.Length==0,"random library excludes audio without breaking manual audio script matching");
            string partialVideo=Path.Combine(audioFolder,"视频局部脚本.mp4");
            File.Copy(Path.Combine(Environment.CurrentDirectory,"Fixtures","duration-12s.mp4"),partialVideo);
            File.WriteAllText(Path.Combine(audioFolder,"视频局部脚本.funscript"),partial);
            nativePlayer.GetType().GetMethod("Handle",new[]{typeof(IMediaSourceControlMessage)}).Invoke(nativePlayer,new object[]{new MediaChangePathMessage(partialVideo)});
            Check(await WaitForScript(partialVideo,"视频局部脚本.funscript"),"manually opened video also loads its partial-coverage script");
            using(var observing=Process.GetProcessesByName("JxoPlaybackProbe").Single(p=>p.MainModule.FileName.StartsWith(Environment.CurrentDirectory,StringComparison.OrdinalIgnoreCase)))
            {
                var window=observing.MainWindowHandle;
                void Send(IMediaSourceControlMessage message)=>nativePlayer.GetType().GetMethod("Handle",new[]{typeof(IMediaSourceControlMessage)}).Invoke(nativePlayer,new object[]{message});
                int State(){SendMessageTimeout(window,1024,new UIntPtr(20486),IntPtr.Zero,2,150,out var value);return unchecked((int)value.ToUInt64());}
                string Current()=>(string)Get(Get(scriptView,"MediaResource"),"OriginalPath");
                void Auto(bool enabled)=>service.GetType().GetMethod("SetAutoAdvance").Invoke(service,new object[]{enabled});
                async Task Finish()
                {
                    Send(new MediaSeekMessage(TimeSpan.FromSeconds(8.5)));Send(new MediaPlayPauseMessage(true));
                    await Task.Delay(500);var until=DateTime.UtcNow.AddSeconds(6);
                    while(State()!=0 && DateTime.UtcNow<until)await Task.Delay(100);
                    Check(State()==0,"real player reaches natural end");
                }
                string nextVideo=Path.Combine(audioFolder,"同档下一部.mp4");File.Copy(partialVideo,nextVideo);
                File.WriteAllText(Path.ChangeExtension(nextVideo,".funscript"),partial);
                await (Task)service.GetType().GetMethod("RefreshAsync").Invoke(service,null);
                entries=((IEnumerable)Get(service,"Library").GetType().GetField("Videos").GetValue(Get(service,"Library"))).Cast<object>().ToArray();
                Check(entries.Length==2,"mixed folder contains only its two videos in random tiers");
                await Finish();await Task.Delay(2500);
                Check(Current()==partialVideo && State()==0,"auto-next off leaves finished video stopped");
                Auto(true);await Task.Delay(500);
                Check(Current()==partialVideo && State()==0,"enabling auto-next does not replay an already stopped video");
                Send(new MediaSeekMessage(TimeSpan.FromSeconds(8.5)));Send(new MediaPlayPauseMessage(true));await Task.Delay(1800);
                Send(new MediaPlayPauseMessage(false));await Task.Delay(2600);
                Check(Current()==partialVideo && State()==1,"pausing near the end does not auto-open another video");
                await Finish();
                Check(await WaitForScript(nextVideo,"同档下一部.funscript"),"natural EOF automatically opens another video and its matching script");
                var autoDone=DateTime.UtcNow.AddSeconds(4);while((bool)Get(service,"Busy") && DateTime.UtcNow<autoDone)await Task.Delay(100);
                Check(((string)Get(service,"Status")).Contains("自动联播") && State()==2,"same-tier automatic opening completes and releases the controls");
                for(int cycle=0;cycle<3;cycle++)
                {
                    string expected=Current()==nextVideo?partialVideo:nextVideo;
                    await Finish();
                    Check(await WaitForScript(expected,Path.GetFileNameWithoutExtension(expected)+".funscript"),"consecutive auto-next cycle "+(cycle+2)+" loads its paired script");
                    var done=DateTime.UtcNow.AddSeconds(4);while((bool)Get(service,"Busy") && DateTime.UtcNow<done)await Task.Delay(100);
                    Check(!(bool)Get(service,"Busy") && State()==2 && ((string)Get(service,"Status")).Contains("自动联播"),"consecutive auto-next cycle "+(cycle+2)+" resumes and releases controls");
                }
                // Native playlist continuation is represented by a normal player
                // path change during the grace period, without the random bridge.
                await Finish();Send(new MediaChangePathMessage(manual));
                Check(await WaitForScript(manual,"强度 1.funscript"),"native next video loads during the auto-next grace period");
                await Task.Delay(2500);Check(Current()==manual && State()==2,"native playlist continuation is not overwritten by random playback");
                Auto(false);
            }
            folders.Clear();folders.Add(Path.Combine(Environment.CurrentDirectory,"videos"));await (Task)service.GetType().GetMethod("RefreshAsync").Invoke(service,null);
            // Remove bridge search notifications entirely: direct loading must
            // remain usable even if native repository completion is unavailable.
            var bridge=service.GetType().GetField("bridge",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(service);
            var events=(IEventAggregator)main.DataContext.GetType().GetField("_eventAggregator",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(main.DataContext);
            events.Unsubscribe((IHandle)bridge);
            libraries.GetType().GetMethod("Clear").Invoke(libraries,null);
            repository.GetType().GetProperty("Enabled").SetValue(repository,false);
            await (Task)service.GetType().GetMethod("PlayTierAsync").Invoke(service,new object[]{3});
            Check(((string)Get(service,"Status")).StartsWith("3档"),"real random playback succeeds without native library or search notifications");
            Check(await WaitForScript(Path.Combine(Environment.CurrentDirectory,"videos","强度 3.mp4"),"强度 3.funscript"),"preloaded script is displayed when native lookup supplies no result");
            events.Subscribe((IHandle)bridge);
            repository.GetType().GetProperty("Enabled").SetValue(repository,true);
            libraries.GetType().GetMethod("Add").Invoke(libraries,new[]{library});
            // Switch repeatedly without sleeps; verify each completed request
            // actually owns the displayed script rather than merely a loaded path.
            foreach(int rapidTier in new[]{2,5,1,4,3,1,5,2})
            {
                await (Task)service.GetType().GetMethod("PlayTierAsync").Invoke(service,new object[]{rapidTier});
                Check(((string)Get(service,"Status")).StartsWith(rapidTier+"档") && await WaitForScript(Path.Combine(Environment.CurrentDirectory,"videos",$"强度 {rapidTier}.mp4"),$"强度 {rapidTier}.funscript"),"repeated selection confirms the current script for tier "+rapidTier);
            }
            var pending=(Task)service.GetType().GetMethod("PlayTierAsync").Invoke(service,new object[]{3});
            var scriptModel=Get(main.DataContext,"Script");
            await Task.Delay(50);
            Check(!pending.IsCompleted && (bool)Get(service,"Busy"),"real player can be closed while a selection is still pending");
            string playerPath=Path.Combine(Environment.CurrentDirectory,"PotPlayer","JxoPlaybackProbe.exe");
            var testPlayer=Process.GetProcessesByName("JxoPlaybackProbe").Single(p=>string.Equals(p.MainModule.FileName,playerPath,StringComparison.OrdinalIgnoreCase));
            testPlayer.CloseMainWindow();
            await pending.WaitAsync(TimeSpan.FromSeconds(4));
            Check(!(bool)Get(service,"Busy") && !((string)Get(service,"Status")).Contains("正在"),"closing real PotPlayer releases controls and clears the opening state");
            await testPlayer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));testPlayer.Dispose();
            var restart=new ProcessStartInfo(playerPath){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(playerPath)};
            restart.ArgumentList.Add(Path.Combine(Environment.CurrentDirectory,"videos","强度 1.mp4"));
            using(var reopened=Process.Start(restart))
            {
                try
                {
                    var ready=DateTime.UtcNow.AddSeconds(5);
                    do{await Task.Delay(100);reopened.Refresh();}while(!reopened.HasExited && reopened.MainWindowHandle==IntPtr.Zero && DateTime.UtcNow<ready);
                    Check(!reopened.HasExited && reopened.MainWindowHandle!=IntPtr.Zero,"restarted isolated player window is ready before reconnecting");
                    await (Task)service.GetType().GetMethod("PlayTierAsync").Invoke(service,new object[]{5});
                    Check(((string)Get(service,"Status")).StartsWith("5档") && !(bool)Get(service,"Busy"),"random selection succeeds again after restarting the isolated player ("+Get(service,"Status")+")");
                }
                finally{if(!reopened.HasExited){reopened.CloseMainWindow();await reopened.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}}
            }
            // Let native polling finish its disconnect/reset callback before
            // closing the WPF host; closing both processes in the same instant
            // can race the original application's synchronous axis teardown.
            var disconnectDeadline=DateTime.UtcNow.AddSeconds(4);
            while((Get(nativePlayer,"Status").ToString()!="Disconnected" || Get(scriptView,"MediaResource")!=null) && DateTime.UtcNow<disconnectDeadline)await Task.Delay(100);
            await Task.Delay(300);
            Check(Get(nativePlayer,"Status").ToString()=="Disconnected","native player disconnect completes before isolated host shutdown");
            File.AppendAllText("potplayer-results.txt","COMPLETE "+checks+" checks\n");
        }catch(Exception ex){File.AppendAllText("potplayer-results.txt","FAIL "+ex.GetBaseException()+"\n");}
        finally{main.Close();}
    }
}
