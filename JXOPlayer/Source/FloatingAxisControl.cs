//#r "PresentationFramework"
//#r "PresentationCore"
//#r "WindowsBase"
//#r "System.Xaml"
//#r "System.Runtime.InteropServices"
//#r "System.IO.Compression"
//#r "System.Diagnostics.Process"
//#r "System.Security.Cryptography"
//#r "System.Net.Sockets"
//#r "System.Net.Primitives"
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using JXOPlayer.Common;
using JXOPlayer.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// JXOPlayer: loaded through the application's existing plugin compiler.
// No serial/network commands are sent here. The normal output pipeline retains
// its transport, enabled-axis filtering and configured update interval.
public sealed class FloatingAxisSettings : PluginSettingsBase
{
    // Legacy setting: accepted for migration, but never gates startup anymore.
    [JsonIgnore] public bool Enabled { get; set; } = true;
    [JsonProperty] public bool AutoStart { get; set; } = true;
    [JsonProperty] public double Opacity { get; set; } = 0.9;
    [JsonProperty] public int Step { get; set; } = 5;
    [JsonProperty] public bool Collapsed { get; set; }
    [JsonProperty] public double ScreenX { get; set; } = double.NaN;
    [JsonProperty] public double ScreenY { get; set; } = double.NaN;
    [JsonProperty] public bool PauseVideo { get; set; }
    [JsonIgnore] public FloatingAxisControl Running;
    private DispatcherTimer _startup;
    private TextBlock _notice;
    private CheckBox _autoCheck, _videoCheck;
    private CheckBox _mainAutoCheck;
    private Button _mainToggle, _mainPause;
    private TextBlock _mainStatus;
    private bool _pauseRequested;
    private int _stimulusRequested;
    private JXOPlayer.Features.RandomPlaybackService _random;
    public JXOPlayer.Features.RandomPlaybackService RandomPlayback => _random ??= new JXOPlayer.Features.RandomPlaybackService(Application.Current.MainWindow);
    public void DisposeRandomPlayback() { _random?.Dispose(); _random = null; }
    private static string ConfigPath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath), "Plugins", "FloatingAxisControl.config.json");

    public void Save()
    {
        try
        {
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JObject.FromObject(this).ToString());
            File.Move(temp, ConfigPath, true);
        }
        catch (Exception e) { if (_notice != null) _notice.Text = "设置保存失败：" + e.Message; }
    }

    public override UIElement CreateView()
    {
        var panel = new StackPanel { Margin = new Thickness(12), MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = "置顶悬浮轴控制", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "暂停运动 · 手动上下移动 · 范围加减 · 拖动与折叠", Margin = new Thickness(0, 8, 0, 10), TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var show = new Button { Content = "开启 / 显示悬浮窗", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        show.Click += (s, e) => RequestShow();
        var hide = new Button { Content = "临时隐藏悬浮窗", Padding = new Thickness(12, 6, 12, 6) };
        hide.Click += (s, e) => Running?.HidePanel();
        row.Children.Add(show); row.Children.Add(hide); panel.Children.Add(row);
        var auto = new CheckBox { Content = "每次启动软件时显示悬浮窗（不受临时隐藏影响）", IsChecked = AutoStart, Margin = new Thickness(0, 12, 0, 6) };
        _autoCheck = auto;
        auto.Click += (s, e) => SetAutoStart(auto.IsChecked == true); panel.Children.Add(auto);
        var video = new CheckBox { Content = "暂停轴运动时，也暂停视频", IsChecked = PauseVideo, Margin = new Thickness(0, 4, 0, 8) };
        _videoCheck = video;
        video.Click += (s, e) => { PauseVideo = video.IsChecked == true; Save(); }; panel.Children.Add(video);
        _notice = new TextBlock { Text = "也可在主窗口顶部直接显示/隐藏、暂停/恢复。点悬浮窗“×”只是本次隐藏，不会关闭下次自动显示；隐藏时暂停状态保持不变。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_notice);
        return panel;
    }

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);
        if (action == SettingsAction.Saving) settings.Remove("Enabled");
        if (action != SettingsAction.Loading) return;
        Enabled = true;
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.25, 1.0) : 0.9;
        if (Step != 1 && Step != 5 && Step != 10) Step = 5;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_autoCheck != null) _autoCheck.IsChecked = AutoStart;
            if (_videoCheck != null) _videoCheck.IsChecked = PauseVideo;
            _startup?.Stop();
            int attempts = 0;
            var startup = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _startup = startup;
            startup.Tick += (s, e) =>
            {
                if (++attempts > 120 || (InstallMainControls() && (!AutoStart || StartContainer())))
                {
                    startup.Stop();
                    if (ReferenceEquals(_startup, startup)) _startup = null;
                }
            };
            startup.Start();
        }));
    }

    private void SetAutoStart(bool value)
    {
        AutoStart = value;
        if (_autoCheck != null) _autoCheck.IsChecked = value;
        if (_mainAutoCheck != null) _mainAutoCheck.IsChecked = value;
        Save();
    }

    private void RequestShow()
    {
        if (Running != null) Running.RevealPanel();
        else { Collapsed = false; Save(); StartContainer(); }
    }

    private bool InstallMainControls()
    {
        var main = Application.Current?.MainWindow;
        if (main?.DataContext == null || main.Content == null) return false;
        if (main.DataContext.GetType().GetProperty("Plugin") == null) return false;
        const string hostTag = "JXOPlayer.FloatingAxisControl.MainHost";
        var host = main.Content as DockPanel;
        bool installed = host != null && Equals(host.Tag, hostTag);
        if (installed && host.Children.Count > 0 && ReferenceEquals(((FrameworkElement)host.Children[0]).Tag, this)) return true;
        var bar = (Border)XamlReader.Parse(MainBarXaml);
        if (installed && host.Children.Count > 0)
        {
            var previous = ((FrameworkElement)host.Children[0]).Tag;
            previous?.GetType().GetMethod("DisposeRandomPlayback")?.Invoke(previous, null);
        }
        var mainRows = new StackPanel();
        var controls = bar.Child; bar.Child = null;
        mainRows.Children.Add(controls); mainRows.Children.Add(RandomPlayback.CreateStrip(false));
        bar.Child = mainRows;
        bar.Tag = this;
        _mainToggle = (Button)bar.FindName("ToggleOverlay");
        _mainPause = (Button)bar.FindName("ToggleMotion");
        _mainStatus = (TextBlock)bar.FindName("OverlayStatus");
        _mainAutoCheck = (CheckBox)bar.FindName("ShowOnStartup");
        _mainAutoCheck.IsChecked = AutoStart;
        _mainAutoCheck.Click += (s, e) => SetAutoStart(_mainAutoCheck.IsChecked == true);
        _mainToggle.Click += (s, e) => { if (Running?.PanelVisible == true) Running.HidePanel(); else RequestShow(); };
        _mainPause.Click += (s, e) =>
        {
            if (Running != null) Running.ToggleMotionFromMain();
            else { _pauseRequested = true; StartContainer(); }
        };
        ((Button)bar.FindName("MainTremor")).Click += (s, e) => RequestStimulus(1);
        ((Button)bar.FindName("MainRage")).Click += (s, e) => RequestStimulus(2);
        ((Button)bar.FindName("MainHome")).Click += (s, e) => RequestStimulus(3);
        DockPanel.SetDock(bar, Dock.Top);
        if (installed) { host.Children.RemoveAt(0); host.Children.Insert(0, bar); }
        else
        {
            if (!(main.Content is UIElement original)) return false;
            main.Content = null;
            host = new DockPanel { LastChildFill = true, Tag = hostTag };
            host.Children.Add(bar); host.Children.Add(original);
            main.Content = host;
        }
        UpdateMainControls();
        return true;
    }

    public void OnControllerReady()
    {
        if (_pauseRequested) { _pauseRequested = false; Running?.ToggleMotionFromMain(); }
        if (_stimulusRequested != 0) { int mode = _stimulusRequested; _stimulusRequested = 0; Running?.RequestStimulus(mode); }
        UpdateMainControls();
    }

    private void RequestStimulus(int mode)
    {
        if (Running != null) { if (!Running.PanelVisible) Running.RevealPanel(); Running.RequestStimulus(mode); }
        else { _stimulusRequested = mode; if (!StartContainer()) { _stimulusRequested = 0; _mainStatus.Text = "控制器启动失败，请显示悬浮窗后重试。"; } }
    }

    public void UpdateMainControls()
    {
        if (_mainToggle == null) return;
        _mainToggle.Content = Running?.PanelVisible == true ? "隐藏悬浮窗" : "显示悬浮窗";
        _mainPause.Content = Running?.PauseButtonText ?? "Ⅱ  暂停运动";
        _mainStatus.Text = Running?.ControlStatus ?? "悬浮窗未打开";
    }

    private const string MainBarXaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 Background='#172131' BorderBrush='#354256' BorderThickness='0,0,0,1' Padding='10,5'>
 <Border.Resources>
  <Style TargetType='Button'><Setter Property='Margin' Value='4,2'/><Setter Property='Padding' Value='12,6'/><Setter Property='MinHeight' Value='32'/>
   <Setter Property='Foreground' Value='White'/><Setter Property='Background' Value='#347BFA'/><Setter Property='BorderThickness' Value='0'/>
   <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='6' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate></Setter.Value></Setter>
  </Style>
 </Border.Resources>
 <WrapPanel VerticalAlignment='Center'>
  <TextBlock Text='悬浮轴控制' Foreground='#F3F6FC' VerticalAlignment='Center' FontWeight='SemiBold' Margin='0,0,10,0'/>
  <Button x:Name='ToggleOverlay' Content='显示悬浮窗' ToolTip='点击即可找回已隐藏或折叠的悬浮窗'/>
  <Button x:Name='ToggleMotion' Content='Ⅱ  暂停运动' Background='#293548'/>
  <Button x:Name='MainTremor' Content='痉挛刺激' Background='#7457AB' ToolTip='30–60 毫秒密集折返，上下包络 50% + 独立左右旋转；再次点击关闭并恢复脚本与自动回零'/>
  <Button x:Name='MainRage' Content='狂暴刺激' Background='#A94455' ToolTip='1.35 倍节奏、扩大上下行程 + 30% 行程旋转；再次点击关闭并恢复脚本与自动回零'/>
  <Button x:Name='MainHome' Content='零点复位' Background='#267D71' ToolTip='结束接管，全部已启用轴回到各自设置的零位并保持；不是压到行程下限'/>
  <CheckBox x:Name='ShowOnStartup' Content='启动时显示悬浮窗' Foreground='#F3F6FC' VerticalAlignment='Center' Margin='12,4'/>
  <TextBlock x:Name='OverlayStatus' Foreground='#6CDDC3' VerticalAlignment='Center' FontSize='11' Margin='8,4'/>
 </WrapPanel>
</Border>";

    private bool StartContainer()
    {
        try
        {
            var root = Application.Current.MainWindow?.DataContext;
            if (root == null) return false;
            var containers = (IEnumerable)FloatingAxisControl.Get(FloatingAxisControl.Get(root, "Plugin"), "Containers");
            foreach (var entry in containers)
            {
                if (((FileInfo)FloatingAxisControl.Get(entry, "Key")).Name != "FloatingAxisControl.cs") continue;
                var container = FloatingAxisControl.Get(entry, "Value");
                if ((bool)FloatingAxisControl.Get(container, "CanStart")) container.GetType().GetMethod("Start").Invoke(container, null);
                return true;
            }
        }
        catch (Exception e) { if (_notice != null) _notice.Text = "悬浮窗启动失败：" + e.GetBaseException().Message; }
        return false;
    }
}

public sealed partial class FloatingAxisControl : SyncPluginBase
{
    private readonly FloatingAxisSettings _settings;
    private object _script, _output;
    private Window _window, _main;
    private DispatcherTimer _timer;
    private IntPtr _hwnd;
    private HwndSource _source;
    private bool _closing, _held, _resuming, _resumeVideo, _dragging, _cleaned;
    private Func<DeviceAxis, double> _readAxisValue;
    private NativePoint _dragStart;
    private NativeRect _dragRect;
    private readonly Dictionary<object, object> _originalMaps = new Dictionary<object, object>();
    private readonly Dictionary<DeviceAxis, double> _positions = new Dictionary<DeviceAxis, double>();
    private readonly Dictionary<DeviceAxis, double> _moveFrom = new Dictionary<DeviceAxis, double>();
    private readonly Dictionary<DeviceAxis, double> _moveTo = new Dictionary<DeviceAxis, double>();
    private readonly Stopwatch _motionClock = new Stopwatch();
    private readonly Stopwatch _refreshClock = Stopwatch.StartNew();
    private readonly Stopwatch _topmostClock = Stopwatch.StartNew();
    private readonly Stopwatch _holdRefreshClock = Stopwatch.StartNew();
    private double _moveDuration;
    private DeviceAxis _axis;
    private Button _pause, _bubble;
    private TextBlock _state, _position, _minimum, _maximum, _opacityLabel, _message;
    private FrameworkElement _panel;
    private FrameworkElement _heatmap, _heatmapSource;
    private Delegate _heatmapSeekHandler;
    private PropertyChangedEventHandler _heatmapAxisChanged;
    private TextBlock _heatmapTime;
    private Slider _opacity;
    private readonly List<Button> _movementButtons = new List<Button>();

    public FloatingAxisControl(FloatingAxisSettings settings) { _settings = settings; }
    public static object Get(object o, string name) => o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
    private static void Set(object o, string name, object value) => o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, value);
    private static object At(object map, DeviceAxis axis) => map.GetType().GetProperty("Item").GetValue(map, new object[] { axis });
    private object Range(DeviceAxis axis) => At(Get(_output, "AxisSettings"), axis);
    private double Min(DeviceAxis axis) => (double)Get(Range(axis), "Minimum");
    private double Max(DeviceAxis axis) => (double)Get(Range(axis), "Maximum");
    private bool AxisEnabled(DeviceAxis axis) => (bool)Get(Range(axis), "Enabled");
    private IEnumerable<object> Targets() => ((IEnumerable)Get(_output, "Items")).Cast<object>();
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    protected override void Execute(CancellationToken cancellationToken)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _main = Application.Current.MainWindow;
            _script = Get(_main.DataContext, "Script");
            _readAxisValue = GetAxisValue;
            _output = Get(_main.DataContext, "OutputTarget");
            _axis = DeviceAxis.Parse("L0") ?? DeviceAxis.All.FirstOrDefault();
            if (_axis == null) throw new InvalidOperationException("没有可用的轴，请先配置设备。");
            _settings.Running = this;
            _main.Closing += MainClosing;
            BuildWindow();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            _timer.Tick += Tick;
            _timer.Start();
            ShowPanel();
            _settings.OnControllerReady();
        });
        cancellationToken.WaitHandle.WaitOne();
    }

    // Use separate, temporary range maps for the transports. The shared range
    // profile (and therefore saved application settings) is never replaced.
    // Equal min/max holds the physical output regardless of script/Intiface input.
    private object MakeOutputMap(object original, double blend)
    {
        var map = Activator.CreateInstance(original.GetType());
        var indexer = map.GetType().GetProperty("Item");
        foreach (var axis in DeviceAxis.All)
        {
            var source = At(original, axis);
            var value = Activator.CreateInstance(source.GetType());
            var hold = _positions[axis];
            Set(value, "Minimum", Lerp(hold, (double)Get(source, "Minimum"), blend));
            Set(value, "Maximum", Lerp(hold, (double)Get(source, "Maximum"), blend));
            Set(value, "Enabled", (bool)Get(source, "Enabled"));
            indexer.SetValue(map, value, new object[] { axis });
        }
        return map;
    }

    private bool PauseMotion() => AcquireHold(true);

    private bool AcquireHold(bool pauseVideo)
    {
        if (_held && !_resuming) return true;
        // Polled mode can queue long device interpolations; require the native
        // fixed-update mode so a hold is sent at the configured output interval.
        if (Targets().Any(t => Get(t, "UpdateType").ToString() != "FixedUpdate"))
        {
            _message.Text = "请先将设备输出模式设为 FixedUpdate（固定更新）。";
            return false;
        }
        if (_resuming)
        {
            double t = Math.Clamp(_motionClock.Elapsed.TotalSeconds / 0.65, 0, 1);
            foreach (var axis in DeviceAxis.All) _positions[axis] = Lerp(_positions[axis], CurrentPosition(axis), t);
        }
        else
        {
            foreach (var axis in DeviceAxis.All) _positions[axis] = CurrentPosition(axis);
        }
        _held = true; _resuming = false; _moveTo.Clear();
        try { ApplyOutputMaps(0); }
        catch { RestoreOutputs(); _held = false; throw; }
        if (pauseVideo && _settings.PauseVideo)
        {
            _resumeVideo = ReadProperty<bool>("Media::IsPlaying");
            InvokeAction("Media::PlayPause::Set", false, true);
        }
        _message.Text = "各轴已保持当前位置；可手动微调上下轴。";
        Refresh();
        return true;
    }

    private double CurrentPosition(DeviceAxis axis)
    {
        double value = _readAxisValue(axis);
        if (!double.IsFinite(value)) value = axis.DefaultValue;
        return Math.Clamp(Lerp(Min(axis), Max(axis), Math.Clamp(value, 0, 1)), 0, 1);
    }

    private void ApplyOutputMaps(double blend)
    {
        foreach (var target in Targets())
        {
            if (!_originalMaps.TryGetValue(target, out var original))
            {
                original = Get(target, "AxisSettings");
                _originalMaps.Add(target, original);
            }
            Set(target, "AxisSettings", MakeOutputMap(original, blend));
        }
    }

    private void RestoreOutputs()
    {
        foreach (var pair in _originalMaps) Set(pair.Key, "AxisSettings", pair.Value);
        _originalMaps.Clear();
    }

    private void ResumeMotion()
    {
        _atHome = false;
        CancelStimulus();
        if (!_held || _resuming) return;
        _moveTo.Clear(); _resuming = true; _motionClock.Restart();
        if (_resumeVideo) { InvokeAction("Media::PlayPause::Set", true, true); _resumeVideo = false; }
        _message.Text = "正在平滑恢复自动运动…";
        Refresh();
    }

    private void Move(double? absolute, int direction)
    {
        _atHome = false;
        if (!AxisEnabled(_axis)) { _message.Text = "该轴已在设备设置中禁用。"; return; }
        CancelStimulus();
        if (!PauseMotion()) return;
        double target = absolute ?? ((_moveTo.TryGetValue(_axis, out var pending) ? pending : _positions[_axis]) + direction * _settings.Step / 100.0);
        target = Math.Clamp(target, Min(_axis), Max(_axis));
        _moveFrom.Clear(); _moveTo.Clear();
        _moveFrom[_axis] = _positions[_axis]; _moveTo[_axis] = target;
        // Full travel takes at least two seconds. No instantaneous position jumps.
        _moveDuration = Math.Max(0.25, Math.Abs(target - _positions[_axis]) * 2.0);
        _motionClock.Restart();
        _message.Text = "手动保持 · 点击“恢复运动”回到自动控制。";
    }

    private void ChangeRange(bool upper, int direction)
    {
        _atHome = false;
        if (StimulusBusy) StopStimulus("范围已调整，刺激已停止并保持当前位置。");
        var range = Range(_axis);
        double low = Min(_axis), high = Max(_axis), delta = direction * _settings.Step / 100.0;
        if (upper) Set(range, "Maximum", Math.Round(Math.Clamp(high + delta, low, 1), 4));
        else Set(range, "Minimum", Math.Round(Math.Clamp(low + delta, 0, high), 4));
        // Range changes while held deliberately retain the current position until
        // the user moves or resumes, rather than moving hardware as a side effect.
        _message.Text = _held ? "范围已更新；当前位置保持不变，手动移动时应用新范围。" : "运动范围已同步到软件原来的滑条。";
        Refresh();
    }

    private void Tick(object sender, EventArgs e)
    {
        try
        {
            bool stimulusMoved = UpdateStimulus();
            if (_held && _stimulusMode == 0)
            {
                if (_resuming)
                {
                    double t = Math.Clamp(_motionClock.Elapsed.TotalSeconds / 0.65, 0, 1);
                    ApplyOutputMaps(t);
                    if (t >= 1) { RestoreOutputs(); _held = false; _resuming = false; _message.Text = "已恢复自动运动。"; }
                }
                else
                {
                    bool moved = stimulusMoved || _moveTo.Count != 0;
                    if (_moveTo.Count != 0)
                    {
                        double t = Math.Clamp(_motionClock.Elapsed.TotalSeconds / _moveDuration, 0, 1);
                        foreach (var pair in _moveTo) _positions[pair.Key] = Lerp(_moveFrom[pair.Key], pair.Value, t * t * (3 - 2 * t));
                        if (t >= 1) _moveTo.Clear();
                    }
                    if (moved || _holdRefreshClock.ElapsedMilliseconds >= 150 || Targets().Any(t => !_originalMaps.ContainsKey(t)))
                    {
                        ApplyOutputMaps(0);
                        _holdRefreshClock.Restart();
                    }
                }
            }
            if (_refreshClock.ElapsedMilliseconds >= 150)
            {
                _refreshClock.Restart(); Refresh();
                if (_window.IsVisible && _topmostClock.ElapsedMilliseconds >= 1000)
                {
                    SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x13);
                    _topmostClock.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            CancelStimulus();
            _message.Text = "控制异常：" + ex.GetBaseException().Message;
            Logger.Error(ex, "Floating axis control update failed");
            _timer.Stop();
            // Keep a successfully installed hold in place on failure. Never
            // silently restore automatic motion from an error handler.
        }
    }

    private new void Refresh()
    {
        _pause.Content = _stimulusMode == 3 ? "Ⅱ  暂停复位" : StimulusBusy ? "Ⅱ  暂停刺激" : _moveTo.Count > 0 ? "Ⅱ  停在这里" : _held && !_resuming ? "▶  恢复运动" : "Ⅱ  暂停运动";
        _bubble.Content = _stimulusMode == 3 || _atHome ? "零" : _stimulusMode == 1 ? "颤" : _stimulusMode == 2 ? "狂" : _held ? "Ⅱ" : "轴";
        _state.Text = _stimulusMode == 3 ? "全部轴复位中" : _atHome ? "已复位 · 保持零位" : _analyzing ? "正在提取动作…" : _stimulusMode == 1 ? "痉挛刺激中" : _stimulusMode == 2 ? "狂暴刺激中" : _resuming ? "恢复中" : _moveTo.Count > 0 ? "手动移动中" : _held ? "已暂停 · 手动保持" : "自动运动";
        if ((_stimulusMode == 1 || _stimulusMode == 2) && _positions.TryGetValue(_rotation, out var rotationPosition))
            _state.Text += " · 旋转 " + (rotationPosition * 100).ToString("0") + "%";
        _position.Text = ((_held && !_resuming ? _positions[_axis] : CurrentPosition(_axis)) * 100).ToString("0") + "%";
        _minimum.Text = (Min(_axis) * 100).ToString("0") + "%";
        _maximum.Text = (Max(_axis) * 100).ToString("0") + "%";
        _opacityLabel.Text = "不透明度 " + (_settings.Opacity * 100).ToString("0") + "%";
        if (_heatmap != null) _heatmapTime.Text = DisplayTime((double)Get(_heatmap, "Position")) + " / " + DisplayTime((double)Get(_heatmap, "Duration"));
        foreach (var button in _movementButtons) button.IsEnabled = AxisEnabled(_axis) && !_resuming;
        _settings.UpdateMainControls();
    }

    public bool PanelVisible => _window?.IsVisible == true;
    public string PauseButtonText => (string)_pause?.Content;
    public string ControlStatus => _state?.Text;
    public void ToggleMotionFromMain() => Guard(TogglePause);
    public void RevealPanel() { SetCollapsed(false); ShowPanel(); }

    private void BuildWindow()
    {
        _window = (Window)XamlReader.Parse(WindowXaml);
        if (_main?.DataContext != null) ((Border)_window.FindName("RandomPlaybackHost")).Child = _settings.RandomPlayback.CreateStrip(true);
        _panel = (FrameworkElement)_window.FindName("Panel");
        _bubble = (Button)_window.FindName("Bubble");
        _pause = (Button)_window.FindName("Pause");
        _state = (TextBlock)_window.FindName("State");
        _position = (TextBlock)_window.FindName("Position");
        _minimum = (TextBlock)_window.FindName("Minimum");
        _maximum = (TextBlock)_window.FindName("Maximum");
        _message = (TextBlock)_window.FindName("Message");
        _opacityLabel = (TextBlock)_window.FindName("OpacityLabel");
        _opacity = (Slider)_window.FindName("OpacitySlider");
        _heatmapTime = (TextBlock)_window.FindName("HeatmapTime");
        BuildHeatmap();
        _opacity.Value = _settings.Opacity * 100;
        _opacity.ValueChanged += (s, e) => { _settings.Opacity = _opacity.Value / 100; _window.Opacity = _settings.Opacity; Refresh(); };
        _opacity.PreviewMouseLeftButtonUp += (s, e) => _settings.Save();
        Action<string, Action> bind = (name, action) => ((Button)_window.FindName(name)).Click += (s, e) => Guard(action);
        bind("Pause", TogglePause);
        bind("Tremor", () => RequestStimulus(1)); bind("Rage", () => RequestStimulus(2));
        bind("Home", () => RequestStimulus(3));
        bind("Collapse", () => SetCollapsed(true)); bind("Hide", HidePanel);
        bind("Up", () => Move(null, 1)); bind("Down", () => Move(null, -1));
        bind("Top", () => Move(Max(_axis), 0)); bind("Middle", () => Move((Min(_axis) + Max(_axis)) / 2, 0)); bind("Bottom", () => Move(Min(_axis), 0));
        foreach (var name in new[] { "Up", "Down", "Top", "Middle", "Bottom" }) _movementButtons.Add((Button)_window.FindName(name));
        bind("MinMinus", () => ChangeRange(false, -1)); bind("MinPlus", () => ChangeRange(false, 1));
        bind("MaxMinus", () => ChangeRange(true, -1)); bind("MaxPlus", () => ChangeRange(true, 1));
        bind("OpacityMinus", () => { _opacity.Value -= 5; _settings.Save(); });
        bind("OpacityPlus", () => { _opacity.Value += 5; _settings.Save(); });
        foreach (int step in new[] { 1, 5, 10 })
        {
            int value = step;
            bind("Step" + step, () => { _settings.Step = value; UpdateStepButtons(); _settings.Save(); });
        }
        WireDrag((UIElement)_window.FindName("DragArea"), false);
        WireDrag(_bubble, true);
        var menu = new ContextMenu();
        var expand = new MenuItem { Header = "展开控制面板" }; expand.Click += (s, e) => SetCollapsed(false);
        var pause = new MenuItem { Header = "暂停 / 恢复运动" }; pause.Click += (s, e) => Guard(TogglePause);
        var hide = new MenuItem { Header = "隐藏悬浮窗" }; hide.Click += (s, e) => HidePanel();
        var home = new MenuItem { Header = "全部轴零点复位" }; home.Click += (s, e) => RequestStimulus(3);
        menu.Items.Add(expand); menu.Items.Add(pause); menu.Items.Add(home); menu.Items.Add(hide); _bubble.ContextMenu = menu;
        _window.SourceInitialized += (s, e) =>
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            SetWindowLongPtr(_hwnd, -20, new IntPtr(GetWindowLongPtr(_hwnd, -20).ToInt64() | 0x08000000L | 0x80L));
            _source = HwndSource.FromHwnd(_hwnd); _source.AddHook(WindowHook);
        };
        _window.Closing += WindowClosing;
        _window.Opacity = _settings.Opacity;
        UpdateStepButtons(); SetCollapsed(_settings.Collapsed, false); Refresh();
    }

    private void UpdateStepButtons()
    {
        foreach (int step in new[] { 1, 5, 10 })
        {
            var b = (Button)_window.FindName("Step" + step);
            b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.Step == step ? "#347BFA" : "#293548"));
        }
    }

    private void BuildHeatmap()
    {
        if (_script == null) return; // The hardware-free controller unit harness.
        var type = typeof(DeviceAxis).Assembly.GetType("JXOPlayer.UI.Controls.KeyframesHeatmap", true);
        _heatmap = (FrameworkElement)Activator.CreateInstance(type);
        _heatmap.Height = 52;
        _heatmap.DataContext = _script;
        _heatmapSource = FindHeatmap(_main, type);
        var properties = new Dictionary<string, string>
        {
            ["Keyframes"] = "AxisKeyframes", ["Settings"] = "AxisSettings",
            ["Duration"] = "MediaDuration", ["Position"] = "MediaPosition",
            ["ShowRange"] = "HeatmapShowRange", ["BucketCount"] = "HeatmapBucketCount",
            ["InvertY"] = "HeatmapInvertY", ["EnablePreview"] = "HeatmapEnablePreview",
            ["CombineHeat"] = "HeatmapCombineHeat"
        };
        foreach (var pair in properties)
        {
            var dp = (DependencyProperty)type.GetField(pair.Key + "Property").GetValue(null);
            BindingOperations.SetBinding(_heatmap, dp, new Binding(_heatmapSource != null ? pair.Key : pair.Value)
            {
                Source = (object)_heatmapSource ?? _script, Mode = BindingMode.OneWay
            });
        }
        var seek = type.GetEvent("SeekRequest");
        _heatmapSeekHandler = Delegate.CreateDelegate(seek.EventHandlerType, _script, _script.GetType().GetMethod("OnSeekRequest"));
        seek.AddEventHandler(_heatmap, _heatmapSeekHandler);
        if (_heatmapSource is INotifyPropertyChanged observable)
        {
            _heatmapAxisChanged = (s, e) => { if (e.PropertyName == "SelectedAxis") SyncHeatmapAxis(); };
            observable.PropertyChanged += _heatmapAxisChanged;
            SyncHeatmapAxis();
        }
        ((Border)_window.FindName("HeatmapHost")).Child = _heatmap;
    }

    private static FrameworkElement FindHeatmap(DependencyObject parent, Type type)
    {
        if (parent == null) return null;
        if (parent.GetType() == type) return (FrameworkElement)parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindHeatmap(VisualTreeHelper.GetChild(parent, i), type);
            if (found != null) return found;
        }
        return null;
    }

    private void SyncHeatmapAxis()
    {
        if (_heatmapSource == null || _heatmap == null) return;
        var type = _heatmap.GetType();
        var index = type.GetField("_selectedAxisIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        index.SetValue(_heatmap, index.GetValue(_heatmapSource));
        type.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_heatmap, null);
        type.GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null)
            .Invoke(_heatmap, new object[] { "SelectedAxis" });
    }

    private void ClearHeatmap()
    {
        if (_heatmapSource is INotifyPropertyChanged observable && _heatmapAxisChanged != null)
            observable.PropertyChanged -= _heatmapAxisChanged;
        if (_heatmap != null)
        {
            if (_heatmapSeekHandler != null) _heatmap.GetType().GetEvent("SeekRequest").RemoveEventHandler(_heatmap, _heatmapSeekHandler);
            Set(_heatmap, "ToolTipIsOpen", false);
            BindingOperations.ClearAllBindings(_heatmap);
        }
        _heatmapSource = null; _heatmap = null; _heatmapSeekHandler = null; _heatmapAxisChanged = null;
    }

    private static string DisplayTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds) return "--:--";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? ((int)time.TotalHours) + time.ToString(@"\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    private void TogglePause()
    {
        if (StimulusBusy) { StopStimulus("刺激已停止，保持当前位置；点击“恢复运动”回到脚本。"); return; }
        if (_moveTo.Count != 0)
        {
            _moveTo.Clear();
            _message.Text = "已停止手动移动，保持当前位置。";
            Refresh();
        }
        else if (_held && !_resuming) ResumeMotion();
        else PauseMotion();
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { _message.Text = "操作失败：" + e.GetBaseException().Message; Logger.Error(e, "Floating axis action failed"); }
    }

    public void ShowPanel()
    {
        _window.Show();
        _window.UpdateLayout();
        if (double.IsFinite(_settings.ScreenX) && double.IsFinite(_settings.ScreenY))
            SetWindowPos(_hwnd, new IntPtr(-1), (int)_settings.ScreenX, (int)_settings.ScreenY, 0, 0, 0x11);
        else
        {
            var area = SystemParameters.WorkArea;
            _window.Left = area.Right - _window.Width - 28; _window.Top = area.Top + 90;
        }
        ClampToScreen(); SavePosition(); _settings.Save(); _settings.UpdateMainControls();
    }

    public void HidePanel()
    {
        if (StimulusBusy && _stimulusMode != 3) ReleaseStimulus("悬浮窗已隐藏，刺激已关闭，恢复脚本和自动回零。");
        if (_heatmap != null) Set(_heatmap, "ToolTipIsOpen", false);
        SavePosition(); _settings.Save(); _window.Hide(); _settings.UpdateMainControls();
    }

    private void SetCollapsed(bool collapsed, bool save = true)
    {
        _settings.Collapsed = collapsed;
        if (collapsed && _heatmap != null) Set(_heatmap, "ToolTipIsOpen", false);
        _panel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _bubble.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        _window.Width = collapsed ? 62 : 312; _window.Height = collapsed ? 62 : Math.Min(780, SystemParameters.WorkArea.Height);
        if (_hwnd != IntPtr.Zero) _window.Dispatcher.BeginInvoke(new Action(() => { ClampToScreen(); SavePosition(); if (save) _settings.Save(); }));
    }

    private void WireDrag(UIElement handle, bool expandOnClick)
    {
        handle.PreviewMouseLeftButtonDown += (s, e) =>
        {
            GetCursorPos(out _dragStart); GetWindowRect(_hwnd, out _dragRect);
            _dragging = false; handle.CaptureMouse(); e.Handled = true;
        };
        handle.PreviewMouseMove += (s, e) =>
        {
            if (!handle.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            GetCursorPos(out var now);
            int dx = now.X - _dragStart.X, dy = now.Y - _dragStart.Y;
            if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4) _dragging = true;
            if (_dragging) SetWindowPos(_hwnd, new IntPtr(-1), _dragRect.Left + dx, _dragRect.Top + dy, 0, 0, 0x11);
        };
        handle.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (!handle.IsMouseCaptured) return;
            handle.ReleaseMouseCapture(); ClampToScreen(); SavePosition(); _settings.Save();
            if (expandOnClick && !_dragging) SetCollapsed(false);
            e.Handled = true;
        };
    }

    private void ClampToScreen()
    {
        if (_hwnd == IntPtr.Zero) return;
        GetWindowRect(_hwnd, out var r);
        var m = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref r, 2), ref m)) return;
        int x = Math.Clamp(r.Left, m.Work.Left, Math.Max(m.Work.Left, m.Work.Right - (r.Right - r.Left)));
        int y = Math.Clamp(r.Top, m.Work.Top, Math.Max(m.Work.Top, m.Work.Bottom - (r.Bottom - r.Top)));
        SetWindowPos(_hwnd, new IntPtr(-1), x, y, 0, 0, 0x11);
    }
    private void SavePosition() { if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out var r)) { _settings.ScreenX = r.Left; _settings.ScreenY = r.Top; } }
    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE: retain fullscreen-player focus.
        if (msg == 0x7E || msg == 0x2E0) _window.Dispatcher.BeginInvoke(new Action(ClampToScreen));
        return IntPtr.Zero;
    }
    private void WindowClosing(object sender, CancelEventArgs e) { if (!_closing) { e.Cancel = true; HidePanel(); } }
    private void MainClosing(object sender, CancelEventArgs e) { _closing = true; Cleanup(); }

    protected override void Dispose(bool disposing)
    {
        if (_cleaned || Application.Current == null || Application.Current.Dispatcher.HasShutdownFinished) return;
        Application.Current.Dispatcher.Invoke(Cleanup);
    }

    private void Cleanup()
    {
            if (_cleaned) return;
            _cleaned = true;
            CancelStimulus();
            _timer?.Stop();
            ClearHeatmap();
            // On application exit retain the last held output until transport
            // threads finish; do not briefly resume motion during shutdown.
            if (!_closing) RestoreOutputs(); else _originalMaps.Clear();
            _settings.Running = null;
            _settings.UpdateMainControls();
            _settings.Save();
            if (_main != null) _main.Closing -= MainClosing;
            _closing = true;
            if (_source != null) _source.RemoveHook(WindowHook);
            _window?.Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private const string WindowXaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 Title='JXOPlayer 2.0 悬浮轴控制' Width='312' Height='780' WindowStyle='None' ResizeMode='NoResize' AllowsTransparency='True'
 Background='Transparent' Topmost='True' ShowActivated='False' ShowInTaskbar='False' FontFamily='Microsoft YaHei UI' FontSize='13' Foreground='#F3F6FC'>
 <Window.Resources>
  <Style TargetType='Button'>
   <Setter Property='Background' Value='#293548'/><Setter Property='Foreground' Value='#F3F6FC'/><Setter Property='BorderThickness' Value='0'/>
   <Setter Property='Padding' Value='6'/><Setter Property='Margin' Value='3'/><Setter Property='MinHeight' Value='32'/><Setter Property='Focusable' Value='False'/>
   <Setter Property='Cursor' Value='Hand'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
    <Border x:Name='Body' Background='{TemplateBinding Background}' CornerRadius='8' Padding='{TemplateBinding Padding}'>
     <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
    </Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Body' Property='Opacity' Value='0.78'/></Trigger>
    <Trigger Property='IsPressed' Value='True'><Setter TargetName='Body' Property='Opacity' Value='0.55'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Body' Property='Opacity' Value='0.35'/></Trigger></ControlTemplate.Triggers>
   </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='TextBlock'><Setter Property='Foreground' Value='#F3F6FC'/><Setter Property='VerticalAlignment' Value='Center'/></Style>
 </Window.Resources>
 <Grid Margin='2'>
  <Border x:Name='Panel' Background='#172131' CornerRadius='16' BorderBrush='#526077' BorderThickness='1' Padding='12'>
   <DockPanel LastChildFill='True'>
   <StackPanel DockPanel.Dock='Top'>
    <Grid Margin='0,0,0,8'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='36'/><ColumnDefinition Width='36'/></Grid.ColumnDefinitions>
     <Border x:Name='DragArea' Background='Transparent' Cursor='SizeAll'><StackPanel><TextBlock Text='轴控制' FontSize='17' FontWeight='SemiBold'/><TextBlock Text='拖动这里移动' FontSize='10' Foreground='#A3B2C7'/></StackPanel></Border>
     <Button x:Name='Collapse' Grid.Column='1' Content='−' ToolTip='缩成小按钮'/><Button x:Name='Hide' Grid.Column='2' Content='×' ToolTip='隐藏；在软件的插件面板可重新开启'/>
    </Grid>
    <Grid Margin='3,0,3,5'><TextBlock Text='上下轴 L0' Foreground='#A3B2C7'/><TextBlock x:Name='State' HorizontalAlignment='Right' FontSize='11' Foreground='#6CDDC3'/></Grid>
    <Button x:Name='Pause' Content='Ⅱ  暂停运动' Height='46' Background='#347BFA' FontSize='17' FontWeight='SemiBold'/>
    <UniformGrid Columns='2'><Button x:Name='Tremor' Content='痉挛刺激' Background='#7457AB' ToolTip='30–60 毫秒密集折返，上下包络 50% + 独立左右旋转；再次点击关闭，恢复脚本与自动回零'/><Button x:Name='Rage' Content='狂暴刺激' Background='#A94455' ToolTip='1.35 倍节奏、扩大上下行程 + 30% 行程旋转；再次点击关闭，恢复脚本与自动回零'/></UniformGrid>
    <Button x:Name='Home' Content='零点复位 · 全部轴' Background='#267D71' ToolTip='结束刺激或手动接管，所有已启用轴回到各自的回零目标并保持'/>
   </StackPanel>
   <ScrollViewer x:Name='DetailScroll' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>
   <StackPanel>
    <Grid Margin='3,8,3,4'><TextBlock Text='脚本热力图' Foreground='#A3B2C7' FontSize='11'/><TextBlock x:Name='HeatmapTime' Text='--:-- / --:--' HorizontalAlignment='Right' FontSize='10' Foreground='#A3B2C7'/></Grid>
    <Border x:Name='HeatmapHost' Height='54' BorderThickness='1' BorderBrush='#354256' Background='#0E1724' Margin='3,0,3,4' ClipToBounds='True'/>
    <Border x:Name='RandomPlaybackHost' MinHeight='100'/>
    <Grid Margin='3,8,3,3'><TextBlock Text='当前位置'/><TextBlock x:Name='Position' HorizontalAlignment='Right' FontSize='22' FontWeight='SemiBold'/></Grid>
    <UniformGrid Columns='2'><Button x:Name='Down' Content='▼  下移' Height='39'/><Button x:Name='Up' Content='▲  上移' Height='39'/></UniformGrid>
    <UniformGrid Columns='3'><Button x:Name='Bottom' Content='到下限'/><Button x:Name='Middle' Content='到中间'/><Button x:Name='Top' Content='到上限'/></UniformGrid>
    <Grid Margin='3,6,0,6'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='48'/><ColumnDefinition Width='48'/><ColumnDefinition Width='48'/></Grid.ColumnDefinitions>
     <TextBlock Text='每次调整'/><Button x:Name='Step1' Grid.Column='1' Content='1%'/><Button x:Name='Step5' Grid.Column='2' Content='5%'/><Button x:Name='Step10' Grid.Column='3' Content='10%'/>
    </Grid>
    <Border Height='1' Background='#354256' Margin='3,0,3,8'/>
    <TextBlock Text='运动范围' Margin='3,0,0,4' Foreground='#A3B2C7'/>
    <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='45'/><ColumnDefinition Width='57'/><ColumnDefinition Width='45'/></Grid.ColumnDefinitions>
     <TextBlock Text='上限' Margin='3,0,0,0'/><Button x:Name='MaxMinus' Grid.Column='1' Content='−'/><TextBlock x:Name='Maximum' Grid.Column='2' HorizontalAlignment='Center'/><Button x:Name='MaxPlus' Grid.Column='3' Content='+'/>
    </Grid>
    <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='45'/><ColumnDefinition Width='57'/><ColumnDefinition Width='45'/></Grid.ColumnDefinitions>
     <TextBlock Text='下限' Margin='3,0,0,0'/><Button x:Name='MinMinus' Grid.Column='1' Content='−'/><TextBlock x:Name='Minimum' Grid.Column='2' HorizontalAlignment='Center'/><Button x:Name='MinPlus' Grid.Column='3' Content='+'/>
    </Grid>
    <Grid Margin='3,8,0,0'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='40'/><ColumnDefinition Width='40'/></Grid.ColumnDefinitions>
     <TextBlock x:Name='OpacityLabel' FontSize='11' Foreground='#A3B2C7'/><Button x:Name='OpacityMinus' Grid.Column='1' Content='−'/><Button x:Name='OpacityPlus' Grid.Column='2' Content='+'/>
    </Grid>
    <Slider x:Name='OpacitySlider' Minimum='25' Maximum='100' TickFrequency='5' IsSnapToTickEnabled='True' IsMoveToPointEnabled='True' Focusable='False' Margin='4,2,4,4'/>
    <TextBlock x:Name='Message' Text='视频全屏时也可操作 · 上下按钮进入手动保持' FontSize='10' Foreground='#A3B2C7' TextWrapping='Wrap' Margin='3,2,3,0'/>
   </StackPanel>
   </ScrollViewer>
   </DockPanel>
  </Border>
  <Button x:Name='Bubble' Content='轴' Visibility='Collapsed' Background='#347BFA' FontSize='23' FontWeight='SemiBold' Margin='0' ToolTip='点击展开 · 拖动移动 · 右键暂停/恢复'/>
 </Grid>
</Window>";
}
