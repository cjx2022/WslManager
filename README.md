<p align="center">
  <img src="Assets/logo.png" width="128" alt="WSL 管理器">
</p>

<h1 align="center">WSL 管理器</h1>

<p align="center">
  <b>2.0</b> · Windows 10/11 · .NET 8 · MIT License
</p>

在同一个窗口里管理 **WSL 发行版**与 **wslc 容器**：开机、关机、安装、卸载，容器、镜像、网络、备份，一处完成。

Windows 11 Fluent 风格界面，浅色 / 深色跟随系统，关闭窗口最小化到系统托盘。

![界面截图](Assets/screenshot.png)

## 功能

### 环境与安装

- 环境总览：WSL 版本、内核版本、默认发行版、容器数量、镜像数量、wslc 版本
- 六条关键路径一键复制：WSL 虚拟磁盘、wslc 数据目录、wslc 虚拟磁盘、wslc.exe、运行日志、发行版共享路径
- 环境检查：Windows 版本、WSL 组件、WSL 状态、系统服务、Windows 功能、CPU 虚拟化、磁盘空间、管理员权限、网络连通性九项，每项附官方文档
- 一键修复：可自动处理的项目直接修复，修完自动复查
- WSL 升级：走官方渠道，带下载进度
- 执行日志：程序调用的每一条 `wsl` / `wslc` 命令与输出，可一键复制

### 发行版管理

| 功能 | 说明 |
|---|---|
| 状态卡片 | 名称、运行状态、WSL 版本、IP、已运行时长，后台静默刷新 |
| CPU / 内存利用率 | 卡片正中显示，百分比按占用变色：低于 60% 绿、60~85% 黄、85% 及以上红 |
| 开机 / 关机 | 关机走发行版内部流程（`shutdown -h now`），服务正常退出、缓存落盘 |
| 强制关机 | 软关机超时或需要立刻停机时使用 |
| 重启 / 全部开机 / 全部关机 | 单个或批量操作 |
| 安装发行版 | 官方在线列表 + 镜像目录，官方源与国内镜像（清华 TUNA / 阿里云 / USTC / 华为云）分组，测速择优、缓存下载、进度与速度、可随时终止 |
| 卸载发行版 | 先导出备份，输入完全一致的发行版名称确认后卸载 |
| 终端 / 文件 | 优先 Windows Terminal，未安装则回退 conhost；资源管理器打开 `\\wsl$\<发行版>` |
| 更多 | 设为默认发行版、复制 IP、复制名称 |

### 容器管理

- 容器列表（含已停止）、启动 / 停止 / 删除、详情、日志、终端
- 新建容器：镜像、名称、端口映射、挂载、环境变量一次配好

### 镜像管理

- 镜像列表、拉取、删除、空间占用
- 镜像搜索：按关键词检索可用镜像
- 镜像存档：`image save` / `image load`，保留标签

### 容器网络管理

- 网络列表、创建、删除

### 备份与清理

- 容器备份与导出 / 导入（容器文件系统）
- 清理向导：按占用排序，逐项确认后再删
- 磁盘压缩：回收虚拟磁盘空洞

### 设置与加速

- 明暗主题、开机自启、镜像加速源、配置文件路径、镜像索引

## 界面

- 无边框窗口 + 自定义标题栏，拖动与双击最大化
- Mica 质感渐变背景、系统圆角、系统主题色作为强调色
- 卡片悬停上浮、状态点脉冲、开关滑动等动效
- 浅色 / 深色各一套调色板，切换即时生效
- 系统托盘图标带状态点：有发行版或容器在运行时常亮

## 命令行

适合写进批处理与计划任务：

```
WslManager.exe --status              :: 查询所有发行版状态（-q）
WslManager.exe --start Ubuntu        :: 开机（-s）
WslManager.exe --stop Ubuntu         :: 软关机，失败则强制终止（-t）
WslManager.exe --force-stop Ubuntu   :: 强制关机（-f）
WslManager.exe --restart Ubuntu      :: 重启（-r）
WslManager.exe --shutdown            :: 全部关机（-k）
WslManager.exe --tray                :: 只显示托盘图标，不弹窗口（-b）
WslManager.exe --page=distro         :: 启动后直接落在指定页（distro / env / container / image / network / backup / settings）
WslManager.exe --autostart           :: 登录后按名单拉起上次运行的容器
```

运行日志：`%LOCALAPPDATA%\WslManager\run.log`。

**开机自启**：设置与加速页勾选即可，不需要管理员权限；可同时勾选「自启时最小化到托盘」。

## 下载与运行

从 [Releases](https://github.com/cjx2022/WslManager/releases) 下载：

| 文件 | 说明 |
|---|---|
| `WslManager-v2.0-win-x64.exe` | 单文件，双击即用（约 1.8 MB） |
| `WslManager-v2.0-win-x64.zip` | 便携包：程序 + 说明 + 许可证 |

要求：Windows 10 / 11 + [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)。程序只调用官方 `wsl` / `wslc` 命令。

**版本说明**：v2.0 是发行版管理与容器管理的合体版。v1.1 只包含发行版管理（约 0.8 MB），仍在 [v1.1](https://github.com/cjx2022/WslManager/releases/tag/v1.1) 提供下载。

## 从源码编译

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：

```
dotnet publish WslManager.csproj -c Release -r win-x64 -p:PublishSelfContained=false -p:PublishSingleFile=true -o publish
```

产物为 `publish\WslManager.exe`（框架依赖单文件）。图标为仓库根目录 `app.ico`。

## 目录结构

```
WslManager.csproj         C# 工程文件（net8.0-windows）
app.ico                   应用图标
Properties\               程序集信息
themes\                   控件样式 + Light/Dark 主题字典
WslManager.WslManager\    应用入口、主窗口、安装 / 卸载 / 环境检查 / 下载管理窗口
WslManager.Install\       镜像源目录、测速下载、安装 / 导出、环境检查、修复服务
WslManager.Wsl\           wsl.exe 调用封装
WslManager.Helpers\       通用辅助类
Wslc\                     容器模块
  WslcView.xaml(.cs)      容器模块页面
  Services\ Models\       wslc 命令封装、镜像检索、容器与镜像模型
  Dialogs\                新建容器 / 详情 / 日志 / 终端 / 通用对话框
  Helpers\ Themes\        磁盘压缩、DPI 文字质量、样式表、主题切换
Assets\                   README 图片
```

## 版本

- **2.0**（2026-10-24）：发行版管理与 wslc 容器管理合并为同一窗口，新增 CPU / 内存利用率、路径一键复制、容器与镜像全套操作、开机自启
- **1.1**（2026-09-29）：发行版管理，含环境检查与一键修复
- **0.1**（2026-09-03）：VB.NET 状态管理器首版

## 关于

**WSL 管理器 2.0** · 开发者 BillChen · © 2026 BillChen · [MIT License](LICENSE)

本软件为个人开发作品，仅供学习与个人使用；按「现状」提供，作者不对使用本软件产生的任何损失承担责任。WSL、Windows 等名称归其各自所有者所有。
