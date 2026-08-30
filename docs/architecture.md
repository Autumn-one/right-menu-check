# 架构与信任边界

## 进程模型

| 组件 | 职责 | 默认权限 | 是否允许加载第三方代码 |
| --- | --- | --- | --- |
| `RightMenuCheck.App` | WPF 展示、任务编排、用户确认 | 标准用户 | 否 |
| `RightMenuCheck.Windows` | 只读盘点、Windows 元数据与受控操作实现 | 标准用户 | 否 |
| `RightMenuCheck.Probe.Worker` | 单项 Shell 扩展计时 | 标准用户、受 Job Object 约束 | 是，仅当前被测项 |
| `RightMenuCheck.Elevated` | 白名单内的机器级注册表或卸载操作 | 按需管理员 | 否 |

主程序和提权助手不得通过反射、`rundll32` 或任意命令行加载被扫描到的 DLL。第三方 COM 激活只允许发生在一次性探针进程中。

## 项目分层

- `RightMenuCheck.Core`：领域模型、统计、备份契约和平台无关规则。
- `RightMenuCheck.Windows`：注册表、COM、包、签名和卸载归属解析。
- `RightMenuCheck.Probe.Protocol`：主程序与探针/提权助手共享的版本化消息协议。
- `RightMenuCheck.Probe.Worker`：x64/x86 隔离测试宿主。
- `RightMenuCheck.Elevated`：最小权限、严格白名单的按需提权助手。
- `RightMenuCheck.App`：中文 WPF 工作台。

## 数据来源

注册表盘点必须分别读取 `HKCU\Software\Classes` 与 `HKLM\Software\Classes` 的 32/64 位视图，保留真实来源后再计算合并视图。不得只依赖 `HKCR`，因为它会隐藏来源和遮蔽关系。

现代菜单还需读取 AppX/MSIX 包清单和 Packaged COM 注册。系统 AppRepository 只读，任何管理动作必须使用受支持的包管理 API。
