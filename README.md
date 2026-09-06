# myXmic

把手机变成电脑的无线/USB 高清麦克风（类 WO Mic），支持降噪、低延迟、低资源占用。

## 架构

```
手机 (Android Kotlin)                        电脑 (Windows .NET 6)
┌──────────────────────────┐                 ┌───────────────────────────┐
│ AudioRecord 48k/16/mono   │   裸 PCM/TCP   │ myXmic (NAudio)      │
│  ├─ NoiseSuppressor (DSP) │ ══════════▶   │  └─ WASAPI → CABLE Input  │
│  └─ AcousticEchoCanceler  │ USB: adb reverse│                            │
└──────────────────────────┘                 └───────────┬───────────────┘
                                             CABLE Output (录音设备)
                                             → Zoom / Discord / OBS 选它
```

## 音质与降噪

- **无压缩传输**：48kHz/16bit Mono PCM ≈ 96 KB/s，Wi-Fi/USB 均无压力，音质不损失
- **降噪**：调用 Android 系统 `NoiseSuppressor`（高通/联发科 DSP 硬件加速，几乎零功耗）
- 后续若要更强降噪（AI 级），可在 Windows 端接 RNNoise（CPU < 3%）

## 使用步骤

### 一次性准备
1. 电脑安装 VB-CABLE（免费）：https://vb-audio.com/Cable/
2. （可选）把"录制"设备 CABLE Output 改名：Win+R → `mmsys.cpl` → 录制 → 属性

### USB 模式（推荐，稳定零延迟）
1. 手机开启 USB 调试，插电脑
2. 电脑执行：`adb reverse tcp:8125 tcp:8125`（myXmic 启动时会自动尝试）
3. Android App 里主机填 `127.0.0.1`，端口 `8125`，点开始

### Wi-Fi 模式
1. 手机和电脑连同一局域网
2. Android App 填电脑局域网 IP（如 192.168.1.23），端口 8125

### 在应用中使用
Zoom / Discord / OBS 的麦克风选 **CABLE Output**。

## 安装与发布（详细）

### Android
1. 安装 Android Studio，打开 `android/` 目录
2. 手机开启「开发者选项 → USB 调试」，连接电脑
3. 点 Run（或生成 APK：Build → Build APK），安装到手机

未装 Android Studio 也可命令行：
```bash
cd android
gradle wrapper   # 首次需本机有 gradle
./gradlew assembleDebug
# 产物在 app/build/outputs/apk/debug/app-debug.apk，拷到手机安装
```

### Windows（生成独立 exe，发给别人用）
需在装了 .NET 6 SDK 的 Windows 上执行一次：
```powershell
cd windows
dotnet publish -c Release -r win-x64 --self-contained `true` ^
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```
产物是 `bin\Release\net6.0-windows\win-x64\publish\myXmic.exe`（单个 exe，无依赖）。双击运行会弹 UAC（需要管理员权限来隐藏/恢复虚拟麦克风，这是正常设计）。

分发时需同时让用户安装一次 VB-CABLE（https://vb-audio.com/Cable/ 的免费版）。

## 使用

1. 打开 myXmic.exe，点「启动服务」
2. USB 模式：手机连电脑（已开 USB 调试），App 里填 `127.0.0.1`
   Wi-Fi 模式：手机电脑同局域网，填电脑的局域网 IP
3. 手机 App 点「开始」，窗口会显示「已连接」并跳电平条
4. Zoom/Discord/OBS 麦克风选 **CABLE Output**
5. 点「停止服务」或直接关窗口 → 虚拟麦克风立即从系统里消失（下次启动自动恢复）

## 参数

| 项 | 值 |
|---|---|
| 采样率 | 48000 Hz |
| 位深/声道 | 16bit / 单声道 |
| 帧长 | 20ms (1920 字节) |
| 端口 | TCP 8125 |
| 播放延迟 | WASAPI 30ms + 网络缓冲 ≤ 250ms |

## 进程生命周期（Windows）

- 启动 myXmic → 自动启用 VB-CABLE 两个端点（虚拟麦克风“插入”系统）
- 最小化 / 点 X → 缩到托盘，服务继续运行
- 托盘菜单「退出」→ 停 TCP 与 WASAPI、释放全部句柄线程、禁用两个端点（虚拟麦克风从系统消失）

## 后续可扩展

- [ ] RNNoise 深度学习降噪（Windows 端）
- [ ] 蓝牙 SCO / Wi-Fi Direct 通道
- [ ] 自研 SYSVAD 虚拟麦克风驱动，摆脱 VB-CABLE
- [ ] iOS 版（仅 Wi-Fi）
