local DataStorage = require("datastorage")
local Dispatcher = require("dispatcher")
local InfoMessage = require("ui/widget/infomessage")
local UIManager = require("ui/uimanager")
local WidgetContainer = require("ui/widget/container/widgetcontainer")

local Todo = WidgetContainer:extend{
    name = "kindletodo",
    is_doc_only = false,
}

function Todo:onDispatcherRegisterActions()
    Dispatcher:registerAction("kindle_offline_todo", {
        category = "none", event = "KindleTodo", title = "离线待办", general = true,
    })
end

function Todo:init()
    self:onDispatcherRegisterActions()
    self.ui.menu:registerToMainMenu(self)
end

function Todo:addToMainMenu(items)
    items.kindle_offline_todo = {
        text = "离线待办",
        sorting_hint = "more_tools",
        callback = function() self:onKindleTodo() end,
    }
end

function Todo:onKindleTodo()
    if self.todo_board then return true end
    local Store = dofile(self.path .. "/store.lua")
    local path = DataStorage:getSettingsDir() .. "/kindle-todo.json"
    local store, err = Store.open(path)
    if not store then
        UIManager:show(InfoMessage:new{
            text = "未能打开待办：" .. tostring(err) .. "\n原文件已保留。请备份并检查：\n" .. path,
        })
        return true
    end
    local Board = dofile(self.path .. "/board.lua")
    local Inbox
    self.todo_board = Board:new{
        store = store,
        import_path = DataStorage:getSettingsDir() .. "/kindle-todo-import.txt",
        inbox_path = DataStorage:getSettingsDir() .. "/kindle-todo-inbox",
        -- Board protects this callback, including loading a missing/damaged inbox module.
        receive_inbox = function(current_store, inbox_path)
            Inbox = Inbox or dofile(self.path .. "/inbox.lua")
            return Inbox.receive(current_store, inbox_path)
        end,
        on_close = function() self.todo_board = nil end,
    }
    UIManager:show(self.todo_board)
    return true
end

return Todo
