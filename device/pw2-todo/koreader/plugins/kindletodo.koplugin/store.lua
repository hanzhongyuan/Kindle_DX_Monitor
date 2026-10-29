-- Single-process, offline JSON store for the KOReader plugin (Lua 5.1/LuaJIT).
-- UI owns the path, normally DataStorage:getSettingsDir() .. "/kindle-todo.json".
-- Public operations use copy-on-write: the last rename commits both disk and RAM.
local Store = {}
Store.__index = Store
Store.MAX_ITEMS, Store.MAX_TEXT_LENGTH = 200, 300
Store.MIN_FONT_SIZE, Store.MAX_FONT_SIZE = 18, 48
Store.MAX_BATCHES = 4096 -- Never evict IDs: old network retries must remain idempotent.
local MAX_BYTES, MAX_ID = 1024 * 1024, 9007199254740991
local sequence = 0

local function integer(value, minimum, maximum)
    return type(value) == "number" and value == math.floor(value)
        and value >= minimum and value <= maximum
end

local function text_value(value)
    if type(value) ~= "string" then return nil, "待办内容必须是文字。" end
    if value:find("[%z\1-\31\127]") then return nil, "每条待办只能有一行，请勿包含制表符或控制字符。" end
    value = value:match("^%s*(.-)%s*$")
    if value == "" then return nil, "请输入待办内容。" end
    if #value > Store.MAX_TEXT_LENGTH * 4 then return nil, "每条待办最多 300 个字符。" end
    -- Count Unicode scalar values, rejecting overlong sequences, surrogates,
    -- lone continuation bytes and code points above U+10FFFF.
    local offset, count = 1, 0
    while offset <= #value do
        local first, second = value:byte(offset, offset + 1)
        local length
        if first < 128 then length = 1
        elseif first >= 194 and first <= 223 then length = 2
        elseif first >= 224 and first <= 239 then length = 3
        elseif first >= 240 and first <= 244 then length = 4
        else return nil, "文字不是有效的 UTF-8 编码。" end
        for index = offset + 1, offset + length - 1 do
            local byte = value:byte(index)
            if not byte or byte < 128 or byte > 191 then return nil, "文字不是有效的 UTF-8 编码。" end
        end
        if (first == 224 and second < 160) or (first == 237 and second > 159)
            or (first == 240 and second < 144) or (first == 244 and second > 143) then
            return nil, "文字不是有效的 UTF-8 编码。"
        end
        count, offset = count + 1, offset + length
        if count > Store.MAX_TEXT_LENGTH then return nil, "每条待办最多 300 个字符。" end
    end
    return value
end

local function array()
    -- rapidjson recognizes this marker, including for an empty array.
    return setmetatable({}, { __jsontype = "array" })
end

local function batch_id_valid(id)
    return type(id) == "string" and #id == 64 and not id:find("[^0-9a-f]")
end

local function batch_history(value)
    if value == nil then value = {} end -- Existing schema-1 stores had no inbox.
    if type(value) ~= "table" then return nil, "收件批次记录格式不正确。" end
    local meta = getmetatable(value)
    if meta and meta.__jsontype == "array" then return nil, "收件批次记录必须是对象。" end
    local clean, count = setmetatable({}, { __jsontype = "object" }), 0
    for id, processed in pairs(value) do
        if not batch_id_valid(id) or processed ~= true then return nil, "收件批次记录格式不正确。" end
        clean[id], count = true, count + 1
        if count > Store.MAX_BATCHES then return nil, "收件批次记录超过 4096 批。" end
    end
    return clean, count
end

local function validate(data)
    if type(data) ~= "table" or data.schema ~= 1 then return nil, "不支持的待办数据版本。" end
    if not integer(data.font_size, Store.MIN_FONT_SIZE, Store.MAX_FONT_SIZE) then return nil, "字号须为 18 到 48 的整数。" end
    if data.compact_mode ~= nil and type(data.compact_mode) ~= "boolean" then return nil, "紧凑显示设置须为布尔值。" end
    if not integer(data.next_id, 1, MAX_ID) or type(data.items) ~= "table" then return nil, "待办数据格式不正确。" end
    local count = #data.items
    if count > Store.MAX_ITEMS then return nil, "最多保存 200 条待办。" end
    local meta = getmetatable(data.items)
    if meta and meta.__jsontype == "object" then return nil, "待办列表必须是数组。" end
    for key in pairs(data.items) do
        if not integer(key, 1, count) then return nil, "待办列表不是连续数组。" end
    end
    local history, history_err = batch_history(data.processed_batches)
    if not history then return nil, history_err end
    local clean = { schema = 1, next_id = data.next_id, font_size = data.font_size,
        compact_mode = data.compact_mode == true, items = array(), processed_batches = history }
    local ids = {}
    for index = 1, count do
        local item = data.items[index]
        if type(item) ~= "table" or not integer(item.id, 1, data.next_id - 1)
            or ids[item.id] or type(item.done) ~= "boolean" then return nil, "待办条目或编号不正确。" end
        local text, err = text_value(item.text)
        if not text then return nil, "第 " .. index .. " 条：" .. err end
        ids[item.id] = true
        clean.items[index] = { id = item.id, text = text, done = item.done }
    end
    return clean
end

local function read_file(path)
    local file, err, code = io.open(path, "rb")
    if not file then
        if code == 2 then return false end -- ENOENT is the only blank-store case.
        return nil, err
    end
    local data, read_err = file:read(MAX_BYTES + 1)
    local closed, close_err = file:close()
    if not closed then return nil, close_err end
    if data == nil then
        if read_err then return nil, read_err end
        data = "" -- An existing empty file is corrupt, not a new installation.
    end
    if #data > MAX_BYTES then return nil, "文件超过 1 MiB。" end
    return data
end

local function protected(fn, ...)
    local ok, value, err = pcall(fn, ...)
    if not ok then return nil, tostring(value) end
    return value, err
end

local function write_file(path, contents, sync)
    local file, err = io.open(path, "wb")
    if not file then return nil, err end
    local ok
    ok, err = protected(file.write, file, contents)
    if ok then ok, err = protected(file.flush, file) end
    if ok and sync then ok, err = protected(sync.fsyncOpenedFile, file) end
    local closed, close_err = protected(file.close, file)
    if not ok then return nil, err end
    if not closed then return nil, close_err end
    return true
end

function Store.open(path, codec)
    if type(path) ~= "string" or path == "" or path:find("%z") then return nil, "待办文件路径无效。" end
    if codec == nil then
        local ok
        ok, codec = pcall(require, "rapidjson")
        if not ok then return nil, "无法加载 KOReader JSON 模块：" .. tostring(codec) end
    end
    if type(codec) ~= "table" or type(codec.encode) ~= "function" or type(codec.decode) ~= "function" then
        return nil, "JSON 编码器无效。"
    end
    local raw, err = protected(read_file, path)
    if raw == nil then return nil, "无法读取待办文件，原文件未改动：" .. tostring(err) end
    local data
    if raw == false then
        local backup, backup_err = protected(read_file, path .. ".bak")
        if backup ~= false then
            return nil, "主待办文件缺失，但备份存在或不可读取。请检查 " .. path .. ".bak，恢复后再打开。" .. (backup_err or "")
        end
        data = { schema = 1, next_id = 1, font_size = 28, compact_mode = false, items = array(), processed_batches = {} }
    else
        data, err = protected(codec.decode, raw)
        if data then data, err = validate(data) end
        if not data then return nil, "待办数据损坏或不兼容，原文件未改动。可检查同目录 .bak 备份：" .. tostring(err) end
    end
    local sync_ok, sync = pcall(require, "ffi/util")
    local self = setmetatable({ path = path, codec = codec, _raw = raw,
        _sync = sync_ok and type(sync) == "table" and type(sync.fsyncOpenedFile) == "function" and sync or nil }, Store)
    self.items, self.font_size, self.next_id = data.items, data.font_size, data.next_id
    self.compact_mode = data.compact_mode
    self._processed_batches = data.processed_batches
    return self
end

function Store:_candidate()
    return validate({ schema = 1, items = self.items, font_size = self.font_size,
        compact_mode = self.compact_mode, next_id = self.next_id, processed_batches = self._processed_batches })
end

function Store:_unchanged()
    -- Detect another writer or a file corrupted/deleted since this view opened.
    local current, err = protected(read_file, self.path)
    if current == nil then return nil, "无法检查原待办文件：" .. tostring(err) end
    if current ~= self._raw then return nil, "待办文件已在其他地方修改或损坏。原文件未改动，请关闭后重新打开待办。" end
    return true, current
end

function Store:_commit(candidate)
    local clean, err = validate(candidate)
    if not clean then return nil, err end
    local payload
    payload, err = protected(self.codec.encode, clean, { pretty = true })
    if type(payload) ~= "string" or #payload > MAX_BYTES then return nil, "无法编码待办数据：" .. tostring(err or "输出无效或过大") end
    local unchanged, current = self:_unchanged()
    if not unchanged then return nil, current end

    sequence = sequence + 1
    local suffix = tostring(os.time()) .. "-" .. tostring({}):gsub("[^%w]", "") .. "-" .. sequence
    local temporary = self.path .. ".tmp-" .. suffix
    local backup_temporary = self.path .. ".bak.tmp-" .. suffix
    local function cleanup()
        pcall(os.remove, temporary)
        pcall(os.remove, backup_temporary)
    end
    local ok
    ok, err = protected(write_file, temporary, payload, self._sync)
    if not ok then cleanup(); return nil, "待办未保存：" .. tostring(err) end
    if current ~= false then
        -- Copy the exact previous bytes; never move the live file away first.
        ok, err = protected(write_file, backup_temporary, current, self._sync)
        if ok then ok, err = protected(os.rename, backup_temporary, self.path .. ".bak") end
        if ok and self._sync and self._sync.fsyncDirectory then
            ok, err = protected(self._sync.fsyncDirectory, self.path .. ".bak")
        end
        if not ok then cleanup(); return nil, "无法保留上一版备份，待办未保存：" .. tostring(err) end
    end
    -- Kindle/Linux rename within the same directory atomically replaces a file.
    -- This is the commit point: failures above/below leave public RAM unchanged.
    ok, err = protected(os.rename, temporary, self.path)
    if not ok then cleanup(); return nil, "无法替换待办文件，待办未保存：" .. tostring(err) end
    self.items, self.font_size, self.next_id = clean.items, clean.font_size, clean.next_id
    self.compact_mode = clean.compact_mode
    self._processed_batches = clean.processed_batches
    self._raw = payload
    -- The commit is already visible. Directory syncing is best-effort: returning
    -- failure now would falsely tell the UI that the old contents are still live.
    if self._sync and self._sync.fsyncDirectory then protected(self._sync.fsyncDirectory, self.path) end
    return true
end

function Store:add(text)
    local value, err = text_value(text)
    if not value then return nil, err end
    local candidate
    candidate, err = self:_candidate()
    if not candidate then return nil, err end
    if #candidate.items >= Store.MAX_ITEMS then return nil, "最多保存 200 条待办，请先删除已完成项目。" end
    if candidate.next_id >= MAX_ID then return nil, "待办编号已用尽。" end
    candidate.items[#candidate.items + 1] = { id = candidate.next_id, text = value, done = false }
    candidate.next_id = candidate.next_id + 1
    return self:_commit(candidate)
end

local function find_item(candidate, id)
    for index, item in ipairs(candidate.items) do if item.id == id then return index, item end end
end

function Store:toggle(id)
    local candidate, err = self:_candidate()
    if not candidate then return nil, err end
    local _, item = find_item(candidate, id)
    if not item then return nil, "未找到这条待办，请重新打开列表。" end
    item.done = not item.done
    return self:_commit(candidate)
end

function Store:edit(id, text)
    local value, err = text_value(text)
    if not value then return nil, err end
    local candidate
    candidate, err = self:_candidate()
    if not candidate then return nil, err end
    local _, item = find_item(candidate, id)
    if not item then return nil, "未找到这条待办，请重新打开列表。" end
    item.text = value
    return self:_commit(candidate)
end

function Store:remove(id)
    local candidate, err = self:_candidate()
    if not candidate then return nil, err end
    local index = find_item(candidate, id)
    if not index then return nil, "未找到这条待办，请重新打开列表。" end
    table.remove(candidate.items, index)
    return self:_commit(candidate)
end

function Store:import_text(text)
    if type(text) ~= "string" or #text > MAX_BYTES then return nil, "导入文件须为不超过 1 MiB 的 UTF-8 文本。" end
    local candidate, err = self:_candidate()
    if not candidate then return nil, err end
    text = text:gsub("^\239\187\191", ""):gsub("\r\n", "\n"):gsub("\r", "\n")
    local added = 0
    for line in (text .. "\n"):gmatch("(.-)\n") do
        if line:match("%S") then
            local value
            value, err = text_value(line)
            if not value then return nil, "导入未保存：" .. err end
            if #candidate.items >= Store.MAX_ITEMS then return nil, "导入后超过 200 条。此次导入未保存，请减少条目后重试。" end
            if candidate.next_id >= MAX_ID then return nil, "待办编号已用尽。" end
            candidate.items[#candidate.items + 1] = { id = candidate.next_id, text = value, done = false }
            candidate.next_id, added = candidate.next_id + 1, added + 1
        end
    end
    if added == 0 then return nil, "没有可导入的待办内容。" end
    return self:_commit(candidate)
end

-- Append a complete network batch. Its ID and items share one atomic commit,
-- so a crash after saving but before deleting the inbox file cannot duplicate it.
-- Return true, added_count; on failure return nil, error and preserve all state.
function Store:import_batch(batch_id, items)
    if not batch_id_valid(batch_id) then return nil, "收件批次编号须为 64 位小写十六进制字符。" end
    if type(items) ~= "table" or #items == 0 or #items > Store.MAX_ITEMS then
        return nil, "每批收件须包含 1 到 200 条待办。"
    end
    local meta = getmetatable(items)
    if meta and meta.__jsontype == "object" then return nil, "收件事项必须是数组。" end
    local values = array()
    for key in pairs(items) do
        if not integer(key, 1, #items) then return nil, "收件事项不是连续数组。" end
    end
    for index = 1, #items do
        local value, err = text_value(items[index])
        if not value then return nil, "第 " .. index .. " 条收件：" .. err end
        values[index] = value
    end
    local candidate, err = self:_candidate()
    if not candidate then return nil, err end
    if candidate.processed_batches[batch_id] then
        local unchanged, unchanged_err = self:_unchanged()
        if not unchanged then return nil, unchanged_err end
        return true, 0
    end
    local _, batch_count = batch_history(candidate.processed_batches)
    if batch_count >= Store.MAX_BATCHES then return nil, "已保留 4096 批收件记录。新收件未导入，请备份数据后处理批次记录；旧批次不会重复添加。" end
    if #candidate.items + #values > Store.MAX_ITEMS then return nil, "收件后超过 200 条待办。请先删除不再需要的项目，再重试收件。" end
    if candidate.next_id + #values > MAX_ID then return nil, "待办编号已用尽。" end
    for _, value in ipairs(values) do
        candidate.items[#candidate.items + 1] = { id = candidate.next_id, text = value, done = false }
        candidate.next_id = candidate.next_id + 1
    end
    candidate.processed_batches[batch_id] = true
    local saved, save_err = self:_commit(candidate)
    if not saved then return nil, save_err end
    return true, #values
end

function Store:set_font_size(size)
    if not integer(size, Store.MIN_FONT_SIZE, Store.MAX_FONT_SIZE) then return nil, "字号须为 18 到 48 的整数。" end
    local candidate, err = self:_candidate()
    if not candidate then return nil, err end
    candidate.font_size = size
    return self:_commit(candidate)
end

function Store:set_compact_mode(enabled)
    if type(enabled) ~= "boolean" then return nil, "紧凑显示设置须为布尔值。" end
    local candidate, err = self:_candidate()
    if not candidate then return nil, err end
    candidate.compact_mode = enabled
    return self:_commit(candidate)
end

return Store
