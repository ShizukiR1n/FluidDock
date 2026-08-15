# FluidDock

Windows 上的 macOS 风格 Dock，用 C# + `Windows.UI.Composition` 写的。

做它的起因是现有的同类软件（RocketDock / Nexus / MyDockFinder）动画质感都不行，而原因几乎是同一个：**动画跑在应用的 UI 线程上**，线程一被布局、图标加载、进程枚举阻塞就掉帧。macOS Dock 的流畅来自 Core Animation —— 动画由独立进程按刷新率求值，和应用逻辑彻底解耦。

Windows 上的等价物是 `Windows.UI.Composition`：`ExpressionAnimation` 由 DWM 合成线程求值。这条承诺是可测的，不是说说而已 —— `tools\StallTest.ps1` 会在 UI 线程上 `Thread.Sleep`，然后证明图标在 UI 线程确凿卡死期间仍在动。

目标环境：1920×1080 @ 239Hz、Windows 10 22H2 (19045)、100% 缩放。

---

## 构建与运行

这台机器上有两个坑，踩过了写在这里：

```powershell
# 必须用用户级 SDK。C:\Program Files\dotnet 只有运行时，没有 SDK。
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src\FluidDock\FluidDock.csproj -c Release -v q --nologo
```

- **不要用 `dotnet run`** —— 会失败。用户级 dotnet 根目录有运行时 8.0 和 10.0，唯独没有 9.0.x，而项目的目标框架是 net9.0。直接跑编译出来的 .exe，它的 apphost 会通过注册表找到 `C:\Program Files\dotnet` 里的 9.0.17。
- 构建前先 `Get-Process FluidDock | Stop-Process -Force`，否则复制步骤会报 MSB3027。
- 日志只在进程退出时落盘，所以磁盘上的日志**永远是上一次运行的**。

打包：

```powershell
tools\Publish.ps1        # -> dist\FluidDock.exe，自包含单文件，约 95 MB
```

自包含是有意的：dev 构建靠注册表找到系统里的 .NET 9，那是运气不是设计。压缩和 ReadyToRun 两个开关默认都关着，理由是实测出来的，见 `tools\Publish.ps1` 头部注释和 `tools\PackagingCost.ps1` —— 简单说，压缩省 92 MB 磁盘要拿 114 MB 内存和双倍启动时间换，因为压缩过的 bundle 条目无法内存映射，只能解压到私有堆上。

退出：托盘右键 → 退出。这是唯一的退出方式，也是有意的 —— 原来还有个 `Ctrl+Alt+Q`，但它注册在 Dock 窗口上，而那个窗口的生死由 Explorer 说了算（见下），所以它在最需要它的时候恰好不能用。

托盘图标的**双击目前不做任何事**，留给以后的设置窗口。同时只能跑一个实例。

---

## 架构上几个不能动的地方

**每个图标是两层嵌套 visual。** 外层拥有 `Offset.X`（归放大镜的 ExpressionAnimation 管），内层拥有 `Offset.Y` 和 `Scale`（归弹跳管）。`Offset` 是单个 Vector3 属性，一条表达式绑上去就整个占住了 —— 不做这个拆分，弹跳和放大镜会互相覆盖。

**衰减函数和常量只有一处定义**，同时供表达式字符串和 C# 命中测试使用。两边各写一份迟早会漂移。

**基于时间的动画必须显式释放。** `ExpressionAnimation` 是依赖追踪的，空闲时零成本；但 KeyFrame / Spring 动画不 `StopAnimation`（配合 `CompositionScopedBatch`）就会永远请求逐帧回调。`tools\IdleTest.ps1` 守着这条。

**桌面层的窗口区域。** Dock 沉在桌面层时是 Progman 的子窗口，而整条桌面窗口链（Progman / SHELLDLL_DefView / SysListView32）都设了 `WS_CLIPSIBLINGS | WS_CLIPCHILDREN` —— 这是量出来的，不是猜的（`tools\DesktopStyles.ps1`）。意味着这棵树里任何位置的子窗口都会占住桌面拒绝绘制的像素，往更深处 reparent 也躲不掉，`SetWindowRgn` 是唯一的杠杆。所以窗口区域在空闲时收缩到只包住静止图标，**立即扩张、延迟收缩**：UI 线程的窗口属性追不上合成线程的动画状态，放大收尾要 280ms，弹跳可以持续数秒。

---

## 测试工具

`tools\` 下都是 PowerShell 脚本，每个都是量出结论而不是看着像。

| 脚本 | 证明什么 |
|---|---|
| `StallTest.ps1` | UI 线程卡死时动画仍在跑（整个架构的决定性测试） |
| `IdleTest.ps1` | 空闲时没有逐帧回调泄漏 |
| `InputTest.ps1` | 500 次移动里 Dock 实际处理了多少 —— 掉采样就是"卡顿"从内部看的样子。`-Exe` 选择测哪个构建 |
| `RegionTailTest.ps1` | 窗口区域没有裁掉放大/弹跳的收尾 |
| `MarqueeShot.ps1` | 桌面框选划过 Dock 时不出现空洞 |
| `BounceTest.ps1` | 点击→启动→弹跳→停止的完整链路 |
| `HideShowTest.ps1` | 托盘隐藏再显示后 Dock 仍然完好 |
| `TrayMenuTest.ps1` | 托盘菜单两个命令都有效，退出会清理图标 |
| `ExplorerRestartTest.ps1` | Explorer 重启后 Dock 自己重建、回到桌面层、还会放大，且退出仍然有效 |
| `PackagingCost.ps1` | 四种打包方式的磁盘/内存/启动对比 |

### 写这些脚本时反复踩的坑

- **`.ps1` 文件必须带 UTF-8 BOM。** Windows PowerShell 5.1 按系统 ANSI（这里是 GBK）解析脚本，中文字面量会被静默改写 —— `"退出"` 变成 `"閫€鍑?"`。阴险在于文件本身是合法 UTF-8，运行时输出也正常，只表现为"本该匹配的比较不匹配"。
- **UI Automation 看不见 shell 的这些窗口。** 它不下钻到托盘的 `ToolbarWindow32`，对弹出菜单也返回空树。托盘图标要靠直接读工具栏（`TB_GETBUTTON` + 跨进程内存读 `TRAYDATA`），菜单要靠 `MN_GETHMENU` + `GetMenuStringW`。
- **PowerShell 把 `$null` 强转成 `""`** 传给字符串 P/Invoke 参数，所以 `FindWindowW("Progman", $null)` 什么都匹配不到。这个 bug 在两个不同脚本里各犯了一次。
- **函数会把输出流上的一切当返回值。** 函数里的 `Write-Output` 会被调用方连同真正的返回值一起收走 —— 早期版本因此把一个字符串当成"找到的托盘图标"报了出来。函数内一律用 `[Console]::WriteLine`。
- **截屏的 alpha 恒为 255**，`CopyFromScreen` 出来的位图不带透明度，拿 alpha 判断图标位置等于什么都没测。饱和度阈值也不通用（它假设桌面是灰的）。可靠的做法是**与静止帧做差分**。

---

## Explorer 重启

Dock 是 Progman 的子窗口，所以 Explorer 一重启，Dock 窗口就被销毁 —— 进程还活着，但屏幕上什么都没有。曾经这是个死局：退出是往 Dock 窗口发 `WM_CLOSE`，热键也注册在同一个窗口上，两条路一起断，只能开任务管理器。

现在两条都不再依赖那个窗口：

| | 做法 |
|---|---|
| 退出 | 直接 `PostQuitMessage`，走托盘窗口 —— 它是独立的顶层窗口，Explorer 拿不走 |
| 重建 | 托盘窗口收到 `TaskbarCreated` 后调 `DockWindow.Recreate()` |
| 保留的东西 | Compositor、SurfaceFactory（及其 D3D 设备）、配置监视器、前台钩子。只有 HWND 和绑在它上面的 `DesktopWindowTarget` 需要换 |
| 何时认爹 | 等 `SHELLDLL_DefView` 出现再 `SetParent`，每 500ms 试一次，最多 10 秒 |

最后一条不是保险起见。`TaskbarCreated` 是在 Explorer 还没装配完时广播的，那时 Progman 可能已经在了而图标视图还没有；此刻 reparent 进去，图标视图随后创建并落在 z-order 顶上，结果是一个**画得出来但点不动**的 Dock。等视图出现就不会落到那个位置。等待期间 Dock 是普通窗口 —— 在屏幕上、能用，只是没沉进桌面。

`WM_DESTROY` 因此**不再** `PostQuitMessage`：这个窗口的死不等于程序的死。

## 已知限制

- 静止时图标之间约 12px 的间隙在窗口区域之外，光标正好从间隙进入时要碰到图标才会触发放大。
- 弹跳期间区域完全打开，此刻恰好在拖框选会看到空洞。
- 启动弹跳大部分时候被拉起的应用窗口盖住了。
- `Rebuild()` 不释放 `CompositionDrawingSurface`，改配置热重载会涨内存。
- 托盘图标默认进溢出区（任务栏的「^」里），需要手动拖到任务栏上才常驻。这是 Win10 对新注册图标的默认行为。
