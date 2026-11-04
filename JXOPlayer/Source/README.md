# JXOPlayer 2.0 开发与验证

本目录保存新增功能的维护源码与发行版改造工具。原软件仅提供编译后的发行文件，未提供完整工程；本目录不等同于原软件全部源代码。

## 模块

- `FloatingAxisControl.cs`：主窗口控制栏、悬浮窗、轴暂停与手动控制、原生热力图绑定。
- `RandomPlayback/IntensityLibrary.cs`：脚本解析、ES 强度算法、相对分档、视频配对、缓存与随机选择。
- `RandomPlayback/DurationMatching.cs`：时长容差、歧义判断、脚本元数据流式读取、视频时长缓存与限并发补充读取。
- `RandomPlayback/ContainerDurationReader.cs`：直接读取 MP4/MOV/M4V 的 mvhd 和 MKV/WebM 的 Info 时长，跳过媒体数据，限制头部数量和读取边界；碎片 MP4 等情况交给补充读取。
- `Bin/DurationProbe.lua`：附带 MPV 的时长补充读取器，在暂停状态的 file-loaded 事件报告时长并退出，不解码视频帧、不启动 PotPlayer。
- `RandomPlayback/LibraryCacheSession.cs`：兼容原评分缓存，按文件指纹更新；每 5 秒及结束/取消时原子保存；同一路径扫描串行交接，避免旧请求覆盖新结果。
- `RandomPlayback/LocalDurationMatcher.cs`：在原生本地检索按名称挑选脚本前选择时长相符的主脚本目录或压缩包，附属轴只使用同一来源。
- `RandomPlayback/PotPlayerBridge.cs`：通过原生媒体源管理器联动 PotPlayer，并确认所选脚本加载。
- `RandomPlayback/RandomPlaybackService.cs`：异步扫描、设置与主窗口 / 悬浮窗五档按钮。
- `Build`：生成运行插件、修改发行程序集、构建图标和原生程序资源。
- `Tests`：算法、悬浮控制、连续启动、实际 PotPlayer 换片验证。
- `Tests/BridgeRegression.cs`：脚本确认、轴锁等待、同片重播、取消及断开连接回归检查。
- `Results`：最近一次测试结果与预览图。
- `Tools`：修改程序集与 BAML 所需的开发依赖，不参与正常运行。

## 修改插件

`MotionControl/RepeatedMotion.cs` 负责重复段提取，`MotionControl/MotionSession.cs` 在原生输出线程按实际时间连续采样，`StimulusController.cs` 负责 UI 接管及归还。`Build/AddMotionOutputHook.ps1` 给原生 `AbstractOutputTarget.GetValues` 的范围映射结果添加空值直通的委托接入点，已经集成到 `Rebrand.ps1`。不改原来的输出线程、变化过滤、TCode 插值时长和设备传输。

`Tests/TestStimulus.cs` 验证增强后的幅度与速度、最快重复周期、限速、UI 阻塞及反复开关；`StartupRegression.cs` 在禁用设备连接的原生程序副本中验证真实蓝牙 `GetValues`、R0 变化过滤和 TCode 指令格式、范围引用及自动回零设置。刺激期间保持原范围字典，退出接管后清空委托；再次点击刺激按钮恢复自动控制，顶部暂停按钮才进入持续保持。

`MotionControl/TremorPattern.cs` 生成密集的分段线性不规则折返；参考统计与匿名局部数值保存在 `Results/tremor-reference-analysis.json`。`HomeSession.cs` 与 `HomeController.cs` 实现全部轴显式回零，使用各轴既有目标、时长和限速，结束后保持。复位计时从首次设备取样开始。`Tests/TestHome.cs` 覆盖跨模式接管、取消迟到分析、禁用轴、独立目标、暂停重试和限速；`RenderMotionPreview.py` 从实际测试输出数据绘制曲线对照图。

编辑上述源码，然后在 PowerShell 7 中执行：

```powershell
& .\Source\Build\BuildPlugin.ps1
```

脚本生成 `Plugins/FloatingAxisControl.cs`，软件启动时自动编译加载。不要直接修改生成文件。

## 修改主程序

```powershell
& .\Source\Build\BuildApplication.ps1 -OriginalAssembly '<原始发行程序集路径>' -OriginalExecutable '<原始发行启动程序路径>'
```

必须使用更新前备份中的原始发行文件作为输入，不能把改造后的程序集再次当作原始输入。目标目录需已有 `Runtime` 运行依赖以及对应的 `JXOPlayer.deps.json` / `JXOPlayer.runtimeconfig.json`。构建脚本更新名称、版本、图标、内嵌资源与界面，移除原帮助模块，并修正目录调整后的资源定位和插件编译逻辑。

这些构建脚本针对当前发行文件，升级原始程序时需重新检查补丁匹配条件。

`Build/AddDurationMatchingHook.ps1` 给原生 `LocalScriptRepository.SearchForScripts` 添加候选过滤委托，已集成到重建流程。运行插件在主控制栏初始化时注册过滤器，并重新发起一次当前媒体检索。过滤器未就绪时本地检索返回空结果，避免先按名称输出错误脚本。关闭或热更新时仅注销自己的委托；原生脚本锁定设置仍有效。

## 测试

```powershell
& .\Source\Tests\RunTests.ps1
& .\Source\Tests\RunTests.ps1 -PotPlayerOnly
```

测试创建临时程序副本和合成脚本 / 视频。仅在测试副本中禁用设备连接管理器，防止启动扫描设备；正式程序保持原连接行为。实际 PotPlayer 测试需先关闭现有 PotPlayer，防止单实例转发。测试播放器使用 `JxoPlaybackProbe.exe`，只在临时主程序集里替换两处进程发现规则、临时插件里替换联播进程发现规则，且禁用测试媒体源的自动启动。正式 JXOPlayer 的 `potplayer` 发现规则不会找到测试播放器。测试结束删除临时副本，结果写入 `Results`。

`Tests/TestDurationMatching.cs` 使用合成 12 秒视频验证真实时长读取、同名跨目录选择、片尾差异、元数据、缓存失效、ZIP、本地原生检索及辅助轴来源一致性。普通算法测试的视频占位文件使用注入的时长读取器；真实媒体测试使用 `Tests/Fixtures/duration-12s.mp4`。PotPlayer 集成测试额外放入同目录的错误 120 秒脚本和其他目录的正确 60 秒脚本，验证随机及直接打开视频两条路径。

`Tests/TestScanPerformance.cs` 验证 MP4 32/64 位时长、扩展长度、碎片/损坏头部、MKV、AVI 补充读取、取消续扫、旧缓存迁移及失败缓存过期，并对比原逐文件启动 MPV 和新的直接读取耗时。格式依据：[Apple mvhd](https://developer.apple.com/documentation/quicktime-file-format/movie_header_atom)、[Matroska Info/Duration/TimestampScale](https://www.matroska.org/technical/elements.html)。不读取整部视频或解析每帧。

`Tests/MeasureLibraryScan.ps1` 用独立控制台程序扫描当前配置的真实库两次，记录首次升级与缓存刷新耗时；不运行主程序或设备逻辑。默认仅写入 Results 下的测量缓存；使用 `-PublishCache` 时，须主程序未运行且原缓存/目录设置未变才安装测量缓存。结果写入 `Results/library-scan-benchmark.json`。

2026-10-19 兼容性修正：时长用于多个同名候选的相对比较，不作为唯一脚本的准入门槛；只有末尾动作时间的部分覆盖脚本照常可用。`MotionIdentity.cs` 对规范化的动作时间/位置计算 SHA-256，只在候选相近时用来识别相同动作副本，旧缓存的指纹按需补充。原生过滤器与随机库共用选择器，播放桥仅校验文件变化，不再次检查时长接近度。按后续要求，音频扩展名从随机库排除，原生音频脚本匹配继续保留。MPV 补充读取保留音频流并使用 null 音频输出与暂停状态，不能使用 `--no-audio`，否则纯音频的 file-loaded 时长读取失败。

`RandomPlayback/AutoAdvance.cs` 提供可独立测试的结束检测器与有超时的后台 PotPlayer 状态读取。服务仅在启用开关、有当前库分档且原生连接存在时读取，使用路径索引，不重新扫描库或读取脚本。连续进度接近结尾、原生状态从播放变为停止后等待 2 秒；结尾容差结合实际采样间隔，最大 1.5 秒。一次读取失败保留最近可靠样本，超过 1.5 秒无可靠样本才失效；缺失数据本身绝不触发联播。期间换片、循环、暂停、断开、失去分档均取消。原生协议没有独立 EOF 事件，因此片尾瞬间的手动停止可能与自然结束无法区分；需要无条件保持停止时关闭联播开关。界面平滑外推的播放进度不能作为 EOF 依据。

自动打开沿用原播放桥；脚本预载后再次验证旧路径、窗口、停止状态及开关版本，播放器已继续则取消。自动请求不会重启或重连播放器。`Tests/TestAutoAdvance.cs` 覆盖时序和取消边界，`BridgeRegression.cs` 覆盖异步预载竞态；真实 PotPlayer 测试覆盖自然结束换片、暂停、原生继续优先和 MP3 手动脚本匹配。

`Tests/RunTests.ps1 -ReportedMediaOnly` 读取日志中曾经失败的 MP3 路径，核对实际时长及原生候选过滤结果，只读取元数据和脚本，不启动主程序或播放媒体。需要先运行真实库测量，生成 Results 下的缓存。

随机播放预读已经配对的脚本，PotPlayer 确认路径后直接应用，不依赖原生全库检索的完成通知。后台设置脚本后短暂等待并再次核对当前 L0，避免“打开成功”只代表视频路径切换。任意原生检索迟到回调完成时，若仍是当前随机视频，则重新核对所选脚本，修复被旧回调清空或覆盖的 L0；若用户已换片则不应用。脚本替换仍在后台执行，避免界面线程等待原生轴锁。每次打开有独立取消令牌和总时限；日志 `JXOPlayer.RandomPlayback` 记录请求、实际应用、修复、完成、异常和联播触发，便于区分加载与结束检测问题。

ES 算法对照样本由 `GenerateIntensityGolden.py` 生成，参考相邻 ES 工具工程的 `organizer/local_tools/script_intensity.py`，不修改 ES 工具。相对档位针对当前有效脚本集合计算，按唯一脚本去重、相同分数使用中位名次，避免同脚本关联多个视频影响分档。

已验证功能不包括真实设备运动、PotPlayer 独占全屏覆盖。

2026-11-01 文件名兼容修正：`Build/FixLiteralMediaPaths.ps1` 对当前程序集的本地路径解析和 PotPlayer 文件名发送位置做定点补丁，同时接入 `Rebrand.ps1` 供以后重建。完整 Windows 路径先按字面解析，显式 `file://` URI 仍只解码一次。插件的 `PathIdentity` 统一普通/扩展 Windows 前缀及既有 8.3 别名，仅用于比较；文件操作保留原文件名。名称不做符号删除或兼容字符折叠。

`RandomPlayback/LocalMediaRelay.cs` 是播放器长路径兼容层：普通路径直接打开，超长路径优先使用现有短路径别名，否则注册仅监听 `127.0.0.1` 随机端口、随机路由的读取通道。请求仅允许注册视频的 GET/HEAD 和单段 Range，支持 64 位偏移，拒绝任意路径、写入和失效文件指纹；每条连接有超时、请求头上限和并发上限。原生解析钩子将本次进程已注册地址还原为原文件的名称/目录，路径比较和联播索引使用同一映射。关闭服务即关闭端口并清理映射；依赖该通道的播放需要保持 JXOPlayer 运行。普通打开指令与随机打开共用这套处理。

`TestDurationMatching.TestFileNames` 验证超过 500 字符的本地路径、混合语言、表情、字面百分号及原生配对。`TestLocalMediaRelay.cs` 验证 HTTP 读取、跳转范围、访问限制、变更拒绝和关闭。真实播放器测试另外验证 267/274/468 字符路径、组合字符、跳转、普通打开和长路径视频联播。测试均使用合成媒体和禁用设备连接的独立进程。
