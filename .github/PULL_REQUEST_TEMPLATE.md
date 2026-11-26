## 插件提交说明

- **插件 ID**：`community.<name>`（或 `<org>.<name>`，不得以 `core.` 开头）
- **目录**：`packages/<slug>/`
- **实现语言**：C# (.NET 10) / PowerShell / Python / Node.js / 其他独立可执行程序

## 自检清单

- [ ] 包含完整的 `plugin.json`、`README.md` 和 `LICENSE`
- [ ] 已在 `registry.json` 中登记插件条目与中英文 `summary`
- [ ] 未硬编码覆盖源文件，转换/导出类操作生成新输出或支持预览确认
- [ ] 在隔离临时目录下运行过单元测试且通过：
  - `.\scripts\check.ps1 <slug>`
  - `.\scripts\test.ps1 <slug>`
- [ ] 若改动插件功能，已递增 `plugin.json` 中的 `version`（合并到 `main` 后将自动发布到 `dist` 分支）
