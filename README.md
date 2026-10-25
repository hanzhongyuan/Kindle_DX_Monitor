# Kindle DX Monitor

让旧 **Kindle DX Graphite（B009，固件 2.5.8）**继续发挥作用，提供两个独立的 Windows 程序。

| 程序 | 用途 | 日常操作 |
| --- | --- | --- |
| **KindleMessageBoard.exe — Kindle 留言板** | 最多十行文字的休眠封面 | 编辑、预览、发送；成功后可关闭程序并拔线 |
| **KindleDisplay.exe — Kindle 显示器** | Windows 独立扩展屏 | 连接后把窗口拖过去，支持四个方向 |

两个程序各有自己的窗口和任务栏入口。留言板适合待办、提醒和长期保留的文字；扩展屏有墨水屏刷新延迟，适合静态内容，不适合视频。

## 第一次使用

**没有编程经验，先按 [新手上手指南：从原装 Kindle 到留言板](docs/BEGINNER_GUIDE.zh-CN.md) 操作。**

指南从确认型号开始，逐步说明下载哪个文件、在 Kindle 上按什么键、在 Windows 里点哪里、复制哪些命令，以及完成后应该看到什么。

- 只用留言板：不需要虚拟显示驱动、TightVNC、PowerShell 7 或 Kindle VNC 客户端。
- 想把电脑窗口拖到 Kindle 上：基础配置完成后，再看 [扩展显示器设置指南](docs/EXTENDED_DISPLAY.zh-CN.md)。

下载方式：本页点击 **Code → Download ZIP**，再“全部提取”。使用完整解压目录，不能只拿走一个 EXE。首次配置仍需要 Python 等依赖，具体操作已写入指南。

## 配置好后的日常操作

### Kindle 留言板

1. 唤醒 Kindle，接上 USB 数据线，保持 USB 网络模式。
2. 双击 **KindleMessageBoard.exe**，直接进入留言编辑窗口。
3. 在一个多行文本框里编辑，一行一条，支持 **Ctrl+A / Ctrl+C / Ctrl+V**。
4. 点击“预览”，再点“发送到 Kindle”，等待完成提示。

超过十行会提示调整，文字不会被截掉；长行会缩小字号，仍放不下会提示。空行可保留，不能发送全部为空的内容。

编辑自动保存为本机 **`runtime/messageboard-draft.txt`**，未发送的修改也会保存。下次打开继续编辑；旧版 JSON 草稿可兼容迁移。

发送后可关闭程序、拔掉 USB，留言保留在休眠封面。“恢复原休眠封面”可撤销。**Kindle 完整重启后需重新发送；普通休眠和拔线可保留。** 留言板固定使用竖向 824×1200 图片，不会自动同步网络通知。

### Kindle 显示器

1. 双击 **KindleDisplay.exe**，选择 0°、90°、180° 或 270°，点击“连接”。
2. Windows 保持“扩展这些显示器”，把窗口拖到 Kindle 对应的屏幕。
3. 换方向后点“应用方向”；需要重新加载时点“刷新”。
4. 用完点“断开连接”或“退出”。最小化会继续传输。

切到留言板时，**先在显示器程序断开连接，再到留言板发送**。留言板不会自动启停显示器。关闭留言板窗口只保存文字并退出，已发送的休眠图片仍保留。

两个窗口可以同时打开。发送或恢复留言期间，显示器的设备操作会提示等待；显示器连接中或正在重连时，留言板会提示先断开。

## 更新时保留自己的内容

关闭两个程序，先备份旧项目文件夹，再把新版文件覆盖到原目录。**保留原来的 `runtime`，不要删除旧目录后重新下载。** 有依赖更新时，在项目目录执行：

```powershell
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

| 本机文件 | 内容 |
| --- | --- |
| `runtime/messageboard-draft.txt` | 上次编辑的留言 |
| `runtime/messageboard-state.json` | 本程序管理 Kindle 留言图片的归属记录，需一并保留 |
| `runtime/kindle_known_hosts` | 自己的 Kindle 主机公钥 |
| `kindle-display.local.json` | 可选的本机路径配置 |
| `virtual-display-install.json`、`scripts/display-baseline.json` | 扩展屏设备记录，扩展屏用户需保留 |

这些本机记录已被 `.gitignore` 排除，不随仓库发布。不要用别人电脑的记录覆盖自己的文件。

## 适用范围

- Kindle DX Graphite / B009 / 2.5.8；实测电脑为 Windows 11 x64。其他型号和升级到 3.x 的 DXG 不在本指南范围内。
- 桌面程序为 C# / WinForms；后台使用 Python 3.13、Paramiko、Pillow。
- USB 为电脑 `192.168.2.1` 到 Kindle `192.168.2.2` 的直连网络，沿用 USBNetwork 默认 USB 登录方式。
- 更新留言会短暂重新载入 Kindle 阅读界面再进入休眠，不改写原始封面或系统启动文件。
- 扩展屏竖向 824×1200、横向 1200×824；Windows 的 60 Hz 不代表 Kindle 能以 60 帧刷新。

## 源码与构建

`app/KindleDisplay.cs` 包含两个程序入口。`scripts/kindle-messageboard.py` 负责留言板，其他显示控制脚本负责扩展屏。

开发者在 PowerShell 7 的项目目录执行下列命令，可生成两个 Windows GUI EXE：

```powershell
.\scripts\build-windows-app.ps1
```

仓库中的 Python 离线测试可自行运行：

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s scripts -p 'test_*.py' -v
```

## 第三方项目

本仓库不打包 Kindle 固件、越狱工具、USBNetwork、第三方驱动、TightVNC 或 Kindle VNC 客户端二进制。相关下载和配置步骤见指南，请保留原作者许可文件。

- [NiLuJe legacy Kindle hacks](https://www.mobileread.com/forums/showthread.php?t=88004)
- [hwhw/kindlevncviewer](https://github.com/hwhw/kindlevncviewer)
- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
- [TightVNC](https://www.tightvnc.com/)
- [Paramiko](https://www.paramiko.org/)
- [Pillow](https://pillow.readthedocs.io/)
