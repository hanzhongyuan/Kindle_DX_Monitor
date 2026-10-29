# Paperwhite 2 离线待办：点击完成，再点撤销

这个功能运行在 **Kindle Paperwhite 2 本机的 KOReader** 中。安装一次后，可以断开电脑和 Wi-Fi，直接点击待办：完成时显示删除线，再点一次恢复未完成。

适用目标是 **Paperwhite 2 / 固件 5.12.2.2**。电脑上的 `KindlePaperwhite2MessageBoard.exe` 新增 **“推送待办”**，可经 Wi-Fi 追加事项，保留原有完成状态；电脑草稿与设备清单各自保存，不进行双向同步，也不修改 linkss 或 DX 程序。**已用独立测试清单验证真机安装、无线收件、持久化和重复批次去重；实际触摸和休眠唤醒仍待验证，请按文末步骤检查。**

## 一、准备 KOReader

如果这台 Kindle 已经能正常打开 KOReader，可跳到第二节。否则，需要先完成适合本机的越狱和 KUAL 配置；不要为此升级固件或重复安装其他越狱方案。

截至 2026-10-29，官方安装说明将 PW2、5.12.2.2 对应到 **`kindlepw2`** 包，启动方式可用 KUAL。这里不使用 `kindlehf`、`kindle` 或 `kindle-legacy` 包。机型与固件对应关系见 [KOReader 官方 Kindle 安装说明](https://github.com/koreader/koreader/wiki/Installation-on-Kindle-devices#err-there-are-four-kindle-packages-to-choose-from-which-do-i-pick)。

1. 从 [KOReader v2026.07.1 官方发布页](https://github.com/koreader/koreader/releases/tag/v2026.07.1)下载 **[koreader-kindlepw2-v2026.07.1.zip](https://github.com/koreader/koreader/releases/download/v2026.07.1/koreader-kindlepw2-v2026.07.1.zip)**。本项目的插件 ZIP 不含 KOReader 主程序。
2. 如果 KOReader 正在运行，先从它的菜单退出，回到 Kindle 原生界面，再连接 USB 数据线，让 Windows 显示 Kindle 存储盘。官方不支持在 KOReader 运行时切入 USB 存储模式。[官方 USB 说明](https://github.com/koreader/koreader/wiki/Installation-on-Kindle-devices#usb)
3. 解压官方包，把其中的 `koreader`、`extensions` 等目录合并到 **Kindle 盘最外层**，与 `documents` 并列。不要套进额外一层压缩包名称目录；已有目录应合并，不要删除旧 `koreader` 或它的 `settings`。
4. Windows 安全弹出 Kindle，拔线，在 Kindle 打开 **KUAL → KOReader → Start KOReader**。能进入 KOReader 后再安装待办插件。以上手动复制与 KUAL 步骤对应 [官方 KUAL 安装方法](https://github.com/koreader/koreader/wiki/Installation-on-Kindle-devices#5-manual-copying-and-kual-for-kindle-firmware-5163)。

本次核对的官方 ZIP SHA256 为：

```text
ea1f575c54492a2c679d128b7f3210fd7d6a87e5f5a1ff1f7a7fe2080ff68f86
```

它可与下载文件的 `Get-FileHash -Algorithm SHA256` 结果核对。[官方发布附件](https://github.com/koreader/koreader/releases/expanded_assets/v2026.07.1)

## 二、安装离线待办插件

1. **退出 KOReader 后**，连接 USB，打开 Kindle 存储盘。
2. 在电脑解压项目提供的 **`KindlePaperwhite2Todo.zip`**，把解出的 **`koreader` 文件夹合并到 Kindle 盘最外层**。已有同名插件时，仅覆盖该插件的程序文件。
3. 检查文件层级，至少应有：

   ```text
   Kindle 盘/
   └─ koreader/
      └─ plugins/
         └─ kindletodo.koplugin/
            ├─ main.lua
            ├─ _meta.lua
            ├─ board.lua
            ├─ inbox.lua
            └─ store.lua
   ```

   包内可能还有其他 Lua 界面文件，一并复制。不要把插件放进 `documents`、`extensions` 或 `koreader/koreader`。
4. 安全弹出并拔线。打开 **KUAL → KOReader → Start KOReader**，再从 KOReader 的 **工具 → 更多工具 → 离线待办** 打开列表。

安装完成后，日常点击待办不需要电脑、USBNetwork、Wi-Fi、Python 或 Windows 留言板程序。

## 三、在 Kindle 上使用

| 操作 | 结果 |
| --- | --- |
| 点击一条待办 | 标记完成并显示删除线 |
| 再点已完成待办 | 撤销完成，恢复普通文字 |
| 长按一条待办 | 查看完整内容，并可编辑或删除 |
| 点“新增” | 输入一条新待办并保存 |
| 点“显示/字号”调整字体 | 可选 18、24、28、32、40、48 号 |
| “显示/字号”→“隐藏上下栏” | 隐藏标题、顶部操作栏和底部翻页状态栏，让一页显示更多事项 |
| 精简模式右上角“菜单”→“显示上下栏” | 恢复完整界面，也可新增、接收、调整字号或返回 KOReader |
| 使用翻页按钮 | 查看其他页的待办 |
| 左右滑动 | 左滑下一页，右滑上一页；隐藏上下栏后仍可使用 |

最多保存 200 条，每条最多 300 字。长条目在列表中显示省略号，长按可查看完整内容。完成状态保存在 Kindle 本机，关掉电脑不会影响它。电脑编辑好的清单可通过下面的 Wi-Fi 推送或 USB 文本导入。

隐藏上下栏的选择会保存在 Kindle 本机，退出、重开和接收新事项后继续生效。精简模式右上角只保留一个小“菜单”按钮；第一条会为它留出空间，不遮挡事项，也不会点菜单时误标为完成。页码和翻页按钮移到菜单中，空清单时也能新增或退出。

在 758×1024 的验证界面中，28 号字每页由 7 行增加到 11 行，48 号字由 5 行增加到 8 行。

## 四、从电脑推送或导入多条待办

### Wi-Fi 推送

先安装包含 `inbox.lua` 的新版插件，并退出、重新打开 KOReader。无线连接复用 [PW2 无线配对指南](PAPERWHITE2_MESSAGEBOARD.zh-CN.md)中的配置；已经能从电脑无线发送封面的设备，可继续使用原连接记录和 IP 地址。

1. 唤醒 Kindle，确认电脑与 Kindle 连接同一 Wi-Fi，拔掉 USB。无需让电脑持续连接。
2. 打开电脑上的 **`KindlePaperwhite2MessageBoard.exe`**，检查窗口里的 **Kindle IP 地址**。
3. 在编辑框中输入文字，**每行一项**，空行忽略。每批最多 200 项、每项最多 300 字；待办推送不受封面行数、字号或方向限制，无需先生成封面预览。
4. 点击 **“推送待办”**。**“无线发送”仍然用于屏保封面**，不要用它代替待办推送。
5. 电脑提示送达后，在 Kindle 打开 **工具 → 更多工具 → 离线待办** 自动接收；如果待办窗口已经打开，约 5 秒刷新。电脑若提示“已接收”，表示已确认这批内容进入设备待办记录。

推送会**追加事项，保留原清单和完成状态**，不会重启阅读界面或覆盖静态屏保。待办接收后可以断开 Wi-Fi，继续在 Kindle 本机点击完成或撤销。

**连续重发同一份草稿不会重复添加。改了草稿再推送，会把整份草稿作为新一批追加，包括未修改的行；不是按差异更新。** 只想新增一项时，编辑框里只填写新项。设备清单连同新项总计仍不能超过 200 条；达到上限时先在 Kindle 删除不再需要的事项，再重试同一草稿。

电脑会在 `runtime/paperwhite2` 保留发送记录，用于重试时识别同一批次。更新项目时保留该目录。发送中断后先唤醒 Kindle，再重发原草稿，不要删除发送记录来排错。

### USB 文本导入

1. 退出 KOReader，再接 USB。
2. 在 Kindle 盘的 **`koreader/settings`** 目录中建立 **`kindle-todo-import.txt`**，用 UTF-8 编码保存，每行一条，例如：

   ```text
   读书三十分钟
   整理桌面
   给植物浇水
   ```

3. 安全弹出、拔线，重新打开 KOReader 的 **工具 → 更多工具 → 离线待办**，选择导入操作。
4. 确认导入后，内容会**追加**到现有待办。USB 手动导入不使用无线批次去重，重复导入同一文件会再次追加；导入前确认提示、导入后查看结果。

文档中的 `/koreader/settings/kindle-todo-import.txt` 指 Kindle USB 存储盘内的路径；在 Kindle 系统内部，它位于 `/mnt/us/koreader/settings/kindle-todo-import.txt`。

## 五、休眠和唤醒

**真正进入休眠后，触摸不能切换待办状态。** 使用电源键唤醒，再在待办窗口点击。插件保留正常自动休眠，不会强制 Kindle 常醒。

如果希望休眠后仍能看见待办，先从待办菜单返回 KOReader，展开顶部菜单，进入 **齿轮设置 → 屏幕 → 屏保 → 壁纸**，选择 **“保持屏幕原样 / Leave screen as-is”**。若“休眠中”遮住内容，进入 **屏保 → 屏保显示信息**，取消勾选 **“在休眠屏幕上添加自定义信息 / Add custom message to sleep screen”**。无需重启，下次休眠生效；这只是隐藏叠加文字，仍需按电源键唤醒后才能勾选。这些是 KOReader 自己的显示选项，不修改 linkss，插件不会自动替你启用。[KOReader 官方屏保设置源码](https://github.com/koreader/koreader/blob/v2026.07.1/frontend/ui/elements/screensaver_menu.lua)

预期在 KOReader 保持打开、待办窗口未关闭的情况下，唤醒后回到待办窗口；**这一步仍需 PW2 实机验证**。如果回到了原生书库，重新从 KUAL 打开 KOReader；如果回到 KOReader 文件列表，进入 **工具 → 更多工具 → 离线待办**。

静态休眠封面上的文字仍是一张图片，不能点击。使用待办时以唤醒后的 KOReader 窗口为准。

## 六、保存、备份和更新

个人数据位于 Kindle 盘内：

| 文件 | 内容 |
| --- | --- |
| `koreader/settings/kindle-todo.json` | 待办内容、完成状态和设置 |
| `koreader/settings/kindle-todo.json.bak` | 本机待办数据备份（如有） |
| `koreader/settings/kindle-todo-import.txt` | 准备导入的文字（如有） |
| `koreader/settings/kindle-todo-inbox/` | 从电脑收到、等待待办窗口处理的无线批次（如有） |

备份时先退出 KOReader，再连接 USB，把上述文件复制到电脑。更新插件只需覆盖 `koreader/plugins/kindletodo.koplugin` 中的程序文件，保留 `koreader/settings`；插件安装包不包含任何用户数据。保留原来的 linkss 屏保备份和 Windows 留言板草稿，两者与此处的数据各自独立。

若菜单没有“离线待办”，检查插件路径，完全退出并重新打开 KOReader；仍不出现时，在 KOReader 的插件管理中确认它没有被禁用。报错时保留 `koreader/crash.log` 和个人数据文件，先备份，不要通过删除整个 `settings` 目录排错。

## 七、安装后检查

1. 新增两条测试待办，点击其中一条，确认出现删除线；再次点击，确认可撤销。
2. 编辑一条、删除一条，重新打开待办窗口，检查结果。
3. 退出并重新打开 KOReader，确认内容、完成状态和字号保留。
4. 保持待办窗口打开，让 Kindle 正常休眠，再用电源键唤醒，确认窗口和触摸恢复。
5. 断开 USB 和 Wi-Fi，重复点击完成与撤销，确认可以离线使用。

## 开发者打包

在项目目录用 PowerShell 运行：

```powershell
.\scripts\build-pw2-todo.ps1
```

脚本从 `device/pw2-todo/koreader/plugins/kindletodo.koplugin` 读取顶层 Lua 源文件，生成项目根目录的 `KindlePaperwhite2Todo.zip`。同一份源码重复打包得到相同文件；脚本不读取 Kindle、用户数据或本机 `runtime`，不下载或重新打包 KOReader。

## 开发者验证

存储测试可使用额外安装的测试依赖，不影响插件安装包：

```powershell
python -m pip install --target runtime/test-deps lupa
python -m unittest discover -s scripts -p 'test_pw2_todo_store.py' -v
```

界面验证脚本为 [`scripts/test-pw2-todo-ui.lua`](../scripts/test-pw2-todo-ui.lua)，运行说明见文件顶部。它需要 Linux 版 KOReader，可在 WSL 中使用 SDL dummy 驱动运行。

本次精简模式更新已通过 **68 项待办相关 Python 测试**，以及 **82 项真实 KOReader 运行时的界面断言**；界面验证在 Linux 中模拟 **758×1024** 屏幕，涵盖隐藏栏、菜单恢复、滑动翻页、保存失败和空清单退出。另外，已在 PW2 真机使用独立临时清单，验证 Wi-Fi 收件、追加后保留原有完成状态和字号、重新打开后的持久化以及同批次重放去重；该测试未操作用户的真实待办数据。实际触摸和休眠唤醒仍待验证，不能由这些测试替代。
