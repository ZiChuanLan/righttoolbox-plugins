# JSON 批量格式化

纯 C# 实现（`System.Text.Json`），**不需要 Node.js**。

选中 `.json` / `.jsonc` / `.webmanifest` → **开发 → 格式化 JSON 文件**。

- **美化（2 / 4 空格）**：保留原 CRLF/LF 风格与 UTF-8 BOM
- **压缩为单行**：去掉全部空白
- **按字母排序键后美化**：递归按键名排序
- **仅校验，不写回**：只报告语法问题

中文不会被转义成 \uXXXX（使用 UnsafeRelaxedJsonEscaping）；支持注释与尾随逗号（`.jsonc`）。

预览阶段列出每个文件能否处理，确认后才写回；默认生成 `.bak`，同名备份已存在时不再覆盖。
