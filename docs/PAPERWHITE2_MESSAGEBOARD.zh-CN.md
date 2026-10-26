# Paperwhite 2 休眠封面留言板

本页对应 **`KindlePaperwhite2MessageBoard.exe`**，用于 **Kindle Paperwhite 2（第六代，PW2）、固件 5.12.2.2**。它把最多十行文字做成休眠时显示的图片，不提供电脑扩展屏。

配置完成后，可以选择两种发送方式：

| 方式 | 日常步骤 |
| --- | --- |
| **无线发送** | 手动唤醒 Kindle → 确认同一 Wi-Fi、拔掉 USB → 编辑并点击“无线发送” → 等书库返回 → 短按电源显示封面 |
| **USB发送** | 接数据线 → 编辑并点击“USB发送” → 安全弹出并拔线 → 等待刷新 → 短按电源显示封面 |

发送完成后，电脑和 Kindle 不需要持续连接。无线功能不会唤醒正在休眠的 Kindle，也不会让设备一直保持在线。

两种方式都使用已经安装好的 **linkss 屏保插件**。USB 方式只需普通存储盘，不需要 USBNetwork；无线方式需额外完成一次设备配对，见第六节。两种方式都不需要虚拟显示器驱动或 TightVNC。第一次仍需完成设备端准备；仅把图片放进原装 Kindle，不能自动变成休眠封面。

## 一、先确认和备份

1. 在 Kindle 打开 **设置 → 设备选项 → 设备信息**，确认机型和固件。这里是 Paperwhite 2，不是 Paperwhite 第十二代或 Kindle DX。
2. 充电到至少一半，用能传数据的 USB 线接电脑。
3. 在 Windows 的“此电脑”中打开 Kindle 盘。盘符由电脑分配，不一定是 G:。
4. 在电脑另建一个备份文件夹，将 Kindle 盘里的个人文件复制进去。资源管理器可打开“查看 → 显示 → 隐藏的项目”以便完整复制。
5. 记下设备当前是否已越狱、能否打开 KUAL，以及是否已有 `linkss` 文件夹。

这份备份保存可见的用户文件，不是完整系统镜像。不要为运行留言板升级或降级固件，也不要套用 DX 的安装包。

## 二、首次设备准备：先完成适合自己的越狱流程

**已经能正常打开 KUAL、使用 MRPI，且 linkss 能显示自定义图片的用户，可以跳到第四节。** 其他用户先阅读这一节，不能仅创建几个同名文件夹来代替安装。

### 未越狱的原装设备

1. 打开 [KindleModding 的越狱说明](https://kindlemodding.org/jailbreaking/)，再使用其 [兼容性向导](https://kindlemodding.org/jailbreak-wizard.html)。
2. 如实选择 **Paperwhite 2 / 6th Generation** 和 **5.12.2.2**，按向导给出的当前方法操作。
3. 在需要 Wi-Fi、账号注册、文件复制或防止自动更新的步骤，按所选方法原作者的说明完成，不要把不同教程的步骤拼在一起。
4. 完成后继续该方法自己的后续配置，直到实际能运行其指定的程序入口或命令，再安装本页需要的屏保组件。

**5.12.2.2 不适用旧的 KindleBreak。** 不能因为它的名字看起来小于 5.13.3，就下载 KindleBreak 的旧包。原作者给出的支持固件表没有这个版本；WatchThis 的作者则明确列出了 PW2 / 5.12.2.2。[KindleBreak 原帖](https://www.mobileread.com/forums/showthread.php?t=338268)、[WatchThis 原帖](https://www.mobileread.com/forums/showthread.php?t=346037)

### 当前需要知道的 PW2 问题

截至 **2026-10-25**，WinterBreak2 的作者仓库仍有 PW2 / 5.12.2.2 后续组件未正常工作的[公开问题 #8](https://github.com/KindleModding/Winterbreak2/issues/8)。因此，书库里出现 **JAILBROKEN** 文档，不能单独证明准备完成。按当前官方流程检查命令或脚本入口是否真的能运行；只有文字提示而功能不工作时，应先排查该流程。

另一个[问题 #64](https://github.com/KindleModding/WinterBreak/issues/64)中，维护者提醒混合新旧组件可能造成冲突。本指南不提供替换越狱载荷或重复叠装 Hotfix 的快捷修复。遇到这些现象时，保留错误和日志，按对应作者的当前处理说明解决，再往下进行。

### Hotfix 应该怎么处理

- 如果所选现代越狱流程已完成其内置后续配置，沿用该流程；不要额外套用旧教程的 Hotfix。
- 如果原作者明确要求安装 Hotfix，使用该流程指定的包和步骤。传统流程可参考 [Legacy Hotfix 说明](https://kindlemodding.org/jailbreaking/Legacy/post-jailbreak/setting-up-a-hotfix/)。它使用 `Update_hotfix_universal.bin`，但不是所有新旧越狱都需要再次安装这个文件。
- 安装失败、错误 007 或菜单不符合教程时，先停止重复安装。它们不能像 DX 某些 jailbreak 的 U006 那样视作正常成功。

### 可选进阶路线：WatchThis

[WatchThis 原作者教程](https://www.mobileread.com/forums/showthread.php?t=346037)明确支持本机型与固件，但它从 **恢复出厂设置** 开始，并要求设置为 English (United Kingdom)、进入演示模式，再使用与型号和固件完全匹配的载荷。

**恢复出厂会清除设备上的个人设置和内容。** 不要把它当作普通“重启”，也不要在没有备份、没有决定采用整套流程时执行。本页不自动运行这条路线；选择它时完整遵循原帖，包括退出演示模式的专用 Hotfix。

## 三、安装 KUAL、MRPI 和 linkss

KUAL 是在 Kindle 上打开插件菜单的入口，MRPI 是插件安装器，linkss 负责自定义休眠图片。电脑上的留言板只负责生成和复制图片。

### 3.1 准备能够正常运行的 KUAL 和 MRPI

先确认上一节的越狱及后续配置已经完成，再按适合该环境的安装说明操作。

对于已有脚本入口支持的环境，[KindleModding 当前的 KUAL / MRPI 说明](https://kindlemodding.org/jailbreaking/Legacy/post-jailbreak/installing-kual-mrpi/)给出以下文件布局：

1. 下载该页面链接的 **MRPI for modern devices**，解压，将 `extensions` 和 `mrpackages` 两个文件夹复制到 Kindle 盘最外层，与 `documents` 并列。已有文件夹时合并内容，不要替换整个文件夹。
2. 下载页面提供的 [PEKI.zip](https://github.com/KindleTweaks/PEKI/releases/latest/download/PEKI.zip)，解压，将 `KUAL.sh` 和 `KUAL.jar` 放进 `documents`。
3. 安全弹出并拔线，在 Kindle 本地书库中打开 **KUAL**。
4. 确认能进入菜单，并能看到 **Helper → Install MR Packages**。

如果 `KUAL.sh` 根本不显示为可运行入口，先回到所选越狱流程排查，不要仅凭文件已复制就认为安装完成。

已经使用传统 Hotfix / MRPI 环境的设备，也可按 [KUAL 原作者说明](https://www.mobileread.com/forums/showthread.php?t=203326)安装 **Booklet** 版本。作者的 [KUAL coplate 压缩包](https://storage.gra.cloud.ovh.net/v1/AUTH_2ac4bfee353948ec8ea7fd1710574097/mr-public/KUAL/KUAL-c6ac782-20250419.tar.xz)中，普通安装文件是 **`Update_KUALBooklet_c6ac782_install.bin`**；将其放进 `mrpackages`，拔线后在主页搜索栏执行 `;log mrpi`。不要误用同包内的卸载文件。**固件 5.12.2.2 不应使用旧的 `KUAL-KDK-2.0.azw2` 方法。** 使用哪一种入口应与现有环境一致，不要重复安装两套。

安装器的文件布局和菜单操作见 [MRPI 原作者说明](https://www.mobileread.com/forums/showthread.php?t=251143)。

### 3.2 下载正确的屏保包

从 [NiLuJe 原作者快照页](https://www.mobileread.com/forums/showthread.php?t=225030)的 **Packages targeting the Kindle 5** 区域下载 **ScreenSavers Hack**。

本指南核对的文件为：

| 项目 | 文件 |
| --- | --- |
| 压缩包 | [kindle-linkss-0.25.N-r18981.tar.xz](https://storage.gra.cloud.ovh.net/v1/AUTH_2ac4bfee353948ec8ea7fd1710574097/mr-public/Touch/kindle-linkss-0.25.N-r18981.tar.xz) |
| 解压后使用 | `ScreenSavers/Update_linkss_0.25.N_install_pw2_and_up.bin` |
| 不使用 | `Update_linkss_0.25.N_install_touch_pw.bin`、`Update_linkss_0.25.N_uninstall.bin` |

`.tar.xz` 是压缩格式，可以用 [7-Zip](https://www.7-zip.org/)打开；可能需要先解出 `.tar`，再打开一次。这里需要的是里面的安装 `.bin`，不是把整个压缩包复制过去。

只显示本程序生成的图片，不需要安装 Kindle 端 Python 包。屏保作者的 Python 依赖用于提取电子书封面的 **cover mode**，本程序使用普通图片模式。[屏保作者说明](https://www.mobileread.com/forums/showthread.php?t=195474)

### 3.3 在 Kindle 安装 linkss

1. 连接 Kindle，把 **`Update_linkss_0.25.N_install_pw2_and_up.bin`** 复制到 Kindle 盘的 **`mrpackages`** 文件夹。
2. Windows 右键 Kindle 盘 → **弹出**，然后拔掉 USB 线。
3. Kindle 打开 **KUAL → Helper → Install MR Packages**，等待安装结束、界面恢复。
4. 短按电源让 Kindle 休眠；新安装时，应出现屏保插件自己的示例图片。
5. 再接回电脑，应能看到 `linkss`、`linkss/screensavers` 等组件目录。

仅存在一个 `linkss` 文件夹不能证明插件已安装；要以设备上的安装结果和屏保效果为准。报错时记录错误文字，不要自行创建同名目录绕过程序检查。

如果设备休眠时显示 Amazon 的 Special Offers 广告，该插件不会替换这种广告封面。先按 Amazon 的设备管理方式处理订阅状态，再使用自定义图片。[作者的适用范围说明](https://www.mobileread.com/forums/showthread.php?t=195474)

### 3.4 选择普通图片模式

如果插件曾被停用，先在 **KUAL → Screen Savers → Enable ScreenSavers Hack (Default)** 重新启用。已经启用时，这个选项会隐藏。

在 Kindle 打开 **KUAL → Screen Savers → Screen Savers Behavior → Image Cycle (Default)**，然后回到 **Screen Savers → Restart framework now**，等待界面重新载入。已经处于默认图片模式时，对应的切换选项可能不显示。

本程序需要已启用的普通图片模式；`cover`（电子书封面）、`last`（最后画面）、`random` 或 `shuffle` 仍开启时，会提示先切换。使用 KUAL 修改模式，不要随意删除整个 `linkss` 文件夹。

## 四、准备 Windows 程序

1. 从 [项目 GitHub 页面](https://github.com/hanzhongyuan/Kindle_DX_Monitor)选择 **Code → Download ZIP**，再“全部提取”。
2. 保留整个项目文件夹，确认其中有 **`KindlePaperwhite2MessageBoard.exe`** 和 `scripts`。不要只把一个 EXE 拖到桌面。
3. 如果这台电脑以前已配置过本项目的 DX 程序，可以沿用已有 Python 环境。
4. 首次配置的电脑，从 [Python 官方 Windows 下载页](https://www.python.org/downloads/windows/)安装 **Python 3.13.x / Windows installer (64-bit)**，保留 Python launcher，并勾选加入 PATH。
5. 用资源管理器打开项目文件夹，在地址栏输入 `powershell` 并按 Enter，依次执行：

```powershell
py -3.13 -m venv .venv
```

```powershell
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

每条执行结束、没有错误后再继续。不需要激活环境，也不需要配置 USB 网卡。

需要桌面入口时，右键 `KindlePaperwhite2MessageBoard.exe` → **显示更多选项 → 发送到 → 桌面快捷方式**，改名为“Kindle PW2 留言板”。

## 五、用 USB 发送第一份留言

1. 唤醒 Kindle，插数据线，确认“此电脑”里能打开 Kindle 存储盘。
2. 打开 **`KindlePaperwhite2MessageBoard.exe`**。程序会在发送时识别接入的 Paperwhite 2；如果提示设备不明确，先拔掉其他 Kindle 再试。
3. 在一个文本框里输入或粘贴文字，一行一条，最多十行。可用 `Ctrl+A`、`Ctrl+C`、`Ctrl+V`，按 Enter 换行。
4. 点击 **“预览” → “USB发送”**，等待程序提示文件处理完成。
5. 在 Windows **安全弹出 Kindle 盘**，再拔线。
6. 第一次使用自动刷新、此前未启用 `autoreboot` 时，按程序提示在 **已经拔线的 Kindle** 上执行一次 **设置 → 菜单 → 重启**。这是正常重启，不是恢复出厂。
7. 等 Kindle 回到正常界面，再短按电源让它休眠，查看文字。

程序生成 **758×1024、竖向 PNG**。第一次发送会保留接管前的屏保图片备份，再放入留言图片；不要手工删除程序的备份或记录。程序提示写入完成后，还需在设备上完成刷新与休眠，才能确认最终效果。

## 六、无线发送：先手动唤醒 Kindle

### 一次性无线配置

先完成前面的设备准备，并确认 USB 留言板能使用。无线功能需要额外配置自己的 Kindle；**只填写 IP 地址不会自动完成设备配对**。已经完成无线配置的本机用户可以直接看下一小节。

1. 如果尚未安装无线所需组件，在 [NiLuJe 快照页](https://www.mobileread.com/forums/showthread.php?t=225030)的 **Packages targeting the Kindle 5** 区域下载 **`kindle-usbnet-0.22.N-r19297.tar.xz`**。解压后只使用 **`Update_usbnet_0.22.N_install_pw2_and_up.bin`**，不要用 Kindle 2 / DX 的旧包。
2. 按第三节相同的 MRPI 方法，将这个 `.bin` 放进 Kindle 的 `mrpackages`，安全弹出并拔线，在 **KUAL → Helper → Install MR Packages** 安装，等书库回来。已经正确安装的设备不用重复安装。
3. 在 Kindle **开启飞行模式**，再用数据线连接自己的电脑，确认“此电脑”里出现 Kindle 存储盘。
4. 用文件资源管理器打开本项目文件夹，在地址栏输入 `powershell`，按 Enter，然后执行：

```powershell
.\.venv\Scripts\python.exe .\scripts\pw2-wireless-setup.py
```

5. 等待脚本提示配置完成。它会识别这台 PW2，并保存本机与设备的配对记录；不需要手工编辑连接文件。命令报错时先按提示处理，不要继续下一步。
6. 在 Windows 安全弹出 Kindle，拔线，然后在 Kindle 的设置菜单**完整重启一次**。
7. 等书库回来，关闭飞行模式，将 Kindle 连接到与电脑相同的 Wi-Fi。
8. 在 Kindle 主页搜索栏输入 **`;711`**，查看网络信息里的 IP 地址；若没有显示，可以在路由器的已连接设备列表里找到这台 Kindle。打开电脑的 PW2 留言板，填写 **“Kindle IP 地址”**，点击 **“保存地址”**。

如果已经知道 Kindle 的地址，第四步也可以执行下面的命令，将示例地址替换为自己的地址；以后仍可在窗口里修改：

```powershell
.\.venv\Scripts\python.exe .\scripts\pw2-wireless-setup.py --host 192.168.1.25
```

首次配置由 [`scripts/pw2-wireless-setup.py`](../scripts/pw2-wireless-setup.py)完成；参数说明可在同一条命令末尾使用 `--help` 查看。不要拿别人的无线配置覆盖自己的文件。完成后日常使用无需数据线，也无需重复运行这个脚本。

### 无线配对文件丢了怎么办

仅无线连接记录丢失、换电脑或需要重新配对时，可以重新用 USB 连接这台 Kindle，按上面的流程运行配置脚本。它会重新建立无线配对，**不需要重新越狱，也不会删除留言图片**。

仍要保留原来的 **`runtime/paperwhite2`**，尤其是 `devices` 里的封面备份记录，以及 Kindle 上的 `linkss/pw2-messageboard-backup`。重新配对不能重建丢失的封面备份记录；换电脑时，先保留这些记录，再创建新电脑的 Python 环境和无线配对。

### 每次怎样发送

1. 先在 Kindle 上**短按电源唤醒**，等书库出现，并确认 Wi-Fi 已连接。
2. 让电脑和 Kindle 连接**同一个 Wi-Fi**。无线使用时拔掉 USB 数据线，不能停留在 Kindle 的 USB 存储画面。
3. 打开 **Kindle PW2 留言板**，确认 **“Kindle IP 地址”**。安装时已填写正确地址的话，不需要每次修改。
4. 在十行文本框编辑文字，点 **“预览” → “无线发送”**。
5. 等程序提示完成，再等 Kindle 的书库重新出现，最后**短按电源**显示新的留言封面。

发送过程中不要提前让 Kindle 休眠。界面短暂回到 Home 属于重载封面的过程；无线方式不需要执行 USB 的“安全弹出”。程序不会自动按电源，也不会在 Kindle 休眠后继续保持连接。

### IP 地址改变了怎么办

更换 Wi-Fi 或路由器重新分配地址后，旧地址可能失效。在 Kindle 主页搜索 **`;711`** 查看网络信息，或在路由器的已连接设备列表里找到这台 Kindle 的当前 IP，将它填入程序，再点 **“保存地址”**。格式例如 `192.168.1.25`，这个数字只是示例，要使用自己的设备地址。

程序会记住地址，下次打开自动填入；点击无线发送或无线恢复时，也会保存当前填写的地址。这里只修改地址，原来的配对记录会保留。本功能用于同一局域网，不需要设置外网访问或路由器端口转发。

## 七、以后怎样更新文字

使用无线时，按第六节先唤醒 Kindle，再点击 **“无线发送”**。使用数据线时：接线 → 打开 PW2 留言板 → 修改上次文字 → 预览 → **“USB发送”** → 安全弹出并拔线。

USB 的自动刷新已启用时，拔线后约十秒会触发 Kindle 阅读界面的重新载入。等待书籍列表恢复，再短按电源休眠。**自动刷新不等于自动休眠。** USB 存储模式下，程序无法直接确认屏幕上已经出现新图片。

如果没有自动刷新，拔线后在 **KUAL → Screen Savers → Restart framework now** 手动刷新，再休眠。其他插件也管理自动重启时，优先采用这条手动方式，不要删除其他插件的文件。

文字自动保存为电脑项目中的 **`runtime/paperwhite2/messageboard-draft.txt`**；未发送的修改也保留。下次打开继续编辑，与 DX 的留言草稿分开。超过十行或单行太长时，按程序提示调整后再发送。窗口可以最小化到任务栏，点“关闭”保存草稿并退出。

## 八、恢复和更新

恢复的是 **本程序第一次接管前保存的屏保图片**，不是卸载 linkss。可以选择：

- **“无线恢复封面”**：先唤醒 Kindle，确认同一 Wi-Fi 并拔掉 USB，再点按钮。完成后等书库回来，再短按电源查看封面。
- **“USB恢复封面”**：接上数据线，确认 Kindle 存储盘出现，再点按钮。安全弹出并拔线后，按程序提示重载封面，再短按电源查看。

恢复时会撤回本程序新启用的自动刷新设置；原本已有的设置予以保留。需要手动重载时，在 **KUAL → Screen Savers → Restart framework now** 操作。

以下文件要一并保留，不能只备份文本：

| 位置 | 内容 |
| --- | --- |
| 电脑 `runtime/paperwhite2/messageboard-draft.txt` | 上次编辑的文字 |
| 电脑 `runtime/paperwhite2/wireless.json` | 无线地址和这台设备的配对记录；无线用户应保留整个 `runtime/paperwhite2` 目录 |
| 电脑 `runtime/paperwhite2/devices/<设备标识哈希>/owner.json` | 本机管理这台设备的记录；目录名由程序生成，不用手填 |
| Kindle `linkss/pw2-messageboard-backup/original` | 接管前的屏保图片 |
| Kindle `linkss/pw2-messageboard-backup/manifest.json` | 对应的备份控制记录 |
| Kindle `linkss/pw2-messageboard-backup.preparing-*`（如果出现） | 初次备份中断留下的准备目录；保留它，程序会在确认原图未变后续建 |

恢复后备份仍保留，供之后再次发送或恢复使用，不需要手工清空。

关闭电脑窗口不撤销已经发送的图片。要完全移除 linkss，应按插件原作者的卸载方法处理，而不是直接删除文件夹。

更新电脑程序前，关闭窗口并备份整个项目目录，将新版文件覆盖进去，保留 **`runtime/paperwhite2`**、Python 环境和本机配置。Kindle 上的备份也应保留。私人留言和本机记录不随 GitHub 仓库发布。

## 常见问题

| 现象 | 处理 |
| --- | --- |
| USB发送时只有充电，没有 Kindle 盘 | 换支持数据的线，检查 USB 接口；USB发送需要存储模式 |
| 无线发送提示连接不到 Kindle | 先手动唤醒、确认同一 Wi-Fi、拔掉 USB，再核对 IP 地址 |
| 无线地址正确，仍提示未配置 | 首次无线配对尚未完成，先完成本节的设备配置；填写地址不能代替配对 |
| 无线发送后仍是旧图片 | 等书库重新出现，再短按电源；若程序报错，按提示处理后再发 |
| 程序没识别出 PW2 | 确认型号、固件和连接状态，不要手工把其他盘伪装成 Kindle |
| 提示缺少 linkss | 先完成第三节安装，单纯创建文件夹没有作用 |
| 提示屏保插件未启用 / 缺少 auto | 在 KUAL 的 Screen Savers 中选择 Enable ScreenSavers Hack (Default)，再重载 |
| 提示 cover / last / random / shuffle 模式 | 用 KUAL 切回 Image Cycle (Default)，刷新后重新连接 |
| 已出现 JAILBROKEN 文档，但后续命令只执行搜索 | 回到所选越狱方法的故障说明，不能认为准备完成 |
| USB发送后还是旧图片 | 安全弹出拔线；首次启用自动刷新需重启，或用 KUAL 手动刷新 |
| 拔线后回到 Home | 阅读界面重载可能出现这个过程；等恢复后再短按电源休眠 |
| 图片格式或尺寸错误 | 使用本程序生成的 758×1024 PNG，不要套用 DX 图片 |
| 恢复时提示记录或备份不匹配 | 保留现场和备份，不要删除记录后强行覆盖 |

无线发送需要已唤醒的设备，不提供远程唤醒或外网发送。本次无线代码更新未进行实际发送和恢复测试，请在首次使用时确认封面结果。
