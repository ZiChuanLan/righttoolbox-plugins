# 文本批处理工具 (`community.text-ops`)

通用文本批量处理插件，全部本地离线运行，不覆写原文件。

## 包含命令

- **文本字数与行数统计** (`community.text-ops.stats`)：统计总行数、非空行、字符数、词数（支持中文按字与英文按词计数）、编码（UTF-8 / BOM / UTF-16 / GBK）与换行符格式。
- **文本去重与排序清理** (`community.text-ops.cleanup`)：支持按行去重、字典序/长度排序、清理首尾空白与移除空行，输出为 `*_clean` 新文件。
- **多文本文件顺序合并** (`community.text-ops.merge`)：按选中顺序合并多个文本、日志或 Markdown 文件，可选文件名分隔头。

## 测试

```powershell
..\..\scripts\check.ps1 text-ops
..\..\scripts\test.ps1 text-ops
```
