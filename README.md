# RightToolbox Community Plugins

[RightToolbox](https://github.com/ZiChuanLan/RightToolbox) 的开源社区插件生态仓库（架构参考 [magpie-community/plugins](https://github.com/magpie-community/plugins)）。

每个插件均遵循 RightToolbox v1 UTF-8 JSON Lines 独立进程协议，无需修改或重编译宿主即可直接进入 Windows 资源管理器右键菜单、快捷浮窗与完整工作台。

## 先装宿主

插件跑在 [RightToolbox](https://github.com/ZiChuanLan/RightToolbox/releases/latest) 里，本体不需要任何第三方引擎：

- `RightToolbox-x.y.z-setup.exe`（约 6.5 MB）：缺 .NET 10 桌面运行时时会引导下载
- `RightToolbox-x.y.z-setup-offline.exe`（约 63 MB）：自带运行时，无网也能装

装到 `%LOCALAPPDATA%\Programs\RightToolbox`，不需要管理员权限。

## 宿主内置与本仓库的边界

宿主自带 **27 个内置命令**，本仓库的 **19 个插件补的是内置没做的另一半**，两边都不重复造第三方已有的东西：

| 宿主已内置 | 本仓库补充 |
| --- | --- |
| 图片压缩 / 转格式 / 缩放 / 去 EXIF | 压到指定大小、拼长图、批量水印、证件照、相似照片去重、方向修正、边框圆角 |
| 图片合并为 PDF、Office 转 PDF、PDF 转图片 | PDF 合并 / 拆分 / 按页码提取 |
| ZIP / TAR 打包解压 | 乱码文件名修复 |
| 文本统计与格式化 | 跨文件批量替换、JSON 批量格式化、Base64 编解码 |
| —— | 手机照片整理、清理垃圾文件、生成二维码 |

宿主负责日常高频，插件负责补齐边角场景。`.7z` / `.rar` 解压这类系统里已有成熟工具的事，两边都不实现。

## 已收录插件

| 包目录 | 插件 ID | 名称 | 语言 | 说明 |
| --- | --- | --- | --- | --- |
| [text-ops](packages/text-ops) | `community.text-ops` | 文本批处理工具 | C# (.NET 10) | 文本字数/行数/编码统计、去重排序清理、多文件顺序合并 |
| [table-convert](packages/table-convert) | `community.table-convert` | 表格与数据转换 | C# (.NET 10) | CSV/TSV ↔ JSON 对象数组 / Markdown 表格 / UTF-8 BOM CSV |
| [dir-tree](packages/dir-tree) | `community.dir-tree` | 目录树与文件清单 | C# (.NET 10) | 一键生成 Unicode/ASCII/Markdown 目录树到剪贴板，导出 CSV/JSON 清单 |
| [file-time](packages/file-time) | `community.file-time` | 批量调整文件时间 | C# (.NET 10) | 创建/修改时间同步、时区偏移与文件名日期提取（带预览确认） |
| [base64-kit](packages/base64-kit) | `community.base64-kit` | Base64 / Data URI 编解码 | C# | 文件转 Base64 / Data URI 写入剪贴板，或把 Base64 还原成文件 |

| [gbk-fix](packages/gbk-fix) | `community.gbk-fix` | 乱码文件名修复 | C# | 还原 ZIP/FTP 带来的中文乱码文件名，先预览再改名 |
| [json-format](packages/json-format) | `community.json-format` | JSON 批量格式化 | C# | 美化/压缩/校验/按键排序，预览后写回并保留 .bak |
| [text-replace](packages/text-replace) | `community.text-replace` | 批量文本替换 | C# | 跨文件字面/正则替换，UTF-8、GBK 自动探测 |
| [image-shrink](packages/image-shrink) | `community.image-shrink` | 图片压到指定大小 | C# | 报名/上传前压到 100KB–2MB，先降质量再缩尺寸 |
| [image-collage](packages/image-collage) | `community.image-collage` | 拼长图 / 九宫格 | C# | 竖向横向下长图、3×3、2×2，等比留白不裁切 |
| [image-watermark](packages/image-watermark) | `community.image-watermark` | 批量加文字水印 | C# | 四角/居中/斜向平铺，字号自适应、透明度可调 |
| [id-photo](packages/id-photo) | `community.id-photo` | 证件照裁切 | C# | 一寸/二寸/小一寸/大一寸，居中裁切或完整留白 |
| [photo-dedupe](packages/photo-dedupe) | `community.photo-dedupe` | 相似照片清理 | C# | 感知哈希找连拍与转发图，每组保留最清晰那张 |
| [photo-import](packages/photo-import) | `community.photo-import` | 手机照片整理 | C# | 按拍摄日期改名 + 年/月归档 + HEIC 转 JPG + 可选压缩 |
| [clean-junk](packages/clean-junk) | `community.clean-junk` | 清理垃圾文件与空文件夹 | C# | Thumbs.db、临时文件、空目录，先预览再删 |
| [image-orient](packages/image-orient) | `community.image-orient` | 照片方向修正 | C# | 按 EXIF 自动转正，或手动旋转/镜像 |
| [image-frame](packages/image-frame) | `community.image-frame` | 批量加边框 / 圆角 | C# | 白边、拍立得、细黑边、圆角透明 |
| [qr-code](packages/qr-code) | `community.qr-code` | 生成二维码 | C# | 网址/文字/WiFi 转 PNG 二维码 |
| [pdf-merge](packages/pdf-merge) | `community.pdf-merge` | PDF 合并 / 拆分 / 提取 | C# | 合并、按页拆分、按 1-3,5 提取页码 |
## 安装方式

### 方式 A：在 RightToolbox 社区市场一键安装（推荐）

1. 打开 RightToolbox → **「插件」** → 切换到 **「社区市场」**。
2. 仓库源输入 GitHub 仓库地址（如 `ZiChuanLan/righttoolbox-plugins`）或本地克隆目录（如 `H:\WorkSpace\righttoolbox-plugins`）。
3. 点击 **「同步目录」** → 选择插件点击 **「安装」**（安装前自动核验 `dist` 分支 `index.json` 的 SHA-256 与能力权限声明）。
4. 到 **「设置 → 安装 / 更新右键菜单」** 刷新 Windows 右键级联子菜单即可生效。

### 方式 B：离线打包与本地导入

```powershell
# 在仓库根目录构建并打包所有插件为 .zip（输出至 artifacts/dist/）
.\scripts\dist.ps1
```

随后可在 RightToolbox **「插件 → 导入 ZIP 包」** 选择 `artifacts/dist/zips/<id>-<version>.zip` 安装。

## 多语言 SDK 与快速制作插件

仓库内置 4 套零依赖 SDK 与模板，无论熟悉哪种语言都能 3 分钟写出一个插件：

- **C# (.NET 10)**：[`sdk/csharp/PluginHost.cs`](sdk/csharp/PluginHost.cs) — 随宿主共用 .NET 10 运行时，编译产物仅 ~150 KB，开箱即用。
- **PowerShell**：[`sdk/powershell/RightToolbox.Plugin.psm1`](sdk/powershell/RightToolbox.Plugin.psm1) — Windows 10/11 原生自带，真正免编译即改即用。
- **Python 3**：[`sdk/python/righttoolbox_plugin.py`](sdk/python/righttoolbox_plugin.py) — 纯标准库封装（含后台 `CANCEL` 协同取消线程）。
- **Node.js / Bun**：[`sdk/node/righttoolbox-plugin.mjs`](sdk/node/righttoolbox-plugin.mjs) — 纯 ESM 封装（含 `AbortSignal` 取消支持）。

一键生成新插件骨架：

```powershell
.\scripts\new-plugin.ps1 -Slug my-tool -Name "我的工具" -Template powershell -Group "实用" -Register
```

本地验证：

```powershell
.\scripts\check.ps1     # 校验 registry.json、plugin.json、目录安全与真实 JSON Lines 协议握手
.\scripts\test.ps1      # 运行所有插件的独立单元测试
.\scripts\dist.ps1      # 编译 C# 插件、内联脚本 SDK 并打包 zips/*.zip + index.json
```

详细贡献规范请参阅 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 自动发布机制（`dist` 分支）

1. 贡献者向 `main` 提交 PR（`.github/workflows/pr.yml` 自动执行 `check.ps1`、`test.ps1` 与 `dist.ps1` 预检）。
2. PR 合并进 `main` 后，`.github/workflows/publish.yml` 在 `windows-latest` 上编译构建全部插件，将独立可安装的 `.zip` 与含 `sha256` 校验的 `index.json` 推送到 `dist` 分支。
3. RightToolbox 客户端读取 `main/registry.json` 展示目录，并从 `dist/index.json` 拉取 `.zip` 进行 SHA-256 校验安装，无需维护单独的后端服务器或包管理器账号。

## License

仓库脚手架、SDK、模板及官方示例插件均采用 [MIT License](LICENSE)。各个社区插件的具体许可证以其自身 `plugin.json` 与 `LICENSE` 文件为准。
