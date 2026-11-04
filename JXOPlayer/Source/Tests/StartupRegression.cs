using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using JXOPlayer.Common;

// Runs the real JXOPlayer entry point in a copied, disconnected test directory.
// UI actions below are regression tests against that isolated application.
public static class StartupRegression
{
    static int phase, stage, checks;
    static DateTime started, next;
    static DispatcherTimer timer;
    static string result;
    static System.Threading.Tasks.Task<double[]> outputStream;
    static object outputTarget, originalRanges;
    static string homeSettings;
    static double rotationTravel;
    static int rotationCommands;
    static object Get(object o, string name) => o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
    static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
    static void Assert(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++; File.AppendAllText(result, "PASS " + description + "\n");
    }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [STAThread]
    public static int Main(string[] args)
    {
        Directory.SetCurrentDirectory(Path.GetDirectoryName(Environment.ProcessPath));
        phase = int.Parse(args[0]); started = DateTime.UtcNow;
        result = Path.Combine(AppContext.BaseDirectory, "startup-" + phase + ".txt");
        File.WriteAllText(result, "Real application startup, phase " + phase + "\n");
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += Tick; timer.Start();
        try
        {
            var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "JXOPlayer.dll"));
            typeof(Assembly).GetMethod("SetEntryAssembly", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { assembly });
            // WPF normally derives this from the executable entry assembly.
            // The test executable hosts the original App, so use its resources.
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, assembly);
            var entry = assembly.EntryPoint;
            entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() });
            return File.ReadAllText(result).Contains("COMPLETE") ? 0 : 1;
        }
        catch (Exception e) { File.AppendAllText(result, "FAIL " + e + "\n"); return 1; }
    }
    static void Tick(object sender, EventArgs e)
    {
        try
        {
            if ((DateTime.UtcNow - started).TotalSeconds > 40) throw new Exception("Startup timed out at stage " + stage);
            var main = Application.Current?.MainWindow;
            var host = main?.Content as DockPanel;
            if (host == null || !Equals(host.Tag, "JXOPlayer.FloatingAxisControl.MainHost")) return;
            var bar = (Border)host.Children[0]; var settings = bar.Tag;
            var running = settings.GetType().GetField("Running").GetValue(settings);
            var toggle = (Button)bar.FindName("ToggleOverlay");
            if (stage == 0)
            {
                if (phase != 3 && running == null) return;
                Assert(main.IsVisible, "real main window is visible");
                TestBranding(main);
                Assert(host.Children.Count == 2, "main controls integrated above original UI once");
                Assert(toggle != null, "show/hide control available directly in main UI");
                Assert(bar.FindName("MainTremor") is Button && bar.FindName("MainRage") is Button, "both stimulus buttons are present in the native main toolbar");
                Assert(bar.FindName("MainHome") is Button, "all-axis home button is present directly in the native main toolbar");
                if (phase == 3)
                {
                    Assert(running == null, "disabled autostart keeps overlay closed but main controls available");
                    Click(toggle); stage = 1; return;
                }
                Assert((bool)Get(running, "PanelVisible"), phase == 1 ? "legacy Enabled=false does not suppress autostart" : "overlay automatically reopens after previous close-and-exit");
                if (phase == 1)
                {
                    TestHeatmap(running);
                    // The close button ultimately follows Window.Closing/HidePanel.
                    ((Window)Field(running, "_window")).Close();
                    Assert(!(bool)Get(running, "PanelVisible"), "close hides overlay only for current session");
                    Assert((bool)Get(settings, "AutoStart"), "close retains startup preference");
                    var json = JObject.Parse(File.ReadAllText("Plugins/FloatingAxisControl.config.json"));
                    Assert((bool)json["AutoStart"] && json["Enabled"] == null, "saved settings exclude obsolete hidden-state gate");
                    Capture(main, "主界面-悬浮控制.png");
                    Finish(main); return;
                }
                Click(toggle); Assert(!(bool)Get(running, "PanelVisible"), "main toolbar hides overlay");
                Click(toggle); Assert((bool)Get(running, "PanelVisible"), "main toolbar brings overlay back without plugin dialog");
                var auto = (CheckBox)bar.FindName("ShowOnStartup");
                auto.IsChecked = false; auto.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(!(bool)Get(settings, "AutoStart"), "explicit startup checkbox turns autostart off");
                Finish(main); return;
            }
            if (stage == 1)
            {
                if (running == null || !(bool)Get(running, "PanelVisible")) return;
                Assert(true, "main toolbar starts stopped controller and opens overlay");
                Assert(!(bool)Get(settings, "AutoStart"), "manual opening does not override explicit startup preference");
                Click((Button)bar.FindName("ToggleMotion"));
                Assert((bool)Field(running, "_held"), "main pause button holds real disconnected output transports");
                Click((Button)bar.FindName("ToggleMotion"));
                stage = 2; next = DateTime.UtcNow.AddSeconds(1); return;
            }
            if (stage == 2 && DateTime.UtcNow >= next)
            {
                Assert(!(bool)Field(running, "_held"), "main resume button restores real transport output references");
                outputTarget = ((System.Collections.IEnumerable)Get(Get(main.DataContext, "OutputTarget"), "Items")).Cast<object>().First(t => t.GetType().Name == "BluetoothOutputTarget");
                originalRanges = Get(outputTarget, "AxisSettings");
                homeSettings = HomeSettings(main);
                Click((Button)bar.FindName("MainTremor"));
                Assert((int)Field(running, "_stimulusMode") == 1, "main tremor button controls the real disconnected native output contexts");
                var getValues = outputTarget.GetType().GetMethod("GetValues", BindingFlags.Instance | BindingFlags.NonPublic);
                outputStream = System.Threading.Tasks.Task.Run(() =>
                {
                    var trace = new List<double>();
                    var rotation = DeviceAxis.Parse("R0");
                    double lastRotation = double.NaN, minRotation = 1, maxRotation = 0;
                    rotationCommands = 0;
                    for (int i = 0; i < 40; i++)
                    {
                        var values = new Dictionary<DeviceAxis, double>();
                        getValues.Invoke(outputTarget, new object[] { values, null });
                        if (!DeviceAxis.ToString(values, 10).Contains("I10")) throw new Exception("Native interpolation duration missing");
                        if (DeviceAxis.IsValueDirty(values[rotation], lastRotation) && DeviceAxis.ToString(rotation, values[rotation], 10).StartsWith("R0")) rotationCommands++;
                        lastRotation = values[rotation]; minRotation = Math.Min(minRotation,lastRotation); maxRotation = Math.Max(maxRotation,lastRotation);
                        trace.Add(values[DeviceAxis.Parse("L0")]);
                        System.Threading.Thread.Sleep(10);
                    }
                    rotationTravel = maxRotation - minRotation;
                    return trace.ToArray();
                });
                // Deliberately block WPF in this hardware-isolated copy. Real
                // GetValues and the native TCode formatter must keep progressing.
                System.Threading.Thread.Sleep(450);
                stage = 3; next = DateTime.UtcNow.AddSeconds(1); return;
            }
            if (stage == 3 && DateTime.UtcNow >= next)
            {
                Assert((int)Field(running, "_stimulusMode") == 1, "stimulus timer reads native update intervals and speed settings without errors");
                var trace = outputStream.GetAwaiter().GetResult();
                Assert(trace.Distinct().Count() > 32 && trace.Max() - trace.Min() > .02, "real native GetValues produces continuous interpolated commands while WPF is blocked");
                Assert(rotationCommands > 30 && rotationTravel > .08, "native Bluetooth GetValues and dirty filtering produce changing R0 commands while UI is blocked");
                Assert(ReferenceEquals(Get(outputTarget, "AxisSettings"), originalRanges), "native range profile is unchanged throughout stimulus generation");
                Click((Button)bar.FindName("ToggleMotion"));
                Assert((int)Field(running, "_stimulusMode") == 0 && (bool)Field(running, "_held"), "main stop stimulus holds instead of resuming native scripts");
                Click((Button)bar.FindName("MainRage"));
                Assert((int)Field(running, "_stimulusMode") == 0 && ((TextBlock)Field(running, "_message")).Text.Contains("脚本"), "main rage button reports missing native script without starting movement");
                Click((Button)bar.FindName("ToggleMotion"));
                stage = 4; next = DateTime.UtcNow.AddSeconds(1); return;
            }
            if (stage == 4 && DateTime.UtcNow >= next)
            {
                Assert(!(bool)Field(running, "_held"), "stopped stimulus restores original native mapping on explicit resume");
                Assert(homeSettings == HomeSettings(main), "native automatic-home flags, delay, duration and target remain unchanged");
                for (int i = 0; i < 10; i++) { Click((Button)bar.FindName("MainTremor")); Click((Button)bar.FindName("MainTremor")); }
                stage = 5; next = DateTime.UtcNow.AddSeconds(1); return;
            }
            if (stage == 5 && DateTime.UtcNow >= next)
            {
                Assert(!(bool)Field(running, "_held") && ReferenceEquals(Get(outputTarget, "AxisSettings"), originalRanges), "ten native UI on/off cycles release temporary hold maps");
                Assert(typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Common.MotionOutputHook").GetField("Filter").GetValue(null) == null && homeSettings == HomeSettings(main), "repeated native toggles leave no override and preserve automatic-home settings");
                Click((Button)bar.FindName("MainHome"));
                Assert((int)Field(running,"_stimulusMode") == 3, "main home button resolves native per-axis home settings and starts reset");
                var scriptSettings = Get(Field(running, "_script"), "AxisSettings");
                var expectedHome = DeviceAxis.All.ToDictionary(a => a, a => {
                    var setting = scriptSettings.GetType().GetProperty("Item").GetValue(scriptSettings, new object[]{a});
                    var range = originalRanges.GetType().GetProperty("Item").GetValue(originalRanges, new object[]{a});
                    double lo = (double)Get(range,"Minimum"), hi = (double)Get(range,"Maximum");
                    return lo + (hi-lo) * (double)Get(setting,"AutoHomeTargetValue");
                });
                var getValues = outputTarget.GetType().GetMethod("GetValues", BindingFlags.Instance | BindingFlags.NonPublic);
                outputStream = System.Threading.Tasks.Task.Run(() =>
                {
                    var values = new Dictionary<DeviceAxis,double>();
                    for(int i=0;i<50;i++){getValues.Invoke(outputTarget,new object[]{values,null});System.Threading.Thread.Sleep(10);}
                    return DeviceAxis.All.Select(a=>values[a]-expectedHome[a]).ToArray();
                });
                stage = 6; next = DateTime.UtcNow.AddSeconds(1); return;
            }
            if (stage == 6 && DateTime.UtcNow >= next)
            {
                Assert(outputStream.GetAwaiter().GetResult().All(v=>Math.Abs(v)<1e-6) && (bool)Field(running,"_atHome"), "native all-axis reset ends at range-mapped configured neutral positions and holds them");
                Assert(homeSettings == HomeSettings(main), "explicit reset preserves native automatic-home configuration");
                Click((Button)bar.FindName("ToggleMotion"));
                stage = 7; next = DateTime.UtcNow.AddSeconds(1); return;
            }
            if (stage == 7 && DateTime.UtcNow >= next)
            {
                Assert(!(bool)Field(running,"_held") && ReferenceEquals(Get(outputTarget,"AxisSettings"),originalRanges), "resume after native reset releases all override state");
                Finish(main);
            }
        }
        catch (Exception ex)
        {
            timer.Stop(); File.AppendAllText(result, "FAIL " + ex + "\n");
            Application.Current?.MainWindow?.Close();
        }
    }
    static string HomeSettings(Window main)
    {
        var settings = Get(Get(main.DataContext, "Script"), "AxisSettings");
        return string.Join(";", DeviceAxis.All.SelectMany(axis =>
        {
            var value = settings.GetType().GetProperty("Item").GetValue(settings, new object[] { axis });
            return new[] { "AutoHomeEnabled", "AutoHomeDelay", "AutoHomeDuration", "AutoHomeTargetValue" }.Select(p => Get(value, p).ToString());
        }));
    }
    static void Finish(Window main)
    {
        timer.Stop(); File.AppendAllText(result, "COMPLETE " + checks + " checks\n"); main.Close();
    }
    static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Visuals(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    static void TestBranding(Window main)
    {
        var assembly=typeof(DeviceAxis).Assembly;
        Assert(main.Title=="JXOPlayer 2.0", "main window title shows JXOPlayer 2.0");
        Assert(assembly.GetName().Name=="JXOPlayer" && assembly.GetName().Version==new Version(2,0,0,0), "assembly identity and version migrated");
        Assert(!assembly.GetTypes().Any(t=>t.Name=="InformationView" || t.Name=="InformationViewModel"), "help view and model removed from assembly");
        Assert(main.DataContext.GetType().GetMethod("OnInformationClick")==null && main.DataContext.GetType().GetProperty("Information")==null, "help action and injected help property removed");
        Assert(!Visuals(main).Any(v=>v.GetType().Name=="PackIcon" && Get(v,"Kind").ToString()=="HelpCircleOutline"), "question-mark button removed from rendered UI");
        Assert(Visuals(main).OfType<Image>().Any(i=>i.Source?.ToString().ToLowerInvariant().Contains("jxoplayer.png")==true), "window title uses the supplied avatar resource");
        Assert(File.ReadAllText("JXOPlayer.config.json").Contains("JXOPlayer.OutputTarget"), "settings type names migrated to the new assembly");
        Assert(!Directory.Exists("help"), "removed local help site is not required for startup");
    }
    static void Capture(Window main, string filename)
    {
        var visual = (FrameworkElement)main; visual.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(filename)) encoder.Save(stream);
    }

    static void TestHeatmap(object running)
    {
        var heatmap = (FrameworkElement)Field(running, "_heatmap");
        var source = (FrameworkElement)Field(running, "_heatmapSource");
        Assert(heatmap != null && heatmap.GetType().FullName == "JXOPlayer.UI.Controls.KeyframesHeatmap", "overlay uses the original native heatmap control");
        Assert(source != null && source.GetType() == heatmap.GetType(), "overlay located the existing main-window heatmap");
        var type = source.GetType();
        Func<string, DependencyProperty> property = name => (DependencyProperty)type.GetField(name + "Property").GetValue(null);
        foreach (string name in new[] { "Keyframes", "Settings", "Duration", "Position", "ShowRange", "BucketCount", "InvertY", "EnablePreview", "CombineHeat" })
            Assert(ReferenceEquals(BindingOperations.GetBinding(heatmap, property(name)).Source, source), "heatmap shares native " + name + " binding");
        var frames = new KeyframeCollection();
        for (int i = 0; i <= 1800; i++)
        {
            double t = i / 10.0;
            double amplitude = t < 40 ? .08 : t < 90 ? .48 : t < 130 ? .2 : .45;
            double frequency = t < 40 ? .7 : t < 90 ? 5 : t < 130 ? 2 : 7;
            frames.Add(t, .5 + amplitude * Math.Sin(t * frequency));
        }
        var map = new Dictionary<DeviceAxis, KeyframeCollection> { [DeviceAxis.Parse("L0")] = frames };
        source.SetCurrentValue(property("Keyframes"), map);
        source.SetCurrentValue(property("Duration"), 180.0);
        source.SetCurrentValue(property("Position"), 72.0);
        foreach (string name in new[] { "Keyframes", "Duration", "Position" }) BindingOperations.GetBindingExpression(heatmap, property(name)).UpdateTarget();
        heatmap.UpdateLayout();
        Assert(ReferenceEquals(Get(heatmap, "Keyframes"), map), "script changes propagate into overlay");
        Assert((double)Get(heatmap, "Position") == 72 && (double)Get(heatmap, "Duration") == 180, "playback duration and cursor propagate into overlay");
        var stops = (GradientStopCollection)Get(heatmap, "Stops");
        Assert(stops != null && stops.Count > 2 && stops.Select(s => s.Color).Distinct().Count() > 1, "native renderer draws a multi-color heatmap for changing script intensity");
        Assert(((Delegate)Field(running, "_heatmapSeekHandler")).Method.Name == "OnSeekRequest", "heatmap seeking reuses the original playback handler");
        source.SetCurrentValue(property("Position"), 108.0);
        BindingOperations.GetBindingExpression(heatmap, property("Position")).UpdateTarget();
        Assert((double)Get(heatmap, "Position") == 108, "cursor continues updating after initial binding");
        running.GetType().GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(running, null);
        Assert(((TextBlock)Field(running, "_heatmapTime")).Text == "01:48 / 03:00", "overlay time display follows the native playback position");
        Capture((Window)Field(running, "_window"), "悬浮窗-热力图.png");
    }
}
