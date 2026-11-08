# MDEditor

[English](README.md)

macOS 原生 Markdown 编辑器：**左边写，右边实时渲染**。Swift + SwiftUI/AppKit 编写，预览用 WKWebView，
所有渲染库都打包在 app 内，**完全离线可用**。支持 macOS 13+，Universal 二进制（Apple 芯片 + Intel）。

> **Windows 版**：计划用原生 WinUI 3 实现，目录在 [`windows/`](windows/)，目前尚未提供。

## 安装

### Homebrew

```bash
brew tap JYe16/tap
brew trust --tap JYe16/tap        # 较新的 Homebrew 要求对第三方 tap 显式授信
brew install --cask mdeditor
```

### DMG

到 [Releases](../../releases) 下载 `MDEditor-<版本>.dmg`，双击打开，把 **MDEditor** 拖进「应用程序」。
也可以自己打包后拷到另一台 Mac（AirDrop / U 盘 / 网盘均可），见下方「构建与打包」。

### 首次打开

MDEditor 只做了 ad-hoc 签名，**没有经过 Apple 公证**（没有付费开发者账号）。通过 Homebrew 安装时，cask 会帮你清除下载隔离标记；
如果是用 DMG 安装、第一次打开被 macOS 拦截，任选一种：

- 系统设置 → 隐私与安全性 → 滚到底，点 MDEditor 旁的 **「仍要打开」**
- 或终端执行一次：`xattr -dr com.apple.quarantine /Applications/MDEditor.app`

用 U 盘 / scp / 局域网共享拷贝通常不会被拦截。iPhone / iPad 无法安装这个 app —— 它是 macOS 应用。

## 功能

- **分栏实时预览**（默认），也可切到仅编辑 / 仅预览：`⌘1` `⌘2` `⌘3`，或点工具栏按钮
- **按源码行对齐的滚动同步**：滚任意一边，另一边跟着走；点预览里的 `#锚点` 链接，编辑器也会跳过去
- GitHub 风格 Markdown：表格、任务列表、删除线、自动链接、`> [!NOTE]` 提示块、YAML front matter
- 代码块语法高亮（43 种语言，含 Swift、Python、LaTeX、MATLAB、Julia…）
- 数学公式（KaTeX）：行内 `$E=mc^2$`，块级 `$$ … $$`；`$5 和 $10` 这类价格不会被误判
- 编辑器语法着色（标题、粗斜体、代码、链接、列表、引用…），跟随浅色 / 深色模式
- 回车自动续写列表 / 任务 / 引用，空项再回车结束列表；`Tab` / `⇧Tab` 缩进列表
- 中文输入法友好：拼音组字过程中不会触发高亮和渲染
- 查找替换（`⌘F`）、打印 / 导出 PDF（`⌘P`）、放大缩小（`⌘+` `⌘-` `⌘0`）、字数 / 阅读时间状态栏（CJK 按字计）
- 预览里的相对路径图片按文档所在目录解析；外部链接用默认浏览器打开，相对路径的 `.md` 链接在本 app 打开
- 安全：预览用 DOMPurify 过滤，文档里的 `<script>` 等不会执行
- 界面语言：English / 简体中文（随系统）

暂不支持：Mermaid 图表、脚注、emoji 短代码。

### 快捷键

| 操作 | 快捷键 | | 操作 | 快捷键 |
| --- | --- | --- | --- | --- |
| 粗体 | `⌘B` | | 一 / 二 / 三级标题 | `⌥⌘1` `⌥⌘2` `⌥⌘3` |
| 斜体 | `⌘I` | | 引用 | `⌘'` |
| 删除线 | `⇧⌘X` | | 无序列表 | `⇧⌘8` |
| 行内代码 | `⌥⌘C` | | 任务列表 | `⌥⌘T` |
| 链接 | `⌘K` | | 代码块 | `⌥⌘K` |

## 构建与打包

需要 Xcode（或 Command Line Tools）。不需要 Node：渲染库已放在 `Resources/preview/vendor/`。

```bash
./scripts/build.sh                     # 生成 build/MDEditor.app（Universal，ad-hoc 签名）
./scripts/make-dmg.sh                  # 生成 build/MDEditor-<版本>.dmg
VERSION=1.1.0 ./scripts/make-dmg.sh    # 指定版本号
./scripts/test.sh                      # 编辑命令的无界面回归测试（39 项）
```

开发时可以直接 `swift build && swift run`（在仓库根目录运行，会从 `Resources/preview` 读取预览页面）。

> 如果想免去「首次打开」的提示，需要加入 Apple Developer Program（$99/年），用 Developer ID 签名并公证，
> 再把 `scripts/build.sh` 里的 `codesign --sign -` 换成自己的证书即可。

## 目录结构

```
Package.swift
Sources/MDEditor/
  MDEditorApp.swift          App 入口、菜单命令（视图 / 格式 / 查找 / 打印）
  MarkdownDocument.swift     FileDocument：读写 .md（UTF-8，兼容常见旧编码）
  ContentView.swift          主界面：工具栏切换、状态栏、字数统计
  SplitEditorView.swift      分栏容器：编辑器 + WKWebView + 滚动同步 + 渲染调度
  MarkdownTextView.swift     NSTextView 子类：列表续写、Tab、格式命令
  MarkdownHighlighter.swift  编辑器语法着色
Resources/
  Info.plist                 .md 文件类型关联
  preview/                   预览页面：index.html、app.js（增量渲染）、vendor/（marked、DOMPurify、highlight.js、KaTeX…）
  *.lproj/                   本地化
  AppIcon.icns、AppIcon.png
scripts/                     build.sh、make-dmg.sh、make-icon.swift、test.sh、发布辅助脚本
Tests/EditingTests/          无界面回归测试
windows/                     Windows（WinUI 3）版本——筹备中
examples/sample.md           功能演示文档
```

第三方库及其许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 实现备注

- 编辑器用 **TextKit 1**：滚动同步需要精确的行几何。文本容器宽度由 `MarkdownTextView` 自己维护并设下限，
  因为 TextKit 1 在容器宽度 ≤ 0 时会以 100% CPU 无限排版（窗口刚创建、布局尚未完成时会出现）。
- 预览按顶层块增量渲染：改一处只重建对应的块，其余 DOM 保持不变，所以不闪烁、图片不重载。
- 预览页和 app 之间的滚动同步用「源码行号」做坐标，而不是滚动百分比，所以长文档里也能对齐。

## 参与贡献

欢迎提 issue 和 PR。修改了编辑行为的话，请先跑一遍 `./scripts/test.sh`。发布流程见 [docs/RELEASING.md](docs/RELEASING.md)。

## 许可证

[MIT](LICENSE) © 2026 JYe16。内置的第三方库各自保留其许可证，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
