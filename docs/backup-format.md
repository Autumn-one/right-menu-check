# `.rmcbak` 备份格式

当前格式版本为 `1`。文件是 ZIP 容器，只允许以下两个根条目：

- `manifest.json`：菜单注册、注册表快照、Owner/Group/DACL、包身份以及引用文件的版本、架构、签名和 SHA-256 元数据。
- `integrity.json`：格式版本、`SHA-256` 算法名和 `manifest.json` 的摘要。

读取器限制归档最大 256 MiB、单条目最大 64 MiB，拒绝缺失或重复条目、未知字段、版本不匹配及摘要不匹配。摘要用于发现损坏或未同步的修改，不提供来源真实性；格式目前没有数字签名。

注册表值按原始类型保存。`REG_EXPAND_SZ` 不展开，`REG_MULTI_SZ` 保留元素顺序，二进制使用 Base64，DWORD/QWORD 使用 64 位数值字段。每个键保留 HKCU/HKLM、32/64 位视图和完整键路径。

备份不复制 DLL、EXE、应用安装包或用户文件。文件记录仅用于核对路径、版本、架构、签名和哈希。因此：

- 可以恢复仍有二进制支持的经典菜单注册。
- 不能重新安装已卸载应用或恢复被删除的二进制。
- AppX/MSIX 菜单只保存包身份和清单证据；不得通过写 AppRepository 还原。

破坏性操作必须要求 `IsComplete=true`。安全描述符、键或值读取失败会使备份不完整，并阻止后续禁用、删除或覆盖恢复。

## 恢复与操作日志

恢复预检比较当前键值和 SDDL，并区分缺失键、缺失值、变化值、额外当前值及权限差异。存在冲突时必须由调用方明确确认。

- `Merge`：写回备份中的键值，但保留当前额外值和子键。
- `Exact`：删除选定注册根后按备份重建；这是回到备份状态的推荐模式。

每次注册表写入在 `%LocalAppData%\RightMenuCheck\Journals` 对应目录保存版本化 JSON 日志。状态依次为 `Prepared`、`Applying`、`Completed`；失败时为 `RolledBack` 或 `RollbackFailed`。执行器在首个写入前捕获受影响根的完整状态，任一步失败后按该状态精确重建。

经典处理器禁用使用当前用户 `Shell Extensions\Blocked`，会影响同 CLSID 的所有菜单范围。静态、级联和 DelegateExecute命令使用注册键 `LegacyDisable`。AppX/MSIX 单命令没有受支持的注册表禁用操作。
