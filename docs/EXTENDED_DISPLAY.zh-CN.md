# 可选：把 Kindle 当作电脑扩展屏

如果只想显示十行提醒，完成[新手上手指南](BEGINNER_GUIDE.zh-CN.md)后即可使用留言板，不需要做本页操作。

本页用于把电脑窗口拖到 Kindle 上。它需要额外安装屏幕驱动和画面传输软件。墨水屏更新较慢，适合放文档或静态代码，不适合视频、游戏和快速滚动。

## 开始前

请先完成新手指南中的基础准备，并确认：

- 设备是 **Kindle DX Graphite，序列号以 B009 开头，系统 2.5.8**。
- USBNetwork 已安装，电脑通过 USB 网络连接 Kindle。
- 项目中的 Python 环境、依赖和本机可信主机公钥已配置好。
- 电脑为本项目实际使用过的 **Windows 11 x64**；下面不适用于 ARM 电脑。

本文说的“项目文件夹”，就是直接包含 `KindleDisplay.exe`、`KindleMessageBoard.exe`、`scripts`、`device` 的文件夹。不要在 ZIP 压缩包里直接操作。本页用 **`KindleDisplay.exe`**；十行留言另用 **`KindleMessageBoard.exe`**。

首次配置顺序：**PowerShell 7 → TightVNC → Kindle 客户端 → 虚拟屏驱动 → 开始连接**。

## 第一步：安装 PowerShell 7

这是管理虚拟显示器所需的工具，和 Windows 原有的 PowerShell 可以同时存在。

1. 打开[微软 PowerShell 安装说明](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows)。
2. 找到 **Install the MSI package**，选择文件名以 **`win-x64.msi`** 结尾的 PowerShell 7 稳定版。不要选 ARM64 或 Preview。
3. 双击下载的 MSI 文件，按安装向导完成安装。保留默认安装目录及加入 PATH 的选项；本项目不需要启用远程管理。
4. 关闭之前打开的命令窗口。
5. 用文件资源管理器打开项目文件夹，单击上方地址栏，输入 `pwsh`，按回车。

**完成后应看到：** 新窗口显示 `PowerShell 7...`，命令行当前位置是项目文件夹。若提示找不到 `pwsh`，从开始菜单打开 **PowerShell 7**，不要选名字为 **Windows PowerShell** 的旧版本；也可以重新登录电脑后再试。

## 第二步：安装 TightVNC Server

它负责把虚拟屏画面交给本程序。本项目实机使用的版本是 **2.8.81**；[官方当前下载页](https://www.tightvnc.com/download/)可能提供更新版本，更新版本尚未在本项目中做同样的验证。

1. 如需按本项目已使用的版本复现，可从[官方服务器下载 TightVNC 2.8.81 的 64 位安装包](https://www.tightvnc.com/download/2.8.81/tightvnc-2.8.81-gpl-setup-64bit.msi)。
2. 双击安装包，选择自定义安装 **Custom**，保留 **TightVNC Server**。仅安装 Viewer 不够；Viewer 在本项目中不需要。
3. 保留默认目录 `C:\Program Files\TightVNC`。
4. 到附加选项页面时，取消 **Register TightVNC Server as a system service**。本项目使用应用模式，不需要开机运行的 TightVNC 服务，也不需要为它新增外部访问的防火墙规则。
5. 安装完成后，不要自行启动 TightVNC Server。本项目会在点击“连接”时启动它。

**完成后应看到：** `C:\Program Files\TightVNC` 文件夹里有 `tvnserver.exe`。

如果以前已经装过并正在运行 TightVNC，先退出它自己的会话。托盘里的普通应用可以右键选择退出；服务模式按[官方使用说明](https://www.tightvnc.com/doc/win/TightVNC_for_Windows-Installation_and_Getting_Started.pdf)停止对应服务。如果该服务正用于其他人的远程连接，不要贸然中断。程序检测到另一个 TightVNC 实例时会停止连接，不会擅自接管它。

## 第三步：给 Kindle 复制显示客户端

这一步要临时切回“U 盘模式”，以便复制文件。只做留言板的人不需要这个客户端。

### 3.1 切回存储盘

1. 如果正在显示电脑内容，先在 `KindleDisplay.exe` 里点击“断开连接”。如果想恢复原 Kindle 封面，可以另开 `KindleMessageBoard.exe`，点击“恢复原休眠封面”。
2. 拔掉 USB 线，唤醒 Kindle，按 **Home**。
3. 用实体键盘唤出搜索输入栏，逐条输入并提交以下三条命令：

```text
;debugOn
`usbNetwork
;debugOff
```

4. 重新插线，在电脑的“此电脑”里打开新出现的 Kindle 存储盘。第二条开头是反引号，输入方式见新手指南。

**完成后应看到：** Kindle 盘里原有的 `documents` 文件夹。盘符可能是 E、F、G 等，按本机实际情况操作。

### 3.2 下载并复制客户端

1. 打开 [hwhw 原作者的 Kindle VNC viewer 帖子](https://www.mobileread.com/forums/showthread.php?t=150434)，下载帖子里的 **2012-10-27** ZIP 附件；也可使用[该原始附件链接](https://www.mobileread.com/forums/attachment.php?attachmentid=95008&d=1351345178)。不要换成只支持新款 Kindle 的程序。
2. 在电脑上解压 ZIP，找到里面名为 `kindlevncviewer` 的文件夹。
3. 把这个**完整文件夹**复制到 Kindle 盘的最外层，和 `documents` 并列。不是放进 `documents`，也不是多套一层 `kindlevncviewer`。
4. 在 Kindle 盘的 `kindlevncviewer` 文件夹中，右键 `config.lua`，选择用记事本打开。
5. 按 `Ctrl+F` 查找 `set_k3_keycodes()`。找到实际执行这一调用的行，在行首加两个减号，改成：

```lua
-- set_k3_keycodes()
```

6. 按 `Ctrl+S` 保存。保持文件名为 `config.lua`，不要另存成 `config.lua.txt`。如果这行本来已经注释，就不用重复修改。
7. 回到电脑的项目文件夹，打开 `device`，将其中的 **`kindle-display.sh`** 复制到 Kindle 盘的 `kindlevncviewer` 文件夹中。

最终结构大致如下，图中的“Kindle 盘”指本机实际盘符：

```text
Kindle 盘
├─ documents
└─ kindlevncviewer
   ├─ kindlevncviewer       ← 客户端文件，没有 .exe 后缀
   ├─ config.lua
   ├─ keys.lua
   ├─ rfbkeys.lua
   ├─ libvncclient.so.0
   ├─ libjpeg.so.8
   ├─ kindle-display.sh    ← 从本项目 device 文件夹复制
   └─ 其他原作者随包文件
```

请保留上游包中的版权与许可文件。本程序不需要另外安装 Launchpad。

### 3.3 切回 USB 网络

1. 在电脑中安全弹出 Kindle 存储盘，再拔线。
2. 回到 Kindle 的 Home，重新依次执行 `;debugOn`、`` `usbNetwork ``、`;debugOff`。
3. 重新插线。

**完成后应看到：** Kindle 存储盘消失，Windows 的 USB RNDIS 网卡恢复连接。此前设好的地址 `192.168.2.1` 应仍在；更换 USB 接口后如果生成了新网卡，按新手指南重新确认那块 USB 网卡的地址。

## 第四步：安装一个专用虚拟显示器

这一步会在 Windows 中增加一个显示器，由 Kindle 显示它的画面。

### 4.1 先确认没有重复安装

按 `Win+X`，打开“设备管理器”，展开“显示适配器”。如果已经有 **Virtual Display Driver / MttVDD**，先停在这里，不要再装第二个，也不要删除原来的驱动。已有部署应保留原来的 `virtual-display-install.json` 和 `scripts/display-baseline.json`。

如果电脑还安装了向日葵等软件的虚拟显示器，不需要卸载它们。本项目会识别自己的专用设备。

### 4.2 准备驱动文件

1. 打开 [Virtual Display Driver 的 25.7.23 发布页](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23)。
2. 在 **Assets** 下载 **`VirtualDisplayDriver-x86.Driver.Only.zip`**，也可点[该版本的原始驱动包链接](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip)。尽管包名包含 `x86`，本项目使用包里的 INF 明确支持 `NTamd64`，对应这里的 Windows x64 环境。
3. 在项目文件夹中新建 `packages`，再在其中新建 `vdd`，把驱动 ZIP 解压到 `vdd`。
4. 调整解压层次，确认下面三个文件直接位于同一层：

```text
项目文件夹\packages\vdd\VirtualDisplayDriver\MttVDD.inf
项目文件夹\packages\vdd\VirtualDisplayDriver\MttVDD.dll
项目文件夹\packages\vdd\VirtualDisplayDriver\mttvdd.cat
```

如果里面又套了一层文件夹，打开到实际包含 `MttVDD.inf` 的那层再整理。后面命令中的目录必须指向该层。

### 4.3 放好分辨率配置

1. 在文件资源管理器中打开 `C:\`，新建文件夹 **`VirtualDisplayDriver`**。如果系统提示管理员权限，选择继续。
2. 从本项目最外层复制 **`vdd_settings.xml`**，放入该文件夹。
3. 最终路径应为 **`C:\VirtualDisplayDriver\vdd_settings.xml`**。这个配置只创建一个虚拟屏，包含 `824×1200` 和 `1200×824` 两个方向所需的分辨率。

如果这里原本就有配置文件，先停止操作，确认是不是其他虚拟屏用途；不要直接点“替换目标中的文件”。需要沿用已有驱动时，应先核对它的设置。[驱动项目的默认配置位置说明](https://github.com/VirtualDrivers/Virtual-Display-Driver)

### 4.4 运行项目安装脚本

1. 在文件资源管理器打开项目文件夹，单击地址栏，复制它的完整路径。
2. 打开开始菜单，搜索 **PowerShell 7**，右键选择“以管理员身份运行”，在 Windows 提示中点“是”。
3. 输入下列命令，把引号里的示例路径换成刚才复制的项目路径，然后按回车：

```powershell
Set-Location -LiteralPath 'C:\KindleDXMonitor'
```

4. 再完整复制并运行这一行：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-KindleVirtualDisplay.ps1 -DriverDirectory .\packages\vdd\VirtualDisplayDriver
```

这条命令只对本次脚本调用设置执行方式，不修改电脑永久执行策略。脚本会检查驱动签名，保存安装前的显示器清单，再创建和记录本项目的设备。

**完成后应看到：** 输出中出现 `Virtual Display Driver`，项目目录里生成 `virtual-display-install.json`，`scripts` 目录里生成 `display-baseline.json`。它们属于这台电脑，请保留。如果提示需要重启 Windows，先保存电脑中的工作再重启。

如果提示“已有 Virtual Display Driver”，不要重复运行；如果提示签名无效，不要关闭驱动签名检查；如果提示基线无效，不要删除基线来跳过保护。先核对包是否完整、是否已有驱动，以及文件是否属于这台电脑。

## 第五步：开始显示电脑内容

1. 确认 Kindle 已唤醒、USB 数据线已插好，而且现在是 USB 网络模式。
2. 双击项目文件夹的 **`KindleDisplay.exe`**。
3. 在方向下拉框选一个方向：竖着放选 **0° · 竖屏（正向）**；横着放先选 **90° · 横屏一**。
4. 点击主窗口的 **“连接”**，如果 Windows 询问是否允许修改设备，点“是”。
5. 等待 Kindle 出现电脑画面。程序会启用专用虚拟显示器，再启动传输。

如果此时看不到画面，先按下一节确认 Windows 使用了扩展模式；调整好后点击“断开连接”，再点“连接”。

### Windows 的显示器要设成“扩展”

1. 在电脑桌面空白处右键，选择“显示设置”。
2. 点击“标识”，确认新增显示器的编号。屏幕可能显示为 `1、2`，也可能是 `1、2、3`，不要照抄别人的编号。
3. 选中新增显示器的方框，在“多显示器”附近找到模式下拉框，选 **“扩展这些显示器”**。
4. 点击 **“保留更改”**。

如果看到一个合并的 **`2|3`** 方框，先选中它，将模式改成 **“扩展 2 和 3 上的桌面”**，再保留更改。编号按你的电脑为准。

**完成后应看到：** 显示设置里的屏幕方框是分开的。程序默认把 Kindle 虚拟屏放在主屏右侧，把一个普通窗口向主屏右边拖动，就能移到 Kindle 上。

如果你本来就有多个物理屏幕，主屏右侧可能已被占用。此时要在项目的 `kindle-display.local.json` 中设置空闲位置的 `PositionX`、`PositionY`；程序连接时会按这两个数值放置虚拟屏。不要只在 Windows 中拖动布局后期待程序永久沿用它。配置方法见下面的“安装位置或屏幕布局不同怎么办”。

### 安装位置或屏幕布局不同怎么办

按本文默认位置安装、只有一个电脑主屏时，通常不需要这一节。

1. 先在显示器程序中断开连接，然后退出程序。
2. 在项目文件夹复制 `kindle-display.example.json`，将副本命名为 **`kindle-display.local.json`**。资源管理器选择“查看 → 显示 → 文件扩展名”，避免误命名为 `.json.txt`。已有这个本机文件时直接编辑，不要覆盖。
3. 右键该文件，用记事本打开，只改需要的项目，保留英文双引号、逗号和大括号。Windows 路径中的每个反斜杠写成两个，例如 `C:\\Program Files\\TightVNC\\tvnserver.exe`。
4. 保存，重新打开程序并连接。

| 配置项 | 默认或填写方式 |
| --- | --- |
| `PowerShellPath` | 空字符串 `""` 自动查找 PowerShell 7；安装位置特殊时填 `pwsh.exe` 的完整路径 |
| `PythonPath` | 默认 `.venv\\Scripts\\python.exe`，相对于项目文件夹 |
| `PythonHostPath` | 空字符串自动从虚拟环境查找基础 Python，一般不用改 |
| `VncServerPath` | 空字符串使用 `C:\Program Files\TightVNC\tvnserver.exe` |
| `DisplayInstanceId` | 空字符串读取安装脚本生成的设备记录，不要照抄别人的设备编号 |
| `PositionX`、`PositionY` | 默认 `null` 放到主屏右边、顶部对齐；手动填写时用整数，不加引号 |

例如，主屏是位于 `(0, 0)` 的 1920×1080 屏幕，且它下方没有其他显示器，可以把 `PositionX` 设为 `0`、`PositionY` 设为 `1080`，把 Kindle 放到主屏下方；之后将窗口向下拖即可。屏幕大小、缩放或排列不同，不要直接照抄这两个数值。修改前保留原配置副本，位置不合适时恢复它。

## 以后怎样使用

| 想做什么 | 在程序里怎样操作 |
| --- | --- |
| 开始显示 | 插上数据线，确认 USB 网络模式，点“连接” |
| 暂停扩展屏 | 点“断开连接” |
| 重新加载画面 | 点“刷新” |
| 换方向 | 选 0°、90°、180° 或 270°，再点“应用方向” |
| 横屏反了 | 换另一个横屏选项并应用 |
| 把窗口收起来 | 用右上角最小化，传输继续 |
| 完全退出 | 点“退出”或主窗口关闭按钮，程序先断开显示 |

方向切换可能需要十几秒，期间会闪屏或短暂回到 Kindle 的 Home。旋转机身后，请自己选择方向，不要只依赖重力感应，也不要在 Windows 中反复切换屏幕方向。

Windows 中显示的 **60 Hz** 只是虚拟屏模式，不表示 Kindle 有 60 帧的刷新速度。电脑睡眠、关机或者拔线时，不能继续更新电脑画面。

## 怎样切回留言板

1. 在 **`KindleDisplay.exe`** 里点“断开连接”，等显示会话结束。
2. 另开项目文件夹中的 **`KindleMessageBoard.exe`**。
3. 编辑或粘贴留言，点击“预览”，再点“发送到 Kindle”。留言板不会自动停止扩展屏；若提示显示器仍在运行，先回显示器程序断开。
4. 等提示成功后，可以关闭程序、拔掉 USB，留言仍会保留。

以后想继续用扩展屏，唤醒 Kindle，连接 USB 网络，再打开 `KindleDisplay.exe` 点“连接”。不想保留自定义封面时，在 `KindleMessageBoard.exe` 中点“恢复原休眠封面”。两个程序分别使用，不要同时向 Kindle 发送画面。

## 常见卡点

| 看到的现象 | 先做什么 |
| --- | --- |
| 提示找不到 PowerShell | 确认安装的是 PowerShell 7 MSI，并按第一步重新打开窗口 |
| 提示找不到 `tvnserver.exe` | 确认装了 TightVNC **Server**，不只是 Viewer；路径不同则修改本机配置的 `VncServerPath` |
| 提示另一个 VNC 实例在运行 | 先退出自己已有的 TightVNC 应用或服务，不要删除程序运行记录 |
| Kindle 仍显示 USB 存储画面 | 按新手指南切换 USBNetwork；只有盘符不代表网络连接已完成 |
| 出现 `2|3`，连接失败 | 按上面的步骤改为扩展并保留更改，再重新连接 |
| 找不到 824×1200 / 1200×824 模式 | 核对 `C:\VirtualDisplayDriver\vdd_settings.xml` 是否是本项目文件；保存工作后重启 Windows 让驱动重新加载配置 |
| 按 Kindle Home 后画面又回来 | 后台会重连，想停止应在电脑点“断开连接” |
| 安装时已有驱动 / 基线冲突 | 停止重复安装，保留现场与本机文件，不要通过删除它们绕过检查 |

如果没有按预期完成，记下停在哪一步及程序提示。排查时优先查看项目 `runtime` 中对应日志，不需要反复刷 Kindle 系统。
