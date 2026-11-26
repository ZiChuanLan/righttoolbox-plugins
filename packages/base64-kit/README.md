# Base64 / Data URI 编解码 (`community.base64-kit`)

基于 Windows 原生 PowerShell 的**免编译**脚本插件示例：直接阅读并修改脚本即可生效，打包发布时自动内联 `RightToolbox.Plugin.psm1`。

## 命令

- **文件转 Base64 / Data URI** (`community.base64-kit.encode`)：支持生成带 MIME 前缀的 Data URI 或纯 Base64 字符串，一键写入剪贴板。
- **Base64 文本还原为文件** (`community.base64-kit.decode`)：自动剥离 Data URI 头并按 MIME 推断扩展名。
