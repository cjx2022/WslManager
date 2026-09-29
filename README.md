# WSL 管理器

**版本 1.1 · 开发者 BillChen · MIT License**

![界面截图](Assets/screenshot.png)

用 **C# + WPF**（.NET 8）写的 WSL 发行版管理工具——状态管理、**安装**、**卸载** 一站式操作，Windows 11 Fluent 风格界面（Mica 背景、圆角卡片、明暗主题、系统托盘）。

0.1 版（VB.NET 状态管理器）见 [`v0.1` 标签](https://github.com/cjx2022/WslManager/tree/v0.1)。

## 功能

| 功能 | 说明 |
|---|---|
| 状态总览 | 发行版名称、运行状态、WSL 版本、IP 地址、已运行时长，每 5 秒后台静默刷新 |
| 开机 | 单个发行版启动 |
| 关机（软关机） | 以 root 在发行版内执行 `shutdown -h now`，等待其自行停机；超时（20 秒）才强制终止 |
| 强制关机 | 卡片「更多」菜单里，直接 `wsl -t`，不等待软关机 |
| 重启 | 软关机后再开机 |
| 全部开机 / 全部关机 | 批量启动所有停止的发行版；关机时先对所有运行中的发行版发软关机指令，稍等后用 `wsl --shutdown` 兜底 |
| **安装发行版**（1.0 新增） | 两级选择：官方在线列表（22 个）∪ 镜像目录特供；官方下载源与国内镜像（清华 TUNA / 阿里云 / USTC / 华为云）分组显示，**测速择优**、缓存下载、实时进度与速度、任务可随时终止 |
| **卸载发行版**（1.0 新增） | 导出备份后，勾选知晓并输入完全一致的发行版名称确认，再执行卸载 |
| **环境检查**（1.0 新增） | 9 项检查：Windows 版本、WSL 组件、WSL 状态、系统服务、Windows 功能、CPU 虚拟化、磁盘空间、管理员权限、网络连通性，每项附官方文档链接 |
| **一键修复 / 单项修复**（1.1 新增） | 未通过的项目若可自动处理，行内直接出现「修复」按钮；标题栏「一键修复（N）」按顺序批量处理全部可修复项，修完自动复查并高亮结果。不支持自动修复的项（CPU 虚拟化、磁盘空间、网络）保留文档入口手动处理 |
| **下载管理**（1.0 新增） | 查看 / 打开 / 删除本地已缓存的安装包 |
| 打开终端 | 优先 Windows Terminal，未安装则回退 conhost |
| 打开文件 | 资源管理器打开 `\\wsl$\<发行版>` |
| 更多操作 | 设为默认发行版、复制 IP、复制名称、强制关机、**卸载该发行版** |
| 系统托盘 | 关闭窗口即最小化到托盘，图标颜色随运行状态变化（绿=有运行中） |
| 明暗主题 | 跟随系统，点标题栏图标可手动切换并记忆；深浅模式强调色一致 |
| 关于 | 状态栏右下角「关于」文本，查看版本、开发者与版权声明 |

## 安装发行版

1. **选发行版**：`wsl --list --online` 官方全量列表 ∪ 镜像目录特供（如 Alpine）；选中即自动测速当前源
2. **选下载源**：官方在线（微软商店在线安装）或国内镜像（清华 TUNA / 阿里云 / USTC / 华为云）

测速与下载策略：

- 测速：读满 4MB 或 8 秒出真实速度，12 秒兜底超时；≥1024 KB/s 显示 MB/s，慢速源显示真实低速而非超时
- 下载：不限总时长 + 60 秒首字节 / 读取停滞看门狗——慢但有进展即可完成，失败自动清理半成品
- 缓存：已下载的安装包保留在 `%LOCALAPPDATA%\WslManager\downloads`，命中缓存直接进入安装
- 格式自动处理：`.wsl` → `wsl --install --from-file`，`.tar.gz` → `wsl --import`
- 任务进行中头部出现「终止」按钮，可随时取消（同时清理半成品）

## 卸载发行版

主窗口「卸载发行版」按钮或卡片「…」菜单进入：

1. 选择发行版（显示安装路径、占用空间、状态）
2. 导出备份到 `%LOCALAPPDATA%\WslManager\backup\`（可勾选「卸载前自动导出」）
3. 勾选知晓并**输入完全一致的发行版名称** → 执行卸载

## 关机方式说明

| 方式 | 命令 | 数据安全 |
|---|---|---|
| 关机（默认，软关机） | `wsl -d X -u root -- /sbin/shutdown -h now` → 轮询等待停止 | 走发行版内部关机流程，服务正常退出、缓存落盘 |
| 强制关机 | `wsl -t X` | 相当于直接掐掉虚拟机，未落盘的数据可能丢失 |
| 全部关机 | 逐个发软关机指令 → `wsl --shutdown` 兜底 | 同上，软关机失败的部分由 `--shutdown` 兜底 |

默认走软关机。若软关机指令没送达（例如发行版内没有 shutdown 或权限异常），会先执行一次 `sync` 刷盘再强制终止，尽量降低数据风险。

## 界面

- 无边框窗口 + 自定义标题栏（拖动、双击最大化）
- 自绘的 Mica 质感渐变背景 + 系统圆角，系统主题色作为强调色（深浅模式一致）
- 扁平卡片、发丝分隔线、下拉列表等全部主题化，明暗切换无白底白字
- 卡片悬停上浮、状态点脉冲、开关滑动等动效

## 命令行用法

适合写进批处理 / 计划任务：

```
WslManager.exe --status              :: 查询所有发行版状态（-q）
WslManager.exe --start Ubuntu        :: 开机（-s）
WslManager.exe --stop Ubuntu         :: 软关机（失败则强制终止）（-t）
WslManager.exe --force-stop Ubuntu   :: 强制关机（wsl -t，不等待）（-f）
WslManager.exe --restart Ubuntu      :: 重启（-r）
WslManager.exe --shutdown            :: 全部关机（-k）
WslManager.exe --tray                :: 后台常驻（只显示托盘图标，不弹窗口）（-b）
```

运行日志在 `%LOCALAPPDATA%\WslManager\run.log`，命令行模式的输出也会写进去（GUI 程序没有控制台）。

开机自启：在「任务计划程序」里建一个登录时触发的任务，程序填 `WslManager.exe`，参数填 `--tray`。

## 运行

在 [Releases](https://github.com/cjx2022/WslManager/releases) 下载 `WslManager.exe`（单文件，约 0.8 MB）双击即可运行。

依赖：Windows 10/11 + [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)

## 重新编译

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：

```
dotnet publish WslManager.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:PublishSelfContained=false -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

发布产物为 `publish\WslManager.exe`（框架依赖单文件，约 0.8 MB）。图标为仓库根目录 `app.ico`。

> **注意**：`--self-contained false` 在 .NET 10 SDK 上会被 `PublishSingleFile` 覆盖（该属性在 `dotnet publish` 期间隐含 `SelfContained=true`），
> 导致产物体积从 0.8 MB 膨胀到 68 MB。必须改用 `-p:PublishSelfContained=false` 才能得到框架依赖的瘦单文件。
> 另外本工程启用了 `UseWindowsForms`（托盘图标），无法使用 `PublishTrimmed`（报 NETSDK1175），也无法使用
> `EnableCompressionInSingleFile`（报 NETSDK1176，压缩仅支持自包含应用）。

## 目录结构

```
WslManager.csproj         C# 工程文件（net8.0-windows）
app.ico                   应用图标
Properties\               程序集信息（版本 1.0.0.0 · BillChen）
themes\                   Controls 样式 + Light/Dark 主题字典
WslManager.WslManager\    应用入口、主窗口、安装/卸载/环境检查/下载管理窗口
WslManager.Install\       安装相关：镜像源目录、测速下载、安装/导出、环境检查、修复服务
WslManager.Wsl\           wsl.exe 调用封装（列表/开关机/默认/终端）
WslManager.Helpers\       通用辅助类
Assets\                   README 截图
```

## 更新记录

- **1.0**（2026-09-28）：C# 全面重写。新增安装发行版（官方源 + 国内镜像测速下载）、卸载（导出备份 + 名称确认）、环境检查、下载管理；界面扁平化与明暗主题细节打磨；修复滚动、列表刷新、UTF-16 输出乱码、下载超时等十余项问题
- **1.1**（2026-09-29）：环境检查支持修复。新增「一键修复」与逐项「修复」按钮：自动安装/更新 WSL 组件、启动相关系统服务、用 DISM 启用 Windows 功能（自动识别 3010 = 需重启）、提权重启自身；修复后自动复查并展示结果
- **0.1**（2026-09-03）：VB.NET 状态管理器首版（[`v0.1` 标签](https://github.com/cjx2022/WslManager/tree/v0.1)）

## About

**WSL 管理器 1.0** · 开发者 BillChen · © 2026 BillChen 版权所有 · [MIT License](LICENSE)

本软件为个人开发作品，仅供学习与个人使用；软件按「现状」提供，作者不对使用本软件产生的任何损失承担责任。WSL、Windows 等名称归其各自所有者所有。
