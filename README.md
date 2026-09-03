# WSL 状态管理器
<img width="1645" height="1118" alt="image" src="https://github.com/user-attachments/assets/827eec9f-58f5-4e3d-94e4-644776694c21" />

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

依赖：Windows 10/11 + [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)

## 重新编译

```bat
dotnet publish -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o app
```

图标由 `tools\gen_icon.py` 生成（`python tools\gen_icon.py Assets\app.ico`）。


## 目录结构

```
App.xaml(.vb)          应用入口：单实例、主题、命令行模式
MainWindow.xaml(.vb)   主界面与交互逻辑
Services\WslService.vb wsl.exe 调用封装
ViewModels\            发行版卡片视图模型
Helpers\               Mica/主题、图标生成、设置、日志、命令、转换器
Themes\                Controls 样式 + Light/Dark 主题字典
```
