# PowerSwitch .NET

> **NAS 上的电源计划自动切换** —— 按 CPU 负载与夜间时段，在「节能 / 平衡 / 高性能」之间自动切换，有效降低 NAS 空载待机功耗。
>
> 这是 [PowerSwitch](https://github.com/Chaconez/PowerSwitch)（PowerShell 版）的 **C# 重写版**，为彻底解决「托盘图标反复丢失」而做。

C# / .NET Framework 4.8 · WinForms · 单进程 · 零安装依赖 · 远程桌面联动

---

## 为什么要重写

旧 PowerShell 版功能是好的，但托盘图标会反复消失。排查下来不是偶发 bug，而是**架构决定的必然结果**：

| 现象 | 证据 | 根因 |
|---|---|---|
| 图标没了，进程也没了 | `tray.log` 记录了 18 次 `tray started`，**0 次 `tray stopped`** | 进程被硬杀（`0xC000013A`），`finally` 没执行 |
| 死了不会自己回来 | 启动项只在登录时跑一次；交互式计划任务被系统拒绝（`0x800710E0`） | **没有任何自愈机制** |
| 会话断开后消失 | 托盘最后一条日志 `14:49:12`，2 分钟后 `TS id=40 session 3 disconnected` | 进程生命周期被 RDP 会话绑死 |
| explorer 重启后图标不回来 | 脚本里没有 `TaskbarCreated` 处理 | NotifyIcon 注册在 explorer 的托盘窗口上，窗口重建后句柄失效 |

三个问题叠加：**进程会死、死了不回、回来了也可能没图标**。PowerShell 脚本单靠补丁无法同时解决，所以换语言重写。

---

## 架构

```
┌─────────────────────────────────────────────┐
│  PowerSwitchWatchdog.exe   (SYSTEM, 会话 0)  │
│  · 每 15 秒枚举用户会话                      │
│  · 会话里没有 PowerSwitch.exe 就拉起来        │
│  · WTSQueryUserToken + CreateProcessAsUser   │
└──────────────────┬──────────────────────────┘
                   │ 跨会话创建
                   ▼
┌─────────────────────────────────────────────┐
│  PowerSwitch.exe   (用户会话, 单进程)         │
│  ┌──────────┐  ┌──────────┐  ┌────────────┐ │
│  │ 托盘图标  │  │ 引擎定时器│  │ 设置窗口    │ │
│  └──────────┘  └────┬─────┘  └────────────┘ │
│                     │ 每 60 秒                │
│                     ▼                        │
│         GetSystemTimes 采样 CPU               │
│                     │                        │
│                     ▼                        │
│         PowerSetActiveScheme (直接 API)       │
└─────────────────────────────────────────────┘
```

**关键差异**：旧版是「计划任务每分钟起一个新 PowerShell 进程 + 另一个进程画图标」，新版是「一个常驻进程内部跑定时器」。没有父子进程、没有任务计划程序、没有脚本释放，那两类架构性故障从根上消失。

---

## 图标不再丢失的三层防护

这是重写的核心目标，做了三道独立防线：

**第一层 · 响应任务栏重建**
程序启动时注册一个隐藏窗口，监听 explorer 广播的 `TaskbarCreated` 消息。explorer 崩溃重启后，任务栏上的图标会被清空，收到消息就立刻重新注册。

**第二层 · 盯住 shell 进程 PID**
`TaskbarCreated` 是广播消息，理论上够用；但若 explorer 以非标准方式重启，消息可能收不到。所以每 5 秒比对一次 explorer 主进程 PID，发现变化就主动重注册图标。

**第三层 · SYSTEM 看门狗**
进程真的被外部杀掉（`taskkill /IM`、会话重置、内存压力），watchdog 15 秒内把它拉回用户会话。

实测：

```
08:15:30  强杀托盘 pid 24592
08:15:30  watchdog: 会话 3 内没有 PowerSwitch.exe，尝试拉起
08:15:30  已启动 ... pid=24424        ← 同一个 15 秒周期内恢复
```

---

## 远程桌面联动（v1.1）

NAS 平时空载跑节能档没问题，但**远程连进来干活的时候被限频**就很难受。所以加了一条硬规则：

```
外部机器 RDP 连入  →  立即切换到「平衡」，冻结自动切换
外部机器 RDP 断开  →  清除防抖计时，自动切换重新接管
```

判定依据不是"本机有没有 mstsc 进程"（那只说明往外连），而是直接问终端服务：枚举所有会话，**状态 = 活动 且 客户端协议 = RDP** 才算远程连接。这样本机物理登录、控制台会话都不会误触发。

两条检测通道并行，互为兜底：

| 通道 | 延迟 | 作用 |
|---|---|---|
| `WTSRegisterSessionNotification` | ~0.4 秒 | 系统在会话变化时主动推消息，负责"立即" |
| 定时轮询（默认 3 秒） | ≤3 秒 | 进程启动前就已连上、消息丢失、跨会话通知不到时兜底 |

优先级（`PowerEngine` 里就这三层，从上到下）：

```
① 远程桌面连接中  → 锁定 RemoteDesktopMode，什么都不做
② 手动暂停标志    → 什么都不做
③ 自动逻辑        → 白天固定 / 夜间按负载防抖
```

> 远程桌面**压过**手动暂停：远程操作时被降到节能档会让整台机器明显卡顿，这个约束比"保持手动选择"更强。暂停标志本身不会被清掉，断开后照旧生效。

日志里能直接看到全过程：

```
15:55:06 远程桌面状态变化: 已连接 Administrator@zhangwenjiedeMa（会话 3）
15:55:06 检测到远程桌面连接：... 立即切换到 平衡 并暂停自动切换
15:58:09 远程桌面连接中 - 强制切换到 平衡          ← 有人手动改档也会被拉回
15:58:30 远程桌面状态变化: 已断开
15:58:31 远程桌面已断开，恢复自动切换
```

托盘提示会显示「· 远程桌面」，右键菜单顶部多一行只读状态「远程桌面已连接 · 自动切换已暂停」。

---

## 特性

- **单进程**：托盘、采样、切换、设置窗口都在一个 exe 里，内存约 39 MB（旧版 128 MB）
- **远程桌面联动**：外部机器 RDP 连入立即锁定平衡方案并暂停自动切换，断开后自动恢复
- **直接调 API**：`PowerSetActiveScheme` 一步到位，不再每次 spawn 一个 `powercfg.exe`
- **防抖状态机**：低负载需持续 N 分钟才降档，高负载 N 分钟才升档，避免阈值附近反复横跳
- **状态持久化**：防抖计时写入 `power_switch_state.json`，进程重启不丢进度
- **配置热生效**：改完设置立刻生效，不用重启、不用重装
- **暂停**：临时接管 30 分钟 / 1 小时 / 2 小时 / 4 小时 / 直到手动恢复
- **日志轮转**：超过阈值自动归档为 `.1`，不会无限增长
- **零安装依赖**：目标 .NET Framework 4.8，Windows 10/11 系统自带

---

## 快速开始

**要求**：Windows 10 / 11（.NET Framework 4.8 已内置），管理员权限。

```powershell
# 1. 把 PowerSwitch.exe 和 PowerSwitchWatchdog.exe 放到同一目录
#    推荐 C:\ProgramData\PowerSwitch\

# 2. 注册看门狗（SYSTEM 身份，开机自启 + 每 5 分钟兜底）
$exe = "C:\ProgramData\PowerSwitch\PowerSwitchWatchdog.exe"
$dir = "C:\ProgramData\PowerSwitch"
$act = New-ScheduledTaskAction -Execute $exe -Argument "--dir `"$dir`"" -WorkingDirectory $dir
$prn = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$set = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew `
       -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
       -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName 'PowerSwitchWatchdog' -Action $act `
    -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $set -Principal $prn -Force

# 3. 启动看门狗（它会把托盘拉起来）
Start-ScheduledTask -TaskName 'PowerSwitchWatchdog'
```

启动后托盘出现，右键即可设置。也可以直接双击 `PowerSwitch.exe`。

**登录自启（可选，只是加速）**：在启动文件夹放一个 `PowerSwitch.vbs`：

```vbs
CreateObject("WScript.Shell").Run """C:\ProgramData\PowerSwitch\PowerSwitch.exe""", 0, False
```

> 没有它也能用 —— 看门狗每 15 秒检查一次，登录后最多 15 秒图标就会出现。程序内有全局互斥量，两条路同时启动也只会有一个实例。

---

## 配置

配置文件 `power_config.json` 与旧 PowerShell 版**格式完全兼容**，迁移时直接沿用即可。

| 字段 | 默认 | 说明 |
|---|---|---|
| `NightStartHour` | `23` | 夜间开始（24 小时制） |
| `NightEndHour` | `7` | 夜间结束 |
| `AllDayMonitor` | `true` | `true` = 忽略夜间时段，全天按负载切换 |
| `CpuThreshold` | `20` | CPU 阈值（%），低于此值算低负载 |
| `LowLoadDuration` | `5` | 低负载持续多少分钟才降档 |
| `HighLoadDuration` | `1` | 高负载持续多少分钟才升档 |
| `SampleCount` | `2` | 每次检测的采样次数 |
| `SampleIntervalSec` | `5` | 采样间隔（秒） |
| `MaxLogSizeMB` | `5` | 日志超过该大小即轮转 |
| `TrayRefreshSec` | `15` | 托盘图标刷新间隔（秒） |
| `DayMode` | `Balanced` | 白天固定方案（仅 `AllDayMonitor=false` 时生效） |
| `NightLowLoadMode` | `PowerSaver` | 低负载目标方案 |
| `NightHighLoadMode` | `Balanced` | 高负载目标方案 |
| `PauseOnRemoteDesktop` | `true` | 远程桌面连接时锁定方案并暂停自动切换 |
| `RemoteDesktopMode` | `Balanced` | 远程桌面期间锁定到哪个方案 |
| `RdpPollSeconds` | `3` | 远程桌面状态轮询间隔（秒），1–60 |

**决策逻辑**：

```
① 远程桌面连接中（PauseOnRemoteDesktop=true）→ 锁定 RemoteDesktopMode，结束
② automation.paused 存在且未到期              → 保持现状，结束
③ 自动逻辑：
   useLoadLogic = AllDayMonitor || 当前处于夜间时段

   ├─ false（白天且未开全天监控）→ 固定 DayMode
   └─ true → 按 CPU 负载走防抖状态机
        ├─ 低负载持续 LowLoadDuration 分钟 → NightLowLoadMode
        └─ 高负载持续 HighLoadDuration 分钟 → NightHighLoadMode
```

**电源方案 GUID**（系统固定值）：

| 键名 | GUID |
|---|---|
| `PowerSaver` | `a1841308-3541-4fab-bc81-f71556f20b4a` |
| `Balanced` | `381b4222-f694-41f0-9685-ff5bb260df2e` |
| `HighPerformance` | `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` |
| `UltimatePerformance` | `e9a42b02-d5df-448d-aa00-03f14749eb61` |

---

## 命令行

| 参数 | 说明 |
|---|---|
| 无参数 | 启动托盘（已有实例时静默退出） |
| `--settings` | 启动并直接弹出设置窗口 |

**轻量控制通道**：在运行目录放一个 `open_settings.request` 文件，运行中的实例会在 5 秒内打开设置窗口并删除该文件。适合脚本、快捷方式或远程操作。

---

## 与 PowerShell 版对比

| 维度 | PowerShell 版 | 本版 |
|---|---|---|
| 进程模型 | 计划任务每分钟起新进程 + 独立托盘进程 | **单进程常驻** |
| 每天进程创建 | 1440 次 | **0 次** |
| 常驻内存 | 128 MB | **39 MB**（实测） |
| 切换方式 | `powercfg /setactive`（spawn 子进程） | **`PowerSetActiveScheme` 直接 API** |
| 计划任务 | 2 个（SYSTEM + 交互式用户，后者常被拒） | **1 个（仅 watchdog，SYSTEM）** |
| 图标恢复 | 无 | **TaskbarCreated + PID 监控** |
| 进程自愈 | 无 | **watchdog 15 秒内拉起** |
| 代码可测试性 | 脚本，难以单测 | 可单元测试 |

---

## 构建

**不需要 Visual Studio，也不需要 .NET SDK。** 只要有 Windows 自带的 .NET Framework 4.x：

```powershell
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

产物在 `build\out\`：
- `PowerSwitch.exe`（约 845 KB）
- `PowerSwitchWatchdog.exe`（约 11 KB）

构建脚本直接调用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，绕开 MSBuild 对 Roslyn 的依赖 —— 精简安装的 VS 常常缺 Roslyn 组件，用 MSBuild 会报 `MSB4019`。

**语言版本**：C# 5（Framework 自带编译器只支持到 5.0）。所以代码里没有字符串插值、`?.`、表达式体成员。想用新语法可以装 .NET SDK 后改用 `dotnet build`。

`src\PowerSwitch.csproj` 供 IDE 打开使用。

---

## 故障排查

**托盘图标不见了**

先看进程还在不在：

```powershell
Get-Process PowerSwitch | Select-Object Id, SessionId, @{n='MB';e={[int]($_.WorkingSet64/1MB)}}
```

- **进程在，图标没了** → explorer 重启过。本版会自动恢复；若没恢复，看日志里有没有 `重新注册托盘图标`。
- **进程也没了** → 看 `watchdog.log`，正常应该 15 秒内出现 `已启动 ... pid=`。若没有，检查计划任务状态：

```powershell
Get-ScheduledTaskInfo -TaskName PowerSwitchWatchdog | Select-Object LastRunTime, LastTaskResult
```

`LastTaskResult = 267009`（`0x41301`）表示任务正在运行，正常。

**设置窗口打不开**

```powershell
# 直接触发
Set-Content C:\ProgramData\PowerSwitch\open_settings.request -Value go
```

**看门狗拉不起进程**

`watchdog.log` 里若出现 `WTSQueryUserToken 失败` 或 `CreateProcessAsUser 失败 (1314)`，说明 SYSTEM 缺少 `SeTcbPrivilege` 或目标会话没有登录用户。用 `quser` 确认会话状态。

**日志乱码**

PowerShell 5.1 的 `Get-Content` 默认按 ANSI 解码，而日志是 UTF-8：

```powershell
Get-Content C:\ProgramData\PowerSwitch\power_switch.log -Tail 20 -Encoding UTF8
```

---

## 目录结构

```
PowerSwitch/
├── src/                          # 主程序
│   ├── Program.cs                # 入口：单实例、全局异常兜底
│   ├── TrayApp.cs                # 托盘、菜单、TaskbarCreated 恢复
│   ├── PowerEngine.cs            # 采样 + 防抖状态机 + 远程桌面覆盖
│   ├── PowerNative.cs            # P/Invoke：电源 API、CPU 采样
│   ├── RdpMonitor.cs             # 远程桌面会话检测（WTS API + 会话变更通知）
│   ├── PowerScheme.cs            # 方案 GUID 与名称映射
│   ├── AppConfig.cs              # 配置模型 + 扁平 JSON 读写
│   ├── SettingsForm.cs           # 设置窗口
│   ├── Logger.cs                 # 日志与轮转
│   ├── Paths.cs                  # 路径常量
│   ├── AssemblyInfo.cs
│   ├── PowerSwitch.csproj
│   └── icons/                    # 5 个托盘图标（嵌入资源）
├── watchdog/
│   └── Program.cs                # SYSTEM 看门狗
├── build/
│   └── build.ps1                 # 无 SDK 构建脚本
└── docs/images/
```

**运行时文件**（在 exe 同目录，已被 `.gitignore` 排除）：

```
power_config.json          配置
power_config.default.json  默认配置
power_switch.log           主日志
power_switch_state.json    防抖状态
watchdog.log               看门狗日志
automation.paused          暂停标志（存在即暂停）
tray.stopped               用户主动退出标志（30 分钟内不拉起）
open_settings.request      控制请求（临时）
```

---

## 卸载

```powershell
# 1. 停任务
Stop-ScheduledTask -TaskName PowerSwitchWatchdog
Unregister-ScheduledTask -TaskName PowerSwitchWatchdog -Confirm:$false

# 2. 停进程
Get-Process PowerSwitch* | Stop-Process -Force

# 3. 删自启（如果有）
Remove-Item "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\PowerSwitch.vbs"

# 4. 删目录
Remove-Item C:\ProgramData\PowerSwitch -Recurse -Force
```

程序不写注册表、不装服务、不注入系统目录，删干净即恢复原状。

---

## 兼容性

| 系统 | 状态 |
|---|---|
| Windows 11 | ✅ 已在 Windows 11 24H2 上验证 |
| Windows 10 | ✅ .NET Framework 4.8 内置 |
| Windows Server 2019/2022 | ✅ 理论可用（未实测） |
| Windows 7 / 8.1 | ⚠️ 需要 .NET Framework 4.8 补丁 |

**已知限制**：

- 切换的是**整个电源方案**，会连带应用该方案下的「硬盘关闭时间」。若各方案在这一项配置不同，硬盘休眠行为会跟着变 —— 长期挂机建议先把各方案的硬盘超时统一。
- 省的是 **CPU 侧功耗**，量级取决于 CPU 型号与主板供电，通常几瓦；它不会让机械硬盘停转，也不控制风扇曲线。
- 看门狗需要 SYSTEM 权限注册计划任务，首次安装需要管理员。

---

## 相关

- [PowerSwitch](https://github.com/Chaconez/PowerSwitch) —— 原 PowerShell 实现（功能等价，含更详细的省电原理说明）
