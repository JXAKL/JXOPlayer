# JXOPlayer 2.0

Windows 桌面播放器联动与脚本控制工具。默认使用 PotPlayer，提供可折叠置顶悬浮窗、热力图、轴控制、零点复位、五档强度随机播放和同档自动联播。

本仓库保存新增功能源码、原生程序集改造脚本、测试及干净发行包构建脚本。原软件只提供编译后的发行文件，因此这里不是可从零编译原主程序的完整工程。当前原生程序及运行依赖随 Releases 中的干净发行包提供。

## 使用

打开 **[2.0.2 测试版下载页](https://github.com/JXAKL/JXOPlayer/releases/tag/v2.0.2)**，在 Assets 中下载 `JXOPlayer-2.0.2-Setup.exe` 安装，或下载 `JXOPlayer-2.0.2-Portable.zip` 完整解压后运行。适用于 Windows 10 2004（19041）及以上、Windows 11 64 位，自带 .NET 运行环境、Windows 蓝牙接口组件、PotPlayer 与媒体时长读取组件。GitHub 自动生成的 Source code 是源码，不是可直接运行的软件包。

2.0.2 修复串口筛选和手动 UDP 握手限制，并用 Windows 蓝牙接口替换依赖调试运行库的旧组件。BLE 实现支持 FFE0/FFE1 和 Nordic UART，固件需通过相应服务接收 TCode。已通过 62 项隔离连接与启动检查，尚未完成真实 ESP32 BLE 收发验证，因此以预发布测试版提供。

发行包不携带媒体库、个人设备配置、播放历史或缓存。首次运行需要配置设备、轴范围和脚本/视频目录。音频不进入随机视频库，但支持手动播放匹配脚本。超长路径兼容播放期间需保持 JXOPlayer 运行。

## 目录

- `JXOPlayer/Source`：悬浮窗、脚本配对、随机播放、运动控制、构建工具和测试。
- `JXOPlayer/Bin/DurationProbe.lua`：媒体时长补充读取脚本。
- `JXOPlayer/Plugins/jiaxin.jpg`：应用品牌图像。
- `Packaging`：干净发行包构建、首次启动与安装/卸载检查。

运行配置、日志、媒体索引和测试输出均被 `.gitignore` 排除。测试 Fixtures 中的短音视频为合成素材。

## 开发

1. 克隆仓库，在仓库根目录解压 Releases 的免安装 ZIP，使运行文件位于 `JXOPlayer` 下。
2. 使用 PowerShell 7 执行 `JXOPlayer/Source/Build/BuildPlugin.ps1`。该命令将维护源码组合为运行插件，软件启动时自动编译。
3. 需要验证时执行 `JXOPlayer/Source/Tests/RunTests.ps1`；实际播放器检查加 `-PotPlayerOnly`，并先关闭 PotPlayer。此测试需要 FFmpeg，可通过 `FFMPEG_PATH` 环境变量指定或加入 PATH。

测试使用隔离副本并禁用设备连接。有关原生补丁输入、模块和测试说明见 [开发文档](JXOPlayer/Source/README.md)。修改原生程序集需要原始发行文件；不能把已改造的程序集再次作为原始补丁输入。

## 打包

准备完整运行目录后，使用 `Packaging/BuildRelease.ps1` 重新生成干净 EXE 安装包和 ZIP。需要 Inno Setup（放在 `Packaging/Tools/InnoSetup`）及 7-Zip；中文安装翻译位于该编译器的 Languages 目录。

打包从白名单复制程序文件，并从头生成默认设置；不会读取原用户配置。安装到当前用户目录，升级保留配置，卸载保留使用后生成的个人设置。

## 最近验证

文件名兼容修复验证了混合中日英文、表情、组合字符、百分号和超长路径；实际 PotPlayer 覆盖播放、脚本匹配、跳转和联播。干净发行包已完成首次启动、安装文件校验、重复安装保留配置及卸载检查。

第三方组件保留各自许可；本仓库未为原软件、第三方播放器或其依赖重新授予许可。
