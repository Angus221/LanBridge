# 兰桥

兰桥把一台 Windows 电脑和一部 Android 手机（包括能装 APK 的 HarmonyOS）连在同一个 Wi-Fi 上，互传文件和文字。数据不经过云，也不需要账号。

这一版是 V1.0.2。哪些行为来自需求、哪些是规格没写完时定下来的，写在 [DESIGN.md](DESIGN.md)。线上的消息格式在 [docs/protocol.md](docs/protocol.md)。

电脑端主界面是停在屏幕右缘的侧栏：鼠标移到右缘中间的拉手会滑出，移开后自动收起，也可以固定。可以配对多部手机，每部手机有自己的连接状态、剪贴板开关和传输记录。默认 `Ctrl+Alt+V` 把当前剪贴板文字发给选中的手机，`Ctrl+Alt+L` 打开或收起侧栏，都可以在设置里改；两个快捷键相同，或已被其他程序占用时，会在侧栏里说明。

手机端默认显示当前电脑和这台电脑自己的记录。底部可以切换已配对的电脑，或再添加一台。通知里有「发送剪贴板」，快捷设置里也有同名开关：点一下会短暂把兰桥带到前台读取剪贴板（所以刚在微信里复制的文字也能发），发出后马上退出。应用里还可以直接选照片或文件。设置齿轮在左上角。

## 你需要准备

- 电脑：Windows 11，[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。
- 手机：Android 10 或更新（`minSdk 29`）。HarmonyOS 4 能装 APK 即可。编译手机端还需要 Android SDK 34 和 JDK 17 或更新。
- 两台设备连同一个局域网。访客网络如果隔离了设备，会发现不了对方。

## 在电脑上编译和运行

在仓库的 `windows` 目录：

```powershell
dotnet build LanBridge.sln
dotnet run --project src\LanBridge.App\LanBridge.App.csproj
```

第一次运行会在 `%LOCALAPPDATA%\LanBridge` 生成身份和设置，并在「发送到」里放上「发送到手机」。收到的文件默认在：

```text
%USERPROFILE%\Downloads\LanBridge
```

可以在设置里改这个文件夹。

关掉侧栏之后，兰桥还在屏幕右缘和任务栏右下角。要退出，右键托盘图标选「退出」。

Windows 第一次运行会尝试添加入站规则：TCP 监听端口（默认 47821）和 UDP 5353（mDNS）。没有管理员权限时，规则加不上，说明会写在 `%LOCALAPPDATA%\LanBridge\firewall.txt`，并在界面里提示一次。请允许当前网络。没有这一步，手机找不到电脑，二维码里的地址也连不上。电脑只从实际的 Wi-Fi 或以太网网卡发 mDNS，不从回环或虚拟网卡发。

运行测试：

```powershell
dotnet test LanBridge.sln
```

## 在手机上编译和安装

在仓库的 `android` 目录准备 SDK 路径（把路径换成你自己的）：

```bash
echo "sdk.dir=$ANDROID_HOME" > local.properties
./gradlew :protocol:test :app:assembleDebug
```

Debug 包在 `android/app/build/outputs/apk/debug/app-debug.apk`。把它拷到手机上安装。安装前如果系统提示「未知来源」，按系统的步骤允许这一次即可。

打开「兰桥」后，通知栏会留着一条「正在连接」的前台服务。这是为了在配对之后还能收电脑发来的文件。鸿蒙的省电策略仍可能在锁屏后停掉它，那时需要再打开一次应用。

## 配对

1. 电脑上点「配对手机」。屏幕上会出现 6 位数字和一张二维码，大约 2 分钟内有效。
2. 手机上点「配对电脑」。
   - 扫这张二维码（不依赖 Google Play 服务），或
   - 点「搜索同一局域网里的电脑」，选中你的电脑，输入那 6 位数字，或
   - 手动输入电脑上显示的 `IP:端口`（端口可省略，默认 47821），或
   - 把 `lanbridge://` 链接粘贴进去。
   选中电脑并且配对码是 6 位数字后，底部的「连接」才会亮。还没选中电脑时，按钮旁边会写明原因。
3. 电脑会再问一次「允许这台手机配对？」。核对名称和密钥指纹，是你本人在操作再点「是」。

点「否」、超时，或者 PIN 错了，都不会建立信任。错 5 次后要在电脑上重新打开配对。设置里可以「解除信任」，之后必须重新配对。

## 传文件和文字

电脑：

- 「发送文件」选择一个或多个文件。
- 「发送剪贴板」发送当前文字。如果内容像密码，会先问你。
- 资源管理器里选中文件，右键「发送到」→「发送到手机」。
- 剪贴板自动同步默认是关的。要在设置里打开才会在复制时自动发到手机，并且可以随时暂停。

手机：

- 主界面「发送剪贴板」。
- 其他应用的分享菜单里选「发送到电脑」。
- 选中一段文字时，菜单里的「发送到电脑」（`ACTION_PROCESS_TEXT`）。
- 下拉快捷设置里的「发送到电脑」。这个开关会先打开一个界面再读剪贴板，因为 Android 10 以后在后台读剪贴板是空的。

收到的文件只保存，不会自动打开或运行。电脑上可以点「打开所在文件夹」，那是你自己的操作。

## 隐私相关的默认值

- 自动接收已配对设备的文件：开。
- 剪贴板自动同步：关。
- 疑似密码、私钥、云密钥的过滤：开。被拦住的文字不写入历史。
- 文字历史：开。可以关掉。

日志在电脑的 `%LOCALAPPDATA%\LanBridge\lanbridge.log`。

## 打开发布包

Windows 11 x64 自包含单文件（不要求目标电脑另装 .NET）：

```bash
scripts/package-windows.sh
```

压缩包在 `dist/windows/LanBridge-1.0.2-win-x64.zip`，里面是 `LanBridge.exe`。脚本可以在装了 .NET 8 SDK 的系统上交叉编译，程序本身要在 Windows 11 x64 上运行。

Android 正式签名 APK（含 arm64-v8a，可供华为 Mate 60 旁加载安装）：

```bash
export ANDROID_HOME="$ANDROID_HOME"
scripts/package-android.sh
```

APK 在 `dist/android/LanBridge-1.0.2.apk`，带 v1、v2、v3 三种签名（没有 v4 的 `.idsig`）。直接跑 `apksigner verify` 时，因为 `minSdk` 是 29，v1 和 v2 会显示成 false；脚本会用 `--min-sdk-version 21` 确认三种都通过。第一次运行会在 `android/.signing/` 生成密钥库和密码文件。这两样都不会进 Git。换一台电脑签名时，把 `LANBRIDGE_KEYSTORE` 和 `LANBRIDGE_KEY_PASSFILE` 指到原来的文件。升级安装必须沿用同一把密钥。

## 这一版没有做的

图片剪贴板、整个文件夹、断点续传的界面、Win11 原生右键菜单。分块上传的接口已经留好，手机上大于 1 MiB 的文件会按块发送，但没有「从上次断开的地方继续」的按钮。
