# 首次上传与发布

本目录使用独立 Git 历史。旧开发目录中的 `.git`、运行配置、诊断记录与构建产物不属于公开源码。此文档按 GitHub 仓库名 `yushuovo/CodexBar` 编写；若使用其他仓库名，先同步修改 README 的徽章、下载与克隆链接。

## 1. 创建 GitHub 仓库

在 GitHub 中创建名为 `CodexBar` 的 **Public** 仓库，不初始化 README、`.gitignore` 或许可证。本地已提供这些文件以及 MIT 许可证。

GitHub 邮箱设置中启用 **Keep my email addresses private**，并建议启用 **Block command line pushes that expose my email**。网页设置不会自动改写本地 Git 提交身份，也不清理文件正文或已有历史。

## 2. 检查本地独立历史与身份

在本公开版目录打开 PowerShell，先检查：

```powershell
git rev-parse --show-toplevel
git log --format=fuller
git config --local user.email
git status --short
```

仓库根目录应为当前公开版目录，首次迁移后的历史应只有一个根提交；作者和提交者邮箱应为 GitHub 提供的 `noreply` 地址。该地址仍关联公开 GitHub 账号，不表示匿名。真实邮箱、令牌、个人运行数据不要写进文件或提交说明。

## 3. 首次推送

本地不会预先关联远程。确认新仓库地址后执行：

```powershell
git remote add origin git@github.com:yushuovo/CodexBar.git
git push -u origin main
```

如果提示远程已有提交，先核实是否推到了刚创建的新仓库，不要直接使用强制推送。不要从旧开发目录推送旧历史到这个新仓库。

源码推送会触发现有 **Windows build** 检查，它只构建、运行离线测试并保留 Actions 附件，不会自动创建 Release。

## 4. 准备 Release 文件

先确认对应源码已经提交、推送，且 GitHub Actions 检查通过。在公开版目录执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release.ps1
```

当前版本生成 `release/v0.6.0/`：

| 文件 | 用途 |
| --- | --- |
| `CodexBar.exe` | 可直接下载运行的主程序 |
| `CodexBar-v0.6.0-windows.zip` | 主程序、说明、许可证与更新记录 |
| `SHA256SUMS.txt` | 上述两个下载文件的 SHA-256 |
| `RELEASE-NOTES.md` | 复制到 GitHub Release 描述的正文 |

`release/` 与 `dist/` 被 Git 忽略；EXE 作为 Release 附件上传，不提交到源码历史。只选择表中的下载文件，不上传测试程序、个人配置或诊断资料。

## 5. 在网页发布

1. 进入仓库 **Releases → Draft a new release**。
2. 创建标签 `v0.6.0`，Target 选择已通过构建检查的 `main` 提交。
3. 标题填 `CodexBar v0.6.0`，描述粘贴 `RELEASE-NOTES.md` 的内容。
4. 在附件区上传 `CodexBar.exe`、`CodexBar-v0.6.0-windows.zip` 和 `SHA256SUMS.txt`。
5. 先保存草稿核对文件，确认后点击 **Publish release**，将正式版本设为 Latest。

发布后从网页重新下载 EXE，用 `Get-FileHash -Algorithm SHA256` 与本地校验文件比较，确认下载地址和附件正确。README 的“最新 Release”链接在首个正式 Release 发布后可用。

## 后续维护

开发目录修改完成后，按源码与文档文件清单同步到本目录，检查差异，再从本目录提交。不要整体复制开发目录或 `.git`，也不要合并旧开发历史。新增脚本、图片或文档应先检查内容再纳入公开版本。

发布新版本时，统一更新代码版本号、README、CHANGELOG 及 `docs/releases/v版本号.md`。打包脚本从程序集版本读取输出版本号；同一标签对应的代码和下载文件应保持一致，修复后使用新版本发布。
