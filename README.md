# YumeShelf

Windows 本地 Galgame 启动器，当前处于首个垂直切片开发阶段。

## 当前能力

- 添加本地 `.exe` 游戏。
- 保存游戏目录、启动文件和工作目录。
- 搜索标题、引擎、标签和简介。
- 全部游戏、最近游玩、收藏筛选。
- 封面网格和紧凑列表视图切换。
- 启动游戏并监听进程退出。
- 记录最近游玩时间和累计游玩时长。
- 本地 JSON 数据持久化。
- 编辑标题、引擎、年份、工作目录、启动参数、封面和简介。
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

设置回归检查（在隔离目录读写，不修改用户设置和游戏库）：

```powershell
dotnet run --project .\tests\YumeShelf.SettingsChecks --configuration Release
```

## 数据位置

添加游戏支持“自动搜索添加”和“手动添加”两种方式。自动模式可选择磁盘或文件夹，按 Unity、Ren'Py、KiriKiri、RPG Maker、NW.js 等本地特征筛选候选，用户勾选确认后才会写入游戏库。

```text
%LOCALAPPDATA%\YumeShelf\library.json
%LOCALAPPDATA%\YumeShelf\settings.json
```

游戏本体文件不会被复制、移动或删除。删除游戏条目只会删除启动器中的记录。

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
- Yume 智能体开发基线（v1.2，2026-09-15 更新内嵌导航与操作反馈）：[ai-integration-development.md](ai-integration-development.md)
- 开发日志：`docs/development-log.md`
- 当前质量审查与修复优先级：[project-audit-2026-09-15.md](project-audit-2026-09-15.md)
