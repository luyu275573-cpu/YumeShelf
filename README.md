# YumeShelf

Windows 本地 Galgame 启动器，当前版本 0.2.0（可靠性修复版，尚未完成正式发行环境验收）。

## 当前能力

- 添加本地 `.exe` 游戏。
- 保存游戏目录、启动文件和工作目录。
- 搜索标题、引擎、标签和简介。
- 全部游戏、最近游玩、收藏筛选。
- 封面网格和紧凑列表视图切换。
- 大游戏库每页最多显示 60 项，搜索和排序覆盖全部条目。
- 启动游戏并监听进程退出。
- 记录最近游玩时间和累计游玩时长。
- 本地 JSON 原子保存、上一版备份、损坏数据只读恢复、单实例和保存冲突保护。
- 编辑标题、引擎、年份、工作目录、启动参数、封面和简介；游戏移动后可重新选择 EXE，保留游玩记录。
- 主窗口内导航：游戏库、设置和 AI 助手在右侧切页，左侧固定；支持上下过渡，切换时保留草稿和会话。
- 分类设置页：应用美化、AI 检索、页面分栏；保存或取消后留在当前页。
- 统一操作结果提示：设置保存、游戏管理和 API 测试等有明确反馈；游戏库写入失败保留修改并可重新保存。
- AI 基础问答：自配兼容 API 地址、Key 和模型，支持模型语义路由、固定短拒答及会话消息；联网检索和资料审核待开发。
- 背景图片预览、页面透明度、高斯模糊和简化/完整布局；确认后保存，取消丢弃更改。

## 开发环境

- Windows 10/11 x64
- .NET 8 SDK
- Visual Studio 2022（推荐）或 Rider

## 打开和运行

```powershell
dotnet restore .\YumeShelf.sln
dotnet build .\YumeShelf.sln
dotnet run --project .\src\YumeShelf\YumeShelf.csproj
```

也可以使用项目脚本：

```powershell
.\scripts\run.ps1
```

本机已安装 .NET 8 SDK。构建和运行脚本在 PATH 未刷新时会查找标准安装位置，验证记录见 `docs/development-log.md`。

统一验证（Release 构建、设置/导航/反馈回归、可靠性与模拟 AI 回归、发布资源检查；隔离数据，不调用真实 AI）：

```powershell
.\scripts\check.ps1
```

## 数据位置

添加游戏支持自动搜索和手动添加。识别 Ren'Py、BGI、KiriKiri、TyranoScript 和具有脚本/背景/语音组合的自定义结构；Unity 等通用引擎还需视觉小说上下文。扫描有深度、数量和时间限制，达到限制明确提示“扫描不完整”；候选经勾选确认后批量入库。

```text
%LOCALAPPDATA%\YumeShelf\library.json
%LOCALAPPDATA%\YumeShelf\settings.json
```

游戏本体文件不会被复制、移动或删除。删除游戏条目只会删除启动器中的记录。

损坏或无法完整读取游戏库时暂停写入，提示中可打开数据目录或确认恢复当前可读列表。恢复前保留原文件为 `library.json.recovery-*.json`，正常保存保留 `library.json.bak`。失败修改可重试，关闭应用前需处理待保存内容。诊断日志位于同目录 `logs/`，只记操作分类与异常类型，不记录 Key 或对话正文。

云端 AI 地址必须为 HTTPS，本机回环服务允许 HTTP。修改服务来源后需重新填写 Key。问题最多 6000 字符，整项任务超时可设 5–120 秒；失败、取消或截断不会写入成功历史，支持原消息重试。

检查产物在 `.runtime/checks/`，依赖框架的发布目录在 `.runtime/publish/`（需 .NET 8 Desktop Runtime）。本项目的回归是控制台程序，单独执行 `dotnet test` 不会运行这些检查。

## 项目结构

```text
src/YumeShelf/
├── Application/      游戏库和启动服务
├── Common/           MVVM 基础类和转换器
├── Domain/           游戏领域模型
├── Infrastructure/  本地文件存储和 Windows 进程启动
├── Presentation/    WPF 主窗口和 ViewModel
└── Resources/       颜色和控件样式
```

## 记录

- 产品/技术设计：`docs/galgame-launcher-development.md`
- Yume 智能体开发基线（v1.3，包含协议安全、预算和异常恢复）：[ai-integration-development.md](ai-integration-development.md)
- 开发日志：`docs/development-log.md`
- 当前质量审查与修复优先级：[project-audit-2026-09-15.md](project-audit-2026-09-15.md)
