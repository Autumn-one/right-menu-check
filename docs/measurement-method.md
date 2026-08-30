# 性能测量方法

## 单处理器探针

每次试验启动一个新的、非提权、STA worker，并在发送请求前加入 kill-on-close Job Object。默认执行 7 次，可选 3、5、7、10、15 或 20 次；每次都有独立 PID。超时、进程崩溃、COM激活失败和接口失败分别统计。

经典 `IContextMenu` 记录：

1. `CoCreateInstance`；
2. `IShellExtInit.Initialize`；
3. `IContextMenu.QueryContextMenu`。

现代 `IExplorerCommand` 记录：

1. `CoCreateInstance`；
2. `GetTitle`；
3. `GetIcon`；
4. `GetState(false)`；
5. `EnumSubCommands`。

工具不会调用 `IContextMenu.InvokeCommand` 或 `IExplorerCommand.Invoke`。PIDL、IDataObject/IShellItemArray、进程启动和 IPC成本不计入处理器耗时；列表中的处理器总耗时是 worker返回阶段耗时之和。

## 统计与排序

- 中位数：偶数样本取两个中间值平均数。
- P95：最近秩法，索引为 `ceil(0.95 * n) - 1`。
- 默认顺序：存在超时、存在崩溃、其他失败、P95降序、不可归因。
- 失败样本不伪装成 0 ms，也不加入成功耗时分布。

每次使用新进程可避免模块已在同一进程加载，但不会清空 Windows文件缓存，因此界面称为“新进程试验”，不称为绝对冷启动。

## 聚合菜单

聚合基准在另一个隔离 worker中调用 `SHCreateDefaultContextMenu` 和 `QueryContextMenu`，用于测量给定文件、文件夹或空白处的完整 Shell菜单构建。它可以揭示单项探针之和不能解释的总体成本，但仍不是 Explorer窗口从鼠标事件到画面出现的绝对 UI延迟。

## 不可归因项

纯静态 verb没有单独的菜单构建回调；DelegateExecute只在用户选择命令后执行；注册表级联菜单的成本不能安全归因到一个 COM处理器。这些项显示“不适用/不可归因”，仍可在聚合基准、备份和管理视图中检查。
