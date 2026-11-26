# 贡献插件指南 (Contributing)

欢迎为 **RightToolbox** 社区插件仓库贡献通用、实用的右键文件工具！本仓库采用类似 [magpie-community/plugins](https://github.com/magpie-community/plugins) 的协作与自动发布机制：PR 通过校验并合并到 `main` 后，GitHub Actions 会自动将每个版本构建打包到 `dist` 分支，RightToolbox 客户端即可直接从「社区市场」拉取与校验安装。

## 1. 快速新建一个插件

我们提供 **C# (.NET 10)**、**PowerShell**（Windows 原生免编译）、**Python 3**、**Node.js** 四套零依赖 SDK 与模板：

```powershell
# 例：创建一个 PowerShell 插件
.\scripts\new-plugin.ps1 -Slug my-tool -Name "我的通用工具" -Template powershell -Group "实用" -Description "一句话说明用途" -Register

# 或创建一个 C# (.NET 10) 插件
.\scripts\new-plugin.ps1 -Slug my-csharp-tool -Name "我的高性能工具" -Template csharp -Group "文件" -Description "一句话说明用途" -Register
```

## 2. 目录与规范要求

每个插件位于 `packages/<slug>/`，需包含：

- `plugin.json`：符合 `schemas/plugin.schema.json` 的 v1 清单
  - `id` 使用小写域名格式（推荐 `community.<slug>` 或 `<org>.<slug>`），**禁止使用 `core.`**
  - `version` 遵循 SemVer（如 `0.1.0`），每次发布更新必须递增版本号
  - `runtime` 必须为 `"process"`
  - `entry` 为包内相对入口文件（`.exe`、`.ps1`、`.py`、`.mjs`/`.js`）
  - 如实声明 `permissions`（`fs.read`、`fs.write`、`clipboard.write`、`network`、`process.inspect`、`process.spawn`）
- `README.md`：说明命令用途、参数与注意事项
- `LICENSE`：明确开源许可证（推荐 MIT）
- 单元测试：`*.Tests.csproj` / `*test*.ps1` / `test_*.py` / `*.test.mjs`（必须在临时目录运行，不得触碰用户真实文件或外部网络）
- 在 `registry.json` 添加对应的市场登记项与中英文 `summary`

## 3. 安全与可用性原则

1. **绝不静默覆盖原文件**：转换、格式化、清理、解码类工具默认使用新文件名（SDK 已提供 `UniqueOutput` / `Get-RtUniqueOutput`）。
2. **破坏性操作必须先预览**：修改原文件元数据或批量移动/重命名时，请设置 `"requiresPreview": true` 并验证计划再提交。
3. **支持完整多选与 Unicode**：从 `Invocation.Paths` 一次性读取完整选区数组，避免在插件内自行拼接命令行字符串。
4. **不要包含硬编码外部下载或遥测**：插件应本地优先、无账号即可使用。

## 4. 本地验证与打包预检

提交 PR 前请在仓库根目录运行：

```powershell
# 1. 清单规范 + 唯一性 + 协议握手烟测
.\scripts\check.ps1

# 2. 运行所有插件的单元测试
.\scripts\test.ps1

# 3. 验证打包生成 zips/*.zip 与 index.json（含 SHA-256）
.\scripts\dist.ps1
```

PR 提交后，`.github/workflows/pr.yml` 会在 `windows-latest` 上自动执行以上三步校验；合并入 `main` 后，`.github/workflows/publish.yml` 会自动发布更新到 `dist` 分支。
