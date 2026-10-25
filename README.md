# Kindle DX Monitor

把 **Kindle DX Graphite** 用作 Windows 独立扩展显示器，可将文档、代码或其他窗口拖到墨水屏上。

提供原生 **C# / WinForms** 控制窗口：连接、断开连接、刷新、退出，支持任务栏最小化，以及 **0°、90°、180°、270°** 四个手动方向。后台通过 USB 网络与 SSH 隧道传输 VNC 画面，断线后自动重试。

## 下载与日常使用

点击仓库 **Code → Download ZIP**，解压整个目录。完成下方首次配置后，双击 `KindleDisplay.exe`，选择方向并点击“连接”。请保留 EXE 旁边的 `scripts` 等文件；它是桌面控制程序，**不是包含驱动、Python 和 Kindle 客户端的单文件安装包**。

| 操作 | 行为 |
| --- | --- |
| 连接 | 启用专用虚拟屏并连接 Kindle |
| 断开连接 | 停止传输，恢复阅读界面及之前的 VNC 设置，保留控制窗口 |
| 刷新 | 重启 Kindle 显示客户端，重新加载画面 |
| 应用方向 | 将下拉框的方向同时应用到 Kindle 和 Windows 虚拟屏 |
| 退出 / 关闭窗口 | 先断开连接，再退出程序 |
| 最小化 | 继续显示，窗口留在任务栏 |

横屏两个方向分别标为“横屏一”和“横屏二”。方向选择成功后会保存。切换通常需要十几秒，期间可能闪屏或短暂回到 Home；不再通过持续查询重力感应来改变方向。

Windows 的多显示器设置必须保持 **扩展这些显示器**。如果误设成“复制”，看到合并的 `2|3` 方框，请选中它，将下方多显示器选项改为“扩展 2 和 3 上的桌面”，点击“保留更改”后再连接。具体编号以自己的电脑为准。横竖方向在本程序中调整。

## 休眠留言板

点击主窗口的“休眠留言板 · 最多十行”，在一个多行文本框中编辑文字。每行对应画面中的一条留言，支持中文，以及 **Ctrl+A 全选、Ctrl+C 复制、Ctrl+V 粘贴整段文本**。最多发送十行；粘贴超过十行时会提示先调整，输入内容会完整保留，不会截掉多余文字。可先预览，再点击“发送到 Kindle”。发送会先断开扩展显示，然后将文字转成 **824×1200 灰度休眠图片**。长行会适当缩小字号，仍放不下时会提示修改，不会静默截断。

编辑内容会自动保存为电脑本地的 **UTF-8 文本文件 `runtime/messageboard-draft.txt`**，尚未发送的修改也会保存。下次打开留言板会自动加载上次内容，可直接继续编辑。旧版 `runtime/messageboard-draft.json` 中的十行记录也会兼容迁移；本地留言和运行记录不会提交到 GitHub。

留言发送成功后，Kindle 进入原生休眠封面状态。可以拔掉 USB 线，不需要电脑持续运行或刷新；下一次改留言时，唤醒 Kindle、重新连接 USB 网络，再打开程序发送即可。

“恢复原休眠封面”用于撤销留言板。实现使用本次开机有效的目录挂载，不改写原始封面或系统启动文件；**Kindle 完整重启后会恢复原封面，需要重新发送留言**。普通休眠、唤醒和拔线不等于完整重启。

更换留言会短暂重启 Kindle 阅读界面以清除旧图片缓存，请等待程序显示成功。留言板是静态提示页，不会自动同步日历、通知或网络信息。

## 实测环境与限制

- Kindle DX Graphite，固件 **2.5.8 (555370010)**，保留该固件运行。
- Windows 11 x64；其他 Kindle 型号、其他固件未验证。
- 竖屏 **824×1200**，横屏 **1200×824**。
- 虚拟显示驱动的 **60 Hz** 是 Windows 模式参数，并不代表墨水屏能以 60 帧刷新。适合文字、文档与代码，不适合视频和快速动画。
- USB 需要支持数据传输。电脑睡眠、关机时无法传输；重启电脑后需重新打开程序。
- 当前连接代码针对直连 USB `192.168.2.1 → 192.168.2.2`，使用已配置 USBNetwork 的 root 空密码方式；不同认证配置需自行适配，程序不会改写设备密码或安装登录密钥。

## 首次配置

### 1. 准备 Kindle

先备份通过 USB 可以看到的个人文件。下列改装组件需与 **DX Graphite** 型号匹配，不能混用 Kindle 3、DX 白色版等安装包。

本项目已验证的组合：

| 组件 | 版本 / 来源 |
| --- | --- |
| Legacy jailbreak | 0.13.N，DXG 安装包 |
| USBNetwork | 0.57.N DX，DXG 安装包 |
| Kindle VNC viewer | hwhw 原作者 **2012-10-27** 发布包 |

越狱及 USBNetwork 按 [NiLuJe 原作者说明](https://www.mobileread.com/forums/showthread.php?t=88004) 安装；本仓库不包含刷机包，也不自动升级 Kindle 固件。

从 [Kindle VNC viewer 作者帖](https://www.mobileread.com/forums/showthread.php?t=150434) 获取兼容旧 DXG 的客户端（[2012-10-27 原始附件](https://www.mobileread.com/forums/attachment.php?attachmentid=95008&d=1351345178)），将完整 `kindlevncviewer` 目录复制到 Kindle USB 存储根目录。在其中的 `config.lua` 中注释调用 `set_k3_keycodes()` 的那一行，保留 DX 原始键码。再把本仓库 `device/kindle-display.sh` 复制到同一目录。

Kindle 内对应路径为 `/mnt/us/kindlevncviewer/`，至少应有客户端可执行文件、配套共享库、`config.lua`、`keys.lua`、`rfbkeys.lua` 和本项目启动脚本。保留上游包中的许可文件。本项目通过 `sh` 启动自己的脚本，不依赖 Launchpad。

### 2. 开启 USB 网络

安全弹出存储盘并拔线，按 Kindle 的 Home 回到书籍列表。直接按实体键盘即可唤出搜索栏，逐条输入并提交：

```text
;debugOn
`usbNetwork
;debugOff
```

第二行开头是反引号。重新插线后，Windows 应识别 USB RNDIS 网卡。只给这块 USB 网卡设置 IPv4 地址 `192.168.2.1`、子网掩码 `255.255.255.0`，网关和 DNS 留空；Kindle 地址为 `192.168.2.2`。USB 网络模式下不再显示存储盘符，这是正常现象。

Kindle 重启后若回到存储模式，重复上述切换。要传书，先从电脑断开显示，再切回存储模式。

### 3. 安装 Windows 依赖

在正常用户会话中准备：

- **PowerShell 7**，标准安装目录或加入 PATH。
- **Python 3.13**（本项目验证版本）；在项目根目录创建虚拟环境并安装依赖：

```powershell
py -3.13 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

- [TightVNC](https://www.tightvnc.com/) Server，实测 **2.8.81**。本项目使用应用模式，默认路径为 `%ProgramFiles%\TightVNC\tvnserver.exe`；不要让另一个 TightVNC 服务或进程占用该会话。
- Windows OpenSSH 客户端，用于首次核对主机密钥和排障。
- .NET Framework 4.8，用于运行桌面界面；编译时使用系统自带 C# 编译器。

### 4. 安装专用虚拟显示器

实测使用 [Virtual Display Driver 25.7.23](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23) 的签名驱动包。下载解压后，确认目录包含 `MttVDD.inf`、`MttVDD.dll`、`mttvdd.cat`。

本仓库 `vdd_settings.xml` 提供一个虚拟屏和横竖两个分辨率。按 [驱动项目说明](https://github.com/VirtualDrivers/Virtual-Display-Driver) 放入其配置目录，默认 `C:\VirtualDisplayDriver\vdd_settings.xml`。如果已有配置，先自行备份，避免覆盖其他虚拟屏用途。

在项目根目录打开管理员 PowerShell 7，执行（将路径换成下载解压后的目录）：

```powershell
.\scripts\New-KindleVirtualDisplay.ps1 -DriverDirectory 'C:\Downloads\VirtualDisplayDriver'
```

脚本验证驱动签名，安装前保存现有显示器基线到 `scripts/display-baseline.json`，然后将新设备实例写入 `virtual-display-install.json`。请保留这两个本机文件。已有 MttVDD 时脚本会拒绝重复安装；已有有效基线会保留并复用，无效基线则停止。不要删除基线来绕过检查，也不要将别人的基线复制过来。已有部署应沿用安装前记录和对应实例，缺失时需先人工核对设备与基线。

Windows 显示设置中将新显示器设置为“扩展”。启动时默认放到主屏右侧，也可在本地配置中指定空闲位置。

### 5. 配置路径与可信设备

将 `kindle-display.example.json` 复制为 `kindle-display.local.json`。通常只需保持默认值；如有多个显示器或非标准依赖路径，再按下表修改。

| 字段 | 空值 / 缺省行为 |
| --- | --- |
| `PowerShellPath` | 标准 PowerShell 7 安装路径或 PATH 中的 `pwsh.exe` |
| `PythonPath` | 项目 `.venv\Scripts\python.exe` |
| `PythonHostPath` | 根据虚拟环境 `pyvenv.cfg` 推导实际 Python 解释器路径 |
| `VncServerPath` | `%ProgramFiles%\TightVNC\tvnserver.exe` |
| `DisplayInstanceId` | 读取本机 `virtual-display-install.json` 中的实例 |
| `PositionX`、`PositionY` | `null` 表示主屏右边界和顶边；可填写整数坐标，避免与其他屏幕重叠 |

路径支持 `%ENVVAR%` 和相对于项目根目录的路径。本地配置、设备实例、基线、日志均被 `.gitignore` 排除。

在 `runtime/kindle_known_hosts` 中保存**自己这台 Kindle 已核实的 SSH 主机公钥**。程序严格验证该文件，拒绝未知主机或密钥变更。首次可采集候选密钥并查看指纹：

```powershell
New-Item -ItemType Directory -Path runtime -Force | Out-Null
ssh-keyscan -T 5 -t rsa 192.168.2.2 | Set-Content -Encoding ascii runtime\kindle_known_hosts.candidate
ssh-keygen -lf runtime\kindle_known_hosts.candidate
```

将指纹与设备端或此前可信连接记录核对，确认匹配后再将候选文件改名为 `kindle_known_hosts`。采集本身不等于验证；不要用别人的公钥文件，也不要关闭主机密钥验证。

完成后双击 `KindleDisplay.exe`。界面本身不要求管理员权限；点击连接或断开时，Windows 可能提示授权启停专用虚拟显示设备。

## 构建与验证

在 PowerShell 7 的项目根目录执行：

```powershell
.\scripts\build-windows-app.ps1
.\.venv\Scripts\python.exe -m unittest discover -s scripts -p 'test_*.py' -v
```

构建生成根目录 `KindleDisplay.exe`，使用 Windows GUI 子系统，不附带控制台窗口。控制器测试使用模拟设备，不会修改真实 Kindle 或显示器。

实机开发阶段验证了四个方向、横竖分辨率切换、刷新、最小化、断线重连和停止恢复。仓库发布版本另做构建和离线验证；其他电脑仍需按上述步骤完成本机配置。

## 工作原理与文件

```text
Windows 独立虚拟显示器 → TightVNC (127.0.0.1:5905)
                              ↓ USB 上的 SSH 反向隧道
                       Kindle VNC viewer → 墨水屏
```

- `app/`：原生 C# 界面，仅对明确的连接/断开操作启动提权工作进程。
- `scripts/`：Python 连接控制器、VNC 与显示驱动管理、构建及测试工具。
- `device/kindle-display.sh`：Kindle 端启动、休眠状态保存与退出恢复。
- `runtime/`：首次运行生成，包含状态、日志和设备主机密钥，不能作为公共安装数据。

VNC 只监听本机回环地址，禁用远程输入和文件传输；停止时恢复本程序修改过的 VNC 设置。方向调整通过 DXG 驱动接口设置并读回确认，失败时尝试恢复原方向。不会通过定时重力感应查询反复重启客户端。

## 第三方项目

本仓库不打包 Kindle 固件、越狱工具、USBNetwork、第三方驱动、TightVNC 或 Kindle VNC 客户端二进制。请从原作者渠道获取，并保留各自许可和版权信息：

- [NiLuJe legacy Kindle hacks](https://www.mobileread.com/forums/showthread.php?t=88004)
- [hwhw/kindlevncviewer](https://github.com/hwhw/kindlevncviewer)
- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
- [TightVNC](https://www.tightvnc.com/)
- [Paramiko](https://www.paramiko.org/)
