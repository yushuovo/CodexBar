# 参与开发

欢迎通过 Issue 提交问题，也欢迎直接发起 Pull Request。

## 本地检查

在安装了 .NET Framework 4.8 的 Windows 环境中执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\dist\CodexBar.Tests.exe
```

常规测试使用本地模拟 App Server，不需要 Codex 账号、API Key 或网络。需要真实账户的在线探针与重启测试不会在 CI 中运行。

## 发布打包

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release.ps1
```

脚本重新构建并运行离线测试，通过后在 `release/v版本号/` 中生成 EXE、便携 ZIP、SHA-256 校验文件和发布说明。ZIP 只包含 EXE、README、MIT 许可证与更新记录；测试程序和运行数据不进入下载包。脚本不会连接 GitHub 或创建 Release，网页操作见 [发布步骤](PUBLISHING.md)。

修改连接恢复或托盘生命周期后，可以选择运行集成检查：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-restart.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-package-update.ps1
```

这些检查会关闭项目自己的运行实例，再启动测试实例；默认在完成后退出，避免占用项目目录。添加 `-KeepRunning` 可保留最后的实例。`test-tray-interaction.ps1` 用于检查正在运行的实例，需要先用 `verify.ps1 -KeepRunning` 启动。

## 代码结构

| 文件 | 职责 |
| --- | --- |
| `src/Program.cs` | 入口、单实例命令和纯托盘消息循环 |
| `src/CodexPanel.cs` | 右键菜单、额度与设置面板、刷新与通知 |
| `src/CodexQuotaClient.cs` | Windows 安装登记、App Server 通信和进程清理 |
| `src/Core.cs` | 配置、旧版迁移、额度解析和本地记录读取 |
| `src/Controls.cs` | 状态球与共用绘图 |
| `tests/CoreTests.cs` | 离线协议、解析、迁移与界面检查 |

## 提交问题

请提供 Windows 版本、CodexBar 版本、复现步骤及界面中的失败阶段。诊断文件保存在 `%LOCALAPPDATA%\CodexBar`；其中含本地路径与额度快照，提交问题时只摘录有关字段。登录令牌、Cookie、`auth.json` 和完整会话日志不属于排查材料。
