# RightMenuCheck

RightMenuCheck 是一个面向 Windows 10/11 的中文 WPF 右键菜单诊断工具。它将盘点传统 Shell 扩展、静态命令和现代打包菜单，并在隔离进程中测量菜单构建阶段的耗时。

当前实现包含双 Hive/双 RegistryView盘点、AppX/MSIX菜单发现、COM/签名/所属应用解析、x64/x86隔离探针、重复 P95与聚合基准、中文诊断工作台、版本化备份、Exact/Merge恢复、可逆禁用、菜单注册删除、受控 UAC helper和正式应用卸载规划。

## 安全边界

- 主程序不加载第三方 Shell 扩展。
- 扫描和性能探针默认不提权，也不调用菜单命令。
- 禁用、删除和卸载属于不同操作；任何不可逆操作都必须先备份并预览影响范围。
- 菜单注册备份不能重新安装已经卸载的应用或恢复已删除的二进制文件。
- 合成探针结果用于定位嫌疑项，不等同于 Explorer 的绝对端到端延迟。
- 默认不联网、不发送遥测；结构化日志写入 `%LocalAppData%\RightMenuCheck\Logs`，完整磁盘路径默认脱敏。

## 构建

```powershell
dotnet restore RightMenuCheck.slnx
dotnet build RightMenuCheck.slnx --configuration Debug --no-restore
dotnet test RightMenuCheck.slnx --configuration Debug --no-build
```

所有构建产物统一写入仓库内的 `artifacts` 目录。
