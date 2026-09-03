# WSL 状态管理器

用 **VB.NET + WPF** 写的 WSL 发行版开关机小工具，Windows 11 Fluent 风格界面（Mica 背景、圆角卡片、明暗主题、系统托盘）。

## 功能

| 功能 | 说明 |
| --- | --- |
| 状态总览 | 发行版名称、运行状态、WSL 版本、IP 地址、已运行时长，每 5 秒后台静默刷新 |
| 开机 | 单个发行版启动 |
| 关机（软关机） | 以 root 在发行版内执行 `shutdown -h now`，等待其自行停机；超时（20 秒）才强制终止 |
| 强制关机 | 卡片「更多」菜单里，直接 `wsl -t`，不等待软关机 |
| 重启 | 软关机后再开机 |
| 全部开机 / 全部关机 | 批量启动所有停止的发行版；关机时先对所有运行中的发行版发软关机指令，稍等后用 `wsl --shutdown` 兜底 |
| 打开终端 | 优先 Windows Terminal，未安装则回退 conhost |
| 打开文件 | 资源管理器打开 `\\wsl$\<发行版>` |
| 更多操作 | 设为默认发行版、复制 IP、复制名称、**强制关机** |
| 系统托盘 | 关闭窗口即最小化到托盘，图标颜色随运行状态变化（绿=有运行中） |
| 明暗主题 | 跟随系统，点标题栏图标可手动切换并记忆 |

## 关机方式说明

| 方式 | 命令 | 数据安全 |
| --- | --- | --- |
| 关机（默认，软关机） | `wsl -d X -u root -- /sbin/shutdown -h now` → 轮询等待停止 | 走发行版内部关机流程，服务正常退出、缓存落盘 |
| 强制关机 | `wsl -t X` | 相当于直接掐掉虚拟机，未落盘的数据可能丢失 |
| 全部关机 | 逐个发软关机指令 → `wsl --shutdown` 兜底 | 同上，软关机失败的部分由 `--shutdown` 兜底 |

默认走软关机。若软关机指令没送达（例如发行版内没有 shutdown 或权限异常），会先执行一次 `sync` 刷盘再强制终止，尽量降低数据风险。

## 界面

- 无边框窗口 + 自定义标题栏（拖动、双击最大化）
- 自绘的 Mica 质感渐变背景 + 系统圆角，系统主题色作为强调色
- 卡片悬停上浮、状态点脉冲、开关滑动等动效

## 命令行用法

适合写进批处理 / 计划任务：

```bat
WslManager.exe --status              :: 查询所有发行版状态
WslManager.exe --start Ubuntu        :: 开机
WslManager.exe --stop Ubuntu         :: 软关机（失败则强制终止）
WslManager.exe --force-stop Ubuntu   :: 强制关机（wsl -t，不等待）
WslManager.exe --restart Ubuntu      :: 重启
WslManager.exe --shutdown            :: 全部关机
WslManager.exe --tray                :: 后台常驻（只显示托盘图标，不弹窗口）
```

运行日志在 `%LOCALAPPDATA%\WslManager\run.log`，命令行模式的输出也会写进去（GUI 程序没有控制台）。

开机自启：在「任务计划程序」里建一个登录时触发的任务，程序填 `WslManager.exe`，参数填 `--tray`。

## 运行

`app\WslManager.exe`（单文件，约 256 KB）双击即可运行。

依赖：Windows 10/11 + [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（本机已装 8.0.8）。

## 重新编译

```bat
dotnet publish -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o app
```

图标由 `tools\gen_icon.py` 生成（`python tools\gen_icon.py Assets\app.ico`）。

## 实现要点（踩过的坑）

1. **`wsl.exe` 传参必须用 `ProcessStartInfo.ArgumentList`**。
   用 `Arguments` 字符串（哪怕正确加了引号）启动发行版会稳定返回退出码 `-1`，改用 `ArgumentList` 逐项添加后正常返回 `0`。
2. **必须重定向 stdin 且不能关闭**。GUI 进程没有可用 stdin；若把 `StandardInput` 关掉，子进程读到 broken pipe，启动同样失败为 `-1`。
3. **`wsl -l -v` 输出是 UTF-16LE 且不带 BOM**。按 `bytes[1]==0 && bytes[3]==0` 判定后按 Unicode 解码；其他命令（如 `hostname -I`）是 UTF-8，需分别处理。
4. **WSL2 空闲会自动停机**。只执行 `wsl -d X true` 后状态会短暂变 Running 再自动 Stopped，因此额外挂一个 `sleep 8640000` 的后台保活进程，才能真正保持开机。
5. **XAML 中已勾选的 CheckBox，代码里赋同样的值不会触发 `Checked` 事件**，自动刷新定时器必须显式启动，否则永远不生效。
6. **软关机必须用 `-u root`**。以默认用户执行 `shutdown -h now` 会被 polkit 拦下：
   `Call to PowerOff failed: Interactive authentication required.`，命令返回 `rc=1` 且什么都不做。
   这个坑很隐蔽——表面上看发行版"过一会儿确实停了"，其实那是 WSL 空闲自动停机，不是被 shutdown 关掉的。
   挂一个保活进程做对照实验才能分辨。
7. **软关机命令超时不能 Kill 进程**。`wsl.exe` 会一直挂到发行版真正停止，`Kill(True)` 会连 WSL 会话一起杀掉，
   导致关机流程中断。应超时放弃等待、让它在后台跑完，用状态轮询判断结果。
8. **对已停止的发行版不要执行 `wsl -d X ...`**。wsl 会为了运行这条命令把发行版先拉起来，
   "关机"反而变成"开机"。执行前必须先查状态。
9. **WPF 里不能靠 DWM 材质做窗口背景**。WPF 客户区是不透明渲染的，`DWMWA_SYSTEMBACKDROP_TYPE`(Mica)
   申请成功也透不出来；若再把根容器背景设成 `Transparent`，窗口会直接变成纯黑——
   深色主题下不明显，浅色主题下非常刺眼。正确做法是背景自绘（本项目用 `MicaBrush` 渐变模拟云母层次），
   DWM 只用来做圆角和沉浸式深色。
10. VB 注意点：`on` 是保留字不能作变量名；`ObservableCollection.Count(predicate)` 需写成 `AsEnumerable().Count(...)`；
    对象初始化器里不能写注释；`OnPropertyChanged` 与 `FrameworkElement` 基类方法冲突需改名；
    `Async Function` 的返回类型必须是 `Task` / `Task(Of T)`。
11. **重启按钮在卡片内被裁切**。操作区三个按钮总宽超出卡片左侧内容列可用宽度，最右侧按钮显示不全。
    根因：卡片宽 404 + 内边距 + 右侧图标列挤占，左侧操作列仅约 250px，而三个 84px 按钮（268px）放不下。
    修法：卡片 404→420、按钮 84→78、按钮间距 8→6、操作区左缩进 26→18，共腾出约 16px；
    修复后三个 78px 按钮 + 6px 间距 = 246px ≤ 可用约 266px，全部完整显示。
12. **打开终端报「UNC 路径不受支持」**。原 `wt.exe -d "\\wsl$\X"` 把 UNC 作为起始目录传给 Windows Terminal，
    CMD profile 启动时落到 UNC 工作目录，弹"UNC 路径不受支持，默认值设为 Windows 目录"。
    改法：不直接用 UNC 作起始目录，改为 `wt.exe wsl.exe -d "X"`，由发行版自己决定工作目录（通常落在用户家目录）。
13. **卡片布局改为单列自适应**。原 `ItemsPanel` 是 `WrapPanel`（卡片固定 `Width=420`，多列平铺）。
    改成 `StackPanel Orientation=Vertical`，卡片 `HorizontalAlignment=Stretch` 去掉固定宽度 → 一行一个、随窗口宽度拉伸；
    另加 `MaxWidth=900`（超过后居中）避免超宽屏上按钮被拉得过散。`ScrollViewer.HorizontalContentAlignment=Center` 配合居中。

## 目录结构

```
App.xaml(.vb)          应用入口：单实例、主题、命令行模式
MainWindow.xaml(.vb)   主界面与交互逻辑
Services\WslService.vb wsl.exe 调用封装
ViewModels\            发行版卡片视图模型
Helpers\               Mica/主题、图标生成、设置、日志、命令、转换器
Themes\                Controls 样式 + Light/Dark 主题字典
```
