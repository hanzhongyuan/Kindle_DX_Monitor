-- A foreground KOReader widget. Power/suspend handling remains with KOReader.
-- Only a local inbox is checked; networking and sleep remain under user control.
local Blitbuffer = require("ffi/blitbuffer")
local ButtonDialog = require("ui/widget/buttondialog")
local ConfirmBox = require("ui/widget/confirmbox")
local Device = require("device")
local Font = require("ui/font")
local Geom = require("ui/geometry")
local GestureRange = require("ui/gesturerange")
local InfoMessage = require("ui/widget/infomessage")
local InputContainer = require("ui/widget/container/inputcontainer")
local InputDialog = require("ui/widget/inputdialog")
local TextWidget = require("ui/widget/textwidget")
local UIManager = require("ui/uimanager")
local Screen = Device.screen

local Board = InputContainer:extend{
    fullscreen = true,
    covers_fullscreen = true,
    stop_events_propagation = true,
}

local function scale(value) return Screen:scaleBySize(value) end

local function contains(rect, pos)
    return pos and pos.x >= rect.x and pos.x < rect.x + rect.w
        and pos.y >= rect.y and pos.y < rect.y + rect.h
end

function Board:init()
    self.page = 1
    self.changes = 0
    self._closed, self._suspended = false, false
    -- Keep an instance-specific reference so closing this board cancels its task.
    self._poll_inbox = function() self:pollInbox() end
    self.dimen = Geom:new{ x = 0, y = 0, w = Screen:getWidth(), h = Screen:getHeight() }
    self.ges_events = {
        TodoTap = { GestureRange:new{ ges = "tap", range = self.dimen } },
        TodoHold = { GestureRange:new{ ges = "hold", range = self.dimen } },
        TodoSwipe = { GestureRange:new{ ges = "swipe", range = self.dimen } },
    }
    if Device:hasKeys() then self.key_events = { Close = { { Device.input.group.Back } } } end
    self:layout()
end

function Board:layout()
    self.dimen.w, self.dimen.h = Screen:getWidth(), Screen:getHeight()
    self.margin = scale(22)
    local w, h, m = self.dimen.w, self.dimen.h, self.margin
    local probe = TextWidget:new{ text = "待办 Ag", face = Font:getFace("cfont", self.store.font_size), padding = 0 }
    self.row_height = math.max(scale(64), probe:getSize().h + scale(30))
    probe:free()
    self.toolbar_y, self.button_height = scale(88), scale(46)
    self.footer_y = h - scale(92)
    self.list_top = self.store.compact_mode and scale(14) or self.toolbar_y + self.button_height + scale(22)
    self.list_bottom = self.store.compact_mode and h - scale(14) or self.footer_y - scale(12)
    self.per_page = math.max(1, math.floor((self.list_bottom - self.list_top) / self.row_height))
    self.pages = math.max(1, math.ceil(#self.store.items / self.per_page))
    self.page = math.max(1, math.min(self.page, self.pages))
    local gap = scale(10)
    self.buttons = {}
    if self.store.compact_mode then
        self.buttons.menu = {
            x = w - m - scale(82), y = self.list_top + math.floor((self.row_height - self.button_height) / 2),
            w = scale(82), h = self.button_height,
        }
    else
        local bw = math.floor((w - m * 2 - gap * 2) / 3)
        for index, name in ipairs({ "add", "import", "font" }) do
            self.buttons[name] = { x = m + (index - 1) * (bw + gap), y = self.toolbar_y, w = bw, h = self.button_height }
        end
        local fw = math.floor((w - m * 2 - gap * 3) / 4)
        for index, name in ipairs({ "previous", "page", "next", "close" }) do
            self.buttons[name] = { x = m + (index - 1) * (fw + gap), y = self.footer_y, w = fw, h = self.button_height }
        end
    end
    self.rows = {}
    local first = (self.page - 1) * self.per_page + 1
    for index = first, math.min(first + self.per_page - 1, #self.store.items) do
        self.rows[#self.rows + 1] = {
            item = self.store.items[index], x = m, y = self.list_top + (index - first) * self.row_height,
            w = self.store.compact_mode and index == first and self.buttons.menu.x - m - gap or w - 2 * m,
            h = self.row_height,
        }
    end
end

function Board:drawText(bb, text, x, y, width, size, color, center, strike, bold)
    local widget = TextWidget:new{
        text = text, face = Font:getFace("cfont", size), max_width = width,
        padding = 0, fgcolor = color or Blitbuffer.COLOR_BLACK, bold = bold or false,
    }
    local dimen = widget:getSize()
    local left = center and x + math.floor((width - dimen.w) / 2) or x
    widget:paintTo(bb, left, y)
    if strike and dimen.w > 0 then
        bb:paintRect(left, y + math.floor(dimen.h * 0.55), math.min(width, dimen.w), math.max(1, scale(2)), color)
    end
    widget:free()
    return dimen.h
end

function Board:drawButton(bb, rect, label, disabled)
    local color = disabled and Blitbuffer.COLOR_GRAY or Blitbuffer.COLOR_BLACK
    bb:paintBorder(rect.x, rect.y, rect.w, rect.h, math.max(1, scale(1)), color)
    self:drawText(bb, label, rect.x + scale(5), rect.y + scale(10), rect.w - scale(10), 19, color, true)
end

function Board:paintTo(bb, x, y)
    -- This fullscreen widget is always painted at screen origin.
    self:layout()
    bb:paintRect(0, 0, self.dimen.w, self.dimen.h, Blitbuffer.COLOR_WHITE)
    local m, width = self.margin, self.dimen.w - 2 * self.margin
    if self.store.compact_mode then
        self:drawButton(bb, self.buttons.menu, "菜单")
    else
        self:drawText(bb, "待办事项", m, scale(18), width, 30, nil, false, false, true)
        local done = 0
        for _, item in ipairs(self.store.items) do if item.done then done = done + 1 end end
        self:drawText(bb, "已完成 " .. done .. " / " .. #self.store.items .. " · 点击标记，长按编辑",
            m, scale(58), width, 17, Blitbuffer.COLOR_DARK_GRAY)
        self:drawButton(bb, self.buttons.add, "新增")
        self:drawButton(bb, self.buttons.import, "接收/导入")
        self:drawButton(bb, self.buttons.font, "显示/字号")
    end
    if #self.rows == 0 then
        self:drawText(bb, "还没有待办", m, self.list_top + scale(55), width, 26, nil, true)
        self:drawText(bb, self.store.compact_mode and "点右上角“菜单”新增或接收待办。" or "点“新增”，或导入一份文本清单。",
            m, self.list_top + scale(105), width, 19, nil, true)
    end
    for _, row in ipairs(self.rows) do
        local box = scale(24)
        local by = row.y + math.floor((row.h - box) / 2)
        bb:paintBorder(row.x, by, box, box, math.max(1, scale(2)), Blitbuffer.COLOR_BLACK)
        if row.item.done then
            bb:paintRect(row.x + scale(5), by + scale(5), box - scale(10), box - scale(10), Blitbuffer.COLOR_BLACK)
        end
        local label = TextWidget:new{ text = row.item.text, face = Font:getFace("cfont", self.store.font_size), padding = 0 }
        local text_height = label:getSize().h
        label:free()
        self:drawText(bb, row.item.text, row.x + box + scale(18), row.y + math.floor((row.h - text_height) / 2),
            row.w - box - scale(18), self.store.font_size,
            row.item.done and Blitbuffer.COLOR_DARK_GRAY or Blitbuffer.COLOR_BLACK, false, row.item.done)
        bb:paintRect(row.x, row.y + row.h - 1, row.w, 1, Blitbuffer.COLOR_LIGHT_GRAY)
    end
    if self.store.compact_mode then return end
    self:drawButton(bb, self.buttons.previous, "上一页", self.page == 1)
    self:drawText(bb, self.page .. " / " .. self.pages, self.buttons.page.x,
        self.footer_y + scale(10), self.buttons.page.w, 19, nil, true)
    self:drawButton(bb, self.buttons.next, "下一页", self.page == self.pages)
    self:drawButton(bb, self.buttons.close, "返回")
    local status = self.inbox_error and "推送接收遇到问题 · 点“接收/导入”查看并重试"
        or (self.inbox_added and "已收到 " .. self.inbox_added .. " 条推送 · 原有事项和勾选已保留")
        or "修改即保存 · 本页每 5 秒接收推送 · 休眠暂停"
    self:drawText(bb, status,
        m, self.footer_y + self.button_height + scale(13), width, 15, Blitbuffer.COLOR_DARK_GRAY, true)
end

function Board:redraw(full)
    self:layout()
    self.changes = self.changes + 1
    UIManager:setDirty(self, (full or self.changes % 6 == 0) and "flashui" or "ui", self.dimen)
end

function Board:save(method, ...)
    local ok, err = self.store[method](self.store, ...)
    if not ok then
        UIManager:show(InfoMessage:new{ text = "未能保存：" .. tostring(err) .. "\n修改尚未生效，请重试。" })
        return false
    end
    self:redraw(method == "set_font_size" or method == "set_compact_mode")
    return true
end

function Board:onTodoTap(_, gesture)
    local pos = gesture and gesture.pos
    if not pos then return true end
    for name, rect in pairs(self.buttons) do
        if contains(rect, pos) then
            if name == "add" then self:editItem()
            elseif name == "import" then self:receiveMenu()
            elseif name == "font" then self:chooseFont()
            elseif name == "menu" then self:compactMenu()
            elseif name == "previous" and self.page > 1 then self.page = self.page - 1; self:redraw(true)
            elseif name == "next" and self.page < self.pages then self.page = self.page + 1; self:redraw(true)
            elseif name == "close" then self:onClose() end
            return true
        end
    end
    for _, row in ipairs(self.rows) do
        if contains(row, pos) then self:save("toggle", row.item.id); return true end
    end
    return true
end

function Board:onTodoHold(_, gesture)
    for _, row in ipairs(self.rows) do
        if contains(row, gesture and gesture.pos) then self:itemMenu(row.item); break end
    end
    return true
end

function Board:onTodoSwipe(_, gesture)
    local direction = gesture and gesture.direction
    if direction == "west" and self.page < self.pages then
        self.page = self.page + 1
        self:redraw(true)
    elseif direction == "east" and self.page > 1 then
        self.page = self.page - 1
        self:redraw(true)
    end
    return true
end

function Board:setCompactMode(enabled)
    local first = (self.page - 1) * self.per_page + 1
    if not self:save("set_compact_mode", enabled) then return false end
    -- Retain the page containing the old first task after the row count changes.
    self.page = math.floor((first - 1) / self.per_page) + 1
    self:redraw(true)
    return true
end

function Board:compactMenu()
    local dialog
    dialog = ButtonDialog:new{
        title = "待办菜单 · " .. self.page .. " / " .. self.pages .. " 页\n左右滑动可翻页"
            .. (self.inbox_error and " · 推送接收遇到问题" or ""),
        buttons = {
            { { text = "显示上下栏", callback = function()
                if self:setCompactMode(false) then UIManager:close(dialog) end
            end } },
            { { text = "字号", callback = function() UIManager:close(dialog); self:chooseFont() end },
              { text = "新增", callback = function() UIManager:close(dialog); self:editItem() end } },
            { { text = "接收/导入", callback = function() UIManager:close(dialog); self:receiveMenu() end } },
            { { text = "上一页", enabled = self.page > 1, callback = function()
                  UIManager:close(dialog)
                  self.page = math.max(1, self.page - 1)
                  self:redraw(true)
              end },
              { text = "下一页", enabled = self.page < self.pages, callback = function()
                  UIManager:close(dialog)
                  self.page = math.min(self.pages, self.page + 1)
                  self:redraw(true)
              end } },
            { { text = "返回 KOReader", callback = function() UIManager:close(dialog); self:onClose() end } },
            { { text = "关闭菜单", callback = function() UIManager:close(dialog) end } },
        },
    }
    UIManager:show(dialog)
end

function Board:editItem(item)
    local dialog
    dialog = InputDialog:new{
        title = item and "编辑待办" or "新增待办",
        input = item and item.text or "",
        input_hint = "输入一条待办，最多 300 字",
        description = "内容保存在 Kindle 本机。中文输入可使用 KOReader 中文键盘，或从文本导入。",
        allow_newline = false,
        buttons = { {
            { text = "取消", id = "close", callback = function() UIManager:close(dialog) end },
            { text = "保存", is_enter_default = true, callback = function()
                local text = dialog:getInputText()
                local ok
                if item then ok = self:save("edit", item.id, text) else ok = self:save("add", text) end
                if ok then
                    UIManager:close(dialog)
                    if not item then self.page = self.pages end
                    self:redraw()
                end
            end },
        } },
    }
    UIManager:show(dialog)
    dialog:onShowKeyboard()
end

function Board:itemMenu(item)
    local dialog
    dialog = ButtonDialog:new{
        title = item.text,
        buttons = {
            { { text = "编辑", callback = function() UIManager:close(dialog); self:editItem(item) end },
              { text = item.done and "标为未完成" or "标为完成", callback = function()
                  UIManager:close(dialog); self:save("toggle", item.id)
              end } },
            { { text = "删除此项", callback = function()
                UIManager:close(dialog)
                UIManager:show(ConfirmBox:new{
                    text = "删除这条待办？\n" .. item.text,
                    ok_text = "删除", cancel_text = "取消",
                    ok_callback = function() self:save("remove", item.id) end,
                })
            end }, { text = "返回", callback = function() UIManager:close(dialog) end } },
        },
    }
    UIManager:show(dialog)
end

function Board:chooseFont()
    local dialog
    local buttons = {}
    for _, size in ipairs({ 18, 24, 28, 32, 40, 48 }) do
        local selected_size = size
        if #buttons == 0 or #buttons[#buttons] == 3 then buttons[#buttons + 1] = {} end
        table.insert(buttons[#buttons], { text = tostring(size), callback = function()
            if self:save("set_font_size", selected_size) then UIManager:close(dialog) end
        end })
    end
    table.insert(buttons, { { text = self.store.compact_mode and "显示上下栏" or "隐藏上下栏", callback = function()
        if self:setCompactMode(not self.store.compact_mode) then UIManager:close(dialog) end
    end } })
    table.insert(buttons, { { text = "取消", callback = function() UIManager:close(dialog) end } })
    dialog = ButtonDialog:new{ title = "显示/字号 · 当前 " .. self.store.font_size, buttons = buttons }
    UIManager:show(dialog)
end

function Board:receiveInbox(manual)
    if self._closed or self._suspended or not self.receive_inbox then return end
    local ok, added, err = pcall(self.receive_inbox, self.store, self.inbox_path)
    if not ok then err, added = added, 0 end
    if type(added) ~= "number" or added < 0 then
        err, added = err or "接收结果无效，请重新安装待办插件。", 0
    end
    local previous_error = self.inbox_error
    self.inbox_error = err and tostring(err) or nil
    if added > 0 then self.inbox_added = added end
    -- Appending preserves the current page; layout clamps it if necessary.
    -- An unchanged empty/error inbox must not cause a periodic e-ink repaint.
    if added > 0 or previous_error ~= self.inbox_error then self:redraw(added > 0) end
    if manual then
        local message = self.inbox_error and "未能完成接收：" .. self.inbox_error
            or (added > 0 and "已接收 " .. added .. " 条待办，原有事项和勾选已保留。"
                or "没有新的推送。电脑发送后，打开本页会自动接收。")
        if self.inbox_error and added > 0 then
            message = "已接收 " .. added .. " 条；其余推送遇到问题：\n" .. self.inbox_error
        end
        UIManager:show(InfoMessage:new{ text = message })
    end
end

function Board:stopInboxPolling()
    UIManager:unschedule(self._poll_inbox)
end

function Board:pollInbox()
    self:stopInboxPolling()
    if self._closed or self._suspended or not self.receive_inbox or not UIManager:isWidgetShown(self) then return end
    self:receiveInbox(false)
    UIManager:scheduleIn(5, self._poll_inbox)
end

function Board:receiveMenu()
    local dialog
    dialog = ButtonDialog:new{
        title = "接收/导入待办",
        buttons = {
            { { text = "接收 Wi-Fi 推送", callback = function()
                UIManager:close(dialog)
                self:receiveInbox(true)
            end } },
            { { text = "从文本导入", callback = function()
                UIManager:close(dialog)
                self:importText()
            end } },
            { { text = "返回", callback = function() UIManager:close(dialog) end } },
        },
    }
    UIManager:show(dialog)
end

function Board:importText()
    local file = io.open(self.import_path, "rb")
    if not file then
        UIManager:show(InfoMessage:new{
            text = "先退出 KOReader，再用 USB 将 UTF-8 文本保存到：\n" .. self.import_path
                .. "\n每行一条，可复制原留言板草稿。重新打开待办后点“接收/导入”→“从文本导入”。",
        })
        return
    end
    local text = file:read(262145)
    file:close()
    if not text or #text > 262144 then
        UIManager:show(InfoMessage:new{ text = "导入文件为空或超过 256 KiB，请精简后重试。" })
        return
    end
    UIManager:show(ConfirmBox:new{
        text = "将文本逐行追加到当前待办？\n空行会忽略，原有待办会保留；重复导入会再次追加。",
        ok_text = "追加导入", cancel_text = "取消",
        ok_callback = function()
            if self:save("import_text", text) then
                self.page = self.pages
                self:redraw(true)
                UIManager:show(InfoMessage:new{ text = "已导入并保存在本机。", timeout = 2 })
            end
        end,
    })
end

function Board:onShow()
    self:pollInbox()
    UIManager:setDirty(self, "full", self.dimen)
    return true
end

function Board:onSuspend()
    self._suspended = true
    self:stopInboxPolling()
end

function Board:onResume()
    if self._closed then return end
    self._suspended = false
    self:pollInbox()
end

function Board:onClose()
    UIManager:close(self)
    return true
end

function Board:onCloseWidget()
    self._closed = true
    self:stopInboxPolling()
    if self.on_close then self.on_close(); self.on_close = nil end
    UIManager:setDirty(nil, "full")
end

return Board
