# RemoteApp Tool — Strict App Session 项目计划

> 状态：Draft / 待审查
> 目标：把 RemoteApp Tool 的会话语义从“Windows 默认 RemoteApp 会话复用”扩展为可选的 **Strict App Session**：用户关闭最后一个业务应用窗口后，本次 RemoteApp Session 立即注销并由 Windows 清理该 Session 中的残留进程；下一次启动必须是新的应用会话。
> 本文只定义项目计划与技术方案，不包含实现代码。

---

## 1. 背景与问题定义

当前 Windows RemoteApp 的默认行为偏向“快速重连”而不是“应用退出即销毁会话”：

1. RemoteApp 启动时创建/使用一个 RDP Session。
2. 同一用户后续启动 RemoteApp 时可能复用现有 Session。
3. RemoteApp 窗口全部关闭后，Windows 会先保留会话一段时间，然后将 Session 置为 disconnected。
4. disconnected RemoteApp Session 默认可以继续保留，后续 RemoteApp 可以重新连接到旧 Session。
5. 如果应用还有托盘进程、helper、WebView2、COM server、updater、子进程等，Windows 还可能持续认为会话没有自然结束。

这会产生当前要解决的体验问题：

- 用户认为应用已经关闭，但远端业务进程仍存在。
- 再次打开 RemoteApp 时重新进入旧会话/旧登录状态。
- 卡死应用、单实例应用、浏览器/Electron/Office 类应用尤其明显。
- 只设置 disconnected/idle timeout 不能提供“关闭应用 = 销毁本次应用会话”的确定语义。

微软文档明确说明：默认 RemoteApp 会话会被保留以便后续快速 reconnect；`Set time limit for logoff of RemoteApp sessions` 只是在 RemoteApp Session 已进入可注销状态后控制其 disconnected 生命周期。Citrix 同样单独处理“主 published executable 退出，但后台/子进程使会话 lingering”的问题。

本项目目标不是复制 Citrix/Horizon 的完整 Broker、Profile、Load Balancing、HA 等能力，而是实现用户当前最关心的生命周期语义：

> **关闭 RemoteApp 的最后一个业务窗口 => 注销当前 RemoteApp Session => 清理该 Session 中全部残留进程 => 下一次启动进入新 Session。**

---

## 2. 当前仓库事实基线

以下计划基于当前仓库 `kimmknight/remoteapptool` 的实际结构。

### 2.1 技术栈

- 主程序：VB.NET WinForms。
- Target Framework：`.NET Framework 4.0`。
- 主项目：`remoteapp-tool/RemoteApp Tool.vbproj`。
- RemoteApp 注册表抽象：`remoteapplib/RemoteAppLib.vb`。
- RDP 文件模型：`RDPFileLib/RDPFileLib.vb`。
- RDP 创建入口：`remoteapp-tool/RemoteAppCreateClientConnection.vb`。
- Host policy 设置：`remoteapp-tool/RemoteAppHostOptions.vb`。

### 2.2 当前 RemoteApp 保存模型

`RemoteAppLib.RemoteApp` 当前保存：

- `Name`
- `FullName`
- `Path`
- `VPath`
- `IconPath`
- `IconIndex`
- `CommandLine`
- `CommandLineOption`
- `TSWA`
- File Type Associations

`SystemRemoteApps.SaveApp()` 直接写入：

`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList\Applications\<alias>`

其中 `Path` / `VPath` 当前就是业务应用本身。

### 2.3 当前 Host Options 的缺口

`RemoteAppHostOptions.vb` 当前能设置：

- `MaxDisconnectionTime`
- `MaxIdleTime`
- `fResetBroken`
- `fAllowUnlistedRemotePrograms`

但没有设置 RemoteApp-specific 的：

- `RemoteAppLogoffTimeLimit`

因此当前 UI 的 session timeout 不是“最后一个 RemoteApp 关闭后立即注销”的专用策略。

### 2.4 当前已有一个对 Strict Mode 很有用的 RDP 能力

`RDPFileLib` 已支持：

```text
disableconnectionsharing:i:1
```

`RDPOptionsWindow.vb` 也已有该设置说明，但它不在 `RecommendedDefaultOptions` 中。

微软当前文档对该属性的定义是：

- `0`：允许 reconnect 到已有 Session。
- `1`：发起新连接。

这可以作为 Strict Mode “每次启动独立 Session”的客户端侧基础。

### 2.5 当前 RDP 创建链路

`RemoteAppCreateClientConnection.CreateRDPFile()` 当前生成：

```text
remoteapplicationprogram:s:||<alias>
remoteapplicationmode:i:1
alternate shell:s:rdpinit.exe
```

然后把 `AdditionalOptions` 追加进 RDP。

Strict Mode 可以在此处保证 `disableconnectionsharing:i:1` 被写入，而不要求用户手动打开高级 RDP Options。

---

## 3. 项目目标

### 3.1 必须实现

1. **应用关闭即结束应用会话**
   - 用户关闭 Strict RemoteApp 的最后一个被跟踪业务窗口后，立即进入短 grace period。
   - grace period 内窗口没有重新出现，则注销当前 RemoteApp Session。

2. **清理整个当前 Session，而不是只 kill 一个 PID**
   - 使用 Windows Session logoff 语义清理该 Session 的应用、helper、托盘、COM、WebView2、updater 等残留。
   - 不做全局 `taskkill /IM xxx.exe`。

3. **下一次启动不得复用上一次 disconnected Session**
   - Strict Mode 生成的 RDP/MSI 必须强制 `disableconnectionsharing:i:1`。
   - 重新启动后应观察到新的 Session ID。

4. **不能误杀别的用户 Session**
   - Launcher 只能注销自己所在的 Session。
   - 不允许按 username 搜索并注销“看起来像同一个用户”的其它 Session。

5. **不能因 helper/tray 残留阻止业务 Session 结束**
   - 一旦确定用户可见业务应用已经关闭，就不再等待所有 session process 自然退出。

6. **保留现有 RemoteApp 能力**
   - 图标。
   - CommandLineSetting。
   - RequiredCommandLine。
   - File Type Associations。
   - RDP/MSI 生成。
   - RDP Gateway。
   - RDP 签名流程。

7. **Strict Mode 必须是 opt-in**
   - 原有 RemoteApp 默认行为不改变。
   - 已有用户升级后不会自动改变会话生命周期。

### 3.2 应实现

1. 客户端异常断开后可配置自动注销，避免僵尸 Session。
2. Launcher 提供可诊断日志。
3. RemoteApp Tool 能检测/修复 Strict shim 丢失或版本不一致。
4. UI 能提示会破坏“独立 Session”语义的 Host policy。
5. 可以安全关闭 Strict Mode 并恢复原始应用 Path/VPath。

### 3.3 非目标

第一阶段不做：

- RDS Connection Broker 替代品。
- Citrix Director 替代品。
- 用户 Profile Container。
- 多主机负载均衡。
- HA / Farm orchestration。
- 绕过 Windows/RDS 授权或桌面 OS 的并发会话限制。
- RDP Wrapper 等绕过系统限制的集成。
- 任意远端 Session 的管理员式强制终止控制台。

---

## 4. 总体设计决策

### 4.1 主方案

新增一个轻量的、无 UI 主窗口的：

`RemoteAppSessionHost.exe`

Strict RemoteApp 不再直接把业务 exe 作为发布 `Path`，而是发布一个 **该应用专属的 Session Host shim**。

逻辑结构：

```text
mstsc / Remote Desktop client
        |
        v
RemoteApp alias: ||myapp
        |
        v
TSAppAllowList\Applications\myapp
        |
        | Path = <strict shim path>
        v
RemoteAppSessionHost.exe
        |
        |-- 读取当前 Strict App 配置
        |-- 获取自己的 SessionId
        |-- 启动真实 target exe
        |-- 追踪业务窗口 / 业务进程
        |-- 监听 Session disconnect
        |
        +--> 最后一个业务窗口关闭
                  |
                  v
          short close grace
                  |
                  v
       WTSLogoffSession(current SessionId)
                  |
                  v
          Windows 清理整个 Session
```

### 4.2 为什么不用 `RemoteAppLogoffTimeLimit=0` 作为主方案

它适合作为 fallback，但不能单独提供目标语义：

- 它依赖 Windows 先把 RemoteApp Session 判定为 disconnected/可注销。
- tray/helper/child process 可能推迟这一状态。
- 它是 host/user policy，不是每个 RemoteApp 的精细生命周期控制。
- 全局强制为 0 还可能把正常的网络抖动直接变成用户会话丢失。

因此：

- 主路径：Session Host 明确观察“业务应用结束”并注销当前 Session。
- fallback：管理员可选设置 `RemoteAppLogoffTimeLimit`。

### 4.3 为什么不用全局进程 kill

禁止使用：

```text
taskkill /IM app.exe
```

原因：

- 同一主机可以有多个用户/Session。
- 同名 exe 可能属于其他用户。
- helper/COM/浏览器子进程名不稳定。
- kill 一个 PID 不能保证用户环境完全清理。

Session logoff 才是正确清理边界。

### 4.4 为什么第一版采用“每次启动独立 Session”

第一版 Strict Mode 默认：

```text
disableconnectionsharing:i:1
```

优点：

- 生命周期所有权清楚。
- 一个 Strict App 退出时可以安全注销整个 Session。
- 不需要一开始就实现复杂的跨 RemoteApp 引用计数。
- 最容易验证“重新打开一定是新 Session”。

缺点：

- 同一用户同时打开多个 Strict App 时会消耗多个 Session。
- Windows Server 若启用了 “Restrict Remote Desktop Services users to a single Remote Desktop Services session”，会与这种隔离策略冲突。
- Windows Desktop OS 本身存在并发 RDP Session 限制；项目不绕过此限制。

因此后续再实现真正的 **Shared Strict Session**，见第 15 节。

---

## 5. 新组件设计

### 5.1 `RemoteAppSessionHost` 项目

新增项目建议：

```text
RemoteAppSessionHost/
  RemoteAppSessionHost.vbproj
  Program.vb
  StrictAppConfig.vb
  SessionController.vb
  ProcessTracker.vb
  WindowTracker.vb
  WtsNative.vb
  Win32WindowNative.vb
  CommandLineForwarder.vb
  StrictSessionLog.vb
```

技术选择：

- VB.NET，保持仓库一致。
- `.NET Framework 4.0`，第一版不强迫整个项目升级 runtime。
- `WinExe`，避免 RemoteApp 用户看到额外 console 窗口。
- 不以管理员权限运行。
- 不安装 Windows Service。

### 5.2 为什么不先做 Windows Service

Service 会增加：

- SYSTEM 权限面。
- Session 0 隔离处理。
- IPC。
- 安装/升级/卸载复杂度。
- 误注销其他 Session 的风险。

当前目标只需要“当前用户 Session 自己结束自己”，普通 session-local process 足够。

---

## 6. Strict App 注册表模型

### 6.1 设计原则

需要同时满足：

1. Windows 看到的 `Path` 是 shim。
2. RemoteApp Tool UI 仍然显示真实业务应用 Path，而不是 shim。
3. CommandLineSetting / RequiredCommandLine 保持 Windows 原生语义。
4. 关闭 Strict Mode 时能完整恢复。
5. `.reg` Backup 能保存 Strict 配置。

### 6.2 建议新增值

每个 RemoteApp key 下新增项目自有值：

```text
RemoteAppToolStrictSession          REG_DWORD   1
RemoteAppToolStrictId               REG_SZ      <GUID>
RemoteAppToolTargetPath             REG_SZ      C:\Apps\Foo\foo.exe
RemoteAppToolTargetVPath            REG_SZ      C:\Apps\Foo\foo.exe
RemoteAppToolTerminationMode        REG_DWORD   0
RemoteAppToolStartupGraceMs         REG_DWORD   10000
RemoteAppToolCloseGraceMs           REG_DWORD   1500
RemoteAppToolDisconnectBehavior     REG_DWORD   1
RemoteAppToolDisconnectGraceMs      REG_DWORD   30000
RemoteAppToolSchemaVersion          REG_DWORD   1
```

建议枚举：

```text
TerminationMode:
0 = LastTrackedWindowClosed
1 = ProcessTreeExited
2 = PrimaryProcessExited
3 = NamedProcessSetExited   (后续)

DisconnectBehavior:
0 = Preserve
1 = LogoffAfterGrace
2 = Immediate
```

### 6.3 Windows 实际发布值

Strict 开启时：

```text
Path  = %ProgramData%\RemoteAppTool\StrictSession\<StrictId>\RemoteAppSessionHost.exe
VPath = %ProgramData%\RemoteAppTool\StrictSession\<StrictId>\RemoteAppSessionHost.exe
```

保留：

```text
Name
IconPath
IconIndex
CommandLineSetting
RequiredCommandLine
ShowInTSWA
Filetypes
```

特别重要：

- `CommandLineSetting` 不应为了 shim 被强制改成固定值。
- `RequiredCommandLine` 仍由 Windows 按原来的 0/1/2 规则传给 shim。
- shim 把自己收到的参数原样、正确引用后转发给真实 target。

这样不需要占用 RemoteApp 的命令行参数来传递 `<alias>`，降低 FTA/动态参数兼容风险。

### 6.4 为什么每个 App 使用唯一 shim 路径

如果所有 Strict App 都指向同一个：

```text
C:\Program Files\RemoteApp Tool\RemoteAppSessionHost.exe
```

那么 shim 无法可靠知道自己是由哪个 alias 启动；如果用 `RequiredCommandLine` 注入 alias，则会破坏原 RemoteApp command-line 语义。

因此第一版采用：

```text
%ProgramData%\RemoteAppTool\StrictSession\<GUID>\RemoteAppSessionHost.exe
```

shim 从自己的父目录得到 `<GUID>`，再查注册表中 `RemoteAppToolStrictId=<GUID>` 的 app key。

优点：

- 不侵占应用参数。
- alias rename 不影响 StrictId。
- 每个 RemoteApp 配置可独立定位。
- Backup 仍保留 GUID 和真实 target。

### 6.5 shim 目录 ACL

`%ProgramData%\RemoteAppTool\StrictSession` 必须：

- Administrators / SYSTEM：write/modify。
- 普通用户：read/execute。
- 普通用户不得修改 shim executable。

不能把可执行 shim 放到用户可写目录，否则会形成二进制劫持风险。

---

## 7. RemoteAppLib 改造

### 7.1 `RemoteApp` 新属性

建议扩展：

```text
StrictSessionEnabled As Boolean
StrictSessionId As String
StrictTerminationMode As Integer
StrictStartupGraceMs As Integer
StrictCloseGraceMs As Integer
StrictDisconnectBehavior As Integer
StrictDisconnectGraceMs As Integer
```

并把 `Path` / `VPath` 的 public semantic 定义为：

> UI/调用方看到的始终是真实业务应用路径。

### 7.2 `SystemRemoteApps.GetApp()`

读取规则：

```text
if RemoteAppToolStrictSession = 1:
    RemoteApp.Path  <- RemoteAppToolTargetPath
    RemoteApp.VPath <- RemoteAppToolTargetVPath
    RemoteApp.Strict... <- custom values
else:
    RemoteApp.Path  <- Path
    RemoteApp.VPath <- VPath
```

### 7.3 `SystemRemoteApps.SaveApp()`

Strict disabled：保持当前逻辑。

Strict enabled：

1. 验证真实 target 存在。
2. 确保 StrictId 存在，没有则生成 GUID。
3. 部署/验证该 StrictId 对应 shim。
4. 先写 `RemoteAppToolTarget*` 和 Strict metadata。
5. 最后切换 `Path/VPath` 到 shim。

保存顺序要避免出现：

```text
Path 已指向 shim
但 target metadata / shim binary 尚未成功
```

即使用“准备成功后再切 Path”的事务式顺序。

### 7.4 关闭 Strict Mode

顺序：

1. 从 `RemoteAppToolTargetPath/VPath` 恢复 `Path/VPath`。
2. 确认恢复成功。
3. 删除/保留 Strict metadata（建议删除运行时设置，保留可诊断 marker 可再讨论）。
4. 删除对应 shim 目录。

任何恢复失败时不得先删 target metadata。

### 7.5 Rename / Duplicate

Rename：

- 保持同一个 StrictId。
- 不需要重新生成 shim identity。

Duplicate：

- 必须生成新的 StrictId。
- 不能让两个 app key 指向同一个 strict ownership identity。

Delete：

- 删除 app key 后清理对应 shim 目录。
- 清理失败只记录 warning，不影响 RemoteApp 删除结果。

---

## 8. Launcher 生命周期状态机

建议显式实现状态机，避免散落的 timer/if 产生竞态。

```text
BOOT
  |
  v
LOAD_CONFIG
  | failure
  +----------------------> CONFIG_ERROR
  |
  v
CHECK_SESSION_OWNERSHIP
  | unsafe/shared
  +----------------------> SAFE_DEGRADED_MODE
  |
  v
START_TARGET
  | failure
  +----------------------> START_ERROR
  |
  v
WAIT_FOR_APP_READY
  |
  v
TRACKING
  | last tracked window gone
  v
CLOSE_GRACE
  | window returns
  +----------------------> TRACKING
  |
  | grace elapsed
  v
REQUEST_LOGOFF
  |
  v
DONE
```

同时存在独立的 session-state 路径：

```text
TRACKING / WAIT_FOR_APP_READY
        |
        | WTSDisconnected
        v
DISCONNECT_GRACE
        |
        | reconnect
        +------------------> previous state
        |
        | grace elapsed
        v
REQUEST_LOGOFF
```

---

## 9. 业务应用结束检测

### 9.1 默认模式：`LastTrackedWindowClosed`

这是最贴近用户目标的默认模式。

原因：

- 用户关掉窗口后，即使 tray/helper 还活着，也应该结束 Strict Session。
- 只等待 root PID 退出会被托盘应用拖住。
- 只等待所有 child PID 退出又会重现 Citrix 文档里的 lingering session 问题。

### 9.2 Tracking 规则

Launcher 至少记录：

- 自己的 `SessionId`。
- root target PID。
- target 启动时间。
- root 的 descendant PIDs。
- 启动后被识别为 handoff successor 的 PIDs。
- 这些 PID 的 top-level window。

Win32 建议：

- `ProcessIdToSessionId`
- `EnumWindows`
- `GetWindowThreadProcessId`
- `IsWindowVisible`
- 必要时使用 `GetWindow` / owner 信息过滤明显非主窗口。

### 9.3 “窗口已关闭”的判定

不能在 target 启动后尚未显示 UI 时就立即认为“没有窗口 => 应注销”。

必须先 **arm**：

```text
至少观察到一次 qualifying app window
    -> LifecycleArmed = true
```

只有 `LifecycleArmed=true` 后，才允许“最后一个窗口消失”触发 close grace。

### 9.4 Launcher/handoff 类型应用

典型问题：

```text
foo-launcher.exe
  -> foo-real.exe
  -> launcher 很快退出
```

第一版至少处理：

- root process descendants。
- 启动初期一定时间内的 child/handoff successor。
- 子进程拥有 qualifying window 时，把该 PID 纳入 tracked set。

若 root 很快退出、没有观察到任何窗口，但 descendant 仍存在，则继续 startup grace，而不是立即注销。

### 9.5 没有标准窗口的应用

提供可配置 fallback：

- `ProcessTreeExited`
- `PrimaryProcessExited`

UI Advanced 中选择。

第一版不用试图自动完美推断所有 app 类型；要做到：

- 默认对普通 GUI app 好用。
- 对特殊 app 有明确可调模式。
- 不靠无限增长的全局 process-name ignore list。

### 9.6 Close grace

默认建议：

```text
1500 ms
```

用途：

- 应用关闭主窗口后立即创建 final dialog。
- UI framework 在 close/reopen 间短暂销毁 HWND。
- 避免瞬时 window transition 导致过早 logoff。

如果 grace 期间 qualifying window 再次出现，取消注销并回到 `TRACKING`。

---

## 10. Session 注销实现

### 10.1 API

使用：

```text
WTSLogoffSession
```

目标参数永远是：

```text
当前 Launcher 所在的 SessionId
```

### 10.2 安全不变量

必须满足：

```text
TargetSessionId == current process SessionId
```

禁止：

- 按用户名枚举后注销。
- 按“最新 Session”猜测。
- 注销其他用户 Session。
- Launcher 接受任意 SessionId CLI 参数。

### 10.3 调用后验证

`WTSLogoffSession(..., bWait=False)` 返回成功只表示请求已接受，不应作为完整验证。

Launcher 本身可能很快被 Session teardown 终止，因此实际集成测试由外部管理员测试脚本验证：

- 原 SessionId 从 `quser/query session` 消失。
- 旧 Session 的业务 PID 不再存在。

---

## 11. Session ownership / 防误注销设计

即使 RDP 生成器写入 `disableconnectionsharing:i:1`，仍要考虑：

- 用户用了旧 RDP 文件。
- 用户手工删了该 property。
- host policy 强制 single-session-per-user。
- 第三方 RDP client 行为不同。

因此 Launcher 不能盲目假设“这个 Session 一定只属于我”。

### 11.1 Session-local controller mutex

创建：

```text
Local\RemoteAppTool.StrictSession.Controller
```

默认 `Local\` namespace 已按 Session 隔离。

如果同一 Session 已有另一个 Strict controller：

- 记录 `SharedSessionDetected`。
- 第一版进入安全降级：不执行整个 Session 的自动 logoff。
- 向日志写出明确原因。

### 11.2 启动时 baseline

启动业务 target 前，记录当前 Session 已存在的 user-facing process/window baseline。

如果检测到明显的其它业务窗口：

- 标记 `SessionNotExclusive`。
- 默认 fail-safe：业务 App 仍允许运行，但不自动注销整个 Session。
- 日志给出修复建议：重新生成带 `disableconnectionsharing:i:1` 的 RDP，并检查 `fSingleSessionPerUser`。

原则：

> Strict Mode 失去 isolation 时宁可退化为“不会自动清 Session”，也不要误杀其它业务应用。

### 11.3 Host policy 预检

RemoteApp Tool UI 应检查：

`HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\fSingleSessionPerUser`

如果策略明确限制每用户单 Session，而用户启用了 Strict isolated sessions：

- 显示 warning。
- 不自动修改 policy。
- 给出策略路径和后果说明。

---

## 12. 断连处理

“关闭应用”和“网络/客户端断开”是两个不同事件，不能混在一个全局 0 秒 policy 中。

### 12.1 默认建议

Strict App：

```text
On app close:
    immediate lifecycle logoff (close grace only)

On RDP disconnect:
    LogoffAfterGrace = 30 seconds
```

30 秒仅是初始默认值，最终由实测决定。

### 12.2 实现

Launcher 周期查询当前 Session connect state，或使用 WTS session notification：

- `WTSActive`：正常。
- `WTSDisconnected`：进入 disconnect grace。
- 在 grace 内 reconnect：取消注销。
- 超过 grace：注销当前 Session。

### 12.3 可配置模式

```text
Preserve
Logoff after N seconds
Immediate
```

这样用户可以在“网络容错”和“绝不留 Session”之间选择，而不是被全局 GPO 一刀切。

---

## 13. Command line / FTA 兼容

这是实现中必须单独验证的高风险区域。

### 13.1 CommandLineSetting

Windows RemoteApp 支持：

```text
0 = DoNotAllow
1 = Allow
2 = Require / use RequiredCommandLine
```

Strict shim 必须保持原 registry setting 不变。

### 13.2 参数转发

shim 启动真实 target 时必须正确处理 Windows 参数引用：

- 空参数。
- 空格。
- 引号。
- 尾随反斜杠。
- Unicode。
- 路径参数。
- `%ENV%` 是否在 client/server 侧展开。

不能简单：

```text
String.Join(" ", args)
```

必须实现/复用正确的 Windows command-line quoting。

### 13.3 File Type Association

FTA 测试至少包含：

- 双击本地映射文件打开 RemoteApp。
- 文件路径包含空格。
- 文件路径包含 Unicode。
- 同时传多个参数。

如果动态参数不能透明穿过 shim，Strict Mode 不得标记为 GA。

---

## 14. RDP/MSI 生成改造

### 14.1 Strict App 强制隔离 property

当：

```text
RemoteApp.StrictSessionEnabled = True
```

`CreateRDPFile()` 必须确保最终输出恰好包含：

```text
disableconnectionsharing:i:1
```

要求：

- 即使用户保存的 Additional Options 中写了 `0`，Strict Mode 也要覆盖为 `1`。
- 不输出重复冲突的同名 RDP property。
- 非 Strict App 保持用户当前设置。

### 14.2 MSI

当前 MSI 由生成出的 RDP 文件打包，因此只要最终 RDP 正确，MSI 必须继承 Strict isolation property。

需要集成测试确认：

- MSI 安装后的 shortcut 实际 RDP 内容仍为 `1`。

### 14.3 RDP signing

覆盖 Strict property 必须发生在 `rdpsign` 之前。

不能：

1. sign RDP。
2. 再补写 `disableconnectionsharing`。

否则签名失效。

---

## 15. Shared Strict Session（后续阶段）

如果最终希望更接近 Citrix 的资源效率：

```text
Word RemoteApp
Excel RemoteApp
```

可以共享一个 Session，但最后一个 Published App 退出才 logoff。

这需要第二种架构：

```text
SessionAgent (one per Session)
     ^
     | named pipe / local IPC
     |
AppShim A ---- register/unregister App A
AppShim B ---- register/unregister App B
```

Agent 保存：

```text
ActivePublishedApps = {
  AppA instance(s),
  AppB instance(s)
}
```

规则：

```text
ActivePublishedApps.Count > 0
    -> keep session

ActivePublishedApps.Count == 0
    -> close grace
    -> WTSLogoffSession(current session)
```

这时生成的 RDP 可以允许 connection sharing。

### 15.1 为什么不放进第一版

它增加：

- IPC。
- Agent election。
- Agent crash recovery。
- 多实例计数。
- app-level 生命周期冲突。
- 同 session mixed strict/non-strict app 的政策问题。

先证明 isolated Strict Session 能稳定满足核心体验，再做 shared mode。

---

## 16. UI 计划

### 16.1 RemoteApp Properties

新增：

```text
[ ] Strict App Session
    Log off the RemoteApp session when this application is closed.
```

开启后显示 Advanced：

```text
End detection:
  (*) Last application window closes
  ( ) Application process tree exits
  ( ) Primary process exits

Close grace: [1500] ms

When client disconnects:
  ( ) Preserve session
  (*) Log off after [30] seconds
  ( ) Log off immediately
```

### 16.2 明确提示

Strict isolated mode 说明：

- 生成的 client connection 会强制新 Session。
- 同一用户同时开多个 Strict App 可能产生多个 Session。
- Host 的 single-session-per-user policy 可能让 isolation 失效。

### 16.3 Host Options

新增独立区块：

```text
RemoteApp fallback cleanup
[ ] Set time limit for logoff of disconnected RemoteApp sessions
    Delay: [Immediately / 1 min / ...]
```

重要：

- UI 明确标注这是 **host-wide fallback**。
- 开启 Strict App 不自动把这个全局值改成 0。

### 16.4 Repair

主窗口/菜单增加：

```text
Repair Strict Session launchers
```

检查：

- Strict app metadata 是否完整。
- shim 是否存在。
- shim version/hash 是否是当前版本。
- registry Path 是否指向正确 strict directory。

---

## 17. 日志与可诊断性

### 17.1 日志位置

Launcher 以普通用户运行，默认写：

```text
%LOCALAPPDATA%\RemoteAppTool\StrictSession\logs\
```

避免给所有用户共享可写 ProgramData log 目录。

### 17.2 每条生命周期日志至少包含

- timestamp。
- app alias / StrictId。
- current SessionId。
- launcher PID。
- target PID。
- state transition。
- disconnect state。
- tracked window count。
- logoff request result。
- Win32 error code（失败时）。

### 17.3 隐私

默认不记录完整 command line。

原因：command line 可能包含：

- 文件路径。
- URL token。
- 临时 secret。
- 用户数据。

Debug 模式若要记录参数必须明确 opt-in，并做 warning。

---

## 18. 错误处理策略

### 18.1 找不到 Strict 配置

行为：

- 显示用户可理解错误。
- 写日志。
- 不尝试猜 target。
- 不盲目 logoff Session。

### 18.2 target 启动失败

行为：

- 显示 target path / Win32 error（不泄漏敏感 command line）。
- 如果当前 Session 已被明确确认 exclusive，可以在用户关闭错误提示后注销当前 Session。
- 如果 ownership 不确定，则 fail-safe，不强制注销整个 Session。

### 18.3 WTSLogoffSession 失败

行为：

- 记录 GetLastError。
- 不进行跨 Session fallback kill。
- 允许 host-level timeout/fallback policy 最终接管。

### 18.4 Launcher 崩溃

第一版：

- unhandled exception 记录日志。
- 不在异常 handler 中冒险注销 ownership 不确定的 Session。
- 依赖可选 disconnected-session fallback。

后续如真实数据表明 launcher crash 是主要风险，再评估 session agent/service watchdog。

---

## 19. 安全边界

必须满足：

1. Launcher 不请求 elevation。
2. Launcher 不能接受 `--session-id <arbitrary>`。
3. Launcher 只操作自己的 Session。
4. ProgramData strict binary 目录普通用户不可写。
5. Target path 由管理员态 RemoteApp Tool 写入 HKLM。
6. 参数转发不经过 `cmd.exe /c`。
7. 不拼接 shell command string 去启动目标。
8. 不使用全局用户名匹配来选择进程/Session。
9. 日志默认不保存完整 args。
10. 不修改凭据、CredSSP 或认证策略。

---

## 20. OS / 支持边界

### 20.1 建议支持级别

Strict Session MVP：

- Windows Server 2016/2019/2022/2025：优先验证。
- Windows 10/11 支持 RemoteApp 的现有场景：best-effort 验证。
- Windows 7/8：如果现有 project compatibility 必须保留，则做基础验证，但不阻塞现代 OS 首次交付。
- Windows XP：Strict Session 明确不作为首版目标。

### 20.2 Desktop OS 并发限制

项目不尝试绕过 Windows Desktop OS 自身的并发 RDP 限制。

如果目标环境要求：

```text
同一用户同时打开多个相互独立的 RemoteApp Session
```

应优先使用合法配置的 Windows Server RDS 环境，并根据实际部署检查 licensing/CAL 要求。

### 20.3 Single-session-per-user 冲突

若：

```text
fSingleSessionPerUser = 1
```

可能与“每个 Strict launch 都是新 Session”的目标冲突。

RemoteApp Tool 只诊断并提示，不擅自修改企业 policy。

---

## 21. 测试策略

仓库当前没有成熟自动测试体系，因此分三层。

### 21.1 纯逻辑测试

把以下逻辑尽量与 Win32 调用解耦：

- state machine。
- grace timer transition。
- argument quoting。
- registry model mapping。
- Strict metadata migration。
- ownership decision。

如果不引入第三方 test framework，先新增一个可返回 exit code 的轻量 test harness；后续项目升级 framework 后再迁移到正式 unit test framework。

### 21.2 Host-side integration test script

新增 PowerShell 测试辅助脚本，管理员在测试 RDS Host 上运行：

- 记录 launch 前 session 列表。
- 启动 RemoteApp client。
- 记录当前 Session ID。
- 关闭 app。
- 等待验收窗口。
- 验证旧 Session ID 消失。
- 验证旧 target/helper PID 消失。
- 再次启动。
- 验证 Session ID 与上次不同。

测试脚本只用于验证，不进入生产生命周期路径。

### 21.3 手工应用矩阵

#### 基础应用

- Notepad。
- Calculator/Paint（按 OS 可用性）。

#### 多进程应用

- Chromium/Electron 类。
- WebView2 应用。

#### Launcher/handoff 应用

- 一个自制测试 launcher：启动 child 后自己立即退出。

#### Tray 残留应用

- 主窗口关闭但 tray/helper 继续运行的测试程序。

#### 参数/FTA

- no args。
- fixed RequiredCommandLine。
- client supplied remoteapplicationcmdline。
- path with spaces。
- Unicode path。
- FTA 双击文件。

#### Session 行为

- 正常关闭最后一个窗口。
- close 后马上重新打开。
- mstsc/client crash。
- 网络断开 5 秒后恢复。
- 网络断开超过 disconnect grace。
- 同一用户重复 launch。
- 两个不同用户同时使用同一 app。

#### Client artifact

- raw RDP。
- signed RDP。
- MSI shortcut。
- RD Gateway。

---

## 22. 验收标准

### 22.1 核心体验验收

对标准 GUI 测试 App：

1. 启动 Strict RemoteApp，记录 Session ID `S1`。
2. 关闭最后一个业务窗口。
3. 在 `close grace + 合理系统注销时间` 内，`S1` 不再出现在 session list 中。
4. `S1` 中的业务进程、helper/tray process 全部被 Windows 清理。
5. 再次启动同一 RemoteApp，得到新的 Session ID `S2`。
6. `S2 != S1`。
7. 新 App 不恢复旧进程实例。

### 22.2 多用户安全验收

- User A 关闭 Strict App 不影响 User B 的 Session。
- 同名 target exe 在 User B Session 中继续运行。

### 22.3 兼容验收

- 非 Strict RemoteApp registry 和生成的 RDP 行为与当前版本一致。
- Strict 开关关闭后，应用 Path 完整恢复。
- FTA 和 command-line tests 通过。
- RDP signing 在 Strict property 写入后仍有效。

### 22.4 误配置安全验收

如果 Strict App 被启动进一个已经有其它业务应用的 shared Session：

- Launcher 检出 ownership 风险。
- 不注销整个 Session。
- 写明确诊断日志。

---

## 23. 实施阶段

### Phase 0 — 技术 Spike / 不改现有 UI

目标：先证明核心假设。

任务：

1. 创建最小 `RemoteAppSessionHost` PoC。
2. 获取 current SessionId。
3. 启动固定测试 target。
4. EnumWindows + PID mapping 跟踪 target 窗口。
5. 最后一个窗口关闭后调用 `WTSLogoffSession(currentSession)`。
6. 测试断连状态检测。
7. 测试 Windows Server 上重复启动得到新 Session。
8. 验证 `disableconnectionsharing:i:1` + host single-session policy 的实际组合行为。

Exit criteria：

- Notepad 类 App 可以做到 close -> Session 消失 -> reopen -> 新 Session。
- 两用户测试无 cross-session 影响。

### Phase 1 — Strict Isolated Session MVP

任务：

1. 新增正式 `RemoteAppSessionHost` project。
2. 新增 Strict registry schema。
3. 扩展 `RemoteAppLib.RemoteApp`。
4. `GetApp/SaveApp/Delete/Rename/Duplicate` 支持 Strict metadata。
5. 实现 per-app StrictId shim 部署。
6. RemoteApp Properties 增加 Strict checkbox。
7. RDP 生成器强制 `disableconnectionsharing:i:1`。
8. 实现 `LastTrackedWindowClosed`。
9. 实现 current-session logoff。
10. 基础日志。
11. Notepad + launcher handoff + tray test app 验证。

Exit criteria：达到第 22.1、22.2 的核心验收。

### Phase 2 — 兼容与恢复能力

任务：

1. CommandLineSetting 0/1/2 全矩阵。
2. robust Windows argument quoting。
3. File Type Association。
4. MSI。
5. signed RDP。
6. RD Gateway 回归。
7. disconnect behavior。
8. `ProcessTreeExited` / `PrimaryProcessExited` mode。
9. Repair Strict Launchers。
10. disable Strict rollback。
11. backup/restore 验证。
12. host policy diagnostics。
13. `RemoteAppLogoffTimeLimit` host-wide fallback UI。

Exit criteria：Strict Mode 可日常使用，不因升级/backup/FTA/参数出现明显破坏。

### Phase 3 — Hardening

任务：

1. 更严格 ownership detection。
2. launcher crash telemetry/logging。
3. shim binary version/hash repair。
4. installer/uninstaller 生命周期。
5. 大量重复 launch/close soak test。
6. 多用户并发 soak test。
7. Windows Server 版本矩阵。

Exit criteria：长期运行没有稳定复现的 lingering Strict Session。

### Phase 4 — Shared Strict Session（可选）

任务：

1. SessionAgent。
2. session-local IPC。
3. app registration/ref-count。
4. 多 Published App 共 Session。
5. 最后一个 App 退出才 logoff。
6. agent crash recovery。
7. mixed strict/non-strict policy。

这是资源效率优化，不阻塞核心问题解决。

---

## 24. 预计文件影响范围

### 新增

```text
RemoteAppSessionHost/
RemoteAppSessionHost/RemoteAppSessionHost.vbproj
RemoteAppSessionHost/*.vb
```

可能新增：

```text
tests/StrictSession/
scripts/Test-StrictRemoteAppSession.ps1
```

### 修改

```text
remoteapp-tool/RemoteApp Tool.sln
remoteapp-tool/RemoteApp Tool.vbproj
remoteapp-tool/RemoteAppEditWindow.vb
remoteapp-tool/RemoteAppEditWindow.Designer.vb
remoteapp-tool/RemoteAppEditWindow.resx
remoteapp-tool/RemoteAppCreateClientConnection.vb
remoteapp-tool/RemoteAppHostOptions.vb
remoteapp-tool/RemoteAppHostOptions.Designer.vb
remoteapp-tool/RemoteAppHostOptions.resx
remoteapplib/RemoteAppLib.vb
```

视实现封装情况可能增加主程序 helper：

```text
remoteapp-tool/StrictSessionDeployment.vb
remoteapp-tool/StrictSessionDiagnostics.vb
```

`RDPFileLib` 本身已经支持 `disableconnectionsharing`，优先不改其数据模型，除非为了消除 AdditionalOptions 重复 property 必须新增 set/replace API。

---

## 25. 代码修改顺序

为了让每个 commit 可审查，建议按以下拆分：

1. `Add strict session data model`
   - 只加 RemoteApp model + registry round-trip。
   - 不改变 Path。

2. `Add RemoteAppSessionHost spike`
   - current SessionId + target launch + logoff API。

3. `Add window lifecycle tracker`
   - 不接 UI。

4. `Add strict shim deployment`
   - StrictId + ProgramData ACL + version handling。

5. `Wire strict publishing into RemoteAppLib`
   - Path/VPath shim 化和安全 rollback。

6. `Add Strict Session UI`
   - checkbox + advanced options。

7. `Force new RDP connection for strict apps`
   - `disableconnectionsharing:i:1`。

8. `Add disconnect lifecycle`

9. `Add diagnostics and repair`

10. `Add compatibility tests / scripts`

不建议做一个包含所有功能的巨大 commit。

---

## 26. 回滚计划

### 单个 App 回滚

管理员关闭：

```text
Strict App Session
```

工具：

1. 恢复原 target `Path/VPath`。
2. 验证 registry。
3. 删除 Strict runtime metadata。
4. 删除该 StrictId shim。

### 整体功能回滚

提供 repair/disable-all 管理动作（Phase 2/3）：

- 枚举所有 `RemoteAppToolStrictSession=1` app。
- 对每个 app 恢复 target path。
- 生成操作报告。
- shim 目录最后删除。

必须先恢复 registry，再删 shim。

### 版本降级风险

旧版 RemoteApp Tool 不理解 Strict metadata，会把 registry 中的 shim Path 当作真实应用 Path。

因此正式发布前必须：

- 在 release notes 明确 Strict App 的 downgrade 注意事项。
- 提供 “Disable all Strict Sessions before downgrade” 管理动作。

---

## 27. Installer / Upgrade 注意事项

实现代码进入正式 release 前必须处理：

1. 新 `RemoteAppSessionHost.exe` 随安装包部署。
2. 升级后 `Repair Strict Launchers` 能把 per-app shim 更新到当前版本。
3. 卸载时不能直接删除仍被 RemoteApp registry `Path` 引用的 shim。
4. 理想行为：卸载程序先提示存在 Strict Apps，并执行 restore；如果当前 installer 技术不便集成，则至少保留 shim 并输出明确 warning。

在 Installer 生命周期未解决前，不应把 Strict Mode 标成稳定 GA。

---

## 28. 风险清单

### R1 — 某些 App 的 root process 不是实际 UI process

Mitigation：

- startup grace。
- descendant/handoff tracking。
- 可选 termination mode。
- 用专门 test launcher 覆盖。

### R2 — App 关闭主窗口但希望继续 tray 驻留

Strict Mode 的定义就是不保留该会话；这种 App 若需要 tray 持久化，不应该启用 `LastTrackedWindowClosed`，可选 `ProcessTreeExited` 或禁用 Strict。

### R3 — 误把系统窗口算业务窗口

Mitigation：

- 只跟踪 root/descendant/adopted target PID 对应窗口。
- 不用“Session 中任何 visible window”作为结束条件。

### R4 — 同 Session 中存在其他 RemoteApp

Mitigation：

- generated RDP 强制 isolation。
- session-local controller mutex。
- baseline/ownership safety check。
- 不确定时 fail-safe 不 logoff。

### R5 — Host single-session policy 破坏 isolation

Mitigation：

- GUI preflight warning。
- integration test。
- 文档说明。

### R6 — RDP client 忽略/覆盖 `disableconnectionsharing`

Mitigation：

- 支持矩阵明确 Windows MSTSC 为首要客户端。
- ownership safety check 防止误注销。
- 其它客户端逐一验证。

### R7 — command line quoting 破坏 FTA

Mitigation：

- 独立 quoting test suite。
- 不使用 shell。
- 以 FTA 作为 GA gate。

### R8 — launcher binary 被普通用户篡改

Mitigation：

- ProgramData ACL。
- version/hash repair。
- 不放用户可写目录。

### R9 — 断网导致过早注销

Mitigation：

- disconnect grace 独立配置。
- 不默认强设 host-wide `RemoteAppLogoffTimeLimit=0`。

### R10 — Launcher crash 留下 Session

Mitigation：

- 可选 RemoteApp logoff timeout fallback。
- launcher 稳定性/soak test。
- 后续再决定是否需要 SessionAgent watchdog。

---

## 29. 审查时需要确认的设计决策

### D1 — 第一版默认是否接受“每个 Strict App 一个独立 Session”

建议：**接受**。

理由：最小复杂度获得最确定的“关闭即销毁”语义。

如果必须第一版就让多个 app 共享 Session，则项目复杂度会显著上升，需要直接进入 SessionAgent/IPC 设计。

### D2 — 默认结束条件

建议：

```text
LastTrackedWindowClosed
```

而不是：

```text
PrimaryProcessExited
```

理由：真正解决 tray/helper 残留。

### D3 — 默认 disconnect grace

初始建议：

```text
30 seconds
```

需要真实环境验证后再定最终 default。

如果业务要求“客户端一断开就立即销毁”，可以设为 `Immediate`。

### D4 — 是否自动设置 host-wide `RemoteAppLogoffTimeLimit=0`

建议：**不要自动设置**。

只提供可选 fallback UI。

### D5 — Strict shim identity

建议：每 App 一个 GUID 目录，里面放同版本 launcher binary。

这是为了避免占用 RemoteApp command line 传 app alias，从而保住 FTA/动态参数语义。

### D6 — Legacy OS

建议：Strict Mode 首次正式支持聚焦现代 Windows / Windows Server；不让 XP compatibility 阻塞核心实现。

---

## 30. 第一个可验证 PoC 的最小范围

正式改 UI 前，先完成一个非常小的实验闭环：

1. 手工发布一个 alias，其 Path 指向 PoC `RemoteAppSessionHost.exe`。
2. PoC 固定启动 `notepad.exe`。
3. RDP 强制：

```text
disableconnectionsharing:i:1
```

4. PoC 获取 current SessionId。
5. 观察 Notepad top-level window。
6. 用户关闭 Notepad 窗口。
7. 1.5 秒后 `WTSLogoffSession(currentSession)`。
8. 外部 PowerShell 验证 Session 消失。
9. 再次打开，验证新 SessionId。
10. 两个用户同时测试，确保 A 的 close 不影响 B。

只有这 10 步稳定通过，才进入 Registry schema/UI 集成。

---

## 31. 推荐的项目完成定义（Definition of Done）

Strict App Session 可以标记为完成，必须同时满足：

- [ ] 普通 GUI App 关闭窗口后 Session 自动注销。
- [ ] 旧 Session 中残留进程被 Windows 清理。
- [ ] 重新启动得到新的 SessionId。
- [ ] 两用户互不影响。
- [ ] Shared Session 误配置时不会误注销其它业务 App。
- [ ] CommandLineSetting 0/1/2 兼容。
- [ ] FTA 兼容。
- [ ] RDP 兼容。
- [ ] signed RDP 兼容。
- [ ] MSI 兼容。
- [ ] disconnect grace 生效。
- [ ] Strict disable 能完整恢复原 Path。
- [ ] backup/restore 后可 repair。
- [ ] shim binary 普通用户不可写。
- [ ] 有可读的 lifecycle log。
- [ ] Host policy 冲突有诊断提示。
- [ ] 不依赖 `taskkill /IM`。
- [ ] 不依赖 username 选择 Session。
- [ ] 未引入 RDP/OS licensing bypass。

---

## 32. 参考资料

Microsoft：RemoteApp session lifecycle / disconnected behavior：

- https://learn.microsoft.com/en-us/troubleshoot/windows-server/remote/remoteapp-sessions-disconnected

Microsoft：Supported RDP properties（含 `disableconnectionsharing`、`remoteapplicationcmdline`）：

- https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties

Microsoft：`WTSLogoffSession`：

- https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtslogoffsession

Microsoft：`WTSEnumerateProcessesEx`：

- https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsenumerateprocessesexw

Microsoft：RemoteApp `CommandLineSetting` / `RequiredCommandLine`：

- https://learn.microsoft.com/en-us/powershell/module/remotedesktop/set-rdremoteapp?view=windowsserver2025-ps
- https://learn.microsoft.com/en-us/windows/win32/termserv/win32-tspublishedapplication

Microsoft：single-session-per-user policy：

- https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-terminalserver

Citrix：Published Application sign-out / lingering child process troubleshooting：

- https://docs.citrix.com/en-us/citrix-virtual-apps-desktops/2507-ltsr/seamless/troubleshooting.html

---

## 33. 当前建议

按此计划实施时，优先顺序应是：

```text
PoC 证明 current-session logoff
    -> ownership safety
    -> last-window lifecycle
    -> per-app strict shim
    -> registry/UI integration
    -> RDP forced isolation
    -> FTA/command-line compatibility
    -> disconnect/repair/fallback
    -> shared-session optimization
```

不要反过来先大改 UI 或全局 GPO。

核心原则保持不变：

> **业务应用生命周期决定 Strict Session 生命周期；Session 才是最终清理边界。**
