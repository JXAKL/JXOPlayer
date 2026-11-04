using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

public static class CleanStartup
{
    static int checks;
    static DateTime start=DateTime.UtcNow;
    static DispatcherTimer timer;
    static string report;
    static object Get(object obj,string name) => obj.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).GetValue(obj);
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);File.AppendAllText(report,"PASS "+message+"\n");checks++;}
    [STAThread] public static int Main()
    {
        Directory.SetCurrentDirectory(Path.GetDirectoryName(Environment.ProcessPath));
        report=Path.Combine(Environment.CurrentDirectory,"clean-startup.txt");File.WriteAllText(report,"");
        timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
        timer.Tick+=(s,e)=>{
            try{
                if((DateTime.UtcNow-start).TotalSeconds>30)throw new Exception("Clean startup timed out");
                var main=Application.Current?.MainWindow;
                var host=main?.Content as DockPanel;
                if(host==null || !Equals(host.Tag,"JXOPlayer.FloatingAxisControl.MainHost"))return;
                var settings=((Border)host.Children[0]).Tag;
                if(settings.GetType().GetField("Running").GetValue(settings)==null)return;
                Check(main.IsVisible && main.Title=="JXOPlayer 2.0","clean main window and overlay load");
                var random=Get(settings,"RandomPlayback");
                var rs=Get(random,"Settings");
                Check(((IList)rs.GetType().GetField("Folders").GetValue(rs)).Count==0,"random library is empty");
                Check(!(bool)rs.GetType().GetField("AutoAdvance").GetValue(rs),"automatic playback defaults off");
                var output=Get(main.DataContext,"OutputTarget");
                Check(!(bool)Get(output,"AutoSearchOnStartup"),"automatic startup device search defaults off");
                var targets=((IEnumerable)Get(output,"Items")).Cast<object>().ToArray();
                Check(targets.Length==3,"three fresh device transport options are available");
                Check(targets.All(t=>!(bool)Get(t,"AutoConnectEnabled")),"device automatic connections default off");
                Check(targets.All(t=>Get(t,"Status").ToString()=="Disconnected"),"no device is connected");
                Check(File.Exists("PotPlayer/PotPlayerMini64.exe") && File.Exists("Bin/mpv.exe"),"bundled playback and duration components exist");
                File.AppendAllText(report,"COMPLETE "+checks+" checks\n");timer.Stop();main.Close();
            }catch(Exception ex){timer.Stop();File.AppendAllText(report,"FAIL "+ex+"\n");Application.Current?.Shutdown(1);}
        };
        timer.Start();
        try{
            var assembly=Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"JXOPlayer.dll"));
            typeof(Assembly).GetMethod("SetEntryAssembly",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{assembly});
            typeof(Application).GetField("_resourceAssembly",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,assembly);
            assembly.EntryPoint.Invoke(null,assembly.EntryPoint.GetParameters().Length==0?null:new object[]{Array.Empty<string>()});
            return File.ReadAllText(report).Contains("COMPLETE")?0:1;
        }catch(Exception ex){File.AppendAllText(report,"FAIL "+ex);return 1;}
    }
}
