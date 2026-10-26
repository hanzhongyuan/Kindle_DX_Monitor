# 新手上手指南：从原装 Kindle 到自定义留言板

第一次需要配置 Kindle 和电脑。以后只要打开 **`KindleMessageBoard.exe`**，编辑、预览、发送即可，不需要懂编程。

**本文适用：Kindle DX Graphite，序列号前四位 B009，固件 2.5.8；电脑按 Windows 11 x64 说明。** 不是这个型号或版本时，不要照着安装下面的设备更新包。

只用留言板，不需要虚拟显示驱动、TightVNC、PowerShell 7 或 Kindle VNC 客户端。想把电脑窗口拖到 Kindle 上，之后再看 [扩展显示器指南](EXTENDED_DISPLAY.zh-CN.md)。

## 已配置好的用户：日常五步

1. 短拨 Kindle 电源开关并松开，唤醒它，接上 USB 数据线。
2. 双击 **KindleMessageBoard.exe**。
3. 选择行数、字体和字号，在一个文本框里输入或粘贴文字，每行一条。
4. 点击“预览”，再点“发送到 Kindle”，等待成功提示。
5. 成功后可关闭程序并拔线，留言继续留在休眠封面。

不用先开显示器程序或点“连接”。如果之前正在使用扩展屏，先在 `KindleDisplay.exe` 点“断开连接”。首次使用请从下面第一步开始。

## 第一步：确认型号并备份

### 在 Kindle 上

1. 先充电，至少超过一半。
2. 按 **Home → Menu**，用五向键选 **Settings**，按中间确认。
3. 查看设置页的设备信息及底部版本号：序列号应以 **B009** 开头，版本是 **Kindle 2.5.8**。

这里使用实体键盘和按键，不是触摸屏。Home 是主页，Menu 是菜单，五向键上下左右移动，中间按下是确认。设备信息及按键操作可参照 [Amazon DX 用户手册](https://kindle.s3.amazonaws.com/Kindle%20DX%20User%27s%20Guide%2C%203rd%20Edition_English.pdf)。

版本不是 2.5.8 时，先到 [Amazon 官方软件更新说明](https://www.amazon.com/gp/help/customer/display.html?nodeId=GKMQC26VQQMM8XSW) 查找 **Kindle DX / Graphite / B009** 对应的更新，按官方流程完成后再回来。找不到对应型号时先停下，不要安装其他型号固件。本项目不需要把 DX 升到 3.x。

### 在电脑上

1. 用支持数据传输的 USB 线连接 Kindle。
2. 打开“此电脑”，找到新出现的 Kindle 盘。盘符不固定，不一定是 G:。
3. 打开后应该能看到 `documents` 等文件夹。
4. 在电脑新建“Kindle 文件备份”文件夹，把 Kindle 盘中能看到的文件全部复制进去。

**完成标志：电脑中有一份个人文件副本。** 这是用户文件备份，不是系统镜像。没有盘符时，先换一根确认能传数据的线或 USB 接口，不要格式化设备。

## 第二步：下载电脑程序和两个 Kindle 安装包

### 下载电脑程序

1. 打开 [本项目 GitHub 页面](https://github.com/hanzhongyuan/Kindle_DX_Monitor)。
2. 点击绿色 **Code → Download ZIP**。
3. 下载完成，右键 ZIP → **全部提取**。
4. 打开解压后的文件夹，确认能看到 `KindleMessageBoard.exe`、`KindleDisplay.exe`、`app`、`scripts`、`requirements.txt`。
5. 把这个完整文件夹放到容易找到的位置，可以改名为 `KindleDXMonitor`。

后文的 **“项目文件夹”就是直接包含两个 EXE 的那一层**。不要在 ZIP 里直接运行，也不要只复制一个 EXE 到桌面；需要桌面入口时创建快捷方式即可。

想从桌面直接打开：右键 `KindleMessageBoard.exe` → **显示更多选项 → 发送到 → 桌面快捷方式**，把快捷方式改名为“Kindle 留言板”。需要扩展屏时，对 `KindleDisplay.exe` 同样操作并命名为“Kindle 显示器”。以后不要移动原项目文件夹，否则快捷方式会失效。

### 下载 Kindle 安装包

从 [NiLuJe 原作者发布页](https://www.mobileread.com/forums/showthread.php?t=88004) 下载并解压这两个 ZIP。直接附件打不开时，回原作者页面的附件区找同名文件。

| 下载的 ZIP | 解压后选择的安装文件 |
| --- | --- |
| [kindle-jailbreak-0.13.N.zip](https://www.mobileread.com/forums/attachment.php?attachmentid=141327&d=1440341398) | `Update_jailbreak_0.13.N_dxg_install.bin` |
| [kindle-usbnetwork-0.57.N-dx.zip](https://www.mobileread.com/forums/attachment.php?attachmentid=141339&d=1440341468) | `Update_usbnetwork_0.57.N_dxg_install.bin` |

两个文件都选 **`dxg_install`**。USBNetwork 的 ZIP 名字虽然带 `-dx`，里面仍选 `dxg_install`，不要选 `dx_install`。`uninstall` 是卸载文件，这里不用。

本留言板**不需要安装 `kindle-ss` / `linkss` 屏保插件**。已经装过其他屏保插件的设备，请看文末常见问题。

## 第三步：安装 Kindle 组件

已经确认装过这两项且 USB 网络正常的用户可跳过。首次安装一次只放一个 `.bin`。

### 先安装 jailbreak

1. Kindle 接电脑，打开 Kindle 盘。
2. 把 `Update_jailbreak_0.13.N_dxg_install.bin` 放在盘最外层，**与 `documents` 同一层**，不要放进文件夹。
3. 复制完成，Windows 右键 Kindle 盘 → **弹出**，再拔线。
4. Kindle 按 **Home → Menu → Settings → Menu → Update Your Kindle**，确认更新。
5. 等待操作结束，设备回到正常界面。

**仅这个 jailbreak 在 2.x 上出现“更新失败”及 U006，是作者说明的预期结果。** 不要把其他更新包的失败也当作成功。[原作者说明](https://www.mobileread.com/forums/showthread.php?t=88004)

### 再安装 USBNetwork

1. 重新接线，把 `Update_usbnetwork_0.57.N_dxg_install.bin` 放到 Kindle 盘最外层。
2. 弹出、拔线，再进入 **Home → Menu → Settings → Menu → Update Your Kindle**。
3. 等待更新结束并返回书籍列表。
4. 重新接电脑，正常安装后应能看到 `usbnet` 文件夹。

用 **Update Your Kindle** 安装，不要用 **Restart** 代替。更新选项是灰色时，检查文件名、存放位置、是否拔线，以及是否在 Settings 页再次按了 Menu。

保持 USBNetwork 默认配置，不启用 Wi-Fi 或 OpenSSH 模式。本程序沿用默认 USB 地址和 USB 下的空密码登录方式；改过认证方式的设备需要单独适配。

## 第四步：切换 USB 网络模式

1. Windows 弹出 Kindle 盘并拔线。
2. Kindle 按 **Home** 回书籍列表。
3. 直接按实体键盘任意字母，底部会弹出搜索栏；删除刚输入的字母。
4. 按顺序逐条输入下面三行，每行提交后再输下一行：

```text
;debugOn
`usbNetwork
;debugOff
```

- 分号、反引号：按 **Sym** 打开符号表，用五向键选择、中键确认。
- 第二行开头是 **反引号 `**，不是单引号 `'` 或波浪号 `~`。
- `On`、`Off` 的 O 和 `Network` 的 N 要大写，不能只输入 `;debug`。
- 输入后按回车提交；若停留在搜索栏，用五向键选中搜索执行项，再按中间确认。
- 命令可能没有成功提示，以重新插线后电脑识别结果为准。

5. 三行提交完毕，再插线。

**完成标志：电脑出现 USB 网络设备，不再显示 Kindle 存储盘。** 这不是书籍丢失。这条命令是开关，已经进入网络模式时不要反复输入。实体键盘与搜索栏操作见 [Amazon DX 手册](https://s3.amazonaws.com/kindle/KindleDX_Users_Guide.pdf)，2.x 切换命令见 [USBNetwork 作者说明](https://www.mobileread.com/forums/showthread.php?t=88004)。

## 第五步：设置 Windows 的 USB 网卡

### 确认设备和驱动

1. 右键 Windows 开始按钮 → **设备管理器**。
2. 在“网络适配器”里找 **RNDIS、Remote NDIS、USB Ethernet / Ethernet Gadget** 相关设备。
3. 不确定是哪一项时，观察拔掉 Kindle 线后哪一项消失、重新插上哪一项出现。

正常显示为网卡、没有黄色警告时，直接进行下一小节。如果新增 RNDIS 设备有黄色警告：

1. 只右键这个与 Kindle 插拔对应的设备 → **更新驱动程序 → 自动搜索驱动程序**。
2. 未找到时，返回选择 **浏览我的电脑以查找驱动程序 → 让我从计算机上的可用驱动程序列表中选取**。
3. 如果出现类别，选 **网络适配器**。列表中有微软的 **Remote NDIS Compatible Device / 远程 NDIS 兼容设备**时，选择对应项继续。
4. 没有该项、出现不兼容提示或安装后仍报错时，先停下，记录“设备属性 → 详细信息 → 硬件 ID”。不要强装不兼容驱动、禁用签名，或把别的设备改成网卡。

Windows 版本不同，列表名称可能不同。微软提供 Remote NDIS 驱动，但具体设备仍需正确匹配。[微软说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/remote-ndis--rndis-2)

### 只修改 USB 网卡的 IPv4

1. 按 **Win+R**，输入 `ncpa.cpl`，按 Enter。
2. 找到刚出现的 USB RNDIS 网络连接，名称可能是“以太网 2/3/…”，编号不固定。
3. 右键 → **属性** → **Internet 协议版本 4 (TCP/IPv4)** → **属性**。
4. 选“使用下面的 IP 地址”，填写：

| 项目 | 内容 |
| --- | --- |
| IP 地址 | `192.168.2.1` |
| 子网掩码 | `255.255.255.0` |
| 默认网关 | 留空 |
| DNS | 不填写，保持自动获取即可 |

5. 点“确定”保存。

**不要修改电脑平时上网的 Wi-Fi 或有线网卡。** USB 连接显示“无 Internet”正常，它只用于连接 Kindle。Kindle 默认地址为 `192.168.2.2`，无需另设。[Windows 网络设置说明](https://support.microsoft.com/zh-CN/Windows/Experience/Connectivity-Networking/essential-network-settings-and-tasks-in-windows)

## 第六步：安装电脑运行环境

### 安装 Python

1. 打开 [Python 官方 Windows 下载页](https://www.python.org/downloads/windows/)。
2. 找到 **Python 3.13.x**，下载 **Windows installer (64-bit)**。不要选 embeddable package、源码或 ARM64。
3. 运行安装程序，保留 Python launcher（`py`）选项，勾选 **Add python.exe to PATH**，点击 **Install Now**。
4. 完成后关闭安装窗口。

Windows 11 通常已有桌面界面需要的 .NET Framework。EXE 明确提示缺少该组件时，从 [Microsoft 官方页面](https://dotnet.microsoft.com/en-us/download/dotnet-framework) 安装所需 Runtime。

### 复制两条命令

1. 在资源管理器中打开 **项目文件夹**，即能看到两个 EXE 的那一层。
2. 点击顶部地址栏，输入 `powershell`，按 Enter。Windows PowerShell 会在这个文件夹打开。
3. 复制第一条命令，粘贴并按 Enter，等执行结束：

```powershell
py -3.13 -m venv .venv
```

4. 再执行第二条：

```powershell
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

这一步需要联网。不需要激活环境，也不需要修改 PowerShell 执行策略。

**完成标志：项目里出现 `.venv` 文件夹，命令结束且没有报错。** 标准留言板安装不需要编辑 `kindle-display.local.json`。

找不到 `py` 时，先关闭并重新打开 PowerShell；仍不行就检查 Python launcher 是否安装。找不到 `requirements.txt` 时，说明窗口不在正确的项目文件夹。

## 第七步：首次登记自己的 Kindle

这一步让程序记住自己这台设备的公钥，以后会核对。已有可用的 `runtime/kindle_known_hosts` 时直接跳过，不要覆盖旧记录。

1. 确认上一步依赖已经装好。
2. 暂时关闭电脑 Wi-Fi、拔掉电脑上网网线，**只保留自己手边这台 Kindle 的 USB 连接**，避免与同地址的其他设备混淆。
3. Kindle 保持唤醒和 USB 网络模式。在项目目录的 PowerShell 中，一次复制并执行下面整个代码框，包括首尾各行：

```powershell
& {
if (Test-Path .\runtime\kindle_known_hosts) { throw '已登记过设备，请保留原记录，不要重复此步骤。' }
@'
import base64, hashlib, socket
from pathlib import Path
import paramiko

folder = Path("runtime")
folder.mkdir(exist_ok=True)
trusted = folder / "kindle_known_hosts"
candidate = folder / "kindle_known_hosts.candidate"
if trusted.exists():
    raise SystemExit("Existing trusted key: stop without replacing it.")
candidate.unlink(missing_ok=True)
with socket.create_connection(("192.168.2.2", 22), timeout=10,
                              source_address=("192.168.2.1", 0)) as sock:
    with paramiko.Transport(sock) as connection:
        connection.start_client(timeout=10)
        key = connection.get_remote_server_key()
        fingerprint = base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip("=")
        candidate.write_text("192.168.2.2 " + key.get_name() + " " + key.get_base64() + "\n", encoding="ascii")
        print("Kindle key fingerprint: SHA256:" + fingerprint)
        print("Candidate saved. Confirm the connected device before trusting it.")
'@ | .\.venv\Scripts\python.exe -
if ($LASTEXITCODE -ne 0) { throw '登记未完成，请检查 USB 网络，不要继续使用旧候选文件。' }
if (-not (Test-Path .\runtime\kindle_known_hosts.candidate)) { throw '没有生成候选公钥，请检查前面的错误。' }
$confirmation = Read-Host '确认仅直连自己的 Kindle；若有以前的可信指纹请先核对。输入 YES 保存，否则回车取消'
if ($confirmation -ceq 'YES') {
    if (Test-Path .\runtime\kindle_known_hosts) { throw '已有可信记录，未覆盖。' }
    Move-Item -LiteralPath .\runtime\kindle_known_hosts.candidate -Destination .\runtime\kindle_known_hosts -ErrorAction Stop
    Write-Host '设备已登记，可以恢复电脑上网连接。'
} else {
    Write-Host '已取消，未登记设备。'
}
}
```

4. 出现 `SHA256:` 开头的一串字符后，确认连接的是自己的 Kindle；有此前可信指纹时先核对。按提示输入大写 `YES`，按 Enter。
5. 看到“设备已登记”后，可以重新打开 Wi-Fi、接回上网网线。

**完成标志：`runtime` 里有 `kindle_known_hosts` 文件，没有 `.txt` 后缀。** 这里只采集设备公钥，不安装登录密钥、不改密码。首次信任依靠你确认的物理直连，并不是已自动独立验证了设备；此后程序会严格核对同一公钥。不要复制别人的公钥文件。[所用接口说明](https://docs.paramiko.org/en/stable/api/transport.html#paramiko.transport.Transport.get_remote_server_key)

超时或报错时先检查 USB 地址、设备是否唤醒、是否误切回存储模式。出现错误后不要继续确认。

## 第八步：发送第一份留言

1. 双击 **KindleMessageBoard.exe**，直接进入留言编辑窗口。
2. 在一个文本框中输入或粘贴，例如：

```text
今天要做的事
整理桌面
读书三十分钟
给植物浇水
检查明日安排
```

3. 选择需要的行数（1–30 行，默认十行）、本机字体、正文字号（12–120 px）和纵横方向。查看输入框下方的行数提示，不用补满。
4. 点击 **预览**，在右侧查看封面。
5. 点击 **发送到 Kindle**，保持线连接，等待完成提示。

Kindle 会短暂重新载入阅读界面，再显示留言，过程中可能出现启动画面和闪屏。

**完成标志：程序提示已设为休眠封面，Kindle 能看到文字。** 之后可以退出电脑程序、拔掉 USB。只做留言板的人不需要打开 `KindleDisplay.exe`。

## 以后怎样修改、保存、恢复

### 修改内容

唤醒 Kindle → 接 USB 网络 → 打开 `KindleMessageBoard.exe` → 修改上次内容 → 预览 → 发送。

- Ctrl+A 全选、Ctrl+C 复制、Ctrl+V 粘贴，按 Enter 换行。
- 空行也占一行；减少所选行数不会删除文字。超过所选行数时保留全部输入，增加行数或调整内容后才能预览、发送。
- 字体默认为微软雅黑。“自动缩小字号”默认勾选，按行宽和行高适配文字；取消后按指定字号排版，放不下时按提示缩小字号、减少行数或缩短文字。实际效果以预览为准。
- 默认纵向排版，也可选择横向。横向预览为 1200×824 阅读布局，发送后把 Kindle 顺时针转 90° 阅读。行数变化时会重新分配每行的空间。
- 不用重新安装 jailbreak、USBNetwork 或 Python。

### 本地记录

内容自动保存在 **`runtime/messageboard-draft.txt`**，没有发送的修改也会保存。下次打开自动加载；旧版 JSON 草稿兼容读取。

行数、字体、字号、方向和自动缩小设置保存在 **`runtime/messageboard-layout.json`**，下次打开继续使用；PW2 的设置另存于自己的目录。

关闭留言板程序后，可以用记事本编辑这个 UTF-8 文件。不要同时在程序和记事本里改同一份内容，以免互相覆盖。

`runtime/messageboard-state.json` 和 `runtime/kindle_known_hosts` 也必须保留。**备份整个项目文件夹最省事，不要只留一个 EXE，也不要删除 `runtime` 排错。** 私人留言和这些记录不随仓库发布。

### 恢复原封面、处理重启

唤醒设备、连接 USB 网络，在留言板程序点击 **恢复原休眠封面**，等待完成。关闭电脑窗口只退出程序，不撤销已发送图片。

普通休眠和拔线可以保留留言；**Kindle 完整重启会恢复原封面**。重启后若变成存储盘，按第四步切回 USB 网络，再发送一次，不用重复安装 jailbreak。

## 更新程序与常见问题

### 更新时保留原内容

1. 关闭两个程序，备份旧项目文件夹。
2. 下载新版 ZIP 并全部提取。
3. 把新版文件夹**里面的内容**复制到旧项目目录，替换同名程序文件。不要先删除旧目录，也不要嵌套多一层文件夹。
4. 保留 `.venv`、整个 `runtime` 和已有的 `kindle-display.local.json`。
5. 在项目目录再次执行 `.\.venv\Scripts\python.exe -m pip install -r requirements.txt`，再打开程序。

扩展屏用户另保留 `virtual-display-install.json` 和 `scripts/display-baseline.json`。换电脑时要重新创建 Python 环境，不要直接复用旧电脑的 `.venv`。

### 按现象处理

| 现象 | 先做什么 |
| --- | --- |
| 只充电，没有盘也没有网卡 | 换支持数据的线，确认是否已切过 USB 网络模式 |
| 不知道在哪输入命令 | 拔线、按 Home，直接按实体键盘，底部会出现输入栏 |
| `;debug` 搜不到结果 | 输入完整的 `;debugOn`，第二条使用反引号，大小写照抄 |
| USB 网卡无 Internet | 正常，这条连接不负责上网 |
| 找不到 Python | 检查是否在项目根目录完成创建 `.venv` 的命令 |
| 缺少 `paramiko` 或 `PIL` | 在项目目录重新执行安装 `requirements.txt` 的命令 |
| 缺少主机公钥 | 首次用户完成第七步；老用户保留旧记录并确认设备 |
| 主机密钥发生变化 | 暂停发送，先确认是否接错设备、重装过 USBNetwork 或拿错项目记录，不要关闭验证 |
| 显示会话仍在运行 | 在 KindleDisplay.exe 点“断开连接”，完成后回留言板发送 |
| 检测到 `linkss` | 按该屏保插件原作者的卸载流程处理，不能只删除文件夹 |
| 设备目录与本机记录不匹配 | 找回原来的 `runtime/messageboard-state.json`，不要删除设备目录绕过检查 |
| 发送中断或长时间未完成 | 等当前操作结束，记录错误，确认 USB 网络后重试，不要连续反复点按钮 |
| 完整重启后封面恢复原样 | 正常，切回 USB 网络后重新发送 |

遇到和指南不同的型号、菜单或设备，不要猜着安装其他型号的文件。提问时提供型号、固件、卡在哪一步及错误文字即可，无需公开完整序列号或私人留言。
