# Spotlight 搜索（从 V-Notch 拆出）

从 [w0otdev/V-Notch](https://github.com/w0otdev/V-Notch) 里拆出来的搜索功能：
一个 `Alt+Space` 唤出的独立搜索框，界面直接照搬原项目的 XAML，带 **AI 对话**，
外加一个设置面板。

原项目是 4 万行 C# + 4 万行 C++ 的 Windows「灵动岛」应用，搜索只是其中一个模块，
但被焊接在原生 DLL、Liquid Glass 渲染、刘海形变和设置系统上。这里把搜索摘干净了：
**没有 `VNotch.Native.dll`，没有 DirectX，纯托管代码。**

---

## 跑起来

需要 .NET 10 桌面运行时（`Microsoft.WindowsDesktop.App`）。

```bash
dotnet build src/SpotlightSearch/SpotlightSearch.csproj -c Release
dotnet run --project src/SpotlightSearch/SpotlightSearch.csproj
```

启动后没有窗口，只在托盘里。按 `Alt+Space` 呼出（可在设置里改）。

命令行参数（调试/快捷方式用）：

| 参数 | 作用 |
|---|---|
| `--show` | 启动后立刻打开搜索框 |
| `--query "关键词"` | 打开并预填查询 |
| `--ai` | 打开并直接进入 AI 模式 |
| `--settings` | 直接打开设置窗口 |

### 按键

| 按键 | 行为 |
|---|---|
| `Alt+Space` | 打开 / 关闭 |
| `↑` `↓` `PgUp` `PgDn` | 移动选择 |
| `Enter` | 打开（算式则复制结果） |
| `Ctrl+Enter` | 在资源管理器中定位 |
| `Ctrl+Shift+Enter` | 以管理员身份运行 |
| `1`–`9` | 直接打开第 N 条结果 |
| `Tab` | 切换 搜索 ↔ AI |
| `Esc` | 关闭 |
| 右键结果 | 打开 / 管理员运行 / 定位 / 复制路径 / 从历史移除 |

关机、重启、注销、休眠要按两次 `Enter`：第一次只是把底栏变成确认提示。
这是原项目 `Confirm` 标记的行为。

---

## 界面：照搬原项目

`Windows/SpotlightWindow.xaml` 是把原 `SpotlightWindow.xaml` 的视觉结构搬过来的：

- 窗口 968×650、外壳左右各留 124 → 可见药丸 720 宽，圆角 14，纯黑底，投影挂在独立图层上
- 搜索行高 64，20px 粗体输入框，自绘发光光标，右侧 `TAB · AI` 徽章 + 齿轮
- 结果行：46px 图标列 + 32×32 图标，标题 15px / 副标题 11.5px，右侧 `1`–`9` 快捷键徽章
- 悬停 `#10FFFFFF` 淡入 100ms，选中态是一条独立滑动的色块 + 3px 左侧高亮条（带缓动滑行）
- 空状态是居中的图标 + 标题 + 提示（原 `StatusPanel`），不是一行灰字
- AI 面板：返回 / 模型标签 / 字数统计 / 复制 / 历史 / 新对话，欢迎页 ✦ + 两条建议，
  回答是 markdown 块（标题、列表、代码块、粗体、行内代码），带每条独立复制按钮和红色停止按钮
- 失败提示是底部 `FailureBar` + 窗口抖动，和原项目一致

### 没搬的部分（都有原因）

| 原功能 | 为什么没有 |
|---|---|
| Liquid Glass 材质栈 | DirectX 11 + D3D9Ex + HLSL，整个 `Shaders/` 目录。这里用 DWM 合成 + 圆角 + 投影 |
| 刘海形变 / 展开收起动画 | 依赖 `ISpotlightMorphHost`、MainWindow、NotchSettings。改成了 140ms 淡入 + 0.98→1 缩放 |
| 入场音效（`spotlight-sfx`） | 需要原生音频输出 |
| AI 逐词淡入（word fade reveal） | 依赖原生分词扫描 + 自定义 `StreamRenderer`。这里按 90ms 节流整体重渲染 |
| 固定项 / 别名 / 排除目录 | 依赖 `SpotlightPreferences` 的完整设置模型，设置面板里没放 |

---

## AI

`Ai/AiService.cs` 是原 `SpotlightAiService.cs` 的托管重写，SSE 解析、增量提取、用量统计
都改成纯 C#。支持：

- **OpenAI** `api.openai.com/v1/chat/completions`
- **DeepSeek** `api.deepseek.com/chat/completions`
- **Claude** `api.anthropic.com/v1/messages`
- **Gemini** `streamGenerateContent?alt=sse`（跳过 thought 片段）
- **Custom** —— 任何 OpenAI 兼容接口，填 base URL 即可

行为：

- 流式输出，停止按钮随时打断；打断/失败的提问会退回输入框，可以直接重发
- 上下文只保留最近 21 条且总量 ≤ 64k 字符，但界面上保留完整对话
- 错误只映射成固定中文文案（401/403/404/429/5xx），**不会把 provider 的原文、URL、header 带进界面**
- API Key 用 **DPAPI（当前用户）加密**后存 `settings.json`，和原项目一致

---

## 设置

齿轮按钮 / 托盘菜单「设置…」/ `--settings`。存在 `%APPDATA%\SpotlightSearch\settings.json`。

| 设置项 | 说明 |
|---|---|
| 快捷键 | 任意 `Ctrl/Alt/Shift/Win + 键` 组合，格式校验不过不让保存 |
| 打开时进入 AI 模式 | 默认面板 |
| 结果数量 | 4–20 |
| 记录使用历史 | 关掉后不记录、不参与排序 |
| 搜索磁盘文件 | 关掉后只搜应用/设置/算式，不碰磁盘 |
| 文件搜索耗时上限 | 200–5000ms，越小越省电 |
| 开机自启 | 写 `HKCU\...\Run` |
| AI 服务商 / 模型 / API Key / 自定义地址 | 见上 |
| 清空使用历史 / 恢复默认 | — |

---

## 代码地图

```
src/SpotlightSearch/
├── App.xaml(.cs)                 单实例、托盘、热键装配、CLI 参数
├── Models/SpotlightSearchItem.cs
├── Search/                       搜索内核（不依赖 WPF，只有 Icon 字段例外）
│   ├── SearchText.cs             归一化 + 分词打分（移植自原生 C++）
│   ├── Ranker.cs                 排序加权
│   ├── FileVisibility.cs         可见性策略
│   ├── SystemCatalog.cs          37 条固定目标
│   ├── UsageStore.cs             历史与加权
│   ├── SearchService.cs          流式合并
│   └── Providers/                应用 / 算式 / 系统命令 / 磁盘文件
├── Ai/AiService.cs               5 家 provider + SSE 流式
├── Controls/MarkdownRenderer.cs  轻量 markdown → FlowDocument
├── Interop/                      Win32、DPAPI、图标
├── Input/                        热键（RegisterHotKey + LL 钩子兜底）、快捷键解析
├── Settings/                     AppSettings + settings.json + DPAPI
├── ViewModels/SpotlightViewModel.cs  结果 diff 更新 + 图标异步水合
├── Shell/TrayIcon.cs
└── Windows/                      搜索窗口（含 AI 部分）+ 设置窗口
```

---

## 验证边界（重要）

- **编译**：Debug / Release 均 0 error、0 warning，已验证。
- **运行**：进程能起来、`Alt+Space` 能注册、窗口能按预期位置出现、搜索能返回真实结果
  （截图里确认过 Chrome 应用图标、文件行、状态面板都在）—— 这些是实际跑过的。
- **没验证的**：
  - AI 链路没有真实 API Key，**没有跑通过一次完整对话**。SSE 解析、请求体、错误映射是照着原实现写的，
    但没经过真实 provider 往返。
  - 设置窗口的控件交互、开机自启写注册表，只做了代码走查，没点过。
  - 快捷键改绑后热键重新注册的路径没实测。
  - 高 DPI 下（125%/150%）窗口位置与裁剪没有逐项核对。
- **AI 历史**只存在内存里，退出即失（原项目会落盘加密保存）。

## 已知限制

- **文件搜索是全盘扫，不是索引**。每次查询最多 1.5 秒（可调），扫到哪算哪。
  想要秒出结果就接 Everything 的 IPC —— 原项目的 `EverythingSearchProvider.cs` 可以直接照搬。
- **`Alt+Space` 常被别的软件占着**（PowerToys、部分输入法）。占用时退回键盘钩子，
  代价是全局吞掉这个组合。
- 界面固定深色，不跟随系统主题。
