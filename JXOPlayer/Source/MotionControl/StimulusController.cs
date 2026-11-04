public sealed partial class FloatingAxisControl
{
    private int _stimulusMode, _analysisVersion;
    private bool _analyzing;
    private CancellationTokenSource _analysisCancellation;
    private object _stimulusScript;
    private string _stimulusPath;
    private DeviceAxis _rotation;
    private double _startLow, _startHigh, _rotationLow, _rotationHigh;
    private JXOPlayer.Features.IOutputMotion _motionSession;
    private FieldInfo _outputHook;
    private Func<double, DeviceAxis, double> _outputFilter;
    private bool StimulusBusy => _stimulusMode != 0 || _analyzing;

    private object LoadedScript() => _script == null ? null : Get(At(Get(_script, "AxisModels"), _axis), "Script");
    private string LoadedPath()
    {
        if (_script == null) return null;
        var media = Get(_script, "MediaResource");
        return media == null ? null : (string)Get(media, "OriginalPath");
    }

    public void RequestStimulus(int mode)
    {
        Guard(() =>
        {
            if (mode == 3) { StartHome(); return; }
            if (mode != 1 && mode != 2) throw new ArgumentOutOfRangeException(nameof(mode));
            if (_stimulusMode == mode || (_analyzing && mode == 2)) { ReleaseStimulus("刺激已关闭，恢复脚本和自动回零。"); return; }
            if (StimulusBusy) StopStimulus("正在切换刺激模式。");
            _atHome = false;
            _rotation = DeviceAxis.Parse("R0");
            if (_axis != DeviceAxis.Parse("L0") || _rotation == null || !AxisEnabled(_axis) || !AxisEnabled(_rotation))
                throw new InvalidOperationException("请先启用上下轴 L0 和左右旋转轴 R0。");
            if (Max(_axis) <= Min(_axis) || Max(_rotation) <= Min(_rotation))
                throw new InvalidOperationException("上下轴和旋转轴的运动范围必须大于零。");
            if (Targets().Any(t => Get(t, "UpdateType").ToString() != "FixedUpdate"))
                throw new InvalidOperationException("请先将设备输出模式设为 FixedUpdate（固定更新）。");
            var resource = LoadedScript(); var path = LoadedPath();
            if (mode == 1) StartStimulus(1, null, resource, path);
            else AnalyzeStimulus(resource, path);
        });
    }

    // Analysis never mutates the native Script/AxisModel (whose setters can take
    // device locks). A generation check prevents late completion after stop/close.
    private async void AnalyzeStimulus(object resource, string path)
    {
        if (resource == null) { _message.Text = "请先加载当前视频的上下轴脚本。"; return; }
        CancelStimulus();
        int version = _analysisVersion;
        var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _analysisCancellation = cancel;
        _analyzing = true;
        _message.Text = "正在提取重复动作；再点“狂暴刺激”取消，点“暂停刺激”取消并停住。";
        Refresh();
        try
        {
            // Native script resources are immutable after publication. Snapshot
            // only this captured resource, not the live axis dictionary.
            var frames = (JXOPlayer.Common.KeyframeCollection)Get(resource, "Keyframes");
            var result = await System.Threading.Tasks.Task.Run(() =>
            {
                cancel.Token.ThrowIfCancellationRequested();
                return JXOPlayer.Features.RepeatedMotion.Find(frames?.ToArray(), cancel.Token);
            }, cancel.Token);
            if (version != _analysisVersion || _cleaned) return;
            cancel.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(resource, LoadedScript()) || path != LoadedPath())
                throw new InvalidOperationException("视频或脚本已切换，请重新点击狂暴刺激。");
            _analyzing = false;
            StartStimulus(2, result, resource, path);
        }
        catch (OperationCanceledException) { if (version == _analysisVersion && !_cleaned) _message.Text = "动作提取已取消或超时，未接管运动。"; }
        catch (Exception e) { if (version == _analysisVersion && !_cleaned) _message.Text = "无法启动刺激：" + e.GetBaseException().Message; }
        finally
        {
            if (version == _analysisVersion) { _analyzing = false; _analysisCancellation = null; if (!_cleaned) Refresh(); }
            cancel.Dispose();
        }
    }

    private void StartStimulus(int mode, JXOPlayer.Features.RepeatedMotion loop, object resource, string path)
    {
        // Revalidate after asynchronous extraction. Never enable a disabled axis.
        if (!AxisEnabled(_axis) || !AxisEnabled(_rotation) || Max(_axis) <= Min(_axis) || Max(_rotation) <= Min(_rotation))
            throw new InvalidOperationException("轴已禁用或范围为空，请检查设置后重试。");
        if (!AcquireHold(false)) return; // Video keeps playing during takeover.
        _moveTo.Clear();
        _stimulusScript = resource; _stimulusPath = path;
        _startLow = Min(_axis); _startHigh = Max(_axis);
        _rotationLow = Min(_rotation); _rotationHigh = Max(_rotation);
        if (_positions[_axis] < _startLow || _positions[_axis] > _startHigh || _positions[_rotation] < _rotationLow || _positions[_rotation] > _rotationHigh)
            throw new InvalidOperationException("保持位置在新范围外，请先恢复脚本或调整范围后重试。");
        _outputHook = typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Common.MotionOutputHook", true).GetField("Filter");
        _motionSession = new JXOPlayer.Features.MotionSession(mode, loop, _axis, _rotation, _positions,
            _startLow, _startHigh, _rotationLow, _rotationHigh, StimulusSpeed(_axis), StimulusSpeed(_rotation), OutputSampleInterval());
        _outputFilter = _motionSession.Sample;
        _outputHook.SetValue(null, _outputFilter);
        RestoreOutputs(); // No range substitution while generating motion.
        _stimulusMode = mode;
        _message.Text = mode == 1 ? "密集折返：每段 30–60 毫秒，上下包络 50%；旋转行程 20%，每秒 2 次往复。遵守已启用限速；再次点击恢复脚本。"
            : "来源 " + DisplayTime(loop.Start) + "–" + DisplayTime(loop.End) + "（" + loop.Repetitions + " 次相似往复）；1.35 倍节奏，上下行程 " + (Math.Max(.80, loop.Stroke) * 100).ToString("0") + "%；旋转行程 30%，每秒 1.5 次往复。遵守已启用限速；再次点击恢复脚本。";
        Refresh();
    }

    private void CancelStimulus()
    {
        if (_motionSession != null)
        {
            foreach (var pair in _motionSession.Stop()) _positions[pair.Key] = pair.Value;
            // Install the last output before detaching the callback so even a
            // simultaneous transport sample cannot briefly jump back to script.
            ApplyOutputMaps(0);
            if (ReferenceEquals(_outputHook.GetValue(null), _outputFilter)) _outputHook.SetValue(null, null);
            _motionSession = null; _outputFilter = null;
        }
        _analysisVersion++;
        _analysisCancellation?.Cancel(); _analysisCancellation = null;
        _analyzing = false; _stimulusMode = 0; _stimulusScript = null;
    }

    private void ReleaseStimulus(string message)
    {
        ResumeMotion();
        _message.Text = message;
        Refresh();
    }

    private void StopStimulus(string message)
    {
        CancelStimulus();
        _moveTo.Clear();
        // If analysis was pending during normal playback, stop also means hold.
        if (!_held || _resuming) AcquireHold(false);
        if (_held && !_resuming) ApplyOutputMaps(0);
        _message.Text = message;
        Refresh();
    }

    private double StimulusSpeed(DeviceAxis axis)
    {
        // The native output filter is downstream of the normal speed limiter.
        // Reapply only the user's enabled limit, with no extra plugin speed cap.
        double speed = double.PositiveInfinity;
        if (_script != null)
        {
            var settings = At(Get(_script, "AxisSettings"), axis);
            if ((bool)Get(settings, "SpeedLimitEnabled"))
                speed = Math.Min(speed, Math.Max(0, (double)Get(settings, "SpeedLimitUnitsPerSecond")) * (Max(axis) - Min(axis)));
        }
        return speed;
    }

    private double OutputSampleInterval()
    {
        double interval = .01;
        foreach (var target in Targets())
        {
            // A slow disconnected target must not throttle the active device.
            var status = target.GetType().GetProperty("Status")?.GetValue(target)?.ToString();
            if (status != "Connected") continue;
            var contexts = (IDictionary)target.GetType().GetProperty("UpdateContexts")?.GetValue(target);
            if (contexts == null) continue;
            foreach (DictionaryEntry entry in contexts)
                if (entry.Key.ToString() == "FixedUpdate") interval = Math.Max(interval, Convert.ToDouble(Get(entry.Value, "UpdateInterval")) / 1000);
        }
        return interval;
    }

    private bool UpdateStimulus()
    {
        if (_stimulusMode == 0) return false;
        if (_stimulusMode == 3) return UpdateHome();
        if (!ReferenceEquals(_stimulusScript, LoadedScript()) || _stimulusPath != LoadedPath())
        { ReleaseStimulus("视频或脚本已切换，刺激已关闭并恢复自动控制。"); return false; }
        if (!AxisEnabled(_axis) || !AxisEnabled(_rotation) || Min(_axis) != _startLow || Max(_axis) != _startHigh || Min(_rotation) != _rotationLow || Max(_rotation) != _rotationHigh
            || Targets().Any(t => Get(t, "UpdateType").ToString() != "FixedUpdate"))
        { ReleaseStimulus("轴范围、启用状态或输出模式已改变，刺激已关闭。"); return false; }
        ((JXOPlayer.Features.MotionSession)_motionSession).SetLimits(StimulusSpeed(_axis), StimulusSpeed(_rotation), OutputSampleInterval());
        foreach (var pair in _motionSession.Snapshot()) _positions[pair.Key] = pair.Value;
        return true;
    }
}
