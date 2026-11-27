# Base64 / Data URI 编解码

纯 C# 实现，**不需要 PowerShell / Python / Node**，与其他插件一样是真 exe。

## 编码（选中文件）

- 输出格式：`data:<mime>;base64,...` 或纯 Base64
- 结果自动进剪贴板（由宿主写入），可同时保存 `原文件名.b64.txt`
- 多选时按行拼接，超过 2 MB 会提示改用写文件

## 解码（选中任意文件决定保存位置）

把 Base64 或 Data URI 粘到「内容」参数里，插件会剥掉 `data:...;base64,` 前缀、忽略所有空白与换行，
再按 Data URI 里的 MIME 推断扩展名（无法识别时输出 `.bin`）。

## 迁移说明

0.2.0 起入口从 `main.ps1` 换成 `Base64Kit.exe`，参数 key 保持兼容（`outputMode` / `saveToFile` / `outputFolder`），
旧版升级到新版不会有参数错乱。
