-- Receive PC batches without replacing the Kindle's existing tasks or settings.
-- The sender publishes a complete <batch_id>.json via rename in this directory.
local lfs = require("libs/libkoreader-lfs")
local Inbox = {}
Inbox.PROTOCOL = 1
Inbox.MAX_BYTES = 256 * 1024
Inbox.DEFAULT_FILES, Inbox.MAX_FILES = 5, 20
local MAX_DIRECTORY_ENTRIES = 1024

local function directory_attributes(path)
    local attributes, err, code = lfs.symlinkattributes(path)
    if not attributes then
        if code == 2 then return false end
        return nil, "无法读取收件目录：" .. tostring(err)
    end
    if attributes.mode ~= "directory" then return nil, "收件路径必须是普通目录，不能是符号链接。" end
    return attributes
end

local function same_file(first, second)
    if not first or not second or first.mode ~= "file" or second.mode ~= "file" then return false end
    for _, key in ipairs({ "dev", "ino", "size", "modification" }) do
        if first[key] ~= second[key] then return false end
    end
    return true
end

local function filenames(path)
    local result, scanned = {}, 0
    local iterator, directory = lfs.dir(path)
    for name in iterator, directory do
        scanned = scanned + 1
        if scanned > MAX_DIRECTORY_ENTRIES then
            directory:close()
            return nil, "收件目录文件过多，请先整理后再重试。"
        end
        local id = name:match("^([0-9a-f]+)%.json$")
        if id and #id == 64 then result[#result + 1] = name end
    end
    table.sort(result)
    return result
end

local function read_batch(store, path, name)
    local attributes, err = lfs.symlinkattributes(path)
    if not attributes then return nil, "无法检查收件文件：" .. tostring(err) end
    if attributes.mode ~= "file" then return nil, "收件必须是普通文件，不能是目录或符号链接。" end
    if type(attributes.size) ~= "number" or attributes.size <= 0 or attributes.size > Inbox.MAX_BYTES then
        return nil, "收件文件须为非空且不超过 256 KiB 的 JSON。"
    end
    local file
    file, err = io.open(path, "rb")
    if not file then return nil, "无法打开收件：" .. tostring(err) end
    local read_ok, content, read_err = pcall(file.read, file, Inbox.MAX_BYTES + 1)
    local closed, close_err = file:close()
    if not read_ok then return nil, "读取收件失败：" .. tostring(content) end
    if not closed then return nil, "关闭收件失败：" .. tostring(close_err) end
    if not content then return nil, "收件为空或读取失败：" .. tostring(read_err) end
    if #content > Inbox.MAX_BYTES or #content ~= attributes.size then return nil, "收件文件过大或正在变化，请重试。" end
    if not same_file(attributes, lfs.symlinkattributes(path)) then return nil, "收件文件正在变化，请重试。" end
    local decoded, batch, decode_err = pcall(store.codec.decode, content)
    if not decoded or type(batch) ~= "table" then return nil, "收件 JSON 无效：" .. tostring(decode_err or batch) end
    if batch.schema ~= Inbox.PROTOCOL then return nil, "不支持的收件协议版本。" end
    if batch.batch_id ~= name:sub(1, 64) then return nil, "收件批次编号与文件名不一致。" end
    return batch, nil, attributes
end

local function receive(store, path, limit, progress)
    if type(path) ~= "string" or path == "" or path:find("%z") then return 0, "收件目录路径无效。", 0 end
    limit = limit or Inbox.DEFAULT_FILES
    if type(limit) ~= "number" or limit ~= math.floor(limit) or limit < 1 or limit > Inbox.MAX_FILES then
        return 0, "单次收件数量须为 1 到 20。", 0
    end
    local directory, err = directory_attributes(path)
    if directory == false then return 0, nil, 0 end -- No PC batch has arrived yet.
    if not directory then return 0, err, 0 end
    local names
    names, err = filenames(path)
    if not names then return 0, err, 0 end
    local added, processed = 0, 0
    for index = 1, math.min(limit, #names) do
        local name = names[index]
        local current_directory
        current_directory, err = directory_attributes(path)
        if not current_directory then return added, err or "收件目录已移走，请重试。", processed end
        local full_path = path .. "/" .. name
        local batch, attributes
        batch, err, attributes = read_batch(store, full_path, name)
        if not batch then return added, name .. "：" .. err, processed end
        local saved, count_or_err = store:import_batch(batch.batch_id, batch.items)
        if not saved then return added, name .. "：" .. tostring(count_or_err), processed end
        added = added + count_or_err
        progress.added = added
        current_directory, err = directory_attributes(path)
        if not current_directory or not same_file(attributes, lfs.symlinkattributes(full_path)) then
            return added, "待办已保存，但收件文件或目录已变化，文件保留供下次检查。", processed
        end
        local removed, remove_err = os.remove(full_path)
        if not removed then return added, "待办已保存，但无法清理收件文件；重试不会重复添加：" .. tostring(remove_err), processed end
        processed = processed + 1
        progress.processed = processed
    end
    return added, nil, processed
end

-- added counts new tasks; processed counts removed files (including duplicates).
-- On a later batch's failure, return the successful counts plus the error so UI
-- can refresh those tasks. Failed/unreadable files remain available for retry.
function Inbox.receive(store, path, limit)
    local progress = { added = 0, processed = 0 }
    local ok, added, err, processed = pcall(receive, store, path, limit, progress)
    if not ok then return progress.added, "收件未完成：" .. tostring(added), progress.processed end
    return added, err, processed
end

return Inbox
