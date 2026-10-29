-- Run from an extracted, official KOReader Linux runtime directory:
-- SDL_VIDEODRIVER=dummy SDL_RENDER_DRIVER=software EMULATE_READER_W=758 \
-- EMULATE_READER_H=1024 EMULATE_BW_SCREEN=1 ./luajit /path/to/this.lua \
-- /absolute/path/to/kindletodo.koplugin /absolute/path/to/test-output
-- Uses real KOReader widgets, event dispatch, fonts, framebuffer and JSON store.
-- Creates a fresh profile below test-output; never reads an existing user profile.
io.stdout:setvbuf("line")
local plugin_path, output_root = arg[1], arg[2]
assert(plugin_path and plugin_path:sub(1, 1) == "/", "Supply an absolute plugin directory")
assert(output_root and output_root:sub(1, 1) == "/", "Supply an absolute output directory")
require("setupkoenv")
local ffi = require("ffi")
ffi.cdef[[int setenv(const char *name, const char *value, int overwrite);]]
local lfs = require("libs/libkoreader-lfs")
assert(lfs.attributes(output_root, "mode") == "directory", "Create the output directory first")
local run_dir = output_root .. "/ui-" .. os.date("%Y%m%d-%H%M%S")
assert(lfs.mkdir(run_dir), "Use a fresh test run directory")
local profile = run_dir .. "/profile"
assert(lfs.mkdir(profile))
assert(ffi.C.setenv("KO_HOME", profile, 1) == 0)
G_defaults = require("luadefaults"):open()
local DataStorage = require("datastorage")
G_reader_settings = require("luasettings"):open(DataStorage:getDataDir() .. "/settings.reader.lua")
G_reader_settings:saveSetting("language", "zh_CN")
-- The Linux emulator has a physical keyboard; explicitly enable the real soft
-- keyboard to exercise the touch-only PW2 interaction path as well.
G_reader_settings:saveSetting("virtual_keyboard_enabled", true)
require("gettext").changeLang("zh_CN")
local Device = require("device")
require("document/canvascontext"):init(Device)
require("ui/bidi").setup("zh_CN")
local UIManager = require("ui/uimanager")
local Event = require("ui/event")
local Geom = require("ui/geometry")
local Store = dofile(plugin_path .. "/store.lua")
local json = require("rapidjson")
local data_path = DataStorage:getSettingsDir() .. "/kindle-todo.json"
local seed = assert(Store.open(data_path))
assert(seed:set_font_size(28))
assert(#assert(Store.open(data_path)).items == 0, "Native rapidjson preserves an empty item array")
local samples = {
    "读完这一章，写三行笔记", "完成今天的散步", "给家人打电话 / Call family 📚",
    "一条较长的中文待办，用于检查窄屏显示时文字能够截断，长按后仍然可以查看和编辑完整内容。",
}
for _, text in ipairs(samples) do assert(seed:add(text)) end
for index = 5, 16 do assert(seed:add("待办 " .. index .. "：离线完成一件小事")) end
local assertions = 0
local function check(ok, message)
    assert(ok, message)
    assertions = assertions + 1
end
local function contents(path)
    local file = assert(io.open(path, "rb"))
    local text = file:read("*a")
    file:close()
    return text
end
local function snapshot(name)
    UIManager:forceRePaint()
    local path = run_dir .. "/" .. name .. ".png"
    Device.screen.bb:writePNG(path)
    return path
end
local function top()
    return UIManager._window_stack[#UIManager._window_stack].widget
end
local function inputWindow()
    -- The soft keyboard is a separate window above its InputDialog.
    for index = #UIManager._window_stack, 1, -1 do
        local widget = UIManager._window_stack[index].widget
        if type(widget.getInputText) == "function" then return widget end
    end
    error("InputDialog not found")
end
local function gesture(rect, kind, direction)
    UIManager:handleInputEvent(Event:new("Gesture", {
        ges = kind or "tap",
        direction = direction,
        pos = Geom:new{ x = rect.x + math.floor(rect.w / 2), y = rect.y + math.floor(rect.h / 2) },
    }))
end
local function press(dialog, caption)
    for _, row in ipairs(dialog.buttons or {}) do
        for _, button in ipairs(row) do
            if button.text == caption then button.callback(); return end
        end
    end
    error("Missing dialog button: " .. caption)
end

local function pollCount(board)
    local count = 0
    for _, task in ipairs(UIManager._task_queue) do
        if task.action == board._poll_inbox then count = count + 1 end
    end
    return count
end

local function runInboxTick(board)
    -- Exercise the real scheduler without waiting five seconds in every test.
    UIManager:unschedule(board._poll_inbox)
    UIManager:scheduleIn(0, board._poll_inbox)
    UIManager:_checkTasks()
end

local inbox_path = DataStorage:getSettingsDir() .. "/kindle-todo-inbox"
local function queueBatch(character, items, raw)
    if lfs.attributes(inbox_path, "mode") ~= "directory" then assert(lfs.mkdir(inbox_path)) end
    local batch_id = string.rep(character, 64)
    local path = inbox_path .. "/" .. batch_id .. ".json"
    local file = assert(io.open(path, "wb"))
    assert(file:write(raw or json.encode({ schema = 1, batch_id = batch_id, items = items })))
    assert(file:close())
    return path
end

local registered
local Plugin = dofile(plugin_path .. "/main.lua")
local plugin = Plugin:new{
    path = plugin_path,
    ui = { menu = { registerToMainMenu = function(_, object) registered = object end } },
}
check(registered == plugin, "Plugin registers with the KOReader main menu")
local menu = {}
plugin:addToMainMenu(menu)
check(type(menu.kindle_offline_todo.callback) == "function", "Offline todo has a menu entry")
menu.kindle_offline_todo.callback()
local board = assert(plugin.todo_board)
check(top() == board, "Plugin opens the actual foreground board")
check(Device.screen:getWidth() == 758 and Device.screen:getHeight() == 1024, "PW2-sized framebuffer")
check(board.pages > 1, "Initial list paginates")
local original = contents(snapshot("todo-initial"))

gesture(board.rows[1])
check(board.store.items[1].done, "Real gesture dispatch completes a task")
check(assert(Store.open(data_path)).items[1].done, "Completion persists immediately")
check(contents(snapshot("todo-completed")) ~= original, "Completion changes rendered pixels")
gesture(board.rows[1])
check(not board.store.items[1].done, "Second tap undoes completion")
check(not assert(Store.open(data_path)).items[1].done, "Undo persists immediately")
check(contents(snapshot("todo-undone")) == original, "Undo restores the exact original image")

gesture(board.rows[1], "hold")
local actions = top()
check(actions ~= board, "Long press opens a real ButtonDialog")
snapshot("todo-actions")
press(actions, "编辑")
local input = inputWindow()
check(type(input.getInputText) == "function", "Edit opens a real InputDialog")
check(input:getInputText() == samples[1], "Edit keeps the original complete text")
check(input:isKeyboardVisible(), "Edit shows the KOReader keyboard")
snapshot("todo-edit")
input:setInputText("读完本章，并整理中文笔记 / edited")
press(input, "保存")
check(top() == board, "Saving closes edit dialog")
check(assert(Store.open(data_path)).items[1].text == "读完本章，并整理中文笔记 / edited", "Edited text persists")
snapshot("todo-edited")

gesture(board.buttons.add)
local add = inputWindow()
check(type(add.setInputText) == "function", "Add opens an InputDialog")
add:setInputText("在 Kindle 本机新增")
press(add, "保存")
check(#board.store.items == 17 and board.page == board.pages, "Adding navigates to the new item page")
check(assert(Store.open(data_path)).items[17].text == "在 Kindle 本机新增", "Added text persists")
snapshot("todo-added")

board.page = 1
board:redraw(true)
snapshot("todo-before-font")
local previous_rows = board.per_page
gesture(board.buttons.font)
local font_dialog = top()
snapshot("todo-font-dialog")
press(font_dialog, "48")
check(board.store.font_size == 48, "Font dialog applies selected size")
check(board.per_page < previous_rows, "Large text reduces rows per page")
check(assert(Store.open(data_path)).font_size == 48, "Selected font size persists")
snapshot("todo-font48")
gesture(board.buttons.next)
check(board.page == 2, "Next-page button works through gesture dispatch")
snapshot("todo-page2")
gesture(board.buttons.previous)
check(board.page == 1, "Previous-page button works through gesture dispatch")

gesture(board.rows[1], "hold")
press(top(), "删除此项")
local confirm = top()
check(confirm ~= board and type(confirm.ok_callback) == "function", "Delete requires a real ConfirmBox")
snapshot("todo-delete-confirm")
UIManager:close(confirm)
confirm.ok_callback()
check(#board.store.items == 16, "Confirmed deletion removes one item")
check(#assert(Store.open(data_path)).items == 16, "Deletion persists")

gesture(board.buttons.close)
check(plugin.todo_board == nil, "Closing releases the plugin board")
plugin:onKindleTodo()
board = assert(plugin.todo_board)
check(board.store.font_size == 48 and #board.store.items == 16, "Reopening restores saved data and font size")
snapshot("todo-reopened")

check(pollCount(board) == 1, "Opening schedules exactly one inbox check")
gesture(board.rows[1])
local completed_id = board.store.items[1].id
check(board.store.items[1].done, "Existing task is completed before receiving a batch")
gesture(board.buttons.next)
local current_page = board.page
local batch = queueBatch("a", { "电脑推送第一条", "电脑推送第二条 📚" })
runInboxTick(board)
check(#board.store.items == 18 and not lfs.attributes(batch), "Scheduled check imports and acknowledges the complete batch")
check(board.store.items[1].id == completed_id and board.store.items[1].done, "Receiving preserves existing task IDs and checkmarks")
check(board.page == current_page and board.store.font_size == 48, "Receiving preserves page and font size")
check(pollCount(board) == 1, "Receiving leaves exactly one future poll")
local received = assert(Store.open(data_path))
check(#received.items == 18 and received.items[1].done and received.items[18].text == "电脑推送第二条 📚", "Received UTF-8 and existing completion persist together")
snapshot("todo-wifi-kept-page")
board.page = board.pages
board:redraw(true)
snapshot("todo-wifi-received")

queueBatch("a", { "电脑推送第一条", "电脑推送第二条 📚" })
runInboxTick(board)
check(#board.store.items == 18 and not lfs.attributes(batch), "Retried batch is acknowledged without duplicate tasks")

local invalid_batch = queueBatch("b", {}, "{")
runInboxTick(board)
check(board.inbox_error and top() == board and lfs.attributes(invalid_batch), "Malformed batch remains on disk with a non-modal error")
local error_changes, window_count = board.changes, #UIManager._window_stack
runInboxTick(board)
check(board.changes == error_changes and #UIManager._window_stack == window_count, "Repeated receive errors cause no repaint or popup loop")
check(pollCount(board) == 1, "A failed receive keeps a single retry scheduled")
snapshot("todo-wifi-error")
assert(os.remove(invalid_batch))
local original_receive = board.receive_inbox
board.receive_inbox = function() error("injected inbox exception") end
runInboxTick(board)
check(board.inbox_error:find("injected inbox exception", 1, true) and pollCount(board) == 1, "Unexpected receiver exceptions cannot escape the scheduler")
board.receive_inbox = original_receive
runInboxTick(board)
check(not board.inbox_error, "The next successful check clears the receive error")

gesture(board.buttons.import)
local receive_menu = top()
check(receive_menu ~= board, "Receive/import button opens a real action menu")
snapshot("todo-receive-menu")
press(receive_menu, "接收 Wi-Fi 推送")
check(top() ~= board, "Manual receive gives a user-requested result")
UIManager:close(top())

UIManager:broadcastEvent(Event:new("Suspend"))
check(pollCount(board) == 0, "Suspend broadcasts cancel inbox polling")
local asleep_batch = queueBatch("c", { "唤醒后接收这一条" })
board._poll_inbox()
check(#board.store.items == 18 and lfs.attributes(asleep_batch) and pollCount(board) == 0, "Suspended board neither consumes files nor reschedules")
UIManager:broadcastEvent(Event:new("Resume"))
check(#board.store.items == 19 and not lfs.attributes(asleep_batch) and pollCount(board) == 1, "Resume immediately receives pending tasks and restarts polling")
UIManager:broadcastEvent(Event:new("Resume"))
check(pollCount(board) == 1, "Repeated resume cannot duplicate polling")

local closed_board = board
board:onClose()
check(plugin.todo_board == nil and pollCount(closed_board) == 0, "Closing cancels every poll belonging to that board")
local closed_batch = queueBatch("d", { "下次打开时自动接收" })
closed_board._poll_inbox()
UIManager:broadcastEvent(Event:new("Resume"))
check(#closed_board.store.items == 19 and lfs.attributes(closed_batch) and pollCount(closed_board) == 0, "Closed board stays inactive even after stale callback and resume")
plugin:onKindleTodo()
board = assert(plugin.todo_board)
check(#board.store.items == 20 and not lfs.attributes(closed_batch), "Opening automatically receives a batch queued while closed")
check(board.store.items[1].done and board.store.font_size == 48, "Reopening after push preserves completion and font")
snapshot("todo-wifi-reopened")

gesture(board.buttons.font)
press(top(), "28")
board.page = 2
board:redraw(true)
local normal_rows, anchor_id = board.per_page, board.rows[1].item.id
gesture(board.buttons.font)
snapshot("todo-display-options")
press(top(), "隐藏上下栏")
check(board.store.compact_mode and assert(Store.open(data_path)).compact_mode, "Hiding bars persists compact mode")
check(board.per_page > normal_rows, "Compact mode fits more tasks at the same font size")
local anchor_visible = false
for _, row in ipairs(board.rows) do if row.item.id == anchor_id then anchor_visible = true end end
check(anchor_visible, "Switching layout keeps the old first task on the visible page")
check(board.buttons.menu and not board.buttons.font and not board.buttons.close, "Compact layout retains only its discoverable menu button")
check(board.rows[1].x + board.rows[1].w < board.buttons.menu.x, "First task hit area and text cannot overlap the menu")
check(board.rows[#board.rows].y + board.row_height <= board.list_bottom, "Last compact row fits above the bottom margin")
snapshot("todo-compact")

local before_menu_done = board.rows[1].item.done
gesture(board.buttons.menu)
local compact_menu = top()
check(compact_menu ~= board and board.rows[1].item.done == before_menu_done, "Menu tap does not toggle the adjacent task")
check(compact_menu.title:find(board.page .. " / " .. board.pages, 1, true), "Compact menu makes page position visible")
snapshot("todo-compact-menu")
local compact_page = board.page
press(compact_menu, "下一页")
check(board.page == compact_page + 1, "Compact menu can advance pages")
gesture(board.dimen, "swipe", "east")
check(board.page == compact_page, "Right swipe returns to the previous page")
gesture(board.dimen, "swipe", "west")
check(board.page == compact_page + 1, "Left swipe advances to the next page")
snapshot("todo-compact-page2")
board.page = board.pages
board:redraw()
gesture(board.dimen, "swipe", "west")
check(board.page == board.pages, "Swipe never advances beyond the last page")
board.page = 1
board:redraw()
gesture(board.dimen, "swipe", "east")
check(board.page == 1, "Swipe never goes before the first page")
gesture(board.rows[1])
check(board.rows[1].item.done ~= before_menu_done, "Compact rows still toggle through real gesture dispatch")
gesture(board.rows[1])
check(board.rows[1].item.done == before_menu_done, "Compact task completion can be undone")
gesture(board.rows[1], "hold")
check(top() ~= board, "Compact rows still support long-press actions")
press(top(), "返回")

gesture(board.buttons.menu)
press(top(), "字号")
press(top(), "48")
check(board.store.compact_mode and board.store.font_size == 48, "Compact menu retains access to font size")
snapshot("todo-compact-font48")
gesture(board.buttons.menu)
press(top(), "字号")
press(top(), "28")
gesture(board.buttons.menu)
press(top(), "新增")
check(inputWindow():isKeyboardVisible(), "Compact menu retains add and the touch keyboard")
press(inputWindow(), "取消")
gesture(board.buttons.menu)
press(top(), "接收/导入")
check(top() ~= board, "Compact menu retains receive/import actions")
press(top(), "返回")
queueBatch("e", { "隐藏上下栏时仍自动接收" })
runInboxTick(board)
check(#board.store.items == 21 and board.store.compact_mode and assert(Store.open(data_path)).compact_mode,
    "Automatic receiving preserves the compact layout setting")

-- Real store conflict: a failed save must not hide/show UI before committing.
gesture(board.buttons.menu)
compact_menu = top()
local saved_bytes = contents(data_path)
local conflict = assert(io.open(data_path, "wb"))
assert(conflict:write(saved_bytes .. "\n")); assert(conflict:close())
press(compact_menu, "显示上下栏")
check(board.store.compact_mode and board.buttons.menu and top() ~= compact_menu,
    "Storage failure keeps compact mode and presents a recoverable error")
UIManager:close(top())
local restore = assert(io.open(data_path, "wb"))
assert(restore:write(saved_bytes)); assert(restore:close())
anchor_id = board.rows[1].item.id
press(compact_menu, "显示上下栏")
check(not board.store.compact_mode and board.buttons.font and board.buttons.close,
    "Visible menu restores both bars after a successful save")
check(not assert(Store.open(data_path)).compact_mode, "Showing bars also persists")
anchor_visible = false
for _, row in ipairs(board.rows) do if row.item.id == anchor_id then anchor_visible = true end end
check(anchor_visible, "Restoring bars keeps the old first task on the visible page")
snapshot("todo-bars-restored")
gesture(board.buttons.font)
press(top(), "隐藏上下栏")
gesture(board.buttons.menu)
press(top(), "返回 KOReader")
check(plugin.todo_board == nil and pollCount(board) == 0, "Compact menu can exit and cancels polling")
plugin:onKindleTodo()
board = assert(plugin.todo_board)
check(board.store.compact_mode and board.buttons.menu, "Reopening restores compact mode and its menu")
snapshot("todo-compact-reopened")
board:onClose()
check(pollCount(board) == 0, "Final close leaves no inbox task")

local empty_store = assert(Store.open(DataStorage:getSettingsDir() .. "/empty-todo.json"))
assert(empty_store:set_compact_mode(true))
local Board = dofile(plugin_path .. "/board.lua")
local empty_board = Board:new{ store = empty_store }
UIManager:show(empty_board)
check(#empty_board.rows == 0 and empty_board.buttons.menu, "Empty compact board still has an exit menu")
snapshot("todo-compact-empty")
gesture(empty_board.buttons.menu)
press(top(), "返回 KOReader")
check(not UIManager:isWidgetShown(empty_board), "Empty compact board exits through the visible menu")
Device:exit()
print("PASS: " .. assertions .. " real KOReader UI checks")
print("Artifacts: " .. run_dir)
print("No Kindle connections; sleep and hardware touch behavior still require device validation.")
