# YumeShelf 项目全面审查报告

审查日期：2026-09-15。范围：当前 `G:\galgameStart` 内的 .NET 8 / WPF 应用、资源、构建脚本、测试及开发文档。

结论：当前版本具备可运行基础，已有操作反馈和页面导航回归有效；但仍存在游戏库数据丢失、启动数据异常导致崩溃、列表重复、长内容布局失效及 AI 边界不足等问题，尚不适合作为稳定版对外发布。建议先完成数据可靠性修复，再继续扩展 AI 和打包功能。

本轮完成审查与复现，未修改业务代码。测试使用独立 JSON、伪 EXE 导入样本、项目自带的无害测试程序及仅监听回环地址的模拟服务；没有调用真实 AI 服务、使用真实 Key 或改写用户游戏库。审查结束时原 Release 进程仍保持响应。

## 1. 验证结果与证据

| 检查 | 结果及含义 |
| --- | --- |
| 默认 Release 构建 | 成功，0 警告、0 错误 |
| 已有 SettingsChecks | 全部通过，无 WPF 绑定错误；覆盖设置、导航与操作反馈 |
| 本次针对性探针 | 21 个场景复现问题或边界缺口；部分场景属于同一根因，不等于 21 个独立漏洞 |
| 真实进程闭环 | 使用自带 TestGame，运行 2 秒正常退出；确认运行中刷新会丢失退出记录 |
| AI 协议检查 | 本地模拟 HTTP，验证明文传输、失败重试、超长输入和答案截断 |
| WPF 渲染与布局测量 | 确认重复卡片、长简介溢出、编辑页最小尺寸不可用及夜间硬编码白色区域 |
| NuGet 漏洞查询 | 当前源未报告存在已知漏洞的包；应用无显式第三方 PackageReference，不代表业务安全已通过 |
| 扩展 .NET 分析 | 强制重编译并启用 `AnalysisLevel=8.0-all` 后出现 CA1001、CA1031、CA1869 等告警；默认零告警未覆盖这些规则 |
| framework-dependent 发布 | 成功，背景图、默认封面和品牌 PNG 均随输出；仍依赖 .NET 8 Desktop Runtime |
| `dotnet test` | 返回成功，但没有运行本项目的控制台回归程序；属于质量门禁缺口 |

主要证据：

- [探针源码](.runtime/project-audit/probes/Program.cs)和[探针项目](.runtime/project-audit/probes/AuditProbes.csproj)。
- [21 个复现结果 JSON](.runtime/project-audit/probe-build/bin/AuditProbes/release/evidence/5f9e0cc9d717489382891a509213f4c1/results.json)。
- [小窗口游戏库与长简介](.runtime/project-audit/probe-build/bin/AuditProbes/release/evidence/5f9e0cc9d717489382891a509213f4c1/library-small-long-description.png)。
- [编辑窗口最小尺寸](.runtime/project-audit/probe-build/bin/AuditProbes/release/evidence/5f9e0cc9d717489382891a509213f4c1/editor-minimum.png)。
- [夜间游戏库](.runtime/project-audit/probe-build/bin/AuditProbes/release/evidence/5f9e0cc9d717489382891a509213f4c1/library-night.png)。
- 已有回归渲染输出：`.runtime/project-audit/build/bin/YumeShelf.SettingsChecks/release/test-artifacts/2c09637dbff94a408eb7e60bdb129197/`。

探针以 `REPRODUCED` 表示当前代码仍有该行为，是诊断工具，不是宣称功能正确的验收测试。修复时应将相应检查改成“要求正确结果”的正式回归，并纳入统一测试入口。`.runtime` 内的文件为审查产物，清理该目录前应保留需要长期维护的用例。

## 2. 高优先级问题：数据可靠性与启动稳定性

### R01 · P1 · 损坏或读取失败的游戏库会被当成空库并覆盖

位置：[JsonGameStore.cs:23](src/YumeShelf/Infrastructure/JsonGameStore.cs)、`MainWindowViewModel.cs:49`。

启动使用默认 `Load()`；JSON 解析错误及 IO 错误返回空集合。用户看到空库后添加游戏，应用直接把新列表写回原文件，没有备份、只读恢复状态或错误说明。刷新入口的严格读取没有覆盖启动入口。

复现：写入一个被截断但仍含旧游戏标题的测试库，构造主 ViewModel，再添加一个测试条目。原文件被覆盖，旧内容消失。对应 `D1-corrupt-startup-overwrite`。

建议：区分不存在、有效空库、读取失败；损坏时保留原文件并阻止覆盖性写入，提供恢复/备份路径。把一致的数据有效性判断放在存储边界。验收包括截断 JSON、文件锁定及权限异常。

### R02 · P1 · 有效 JSON 中的坏条目可导致启动崩溃

位置：`MainWindowViewModel.cs:49,472`、`Domain/Game.cs`。

反序列化之后没有校验必填路径、null 条目和集合字段。`DeduplicateGames()` 直接对路径调用 `Path.GetFullPath` 并访问属性，后续筛选直接连接 Tags。C# 的非空声明并不能限制 JSON 内容。

复现：`[{}]` 触发 `ArgumentException`，`[null]` 触发 `NullReferenceException`，均从启动构造流程向外抛出。对应两条 `D2` 探针。App 没有启动恢复处理。

建议：载入时验证并隔离坏记录，保留可用条目和原数据，显示恢复提示；不要让坏记录触发自动覆盖。补充 null、缺字段、非法路径、重复 ID 等用例。

### R03 · P1 · 多实例会相互覆盖游戏库

位置：`App.xaml.cs`、`JsonGameStore.cs:35`、`MainWindowViewModel.cs:452`。

应用没有单实例控制，也没有存储版本检查或跨进程协调。每个实例持有自己的列表，保存时全量覆盖同一个 JSON；固定 `.tmp` 名称还有并发写入冲突风险。仅保证最后一步替换文件不能避免丢失更新。

复现：两个独立 ViewModel 同时打开同一隔离库，各添加不同游戏，磁盘最终只保留第二个实例的条目。对应 `D3-two-stale-writers`；实际进程入口没有防止这一存储条件出现的约束。

建议：当前阶段优先采用 Windows 单实例机制，将再次启动定位到已有窗口；同时明确存储冲突策略。若允许多实例，需要锁定与版本冲突检测，不能只加写入互斥后继续覆盖旧快照。

### R04 · P1 · 游戏运行中刷新会丢失游玩记录，仍提示保存成功

位置：`MainWindowViewModel.cs:289,381`。

`MonitorProcessAsync` 捕获启动时的 Game 对象；刷新清空 Games 并换成重新加载的对象。进程退出时更新的是已脱离当前集合的旧对象，保存的是新集合。因此退出日期、时长和状态没有写入实际库，但仍可显示“记录已保存”。

复现：运行自带 TestGame 后立即刷新，2 秒后旧对象的 LastPlayedAt 已更新，磁盘条目仍为 Running，LastPlayedAt 为空。对应 `D4-refresh-loses-play-record`。

建议：按稳定游戏 ID 更新当前库对象，或刷新时合并原对象并保留运行会话。移除、刷新和运行会话之间应共享一致身份规则。还需明确“游戏仍运行时关闭启动器”的记录恢复策略，避免下次启动永久停留 Running。

## 3. 游戏库、扫描与图片处理

### R05 · P2 · 单次添加会在视图里生成两张相同卡片

位置：`MainWindowViewModel.cs:52`。

应用先注册 `Games.CollectionChanged`，在其中同步执行 `GamesView.Refresh()`，再建立默认集合视图。添加事件发生时，视图先被重建，之后又处理原始新增通知，导致视图条目重复。

复现：一次添加后 `Games.Count == 1`、`GamesView.Count == 2`；截图同时显示“1 个项目”和两张卡片。对应 `D5-view-duplicate`。这解释了此前“添加两个，过一会儿又消失一个”的现象，不应通过额外删除数据修补。

建议：让集合视图自行处理增删通知，只更新数量等派生状态；需要统一刷新时延后或批量执行。回归必须同时检查数据集合、视图集合及实际卡片数。

### R06 · P2 · 扫描规则仍存在引擎漏检及非 Galgame 误报

位置：`GameScanService.cs:58,66,99`、`MainWindowViewModel.cs:496`。

扫描器没有 Ren'Py 目录识别分支；入库时的 DetectEngine 又用 `File.Exists` 检查通常为目录的 renpy，因此手动添加也不能正确标注该引擎。另一方面，Unity、GameMaker、NW.js 等运行时特征直接得到高分，没有区分通用应用、其他游戏与视觉小说。两套引擎识别规则也导致扫描结果中的引擎信息在入库时丢失。

复现：`EXE + renpy/ + game/script.rpy` 的标准结构没有候选；不含视觉小说特征的 `Viewer.exe + UnityPlayer.dll` 结构被接受。对应两条 `S1` 探针。此前 G 盘 3 个游戏通过只能说明那几个样本通过。

建议：复用一套引擎/入口识别结果；分别衡量“属于游戏”和“属于视觉小说”的证据。保留人工确认，对不确定候选说明依据，不用某个通用引擎保证 Galgame 身份。

### R07 · P2 · 扫描上限静默截断，容易被误认为已经扫描完整磁盘

位置：`GameScanService.cs:25,34,42,58`、`AddGameWindow.xaml.cs:82`。

深度最多 5 层、最多访问 10000 个目录、每目录特征只读前 80 个文件；外层结果阈值 500 与内层 200 不一致。达到限制后没有“扫描不完整”的返回信息。目录枚举顺序会影响哪些证据被看见，重解析目录也没有专门边界策略。

复现：距搜索根目录 6 层的 BGI 样本被遗漏，直接选中较小目录才能找到。对应 `S2-depth-truncated`。深度限制本身可作为性能取舍，缺陷在于范围与结果没有向用户说明。

建议：返回扫描范围、是否截断和跳过原因；统一上限，允许用户缩小范围继续扫描。针对已知关键文件直接查询，避免关键证据受前 80 文件顺序影响。

### R08 · P2 · 同目录未知引擎被过度合并

位置：`MainWindowViewModel.cs:311`、`MainWindowViewModel.cs:472`。

添加时只要 RootPath 和 Engine 相同就视为重复，包含“未知引擎”；启动清理却只对 BGI 使用同目录去重，身份规则不一致。同目录的独立短篇或不同作品会被错误拒绝。

复现：同目录两个不同 EXE，第二个返回 Duplicate。对应 `S3-same-directory-false-duplicate`。

建议：完整启动路径作为基础身份，仅在有明确引擎/资源关联证据时合并多个入口；低置信情况让用户选择。不要因为用户编辑了 Engine 文案而改变重复判断。

### R09 · P2 · 损坏封面仍被选中，图片处理缺少资源预算

位置：`OfflineGameMetadataService.cs:49,59`、`GameEditorWindow.xaml.cs:54`、`MainWindow.xaml:225`。

封面评分先根据名称给分，解码失败却返回原分；坏的 cover.png 因而仍能胜出。编辑封面只检查文件存在，且允许所有文件，没有可解码校验。显示层直接绑定路径，图片后来被移动也没有统一默认封面回退。

复现：文本内容的 cover.png 被离线元数据解析选为封面。对应 `U3-invalid-offline-cover`。

另外，导入在 UI 线程枚举并尝试解码顶层所有候选图片，没有总数量/时间/文件体积限制；显示封面未指定缩略图解码尺寸。大量或超大图片的卡顿/内存风险由代码确认，未进行压力压测。

建议：解码失败直接排除；加载器统一提供默认封面与缩略图尺寸；枚举和筛选设预算并脱离 UI 线程。编辑支持的格式应以实际解码能力为准。

### R10 · P2 · 游戏移动后无法重新绑定启动文件

位置：`GameEditorWindow.xaml.cs:13,73`、`GameEditorWindow.xaml`。

编辑页只能修改工作目录，不能修改 ExecutablePath 或 RootPath。用户移动游戏后，即使修改工作目录，启动仍会使用旧 EXE 路径；打开目录也继续使用旧 RootPath。

证据级别：完整编辑/保存链路代码确认，未移动真实游戏。建议增加“重新选择启动文件”并原子更新关联路径，保留原游戏 ID、简介、收藏和游玩记录。

## 4. AI 与安全边界

### R11 · P2 · 远程 API 没有强制加密传输边界

位置：`OpenAiCompatibleProvider.cs:17,29,51,53`。

只验证绝对 URI，不区分 HTTPS、远程 HTTP 和本机 HTTP。若用户填写远程 `http://` 地址，Bearer Key 和对话正文会明文发送。DPAPI 保护的是磁盘存储，不能保护这段传输。

复现：仅使用回环模拟服务和虚构 Key，确认 Provider 接受 HTTP 并发送 Authorization；远程主机走同一缺少协议检查的代码分支。对应 `A1-http-key`。没有访问真实远程 HTTP，也没有发现或声称真实 Key 已泄露。

建议：云端要求 HTTPS；本机服务允许 loopback HTTP。若未来支持不加密局域网服务，应作为明确选择。更换服务来源时核对 Key 的使用对象，避免无意复用其他服务凭据。

### R12 · P2 · 失败或取消后重试产生重复用户消息

位置：`AiAssistantViewModel.cs:35,62`。

每次发送都新增用户气泡；失败只恢复 Query，不标记已有气泡状态。再次发送同一问题又新增气泡，用户无法区分失败消息和已回答消息。

复现：模拟第一次 500、第二次正常路由，出现两条相同用户消息。对应 `A2-retry-duplicate`。

建议：用消息/任务 ID 关联状态，重试原任务并复用用户气泡，显示失败/停止状态，继续维持仅完整成功回合进入模型历史的规则。

### R13 · P2 · 输入、上下文、返回体和路由输出缺少完整预算

位置：`AiAssistantViewModel.cs:27,41,48,53,70`、`OpenAiCompatibleProvider.cs:32`。

当前只限制输出 max_tokens 和历史条数。单条 Query 没有长度上限，历史消息没有 token/字符总预算，会话集合没有清理上限。HTTP 默认完整缓冲响应且未设置应用级大小限制。clarify 文本直接展示，没有短澄清字段长度限制。

复现：100000 字符问题原样进入路由请求；5000 字符 clarification 被解析接受。对应两条 `A3` 探针。它们确认成本和协议边界不足，不能单凭模拟结果推断真实模型一定会越界回答。

建议：设单输入、总上下文、返回体和澄清长度预算；保留完整回合裁剪，不恢复本地关键词硬过滤。模型语义约束仍需使用已约定的真实样本集评估，尤其是混合请求、追问、角色注入和剧透要求。

### R14 · P2 · 截断答案被当成完整成功；超时与资源生命周期不一致

位置：`OpenAiCompatibleProvider.cs:13,36`、`AiAssistantViewModel.cs:39,58`。

只读取 content，不检查 finish_reason。答案因输出长度截断仍会清空输入、写入成功历史并显示“完成”。同时设置允许 120 秒，助手却把单阶段超时截到 60 秒；路由和回答分别计时，没有清晰统一的任务期限。每轮/每次测试新建 HttpClient，Provider 未释放或复用，扩展分析报 CA1001。

复现：模拟 `finish_reason=length` 的响应被作为普通答案返回。对应 `A4-truncation-success`。超时与资源问题属于代码确认，未做长时连接压力测试。

建议：保留完成原因和必要用量信息，明确区分完整、截断与协议异常；统一配置含义及任务期限；复用受控 HttpClient，不为每轮创建连接池。

AI 联网检索、资料写回、来源引用、跨重启历史仍是开发计划中的未实现能力，不把这些已明确标示的阶段性缺口当作本轮新漏洞。当前没有任意工具执行路径，不能据此声称存在 AI 任意命令执行漏洞。

## 5. 页面设计与交互一致性

### R15 · P2 · 长简介与编辑页在正常缩放下不可完整使用

位置：`MainWindow.xaml:318,324`、`GameEditorWindow.xaml:39,48`。

详情区使用非滚动 StackPanel；编辑页把简介 TextBox 放在纵向 StackPanel 中，只有 MinHeight，没有受约束高度。即使设置内部滚动条，父级给出的无限测量高度也会使文本框不断增高。封面路径行的 DockPanel 也没有为右侧选择按钮先保留宽度。

复现：900×580 内容区中，长简介内容测量到底部 1349 DIP，超出可见区域；编辑最小尺寸下简介区延伸至按钮/窗口外，无法滚动访问完整内容。对应两条 `U2` 探针及截图。

建议：内容区有独立 ScrollViewer，底部操作行固定；简介框设可用高度约束。长标题、长路径、空详情、缩小窗口和 125%/150%/200% DPI 均应验收。当前游戏卡片封面 178×110 也并非严格 16:9。

### R16 · P2 · 夜间模式仍有白底控件和低对比图标

位置：`Resources/Styles.xaml:53,69,78,206`。

IconButton、ViewToggleButton 和搜索输入框硬编码白底；夜间前景色又从主题切为浅色，悬停或选中后可能接近白底。下拉箭头/边框也残留固定颜色。

复现：夜间渲染中白色搜索区、视图和刷新按钮明显存在，当前视图图标几乎看不见。对应 `U5-night-white-icon`。建议把常态/悬停/选中/禁用颜色全部接入主题资源，并补充对比度及键盘焦点验收。

### R17 · P2 · 关闭应用仍可直接丢弃待保存游戏库修改

位置：`MainWindow.xaml.cs:36`、`MainWindowViewModel.cs:87,452`。

游戏库写入失败会设置 HasUnsavedLibraryChanges 并提供重试，但关闭主窗口只取消 AI/设置测试，没有 Closing 阶段的待保存检查。关闭后内存修改即消失。

证据级别：保存失败和退出路径代码确认，未用真实数据制造失败。建议仅针对真正写入失败的库修改提供重试/退出选择，避免把正常未确认的设置草稿也一律变成阻塞弹窗。

### R18 · P3 · 恢复默认背景的预览与保存结果不一致

位置：`SettingsViewModel.cs:117`、`MainWindowViewModel.cs:183`。

清除背景时设置 PreviewImage=null，却提示“已预览默认背景”；确认后主窗口根据 ColorPalette 重新加载内置图片。日间也可能显示“默认深色背景”的占位文字。

复现：清除后预览为空，确认后实际背景非空。对应 `U4-default-preview-mismatch`。建议预览和应用复用同一个背景解析结果。

### R19 · P3 · 页面仍存在无实际行为或与真实状态不符的元素

位置：`MainWindow.xaml:128,164,267,321`、`AiAssistantPage.xaml:33`。

- 排序按钮可点击但没有 Command 或 Click 处理；`U1-inert-sort` 已核对运行控件和 XAML。
- “可启动”标签是固定文本，文件不存在或未选择游戏时也不能正确反映状态。
- 左下类似容量/进度的彩条固定宽 78，没有对应真实数据含义。
- 列表直接显示 Running / Completed / Failed 等内部状态字符串，缺少统一用户文案。
- AI 输入暂只支持点击发送，文档提出的 Enter/Shift+Enter 和输入法组合输入处理尚未实现。

建议：无功能按钮先隐藏或明确禁用；状态来自可验证的数据。装饰条去除进度条语义。实现快捷键时单独验证中文输入法，不能简单把所有 Enter 都当发送。

## 6. 测试门禁、代码质量与交付

### R20 · P2 · 默认测试命令成功不代表执行了回归

位置：`tests/YumeShelf.SettingsChecks/YumeShelf.SettingsChecks.csproj`、总开发文档 §14.4。

SettingsChecks 是普通控制台程序，没有测试 SDK/测试项目配置。实际执行 `dotnet test YumeShelf.sln --no-build` 返回成功，但没有测试执行结果；必须显式运行 SettingsChecks.dll 或 `dotnet run --project ...`。

建议：当前阶段不必引入新框架，先提供一个能传播退出码的统一 check 脚本，串联构建、控制台回归和发布验证，并同步文档命令。今后迁移测试框架也必须保证实际发现并执行用例。

其他质量问题与改进方向：

| 项目 | 证据与实际影响 | 建议 |
| --- | --- | --- |
| 异常诊断不足 | App.xaml.cs 为空；未建立文档要求的应用日志。多个 catch 只吞掉异常或返回默认值；扩展分析 CA1031 指向扫描和封面解析 | 记录脱敏分类、位置与异常；恢复逻辑与提示分离，避免隐藏数据故障 |
| 资源管理 | Provider 持有未释放 HttpClient，CA1001；设置保存反复创建 JsonSerializerOptions，CA1869 | 优先修正连接生命周期；序列化选项复用属于低优先优化 |
| UI 线程 IO | 添加时同步图片解码、全库 JSON 写入；批量添加逐条保存并刷新，潜在重复扫描/排序成本 | 批量导入统一提交，耗时读取移出 UI 线程；按实际游戏数量做性能测量 |
| 列表扩展性 | 封面网格使用普通 WrapPanel，没有虚拟化；AI 消息使用普通 ItemsControl，集合持续增长 | 在数百游戏/长会话规模测试；达到性能门槛后再引入合适的虚拟化方案 |
| 重复规则 | 扫描、手动添加、启动加载三处身份/引擎判断不一致 | 共享最小的识别结果与身份规则，不建立庞大插件框架 |
| 废弃代码与命名 | AiDomainGuard 无调用；StrongFiles 未使用；隐藏旧配色属性仍保留；InfrastructureSecureSecretStore.cs 位于工程顶层 | 清理确实无用代码，配置兼容项注明保留理由，统一目录和命名 |
| 可读性 | AI ViewModel/Provider 与若干 XAML 把多步操作压在单行，增加状态和异常分支审查难度 | 按请求阶段/视图结构格式化，仅在职责确实独立处拆分，避免无依据重构 |
| 版本回滚 | 当前目录不是 Git 仓库 | 建立版本记录和验证后检查点，忽略运行数据及凭据；初始化仓库不属于本轮审查修改 |
| 发布配置 | 成功发布为依赖框架版本；csproj 无 ApplicationIcon，运行窗口 PNG 不等于 EXE 内嵌图标 | 明确自包含/依赖框架分发方式；补足 EXE 图标、版本与安装/升级策略 |
| 无障碍 | 多处 FocusVisualStyle=null，部分纯图标按钮只有符号或 ToolTip | 给关键操作可访问名称和可见键盘焦点，验证 Tab 顺序与高对比模式 |
| 文档漂移 | 质量门禁仍有无效 dotnet test；总文档中部分历史技术/主题说明与实际代码混合 | 分开已实现、计划和历史记录，以实际验收为完成标准 |

上述性能、无障碍和发布事项属于有代码依据的风险或验收缺口；本轮未将其描述成已经测得的性能故障或兼容性失败。

## 7. 修复顺序与验收建议

1. **先保护数据。** 修复 R01–R04 和 R17；建立损坏恢复、多实例策略和运行会话身份规则。验收必须覆盖异常后重开应用的数据完整性。
2. **恢复游戏库交互可靠性。** 修复 R05、R08、R10、R15；确保一次添加一张卡片、正确区分游戏、路径可重绑定、长内容可滚动。
3. **统一扫描与封面边界。** 修复 R06、R07、R09；用多种引擎的正例与普通应用反例替代仅依赖 G 盘现有 3 个游戏。
4. **完善 AI 任务边界。** 修复 R11–R14；预算、失败重试、返回完成原因及连接管理落实后，再做真实模型语义评估和后续检索功能。
5. **收尾主题与交付门禁。** 修复 R16、R18–R20，统一执行入口，再验收干净 Windows 环境中的安装、运行、升级和卸载。

现有有效设计应保留：主窗口内导航、设置草稿确认后应用、DPAPI 本机密钥保护、失败重试提示、扫描结果经确认再入库、从库中移除不删除游戏本体。修复应针对具体根因，优先复用现有 WPF/.NET 能力。

## 8. 可重复运行命令

在项目根目录运行，输出及测试数据位于 `.runtime/project-audit/`：

```powershell
dotnet build YumeShelf.sln --configuration Release --artifacts-path .runtime\project-audit\build --nologo
dotnet .runtime\project-audit\build\bin\YumeShelf.SettingsChecks\release\YumeShelf.SettingsChecks.dll

dotnet build .runtime\project-audit\probes\AuditProbes.csproj --configuration Release --artifacts-path .runtime\project-audit\probe-build --nologo
dotnet .runtime\project-audit\probe-build\bin\AuditProbes\release\AuditProbes.dll .runtime\project-audit\build\bin\YumeShelf.TestGame\release\YumeShelf.TestGame.exe

dotnet list src\YumeShelf\YumeShelf.csproj package --include-transitive --vulnerable
dotnet build src\YumeShelf\YumeShelf.csproj --configuration Release --artifacts-path .runtime\project-audit\build --no-restore --nologo -t:Rebuild -p:AnalysisLevel=8.0-all -p:EnableNETAnalyzers=true
dotnet publish src\YumeShelf\YumeShelf.csproj --configuration Release --artifacts-path .runtime\project-audit\build --no-restore --self-contained false --output .runtime\project-audit\publish --nologo
```

范围限制：未执行真实云端 API 和搜索、真实模型边界评估、恶意图片压力测试、真实用户游戏兼容矩阵、跨显示器 DPI、屏幕阅读器或干净 Windows 安装验证。因此不能把本报告或无已知 NuGet 漏洞等同于全面安全认证。
