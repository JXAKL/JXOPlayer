public sealed partial class FloatingAxisControl
{
    private bool _atHome;
    private readonly Dictionary<DeviceAxis, (double Low, double High, bool Enabled, double Speed)> _homeLimits
        = new Dictionary<DeviceAxis, (double, double, bool, double)>();

    private double HomeSetting(DeviceAxis axis, string name, double fallback)
    {
        if (_script == null) return fallback;
        var settings = At(Get(_script, "AxisSettings"), axis);
        var property = settings.GetType().GetProperty(name);
        return property == null ? fallback : Convert.ToDouble(property.GetValue(settings));
    }

    private void StartHome()
    {
        // Cancel queued analysis first: it must never reclaim motion after reset.
        CancelStimulus(); _atHome = false;
        if (!AcquireHold(false)) return;
        _moveTo.Clear(); _homeLimits.Clear();
        var targets = new Dictionary<DeviceAxis, double>();
        var durations = new Dictionary<DeviceAxis, double>();
        var speeds = new Dictionary<DeviceAxis, double>();
        foreach (var axis in DeviceAxis.All)
        {
            double speed = StimulusSpeed(axis), low = Min(axis), high = Max(axis);
            bool enabled = AxisEnabled(axis);
            _homeLimits[axis] = (low, high, enabled, speed);
            double home = HomeSetting(axis, "AutoHomeTargetValue", axis.DefaultValue);
            if (!double.IsFinite(home) || home < 0 || home > 1) throw new InvalidOperationException(axis + " 的回零目标无效。");
            targets[axis] = enabled ? Lerp(low, high, home) : _positions[axis];
            durations[axis] = enabled ? HomeSetting(axis, "AutoHomeDuration", 1) : 0;
            speeds[axis] = enabled ? speed : double.PositiveInfinity;
        }
        var homeSession = new JXOPlayer.Features.HomeSession(_positions, targets, durations, speeds);
        _outputHook = typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Common.MotionOutputHook", true).GetField("Filter");
        _motionSession = homeSession; _outputFilter = homeSession.Sample;
        _outputHook.SetValue(null, _outputFilter);
        RestoreOutputs();
        _stimulusMode = 3;
        _message.Text = "正在将全部已启用轴移到各自的回零目标；完成后保持零位，点击“恢复运动”回到脚本。";
        Refresh();
    }

    private bool UpdateHome()
    {
        var home = (JXOPlayer.Features.HomeSession)_motionSession;
        foreach (var pair in _homeLimits)
        {
            var a = pair.Key; var original = pair.Value;
            if (Min(a) != original.Low || Max(a) != original.High || AxisEnabled(a) != original.Enabled || StimulusSpeed(a) != original.Speed)
            { StopStimulus("轴范围、启用状态或限速已改变，复位已暂停；确认设置后可重新复位。"); return false; }
        }
        if (Targets().Any(t => Get(t, "UpdateType").ToString() != "FixedUpdate"))
        { StopStimulus("输出模式已改变，复位已暂停。"); return false; }
        foreach (var pair in home.Snapshot()) _positions[pair.Key] = pair.Value;
        if (home.Complete)
        {
            CancelStimulus(); _atHome = true;
            _message.Text = "零点复位完成，所有已启用轴保持各自零位；点击“恢复运动”回到脚本。";
            Refresh();
        }
        return false;
    }
}
