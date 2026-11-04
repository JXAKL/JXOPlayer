using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JXOPlayer.Common;
using Newtonsoft.Json.Linq;

public class FakeTarget { public object AxisSettings { get; set; } public string UpdateType { get; set; } = "FixedUpdate"; }
public class FakeOutput { public object AxisSettings { get; set; } public List<FakeTarget> Items { get; set; } }
public static partial class TestFloatingAxis
{
    static BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static int count;
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    static object Get(object o, string n) => o.GetType().GetProperty(n, Flags).GetValue(o);
    static void Set(object o, string n, object v) => o.GetType().GetProperty(n, Flags).SetValue(o, v);
    static void Field(object o, string n, object v) => o.GetType().GetField(n, Flags).SetValue(o, v);
    static object Field(object o, string n) => o.GetType().GetField(n, Flags).GetValue(o);
    static object Call(object o, string n, params object[] args) => o.GetType().GetMethod(n, Flags).Invoke(o, args);
    static object At(object o, DeviceAxis a) => o.GetType().GetProperty("Item").GetValue(o, new object[] { a });
    static void Assert(bool b, string message) { if (!b) throw new Exception(message); Console.WriteLine("PASS: " + message); count++; }
    static bool Near(double a, double b) => Math.Abs(a-b) < 0.00001;
    static void Advance(object plugin, int ms)
    {
        Thread.Sleep(ms);
        Call(plugin, "Tick", null, EventArgs.Empty);
    }
    [STAThread]
    public static int Main()
    {
        try { Run(); return 0; } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void Run()
    {
        Directory.SetCurrentDirectory(Path.GetDirectoryName(Environment.ProcessPath));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var assembly = typeof(DeviceAxis).Assembly;
        var deviceType = assembly.GetType("JXOPlayer.Common.DeviceSettings");
        var config = JObject.Parse(File.ReadAllText("JXOPlayer.config.json"));
        var device = config["Devices"].First(x => (string)x["Name"] == "TCode-0.3").ToObject(deviceType);
        typeof(DeviceAxis).GetMethod("InitializeFromDevice", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new[] { device });
        var axis = DeviceAxis.Parse("L0");
        var settingType = assembly.GetType("JXOPlayer.OutputTarget.DeviceAxisSettings");
        var mapType = typeof(ObservableConcurrentDictionary<,>).MakeGenericType(typeof(DeviceAxis), settingType);
        var original = Activator.CreateInstance(mapType);
        foreach (var a in DeviceAxis.All)
        {
            var s = Activator.CreateInstance(settingType);
            Set(s, "Minimum", 0.2); Set(s, "Maximum", 0.8);
            mapType.GetProperty("Item").SetValue(original, s, new object[] { a });
        }
        var target = new FakeTarget { AxisSettings = original };
        var output = new FakeOutput { AxisSettings = original, Items = new List<FakeTarget> { target } };
        var settings = new FloatingAxisSettings { AutoStart = false, PauseVideo = false };
        var plugin = new FloatingAxisControl(settings);
        double sourceValue = 0.5;
        Field(plugin, "_output", output); Field(plugin, "_axis", axis);
        Field(plugin, "_readAxisValue", new Func<DeviceAxis, double>(a => sourceValue));
        Field(plugin, "_timer", new DispatcherTimer());
        Call(plugin, "BuildWindow");
        var window = (Window)Field(plugin, "_window");
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Assert((GetWindowLongPtr(handle, -20).ToInt64() & 0x08000080L) == 0x08000080L, "native window has no-activate and tool-window styles");
        object[] hookArgs = { handle, 0x21, IntPtr.Zero, IntPtr.Zero, false };
        var activateResult = Call(plugin, "WindowHook", hookArgs);
        Assert((bool)hookArgs[4] && (IntPtr)activateResult == new IntPtr(3), "clicking panel explicitly preserves foreground-player focus");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(308, 736)); content.Arrange(new Rect(0, 0, 308, 736)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(308, 736, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create("floating-panel.png")) png.Save(file);
        var details = (ScrollViewer)window.FindName("DetailScroll");
        double stopY = ((Button)window.FindName("Pause")).TransformToAncestor(content).Transform(new Point()).Y;
        details.ScrollToBottom(); content.UpdateLayout();
        Assert(Near(((Button)window.FindName("Pause")).TransformToAncestor(content).Transform(new Point()).Y, stopY), "stop and stimulus controls stay pinned while details scroll");
        Assert(((FrameworkElement)window.FindName("Message")).TransformToAncestor(content).Transform(new Point()).Y + ((FrameworkElement)window.FindName("Message")).ActualHeight <= 730, "scrolled details remain reachable within expanded panel");
        details.ScrollToTop();
        Assert((bool)Call(plugin, "PauseMotion"), "pause accepted in fixed-update mode");
        Assert(!ReferenceEquals(target.AxisSettings, original), "temporary map installed only on transport");
        Assert(ReferenceEquals(output.AxisSettings, original), "shared saved range profile untouched");
        Assert(Near((double)Get(At(original, axis), "Minimum"), .2), "original lower limit preserved");
        Assert(Near((double)Get(At(target.AxisSettings, axis), "Minimum"), .5) && Near((double)Get(At(target.AxisSettings, axis), "Maximum"), .5), "all input positions map to held physical position");
        sourceValue = .99; Advance(plugin, 25);
        Assert(Near((double)Get(At(target.AxisSettings, axis), "Minimum"), .5), "script movement does not move held output");
        Call(plugin, "Move", null, 1); Advance(plugin, 300);
        Assert(Near((double)Get(At(target.AxisSettings, axis), "Minimum"), .55), "up button moves five percentage points");
        Call(plugin, "Move", (double?)1.0, 0); Advance(plugin, 600);
        Assert(Near((double)Get(At(target.AxisSettings, axis), "Minimum"), .8), "manual position cannot exceed configured upper limit");
        Call(plugin, "Move", (double?)0.0, 0); Advance(plugin, 1300);
        Assert(Near((double)Get(At(target.AxisSettings, axis), "Minimum"), .2), "manual position cannot go below configured lower limit");
        var second = new FakeTarget { AxisSettings = original }; output.Items.Add(second); Advance(plugin, 25);
        Assert(Near((double)Get(At(second.AxisSettings, axis), "Minimum"), .2), "new transport inherits paused position");
        Set(At(original, axis), "Enabled", false); Advance(plugin, 180);
        Assert(!(bool)Get(At(target.AxisSettings, axis), "Enabled"), "disabled axes stay disabled");
        Set(At(original, axis), "Enabled", true);
        Call(plugin, "Move", (double?)0.8, 0); Advance(plugin, 200);
        double interruptedAt = (double)Get(At(target.AxisSettings, axis), "Minimum");
        Call(plugin, "TogglePause"); Advance(plugin, 350);
        Assert(Near((double)Get(At(target.AxisSettings, axis), "Minimum"), interruptedAt), "pause during manual travel stops at intermediate position");
        for (int i = 0; i < 25; i++) Call(plugin, "ChangeRange", false, 1);
        Assert(Near((double)Get(At(original, axis), "Minimum"), .8), "lower limit never crosses upper limit");
        for (int i = 0; i < 25; i++) Call(plugin, "ChangeRange", true, -1);
        Assert(Near((double)Get(At(original, axis), "Maximum"), .8), "upper limit never crosses lower limit");
        Set(At(original, axis), "Minimum", .2); Set(At(original, axis), "Maximum", .8);
        Call(plugin, "ResumeMotion"); Advance(plugin, 750);
        Assert(ReferenceEquals(target.AxisSettings, original) && ReferenceEquals(second.AxisSettings, original), "resume restores original map references for every transport");
        target.UpdateType = "PolledUpdate";
        Assert(!(bool)Call(plugin, "PauseMotion"), "unsupported long queued interpolation is rejected without changing outputs");
        target.UpdateType = "FixedUpdate";
        TestStimulus(plugin, output, original, window);
        Call(plugin, "SetCollapsed", true, false);
        Assert(window.Width == 62 && window.Height == 62 && ((FrameworkElement)window.FindName("Bubble")).Visibility == Visibility.Visible, "collapse produces compact button");
        Call(plugin, "SetCollapsed", false, false);
        Assert(window.Width == 312 && window.Height == Math.Min(780,SystemParameters.WorkArea.Height), "expand restores panel size within screen work area");
        settings.Opacity = 0.55; settings.Step = 10; settings.Save();
        var loaded = JObject.Parse(File.ReadAllText("Plugins/FloatingAxisControl.config.json"));
        Assert(Near((double)loaded["Opacity"], .55) && (int)loaded["Step"] == 10, "opacity and step settings persist");
        Assert(loaded["Running"] == null, "runtime controller is excluded from saved settings");
        Call(plugin, "PauseMotion"); Call(plugin, "Cleanup");
        Assert(ReferenceEquals(target.AxisSettings, original), "cleanup restores output maps");

        // Exercise the actual host compiler, including its assembly references,
        // plugin discovery, settings constructor, XAML and view-model binding.
        var compiler = assembly.GetType("JXOPlayer.Plugin.PluginCompiler");
        var viewManager = new Stylet.ViewManager(new Stylet.ViewManagerConfig { ViewFactory = Activator.CreateInstance, ViewAssemblies = new List<Assembly> { assembly } });
        compiler.GetProperty("ViewManager", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, viewManager);
        var compiled = compiler.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { new FileInfo("Plugins/FloatingAxisControl.cs") });
        if (!(bool)Get(compiled, "Success")) throw new Exception("Host compiler: " + Get(compiled, "Exception"));
        Assert(true, "JXOPlayer native plugin compiler accepts the source and settings view");
        File.WriteAllText("test-results.txt", count + " checks passed\n");
        Console.WriteLine(count + " checks passed");
        app.Shutdown();
    }
}
