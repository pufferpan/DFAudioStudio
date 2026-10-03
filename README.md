# 三角洲音频工坊 · DFAudioStudio

把《三角洲行动》Wwise 音频导出（`WwiseAudio\Media` + `WwiseAudio\Localized`，约 **7.6 万条 / 16GB**）
全量分类入库，按分区浏览试听，并用**离线 Whisper** 把语音转成中文文本，结果可导出。

---

## 一、它能做什么

**导航结构（分区树由索引库真实数据自动生成，不是写死的页面）**

| 导航项 | 说明 |
|---|---|
| **概览** | 总数与各分区统计、扫描/识别按钮、日志；**识别是手动开始的** |
| **分区导航** → 按类型 | 语音 (53,468) / 音效 (18,321) / 其它 / 界面 / 音乐 / 环境 |
| **分区导航** → 按场景/用途 | 由分类器按内容判定：角色台词 (14,340)、呼吸/喘息 (9,809)、武器 (9,576)、标记报点 (4,821)、战斗提示 (4,258)、战况通报 (3,073)、技能语音 (2,724)、脚步 (2,090)、赛季任务 (2,038)、快捷消息、界面、活动、转盘活动、阵亡、爆破、表情动作、钓鱼、物件交互、环境氛围、载具、电台、检视、**对局开局 (533)**、大厅、剧情过场、开场、音乐、局内通用、多人大厅、**选人入场 (338)** … |
| **分区导航** → 按干员 | 干员 101 ~ 603（每个带真实条数，如干员 302 (1,799)） |
| **分区导航** → 按活动 | 二周年 (362)、周年庆、Ma1~Ma4、音乐节、彩蛋、圣诞、新年、明日方舟联动、潘多拉、夏日、万圣节 |
| **分区导航** → 按地图 | AZ3核电园区、核电站NPP (837)、AZ5(NPP剧情)、潮汐监狱、森林、崩塌、零号大坝、海战、纪念碑、断层、裂隙、巴克什 … |
| **分区导航** → 按皮肤/联动 | Yanzu（彦祖）、SkinA/SkinB、Outers、Cowboy、Outrage 等 |
| **分区导航** → 按子分类 | Voice_SOL (7,151)、Voice_Breakthrough (4,104)、Music_BF、Voice_SeasonQuest … |
| **识别队列**（底部） | 进度、当前文件与文本、暂停/继续/停止、失败重试 |
| **设置**（底部） | 两个根目录、模型路径/下载、语言、并发、超时、跳过规则、导出目录 |

每组标题都带**真实条数**，点顶部「⟳ 刷新分区」按最新数据重建。进入任意分区后仍可按 分区/干员/活动/地图/子分类/皮肤/识别状态 二次筛选，并可搜索（文件名、识别文本、全部标签）。

**列表播放的操作约定**（列表在分区页右侧）

| 操作 | 行为 |
| --- | --- |
| 单击某条 | **没在播**时只选中（右栏显示识别文本）；**正在播**时直接切到这一条并开播 —— 换着听不用再按播放键 |
| 双击某条 | 从这条开始播放 |
| 播放键 ▶ | 播当前选中项；暂停中且同一条 → 续播（不重新读文件） |
| 上一首 / 下一首 | 在当前筛选结果里按顺序切 |

单击切换只认真实点击（`ItemClick`），所以方向键移动高亮、列表刷新后自动对齐播放状态都不会误触发重新播放。
每次真正开始播放都会在日志里留一行 `开始播放：<文件名>（速度 / 变调）`，切换时另有一行 `单击切换播放：<上一条> → <这一条>`，排查"点了没反应/播的还是上一条"时看这两行最快。

## 二、已完成的索引（当前机器实测）

- 库：`E:\DFAudioStudio\data\index.db`（GUI 与命令行共用，已入库 **75,913** 条；C 盘紧张所以数据与模型都放 E 盘）
- 模型：`E:\DFAudioStudio\models\ggml-small.bin`（465MB）
- **⚠ 模型路径必须是纯英文/ASCII**：Whisper 的原生库用窄字符路径打开文件，放在中文目录（例如 `E:\新建文件夹 (8)\...`）会抛 `External component has thrown an exception`。程序已加兜底（打不开就整个读进内存再加载），但仍建议放英文目录。
- 分区数量：语音 **53,468** · 音效 **18,321** · 其它 1,537 · 界面 1,020 · 音乐 **921** · 环境 646
- 标签示例：二周年 **362** 条、核电站NPP **837** 条、AZ5 剧情过场 14 条、干员 101~603 全覆盖、彦祖(Yanzu) 18 条
- 实测识别效果（small 模型，纯 CPU）：
  - `Voice_302_SOL_GameStart_1_Low.wav` → **"我已就位"**
  - `Voice_ScndAnniversary_e6_InHaavk_Orion_1.wav` → "看见天花板那个摄像头没有AI一直在盯着你们"
  - 长剧情 `Voice_SOL_Relink2_Cutscene_Tide1.wav` → 整段对白基本可用
- 队列吞吐：**约 45 条/分钟**（含自动跳过非语音素材）；全部语音跑完属长任务，可随时暂停/关软件，下次打开自动续跑。

## 三、怎么跑

**图形界面**：双击 `发布\DFAudioStudio\DFAudioStudio.App.exe`（或桌面快捷方式「三角洲音频工坊」）。
首次打开点「全量扫描索引」；要识别时**手动**点「▶ 开始识别（手动）」（默认不自动识别）。

启动性能（实测）：**窗口 1.55 秒出现、内存约 127MB** —— 因为启动路径不加载 Whisper 模型（465MB）、不启动识别队列。

**播放**：播放条上有「变速（保持音调）」与「变调（保持速度）」两个滑块，播放中拖就实时生效
（0.50×~2.00× / −12~+12 半音，无损，详见第十一节）。

**命令行**（适合批量/自动化，与 GUI 共用同一个库）：

```powershell
cd DFAudioStudio
dotnet run --project tools\Indexer -c Release -- index          # 全量扫描入库（增量，约 70 秒）
dotnet run --project tools\Indexer -c Release -- stats          # 分区/标签/识别状态统计
dotnet run --project tools\Indexer -c Release -- query 二周年 20  # 搜索（含标签）
dotnet run --project tools\Indexer -c Release -- transcribe 10   # 识别 10 条语音
dotnet run --project tools\Indexer -c Release -- one "<音频路径>"  # 单文件诊断（解码信息+文本）
dotnet run --project tools\Indexer -c Release -- reset-skipped    # 把「已跳过」的重新排队（换模型后用）
dotnet run --project tools\Indexer -c Release -- export          # 导出 CSV / 文本合集
```

识别策略（避免把时间浪费在非语音素材上）：
- 时长 < 0.35 秒的碎片直接跳过；
- 文件名含 `Breath / Footstep / Foley / _Amb_ / Clapping / Emotes_Body / Effort / Grunt` 的按关键词跳过（可在设置里改 `SkipKeywords`）；
- 同一音频在 `Events` 与 `Media` 各存一份，识别完一份后自动把结果同步给同名的另一份，不重复推理；
- 上次异常退出时卡在「识别中」的条目会在下次启动自动复位重跑。

## 四、语音识别（离线 Whisper）

- 引擎：**Whisper.net + ggml 模型**，纯本地、不联网、不上传。
- 模型放这里即自动识别：`%LOCALAPPDATA%\DFAudioStudio\models\ggml-*.bin`
  （也支持程序目录旁的 `models\`；优先 medium → small → turbo）。
- **一键获取模型**：双击仓库根目录的 `获取模型.bat`（等同运行 `tools\fetch-whisper-model.ps1`）。
  受限网络下的实测可用链路：USTC 镜像装 `numpy/torch` → `openaipublic.azureedge.net` 下官方 `small.pt`(483MB)
  → `ghproxy.net` 取 whisper.cpp 转换脚本 → 转成 `ggml-small.bin` 落到模型目录。
  （本机实测：HF 与 hf-mirror 全部不通；阿里云 PyPI 被限速到 0.1MB/s，USTC 有 12MB/s。）
- 想换更准的模型：把任意 `ggml-medium.bin` / `ggml-large-v3-turbo.bin` 丢进同一目录即可，程序会自动优先选 medium。
- 「打开软件自动开始识别」**默认关闭**（设置里可手动开启）：默认手动点按钮开始，避免启动慢、避免后台抢 CPU；结果按文件落库，随时关软件、下次点开始就从断点继续。

## 五、工程结构

```
DFAudioStudio/
├─ src/DFAudioStudio.Core/          # 分类器、WAV 解析、SQLite 索引、Whisper、队列、导出
│  ├─ Indexing/Classifier.cs        # 分区与标签规则（按实跑数据修正过）
│  ├─ Indexing/WavProbe.cs          # 只读 WAV 头拿时长/采样率
│  ├─ Data/IndexDb.cs               # SQLite（WAL + 连接池，多线程安全）
│  └─ Services/                     # Scanner / WhisperTranscriber / TranscriptionQueue / Export
├─ src/DFAudioStudio.App/           # WinUI 3 界面（非打包 + WindowsAppSDK 自包含）
├─ tools/Indexer/                   # 命令行工具 dfaudio
├─ tools/SmokeTest/                 # 分类器实跑校验（不写库，直接打印分区统计）
└─ tools/fetch-whisper-model.ps1    # 受限网络下的模型获取与转换
```

## 六、构建

```powershell
dotnet build src\DFAudioStudio.App\DFAudioStudio.App.csproj -c Release
dotnet publish src\DFAudioStudio.App\DFAudioStudio.App.csproj -c Release -r win-x64 --self-contained true -o 发布\DFAudioStudio
```

要点：WinUI 3 **非打包**应用需 `WindowsPackageType=None`；本机缺 1.8 的 DDLM 包会报 `0x80670016`，
所以工程用 **`WindowsAppSDKSelfContained=true` + `WindowsAppSdkBootstrapInitialize=false`**（运行时随程序发布，免装框架包）。

## 七、已知限制

1. 列表不会自动刷新识别结果，识别后点「刷新列表」。
2. 概览里的「已识别/待识别」只统计 **语音+音乐** 两区（其它分区不做识别）。
3. 播放依赖系统 WAV 解码（音频是标准 RIFF/WAVE，可直接播）。
4. 识别前必须准备好模型文件；点「开始识别」后会先加载模型（约 1 秒，mmap），期间界面仍可操作。
5. **模型必须放在纯 ASCII 路径**（见第二节）。
6. 应用日志（排查问题用）：`E:\DFAudioStudio\data\logs\app-yyyyMMdd.log`。

## 八、界面设计规范（Apple 风格重设计）

全部视觉令牌集中在 `src/DFAudioStudio.App/Styles/AppleTheme.xaml`（84 个资源键），页面里不再出现任何十六进制颜色字面量。

| 类别 | 规范 |
|---|---|
| 色彩 | 背景纯白 `#FFFFFF`；卡片/次级底 `#F5F5F7`；hover `#EDEDF0`；文字 `#1D1D1F` / `#6E6E73` / `#86868B`；**唯一强调色** `#0071E3`；分隔线 `#D2D2D7` 且极少使用 |
| 字体 | `SF Pro Display, SF Pro Text, Segoe UI Variable Display, Segoe UI, PingFang SC, Microsoft YaHei UI` |
| 层级 | 页面标题 40/SemiBold · 区块标题 24 · 卡片标题 17 · 大数字 44 · 正文 17/行高 1.5 · 次要 14 · 说明 12 |
| 间距 | 页面左右 56 / 上下 48；区块间距 40；卡片内边距 28；内容 `MaxWidth=880` 居中 |
| 圆角 | 卡片 18 · 列表行 12 · 输入/下拉 10 · 按钮胶囊 999 |
| 按钮 | 仅三种：主胶囊（`#0071E3` 填充白字）、次胶囊（`#F5F5F7` 填充）、文字链接（蓝色文字）；无边框、无阴影 |
| 明确去掉 | 阴影、渐变、亚克力、装饰图形、动画、列表分隔线、选中竖条、绝大多数图标、冗余说明文字 |

**主题（跟随系统 / 浅色 / 深色）**：`Styles/AppleTheme.xaml` 里每个视觉令牌都成对定义了
`Light` / `Dark` 两套颜色，控件里一律用 `{ThemeResource …}` 引用，所以换主题即整体变色。

- 切换入口有两个，改的是同一个值（互相同步）：左侧导航栏底部的开关、设置页「外观 → 主题」三选下拉。
- 单一事实来源是 `Services/ThemeService.cs`：当前模式存在 `settings.json` 的 `Theme`
  字段（`System` / `Light` / `Dark`，认不出来按「跟随系统」），模式变化广播 `ModeChanged`，
  主窗口据此改 `RootGrid.RequestedTheme` 并放一段淡入淡出过渡（过渡时用目标主题的页面底色做覆盖层，
  避免切换瞬间闪白/闪黑）。
- 「跟随系统」= `ElementTheme.Default`：启动时取一次系统主题；运行中系统换肤时靠
  `UISettings.ColorValuesChanged` + 背景色感知亮度解析出具体的浅/深再赋一次。

## 九、设置页：自动保存 + 模型文件夹自动识别

### 自动保存（没有「保存」按钮）

设置页所有改动**即改即存**，写进 `%LOCALAPPDATA%\DFAudioStudio\settings.json`
（`AppSettings.SettingsFilePath`，可用环境变量 `DFAUDIO_DATA` 改目录）：

- 文本框（音频目录 / 模型路径 / 导出目录 / 片头视频）：**500ms 防抖**，停了才写盘；
  选择框选完、开关拨完、数字改完 → **立即写盘**；离开页面（`Unloaded`）与输入框失焦时会 `FlushAutoSave()` 兜底；
- 状态栏只报"刚刚存了什么"（`已自动保存（启动片头）· 14:06:08 · …`）；
- 值的比对在 `SaveToSettings()` 里做：**算出来的值跟设置文件里一模一样就不写盘**
  （避免控件初始化回填造成一堆无意义写盘），日志里能看到 `自动保存写入[原因]：…` 的真实流水。

**踩过的坑（写在这里免得以后又踩）**：设置页刚构造时，各个控件会把自己的默认值
（空字符串 / 开关默认开）回填给 ViewModel，这些 change 事件如果触发自动保存，就会把默认值当成
用户选择写进设置文件 —— 实测表现为「**进一次设置页，片头视频路径被冲成空、片头开关被改成开**」。
现在的两道闸门：

1. `SettingsViewModel.AutoSave()` 在 `LoadFromSettings()` 跑完之前**一律不写盘**；
2. 四个路径文本框用 `OneWay` 绑定 + 显式 `TextChanged`，只有**控件有焦点**的变化才算用户输入，
   没焦点又和 ViewModel 不一致就把控件纠正回 ViewModel 的值。

### 模型文件夹自动识别

「识别模型」里除了「选择文件」，还有一个 **「识别文件夹」**：

- 选一个文件夹 → 扫描里面（含两层子目录）的 `*.bin`，**优先 `ggml-*.bin`**、再按体积从大到小排序；
  找到 1 个就自动填入并保存；找到多个会在下面出现下拉让你挑（默认先选最大的）；一个都没有会明确说明
  「这个文件夹里（含子目录）没有找到模型」；跳过 `.part` 半成品与 0 字节文件，`logs/thumbs/node_modules` 不进去翻；
- **模型路径输入框也可以直接填文件夹**：手敲/粘贴时只扫描并报结论（不替换你正在输入的内容），
  离开输入框或点「识别文件夹」时才自动填入并保存；
- 进入设置页时会顺手看一眼当前模型路径 / 默认模型目录（`<数据目录>\models`）里有没有模型，
  把结论写在提示行里。

实现：`Core/Services/ModelManagerAndExport.cs` 的 `ModelManager.FindModels(folder, maxDepth)` +
`SettingsViewModel.ScanModels(folder, autoFill)`。

## 十、新手引导（第一次打开软件时铺满整个窗口）

第一次打开软件（`settings.json` 里 `OnboardingCompleted != true`）时，**整屏铺开**一套 4 步引导，
盖住左侧导航与内容区，教用户把路径配好：

| 步骤 | 内容 |
| --- | --- |
| 1 / 4 | 这个软件做什么（本地索引 + 归类浏览 + 离线语音转文字），全程离线 |
| 2 / 4 | **设置音频目录**：Media / Localized 两个输入框 + 「浏览」按钮，选完立即写设置（引导里就能配好） |
| 3 / 4 | **准备识别模型**：「识别模型文件夹」自动扫描填充，或点「去设置页下载模型」 |
| 4 / 4 | 下一步做什么（扫描索引 / 开始识别 / 变速变调）→ 点「开始使用」关掉引导并跳到「概览」 |

- 走完或点「跳过引导」都会把 `OnboardingCompleted` 记为 true，之后不再自动弹；
- 想重看：左侧「新手教程」页顶部的 **「重看首次引导」**；
- 动画：每一步的文字/控件**从下往上升 + 淡入**，缓动是 **指数 EaseOut（Exponent=4）**——
  非线性，起步快、收尾几乎停住；同一步内每条错开 70ms。左侧「新手教程」页也是同一套
  （卡片升入 + 滚动到哪升哪，「重播动画」可重放）。

自检：环境变量 `DFA_TUTORIAL_PROBE=1` 启动后，教程页每张卡片上升过程中会每 60ms 记一行
`教程动画探针 第N张 t=…ms Y=…`，可用来核对缓动确实是非线性的（实测 720ms 的动画在 183ms 时
位移已走完约 64%，线性的话只该走 25%）。

## 十一、识别结果的导入 / 导出（跨机器搬识别成果）

「概览」页按钮行里有两个入口（和原来的「导出文本」并排）：

| 按钮 | 说明 |
| --- | --- |
| **导出识别结果** | 把库里所有带识别文本的条目写成**可回灌**的 JSON 交换文件，落在 `<导出目录>\识别结果\识别结果_yyyyMMdd_HHmmss.json` |
| **导入识别结果** | 选一个交换文件 → **先预演**（会命中多少 / 会改多少）→ 弹框确认 → 写库 |

「导出文本」还是原来那份给人看的 TXT 合集；交换文件是给程序读的，两者互不影响。

### 文件格式（自己定的，`TranscriptFile`）

UTF-8 无 BOM、中文不转义、PascalCase 键名（与库里 `SegmentsJson` 写法一致），可读可手改：

```json
{
  "Format": "dfaudio-transcripts",
  "Version": 1,
  "App": "三角洲音频工坊 · DFAudioStudio",
  "ExportedAt": "2026-10-03T14:22:01+08:00",
  "Count": 2,
  "Entries": [
    {
      "FileName": "Voice_302_SOL_GameStart_1_Low.wav",
      "Size": 80396,
      "Path": "I:\\...\\Voice_302_SOL_GameStart_1_Low.wav",
      "Text": "我已就位",
      "Segments": [ { "Start": 0, "End": 1.5, "Text": "我已就位" } ],
      "DurationSec": 1.52,
      "Category": "Voice",
      "SubCategory": "Voice_302",
      "UpdatedAt": "2026-09-22T07:50:00+08:00"
    }
  ]
}
```

- 只有 `FileName` 和 `Text` 是必须的；`Segments` 缺了就只写正文；读的时候**键名大小写不敏感**、多余的字段忽略。
- `Format` 不对 / `Version` 比程序新 / JSON 坏了 → 直接报错并说明原因，不会瞎写库。

### 匹配与写入规则

1. `FileName` + `Size` 同时一致 → 命中**所有**同名同大小的条目（同一音频在 Events 与 Media 各一份，两份都会写上）；
2. 只给 `FileName`（`Size` 为 0）→ 命中同名条目；
3. 都没有时退回 `Path` 精确匹配（大小写敏感，走 Path 唯一索引）。

写入前会**去掉首尾空白后比对**：内容一样就跳过（不写库、不改状态），不一样才写，并把状态置为「已完成」。

### 性能（踩过的坑）

- 一开始按 `Path = ? COLLATE NOCASE` 匹配，**这个写法用不上 Path 的唯一索引**（索引是 BINARY 排序），每条都要全表扫 12 万行 → 一万九千条跑了十几分钟还没完。
  现在一律走 `(FileName, Size)` 索引匹配，路径只在内存里做"优先命中"排序。
- 整批导入共用一个数据库连接 + 预处理语句（`IndexDb.ImportSession`），不再每条开一次库。
- 实测：19,537 条的交换文件（20 MB）**预演 11 秒、写库 2.1 秒**（其中命中的库记录 39,211 条，因为同名同大小会命中两份）。

### 自检（不用进界面）

```powershell
# 导出：写出交换文件 + 回读校验 + 生成前 10 条预览
DFAudioStudio.App.exe --exporttranscripts "E:\temp\out.json" report.txt

# 导入：预演 → 写库 → 输出计数（--dryrun 只预演不写库）
DFAudioStudio.App.exe --importtranscripts "E:\temp\out.json" report.txt --dryrun
DFAudioStudio.App.exe --importtranscripts "E:\temp\out.json" report.txt
```

实测（单条往返，最直观）：改过的单条文件 → 预演"2 条内容会更新" → 导入"更新 2 条" →
再预演"0 条会更新"（证明真写进去了）→ 导回原文本 → 再预演"0 条会更新"（回到原样）。

## 十二、性能与稳定性修复记录（针对"点开始识别就卡死"）

| 现象 | 根因 | 修复 |
|---|---|---|
| 点「开始识别」后界面卡死 10~15 秒 | `Start()` 在 **UI 线程**上调用了待识别数量统计，而该统计里的"去除重复副本"子查询（`NOT EXISTS`）没有索引支撑，退化成 O(n²) 全表扫描 | ① 新增索引 `ix_items_name_size(FileName,Size)` 与 `ix_items_pick(Category,Status,DurationSec)`；② `Start()` 不再查库（只发事件）；③ 进度里的剩余数量改为不带子查询的毫秒级计数 |
| 识别时界面发滞 | 每个进度事件都重建日志面板整段文本、并在失败数未变时也刷新失败列表 | 进度上报节流 400ms、日志面板节流 600ms（最多 200 行）、失败列表仅在失败数变化时刷新、识别线程优先级降为 BelowNormal、Whisper 线程数改为 `CPU-2`（最多 6） |
| 点开始后提示"未配置模型" | 模型被我挪到了中文目录，Whisper 原生库打不开 | 模型与数据统一放到 `E:\DFAudioStudio\`（纯英文），并加内存兜底加载 |

## 十三、Alpha 定时过期版（独立发布的版本）

除正式版外，工程还能编译出一个 **限时 Alpha 测试版**，与正式版是两套独立产物，可同时安装：

- **过期时间**：北京时间 **2026 年 9 月 24 日 00:00** 正式过期（= 2026-09-23 16:00 UTC）。
- **校时方式**：启动时不信任本机时钟，向 8 个公网 NTP 服务器并发取时（SNTP/RFC 4330，国内优先）
  `ntp.aliyun.com` / `ntp1.aliyun.com` / `ntp.tencent.com` / `cn.pool.ntp.org` / `ntp.ntsc.ac.cn` / `time.windows.com` / `time.apple.com` / `pool.ntp.org`，
  取往返延迟最小的一份作为网络时间，并校验响应来源与回显时间戳（防伪造）。
- **拦截规则**（任一命中即拦截，不进入主界面）：
  1. 系统时间与 NTP 时间相差超过 **5 分钟**（判定为改过系统时间）；
  2. 网络时间已达到/超过 2026-09-24 00:00；
  3. 所有 NTP 服务器都无有效响应（离线 / 防火墙拦截 UDP 123）。
- **统一提示文案**：

  > 此版本为Alpha测试版本。于2026年9月24日正式过期，请等待正式版本发布

  下面还有一行说明（例如「系统时间与 NTP 服务器（ntp.aliyun.com）相差 1 小时 0 分，时间校验未通过」），
  以及「重新校验 / 退出程序」两个按钮；退出即关闭程序。
- **校验位置**：覆盖层直接铺在主窗口 `RootGrid` 上，**不额外创建窗口**
  （先建独立校验窗口再关掉、之后才建主窗口的写法会让主窗口渲染后原生崩溃 0xC0000005，模型查看器已踩过这个坑）。
  校验通过前界面不可用、启动片头也不会播放。
- **运行期复核**：每 15 分钟用 NTP 再复核一次；已过期或时钟被改立即拦截，NTP 暂时不可达则连续两次失败（约 30 分钟）才拦截，避免单次网络抖动打断识别任务。
- **数据目录与正式版共用**：Alpha 版沿用 `%LOCALAPPDATA%\DFAudioStudio\settings.json` 与其中的 `DataRoot` / `ModelsRoot`，
  因此不会重新扫描 16GB 素材、也不会丢已有的 7.6 万条索引；两个版本读写同一个 `index.db`。
- **界面标识**：窗口标题为「三角洲音频工坊 · DFAudioStudio（Alpha测试版 · 2026-09-24 过期）」。
- **日志**：校时结果接进应用自身日志（`<数据根>\logs\app-yyyyMMdd.log`），带 `[ALPHA]` 前缀。

诊断与自测（不会进入主界面）：

```powershell
# 真实校时结果，退出码 0=通过 3=未通过
DFAudioStudio.App.exe --timecheck report.txt

# 边界自测：按指定网络时间 / 偏差复算判定结论（只复算，不改变实际校验行为）
DFAudioStudio.App.exe --timecheck report.txt --at "2026-09-24T00:00:00+08:00"   # → Expired
DFAudioStudio.App.exe --timecheck report.txt --at "2026-09-23T23:59:00+08:00"   # → Ok
DFAudioStudio.App.exe --timecheck report.txt --drift 3600                       # → ClockMismatch

# 界面自测：强制走拦截界面，确认提示文案与按钮（只拦截、不放行，不是绕过手段）
$env:DMV_ALPHA_FORCE_BLOCK="Expired"; .\DFAudioStudio.App.exe
```

编译 / 发布 / 打包（**正式版完全不受影响**：Release 构建不定义 `ALPHA_EXPIRY`，
`Services/TimeGuard.cs` 整文件被 `#if` 排除，已实测正式版二进制不含任何校时/过期代码）：

```powershell
# 发布（自包含，直接双击可运行）
dotnet publish -c Alpha src\DFAudioStudio.App\DFAudioStudio.App.csproj -r win-x64 -p:SelfContained=true

# 安装包（Inno Setup 7；只装程序本体，不含模型/音频/索引数据）
#   先把 publish 目录内容拷到 installer\app，再编译脚本
ISCC.exe installer\DFAudioStudio_Alpha.iss
# → installer\DFAudioStudio_Alpha_Setup.exe
```

要点：WinUI 3 的 `dotnet publish` 不会自动把 XAML 编译产物（`*.xbf` 与 `DFAudioStudio.App.pri`）带进 publish 目录，
少了它们运行时会报 `Cannot locate resource from 'ms-appx:///MainWindow.xaml'`，工程里已加
`CopyXamlResourcesAfterPublish` 目标自动补齐。

## 十四、背景随音乐频谱变化

播放音频时背景跟着频谱跳动，没有播放时回到原来的三色渐变。

**表现**

- 播放中：背景淡入一层可视化 —— 底部一排 40 根柱子（高度 = 各频段能量）+ 一层全窗脉冲罩
  （亮度跟着整体能量呼吸，低频权重更高，接近"鼓点跳动"的观感）；原来的三色渐变继续在下面缓慢循环。
- 暂停 / 停止 / 播完 / 切歌：能量在半秒内自然衰减，可视化淡出，**只剩三色渐变**。
- 切歌不会闪：`StartPlaybackAsync` 会先 Detach 再 Attach，Detach 刻意不立刻触发淡出，
  由能量衰减决定（所以换歌时背景是连续的）。
- 颜色跟随主题（浅色用主蓝 `#0B57D0`，深色用亮蓝 `#8AB4FF`），主题切换时刷新。

**实现**

| 文件 | 作用 |
| --- | --- |
| `Services/SpectrumService.cs` | 频谱分析：复用 `Core.Indexing.AudioDecoder` 把音频解成 16kHz 单声道 float → 按 `PlaybackSession.Position` 取 1024 点做 FFT（汉宁窗，自实现基 2 迭代 FFT，**无第三方依赖**）→ 40 个对数频段（40Hz~7kHz）→ 快起慢落平滑 + 温和自动增益（安静素材最多放大 3 倍） |
| `MainWindow.xaml` | `BgSpectrumLayer`（`BgSpectrumPulse` 脉冲罩 + `BgSpectrumBars` 柱子）插在三色渐变之上、内容之下 |
| `MainWindow.xaml.cs` | `InitSpectrumVisual()` 生成柱子 / 按主题上色 / 订阅事件；`OnSpectrumActiveChanged()` 淡入淡出；`OnSpectrumFrame()` 每帧刷新柱高与脉冲 |
| `Controls/AudioBrowserControl.xaml.cs` | 播放时 `SpectrumService.Attach(活动引擎的时钟, path)`（系统播放器或 DSP 引擎都行，切引擎会自动重挂）；`StopPlayerSource()` 里 `Detach()` |

**可调参数**

- `SpectrumService.cs` 顶部：`BandCount`（柱子数 40）、`FftSize`（1024）、`MinHz/MaxHz`（40~7000）、
  `SilenceDb`（-58dB 以下算静音）、`Attack/Release`（0.55/0.12 起落速度）、自动增益区间 `0.45f / 1f~3f`。
- `MainWindow.xaml.cs`：柱子区高度比例（`RootGrid.ActualHeight * 0.30`，120~300px 夹取）、
  频谱层淡入目标不透明度（0.95）、柱子圆角/间距、脉冲罩强度（`Level * 1.2`）。

**自检**（不用进界面，正式版与 Alpha 版都可用）：

```powershell
# 解出音频 → 按时间轴取样算频谱 → 输出每段能量、峰值频段与 40 段字符柱状图
DFAudioStudio.App.exe --spectrumtest "I:\...\Media\xxx.wav" report.txt
```

实测：引擎循环峰值落在 141~172Hz（低频持续）、语音片段在 400Hz~3kHz 起伏且首尾静音归零，
真机播放 10s 语音时的日志（`<数据根>\logs\app-yyyyMMdd.log`）：

```
频谱：已就绪（PCM16 48000Hz→16kHz，10.0s）
频谱背景：淡入（音乐播放中）
频谱：level=0.05 峰值=2828-3219Hz 频段能量前 8 段=[...]     ← 每 5 秒一条心跳
频谱背景：淡出（没有播放，回到渐变）
```

## 十五、变速（保持音调）与变调（保持速度）

播放条上两个滑块，播放中拖就能实时生效：

| 控件 | 范围 | 效果 |
| --- | --- | --- |
| 变速（保持音调） | 0.50× ~ 2.00×（步进 0.05） | 快慢变化，**音高不变**（SoundTouch `Tempo`） |
| 变调（保持速度） | −12 ~ +12 半音（步进 1） | 音高升降，**速度不变**（SoundTouch `PitchSemiTones`） |

系统 `MediaPlayer` 的倍速（`PlaybackSession.PlaybackRate`）一定会连带变调，做不到这两件事，
所以这两个滑块背后是**另一条播放链路**：

```
AudioFileReader（NAudio 解码）→ SoundTouchSampleProvider（Tempo / PitchSemiTones）→ WasapiOut
```

**引擎怎么切**（对用户透明，两个值都是中性时完全走原来的系统播放器）：

- 两个值都是中性（1.00× / 0 半音）→ 系统 `MediaPlayer`（多格式解码更稳，原有诊断链路照旧）；
- 只要有一个不是中性 → 自动切到 DSP 引擎，**从当前位置接着放**（暂停中就保持暂停）；
- 两个值都回到中性 → 从当前位置交还系统播放器；
- 变速/变调值不是中性时**新开一首**，直接由 DSP 引擎起播；
- 只影响新开始播放的曲目与当前曲目，滑块值会一直保留（切歌也沿用）；
- DSP 引擎解不了的格式（NAudio 不支持，少见）会自动回退系统播放器，并在播放条上写明原因。

**为什么滑块要"等值稳定"再切引擎**：WinUI 的 `Slider` 值变化自带一段动画，拖一次会连发一串
中间值；中间值各切一次引擎会反复重载文件（卡顿 + 破音）。所以参数实时改，**引擎切换延后 300ms
用最终值判一次**（`ScheduleEngineEvaluation()` / `EvaluateEngine()`）。

| 文件 | 作用 |
| --- | --- |
| `Services/DspAudioPlayer.cs` | DSP 播放引擎：加载/播放/暂停/跳转/音量，`Speed`(0.5~2.0)、`Semitones`(±12)，自然播完事件，`DescribeState()` 诊断 |
| `Services/SoundTouchSampleProvider.cs` | NAudio `ISampleProvider` 包装 SoundTouch；`Clear()` 用于跳转后丢残留 |
| `Controls/AudioBrowserControl.xaml(.cs)` | 两个滑块 + 引擎切换（`TuneNeeded` / `SwitchToDspEngine` / `SwitchBackToMediaPlayer`），进度、音量、暂停、频谱都跟随"活动引擎" |
| `Controls/AudioBrowserControl.xaml`（`SpeedBox` 已移除） | 原来的"倍速下拉"会连带变调，已被这两个滑块取代 |

**踩过的坑（都在这条链路上）**

1. **`Array.Copy` 在 WASAPI 回调里会炸**：NAudio 的 `SampleToWaveProvider` 为了省一次拷贝，
   把声卡的 `byte[]` 直接"当成" `float[]` 传进 `Read(float[], int, int)`，运行时类型仍是 `Byte[]`，
   而 `Array.Copy` 会做运行时数组类型检查 → `ArrayTypeMismatchException: Source array type cannot be
   assigned to destination array type.`。表现极具迷惑性：`PlaybackState=Playing`、进度条不走、没声音。
   解法是把输出拷贝改成 **Span 拷贝**（`_outBuffer.AsSpan(...).CopyTo(buffer.AsSpan(...))`，只按元素
   大小搬运、不做类型检查），并按目标数组真实容量收敛（`limit = Math.Min(count, buffer.Length - offset)`）。
2. **声卡缓冲区比请求小**：`WasapiOut(..., 120ms)` 会请求 120ms 的采样数（48k 立体声 = 11520 个采样），
   而下游中转数组可能更小，所以 `Read` 里**绝不能写超过 `buffer.Length`**。
3. NAudio 3.1.0 与 SoundTouch 支持包不兼容（要求 NAudio 1.9，且改了 `ISampleProvider.Read` 签名），
   最终固定在 `NAudio 2.2.1` + `SoundTouch.Net 2.3.2`，自己写 `ISampleProvider` 适配。

**自检**（不用进界面，正式版与 Alpha 版都可用）：

```powershell
# 离线链自检：同一段音频按不同速度/音调跑一遍，输出时长与主频 → 验证「变速不变调 / 变调不变速」
DFAudioStudio.App.exe --dsptest "I:\...\Media\xxx.wav" dsp_report.txt

# 实时输出通路自检（枚举输出设备 + 正弦波直通 + DSP 链实时播放，用来定位"没声音/进度不走"）
DFAudioStudio.App.exe --audiotest report.txt --file "I:\...\xxx.wav" --seconds 5
DFAudioStudio.App.exe --audiotest report.txt --file "I:\...\xxx.wav" --seconds 9 --switch-tempo 0.5 --switch-at 3
#   --device N 可指定第 N 个输出设备（report 开头会列出设备清单，标出系统默认设备）
```

实测数据（440Hz 测试音，`--dsptest`）：

| 设置 | 时长 | 主频 |
| --- | --- | --- |
| 原速原调 | 3.00s | 440.0Hz |
| 速度 0.5× | 6.00s | 440.1Hz（音调没变） |
| 速度 2.0× | 1.50s | 439.9Hz（音调没变） |
| 变调 +12 半音 | 3.00s | 880.1Hz（速度没变） |
| 变调 −12 半音 | 3.00s | 219.9Hz（速度没变） |
| 速度 1.5× + 变调 −5 半音 | 2.00s | 329.7Hz |

实时链路实测（真机、默认输出设备「耳机 (Realtek(R) Audio)」，`--audiotest`）：

```
1.0×  → 9 秒墙钟进度 9.00s 量级（变速 1.00× 时声卡每秒稳定拉走 0.51s 音频）
播放中途把速度切成 0.50× → 之后 6.08 秒墙钟进度走了 3.12s = 实测 0.51×（期望 0.50×）
```

界面侧实测（Alpha 版真机日志，`<数据根>\logs\app-yyyyMMdd.log`）：

```
[16:41:04] 变速/变调：已切到 DSP 引擎（1.00× / +5 半音，保持暂停）— Music_Blasting_CG_FirstEntry.wav
            拖动滑块时人还在暂停状态，切引擎后位置不动、状态=停      ← 引擎切换保持暂停
[16:41:11] 引擎=DSP 位置=5.63s 速度=1.00× 音调=+5 状态=播放        ← 变调不影响速度
[16:41:17] 引擎=DSP 位置=10.54s 速度=0.50× 音调=+5 状态=播放       ← 2 秒走 1.05s ≈ 0.5×
[16:41:21] 变速/变调：已回到原速原调，交还系统播放器 @ 12.5s（继续播放）
```

**可调参数**

- `DspAudioPlayer.cs`：`MinSpeed/MaxSpeed`（0.5/2.0）、`MinSemitones/MaxSemitones`（−12/+12）、
  `WasapiOut` 延迟（120ms）。
- `SoundTouchSampleProvider.cs`：内部缓冲 4096 帧、Tempo 夹取 0.1~4.0、Pitch 夹取 ±24 半音。
- `AudioBrowserControl.xaml.cs`：引擎切换去抖 `_tuneDebounce.Interval`（300ms）。

---

## 本仓库包含 / 不包含

**包含**：全部 C#/XAML 源码（App + Core）、Inno Setup 安装脚本、`获取模型.bat`、README。

**不包含**（体积或版权原因，请自行准备）：

- Whisper 模型（`tools/whisper-model/*.pt`，约 300MB）：跑 `获取模型.bat` 自动下载，或在「设置」里指定已有模型。
- 音频素材与索引库（`data/`、`models/`、`*.db`）：都是你自己游戏目录与识别结果，不入库。
- 构建产物（`bin/`、`obj/`、`installer/app/`）与已打包的安装包。

## 安装包

见本仓库的 **Releases**。自己构建：

```powershell
dotnet publish src\DeltaMovieStudio.App\DFAudioStudio.App.csproj -c Release -r win-x64 -p:SelfContained=true
dotnet publish src\DeltaMovieStudio.App\DFAudioStudio.App.csproj -c Alpha   -r win-x64 -p:SelfContained=true
```

（把上面的 csproj 路径换成实际的 `src\DFAudioStudio.App\DFAudioStudio.App.csproj`。）

## 许可证

MIT（见 [LICENSE](LICENSE)）。第三方组件：Windows App SDK（MIT）、Whisper.net / whisper.cpp（MIT）、NAudio（MIT）、SoundTouch（LGPL）、Microsoft.Data.Sqlite（MIT）。