# 游戏扫描规则与覆盖范围

更新：2026-09-15。实现入口：`GameScanService`；自动扫描与手动入库共用 `DetectEngine`。全部在本机检查目录和少量文件头，不调用 AI、不联网、不执行候选 EXE，也不解压资源包。

## 使用方式

“添加游戏 → 自动搜索添加”内选择磁盘或文件夹，再选择扫描模式：

- **视觉小说优先（默认）**：视觉小说专用结构进入候选；其他已识别的游戏结构还要满足至少两类上下文：存档名称、汉化/中文/chs 名称、日文假名、符合规则的 visual novel/galgame 路径。
- **扩展游戏扫描**：另接受有游戏资源结构的 RPG Maker、WOLF RPG、Unity、GameMaker，适合补充 ACT、SLG、RPG 等游戏。这些是可能的玩法，程序不自动推断类型或确认 Galgame 身份。

两种模式都保留人工确认。视觉小说专用候选标记“推荐入口”且默认勾选，其他候选标记“待确认”且默认不勾选。可以手动勾选或全选；没有选择时确认按钮只提示，不会导入。分数仅供内部排序，不作为识别概率展示。

切换范围/扫描模式会清空旧候选和旧反馈；重新扫描替换结果。自动/手动表单等高，结果可滚动，确认按钮固定。扫描期间不允许切换模式、修改勾选或重复提交。每次打开添加窗口默认使用视觉小说优先，不新增全局设置。

## 当前识别矩阵

以下是常见 Windows 导出结构的启发式检测，不代表验证了该引擎所有版本或所有作品。每条候选还必须有未被排除的普通 `.exe` 文件。

| 引擎／版本 | 主要结构判据 | 归类 |
| --- | --- | --- |
| Ren'Py | `renpy/` + `game/` | 视觉小说 |
| BGI | BGI.gdb/kdb/hvl 任一，或 sysgrp.arc + sysprg.arc + data*.arc | 视觉小说 |
| KiriKiri | XP3 包，或 startup.tjs + 顶层 KS 脚本 | 视觉小说 |
| TyranoScript | tyrano/ + data/scenario/，支持顶层及 www/ 布局 | 视觉小说 |
| 自定义 BIN | script.bin + bg.bin + voc.bin/snd.bin；EXE 须有同名 BIN | 视觉小说 |
| NScripter / ONScripter | nscript.dat、nscr_sec.dat、0.txt、00.txt 任一 + NSA/SAR 包 | 视觉小说 |
| RealLive | RealLive.exe + Seen.txt | 视觉小说 |
| Siglus | SiglusEngine.exe + Scene.pck | 视觉小说 |
| Artemis | 顶层/data 内 PFS 包，读取 pf2/pf6/pf8 文件头 | 视觉小说 |
| YU-RIS | 顶层/pac 内 YPF 包，读取 `YPF\0` 文件头 | 视觉小说 |
| RPG Maker 2000/2003 | RPG_RT.exe + RPG_RT.ldb + RPG_RT.lmt；不进一步区分二者 | 扩展游戏 |
| RPG Maker XP | Game.ini + rgssad 包，或 Data 下 System/Scripts.rxdata | 扩展游戏 |
| RPG Maker VX | Game.ini + rgss2a 包，或 Data 下 System/Scripts.rvdata | 扩展游戏 |
| RPG Maker VX Ace | Game.ini + rgss3a 包，或 Data 下 System/Scripts.rvdata2 | 扩展游戏 |
| RPG Maker MV | nw.dll + index.html + js/rpg_core.js + data/System.json；支持顶层/www | 扩展游戏 |
| RPG Maker MZ | nw.dll + index.html + js/rmmz_core.js + data/System.json；支持顶层/www | 扩展游戏 |
| WOLF RPG Editor | Game.exe + Data.wolf，或 Data/BasicData 下 Game.dat + SysDatabase.dat | 扩展游戏 |
| Unity | UnityPlayer.dll/GameAssembly.dll + 与 EXE 同名的 _Data 下 globalgamemanagers/data.unity3d | 扩展游戏 |
| GameMaker | data.win 中 FORM 与偏移 8 的 GEN8 文件头 | 扩展游戏 |
| NW.js | nw.dll + 顶层/www 的 package.json | 只标注框架，单独不会作为扫描候选 |

未知引擎仍可通过“两类上下文 + 三个 ARC/BIN/PAK/DAT 资源文件”的保守兜底进入候选，但明确保留“未知引擎”和“待确认”，不再冒充已确认的自定义视觉小说引擎。

## 入口与误报控制

- BGI 不再仅凭 data*.arc 判断，单个 KS 文件不再判断为 TyranoScript；普通运行库、编辑器、配置/卸载/安装/更新程序及目录伪装成 EXE 的条目不进入结果。
- 遍历时忽略 `.git`、`.vs`、`.idea`、`.runtime`、`node_modules`、系统回收站和卷信息子目录，减少开发样例、缓存和系统目录干扰；用户直接指定这些目录为扫描根目录时仍检查根目录。
- 检查引擎使用的已知子目录时不跟随重解析点；PFS/YPF/GameMaker 只读短文件头。无法读取的资源按跳过目录报告，不把读取失败描述为完整无结果。
- 共用资源的已知引擎每目录选择一个排序靠前的启动入口，汉化入口优先；Unity 保留各自配套 `_Data` 的独立 EXE，自定义 BIN 保留资源配对并沿用已有原版/汉化去重。
- 不把其他 EXE 自动视为同一游戏；导入仍由既有路径/明确资源关联规则防重复，不增加按标题删除或合并的行为。
- 目录同时包含多个共享同套资源的独立作品、启动器外置、运行程序被改名、额外加密或自定义资源路径时仍可能漏检，使用手动添加核对。

## 预算与边界

扫描保留 12 层、10000 目录、500 候选、4096 枚举项/目录和 60 秒软时限。PFS 与 YPF 各自最多检查每个启动目录及指定资源子目录合计 32 个包；超出时提示扫描不完整。单次文件系统操作不能保证硬实时取消。

不处理压缩包、ENC、纯网页/脚本入口或移动端安装包。RPG Maker 95、Unite、Bakin、其他特殊导出方式，以及 Unreal、Godot 等暂未建立专用判据。Unity 旧版/单文件、自定义命名的 GameMaker 资源等也不承诺覆盖。拥有引擎资源的模拟器或工具仍有成为候选的可能，扩展结果须人工确认。

新增扫描范围不扩大 AI 服务范围，不自动填入游戏简介、玩法或在线资料；标题和封面继续使用现有离线解析流程。

## 验证与依据

`tests/YumeShelf.ReliabilityChecks/ScanCoverageChecks.cs` 覆盖典型导出结构、打包/散装 RGSS、顶层/www 网页结构、文件头正反例、通用引擎无日文/存档场景、同目录辅助程序、模式切换/重新扫描/未勾选确认、窄窗口、等高面板和夜间主题。样例 EXE 只用于导入和识别，不执行。

统一运行 `scripts/check.ps1`；可传入现有真实目录作只读复测。结构样例通过只说明规则正确执行，不能据此给出全市场识别率，新增引擎仍需更多真实游戏样本验收。

本轮统一门禁通过：原有 SettingsChecks 全通过，ReliabilityChecks 144 项（含两处真实目录；默认 142 项），构建零警告/错误，无 WPF 绑定错误，发布资源检查通过。日志为 `.runtime/checks/check-20260915-213615.log`，详细证据路径见开发日志本次条目。

PFS/YPF 文件头参考 GARbro 的 [Artemis PFS 格式定义](https://github.com/morkt/GARbro/blob/master/ArcFormats/Artemis/ArcPFS.cs) 与 [YU-RIS YPF 格式定义](https://github.com/morkt/GARbro/blob/master/ArcFormats/YuRis/ArcYPF.cs)。仅使用公开格式特征，未复制解包实现或引入其依赖。
