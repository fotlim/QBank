# 题库工坊 QBank

> 一个用 **WinUI 3** 写的 Windows 题库软件：把 OCS 在线题库、大模型 API、本地题库、局域网共享、图片识题放在同一个界面里，粘贴题干就能出答案，答过的题自动攒成自己的题库。

![搜题](docs/01-search.png)

## 它是干什么的

刷网课、做在线测试的时候，最烦的是题目分散在好几个地方：OCS 脚本里挂着几个在线题库、大模型要单独开一个网页问、自己攒的题存在 Excel 里、截图里的题干还得手打。

QBank 把这些合成一个窗口：**输入题干（或者直接截图）→ 同时问本地题库 / 局域网邻居 / 在线题库 / 大模型 → 按相似度排序给出答案 → 一键存回本地**。用得越久本地题库越厚，答案来得越快，最后大部分题几十毫秒就能出结果，不再花 token。

它还能**反过来当 OCS 的题库**：开一个本机 HTTP 接口，生成标准 `AnswererWrapper` 配置，OCS 脚本收题时会来问 QBank，答案由本地题库 + 局域网 + AI 一起给。

## 主要功能

| | 功能 | 说明 |
| --- | --- | --- |
| 🔍 | **多来源并发搜题** | 本地题库、局域网、OCS 在线题库、AI 模型四路同时查，按相似度统一排序去重 |
| 📚 | **OCS 题库完全兼容** | 直接吃 OCS 的 `AnswererWrapper` 配置（JSON / 链接 / Base64），handler 用内置 JS 引擎真跑，连报错文案都和官方逐字一致 |
| 🤖 | **多模型管理** | DeepSeek、硅基流动、阿里云百炼、OpenAI、Ollama、任意 OpenAI 兼容网关；填好 Key 点「获取模型」直接从接口拉可用模型列表 |
| 🖼️ | **图片识题** | 截屏 / 选图片 → OCR → 自动填进题干和选项。四档引擎：Umi-OCR（PaddleOCR 离线，默认）、Windows OCR、Ollama 视觉模型、自定义 HTTP |
| 🗄️ | **本地题库** | SQLite 存储，模糊匹配 + 相似度精排；导入导出 JSON / CSV / TXT，答案可自动写回 |
| 🌐 | **局域网共享** | 把自己变成题库服务器，UDP 自动发现同网段机器，互相查题、互相同步题库 |
| 🕘 | **搜索历史** | 每次搜题的来源、分数、耗时全记录，可一键「再搜一次」 |
| ↔️ | **反向输出给 OCS** | 把自己包装成标准 OCS 题库，让 OCS 脚本用上你的本地题库和 AI |

## 安装

到 [Releases](https://github.com/fotlim/QBank/releases) 下载 `QBank-Setup-1.0.0.exe`，双击安装。

- 安装包**自带 .NET 运行时和 Windows App SDK**，用户机器不用另装任何东西
- 按用户安装（装到 `%LOCALAPPDATA%\Programs\QBank`），**不需要管理员权限**，不弹 UAC
- 卸载时会问要不要连题库数据一起删，默认保留

也可以下绿色版：把 `publish` 目录整个拷走直接跑 `QBank.exe`。

## 界面

| 本地题库 | 历史记录 |
| --- | --- |
| ![本地题库](docs/02-bank.png) | ![历史记录](docs/03-history.png) |

| 局域网共享 | OCS 对接 |
| --- | --- |
| ![局域网](docs/04-lan.png) | ![OCS 对接](docs/05-ocs-bridge.png) |

## 技术栈

- **界面**：WinUI 3 / Windows App SDK 2.6.4，unpackaged 直接跑 exe（免 MSIX）
- **引擎**：.NET 8，`QBank.Core` 无界面依赖，可单独引用
- **JS 执行**：Jint 4.17（纯托管，带超时 / 语句数 / 内存三重限制）
- **存储**：Microsoft.Data.Sqlite（WAL 模式）
- **HTTP**：`TcpListener` 自写极简 HTTP/1.1 服务，不用 `HttpListener`，所以免 urlacl、不需要管理员
- **自测**：`QBank.Cli` 内置 mock 服务，17 项端到端离线自测，不联网不花 token

---

## 1. 跑起来

### 环境要求

用安装包的话什么都不用装，只要求系统是 **Windows 10 1809 (17763) 及以上**。

自己从源码构建才需要：

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809 (17763) 及以上，Windows 11 更佳 |
| 构建 | .NET 8 SDK（`winget install Microsoft.DotNet.SDK.8`） |
| 图形界面 | 无需 Visual Studio，`dotnet build` 就够 |

### 构建与启动

```powershell
cd QBank
dotnet build src\QBank.App\QBank.App.csproj -c Debug
.\src\QBank.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\QBank.exe
```

发布成自带运行时的单目录（目标机无需装 .NET，**推荐**）：

```powershell
dotnet publish src\QBank.App\QBank.App.csproj -c Release -r win-x64 --self-contained true -o publish
.\publish\QBank.exe
```

> 别用 `--self-contained false`：那样产出的目录缺 `coreclr.dll`，目标机必须预装 .NET 8 桌面运行时，否则双击没反应。

发布目录里必须有 `QBank.pri`（XAML 资源索引），少了它启动会直接 `XamlParseException`。`QBank.App.csproj` 里的 `CopyPriToPublish` 目标会在 publish 后自动把它补进去，正常不用管；如果你是手工复制文件部署，记得连同 `QBank.pri` 一起拷。

### 打安装包

```powershell
# 先 publish 自带运行时版本
dotnet publish src\QBank.App\QBank.App.csproj -c Release -r win-x64 --self-contained true -o publish
# 再编译安装包（需要 Inno Setup 6）
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" install\qbank.iss
```

产物在 `install\Output\QBank-Setup-1.0.0.exe`，232 MB 的内容压到约 63 MB。

**便携模式**：在 exe 同目录放一个 `portable.txt`（或 `portable` 文件夹），数据就存到 `exe\data`，不碰 `%LOCALAPPDATA%`。

### 数据放在哪

默认 `%LOCALAPPDATA%\QBank`，里面是 `config.json` + `qbank.db`。如果这个目录写不进去（沙箱、只读盘、权限被策略限制），程序会自动回落到 `exe\data`，再不行回落 `%TEMP%\QBank`，启动日志在 `exe\startup.log` 里写明实际用了哪个目录。

---

## 2. 界面导览

左侧五个入口：**搜题 / 本地题库 / 历史记录 / 局域网共享 / 设置**。

### 搜题

左边输入，右边看结果：

1. 题干框粘贴题目（或点「截图识题」「选图片」自动 OCR 填入）
2. 选项框一行一个，`A. 上海` 这种带前缀的写法最省事
3. 「题目来源」勾选本次要问哪些来源
4. 「开始搜题」或 **Ctrl+Enter**

结果区顶部是「最佳答案」大卡片（一键复制 / 存入本地题库），下面按分数列出所有来源的候选，卡片上标着来源名和相似度。

### 本地题库

搜索、按题型筛选、按更新时间/命中数/标题排序；支持导入（JSON / CSV / TXT，可直接从剪贴板粘贴）、导出（写文件 + 复制到剪贴板）、手动新增、单条删除。

### 历史记录

每次搜题的完整记录：题干、答案、来源、分数、耗时、成功与否。可以「再搜一次」跳回搜题页并自动重搜，也能按关键词/来源筛选、只留成功的、清理旧记录。

### 局域网共享

见第 6 节。

### 设置

- **AI 模型管理**：卡片式管理多个模型服务，填好 BaseUrl 和 Key 后点「获取模型」直接从接口拉可用模型列表
- **在线题库（OCS）**：粘贴 JSON / 链接 / Base64 导入，导出的 JSON 能直接粘回 OCS
- **图片识题**：选引擎、配参数、一键自检
- **搜题选项**：超时、并发、缓存命中阈值、自动入库阈值
- **数据与存储**：数据目录、打开目录、备份数据库、题库统计

---

## 3. 配一个大模型

设置页 →「添加服务商」，六个预设任选：

| 预设 | 接口地址 | 默认模型 |
| --- | --- | --- |
| DeepSeek | `https://api.deepseek.com/v1` | `deepseek-chat` |
| 硅基流动 | `https://api.siliconflow.cn/v1` | `deepseek-ai/DeepSeek-V3` |
| 阿里云百炼 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-plus` |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` |
| Ollama（本地） | `http://127.0.0.1:11434/v1` | `qwen3:8b` |
| 自定义 | 自己填 | 自己填 |

任何 OpenAI 兼容网关都能用（OneAPI、LiteLLM、vLLM、LM Studio……），选「自定义」填 BaseUrl 即可。

### 模型管理

- 点「**获取模型**」：真的去接口拉该账号可用的模型列表（`GET {base}/models`；Ollama 走原生 `/api/tags`，能顺带拿到参数量和量化等级）
- 模型下拉框可以编辑，手填也行
- 拉到视觉模型（`*-vl`、`llava`、`gpt-4o` 之类）时会提示勾选「支持图片输入」
- **高级参数**里可以改温度、最大 tokens、超时、优先级，也能塞额外请求体参数（比如 `{"enable_thinking": false}`）和自定义提示词

程序内置的答题提示词要求模型只输出 JSON（`answer` / `question` / `confidence` / `explain`），并且按题型规范化答案：单选填一个字母，多选连写 `ABD`，判断题映射成选项字母，填空多空用 `#` 分隔。

---

## 4. 导入 OCS 在线题库

完全兼容 [OCS 的 AnswererWrapper 规范](https://docs.ocsjs.com/docs/other/api#AnswererWrapper)。设置页 →「在线题库」→ 粘贴下面任一形式 → 导入：

- JSON 数组字符串
- 题库配置的 http(s) 链接
- Base64 编码的配置

```json
[
  {
    "name": "示例题库",
    "url": "https://example.com/api/search?title=${title}&type=${type}",
    "method": "get",
    "contentType": "json",
    "handler": "return (res) => res.data.map(item => [item.question, item.answer])"
  }
]
```

字段与 OCS 一致：`url`（必填）、`name`（必填）、`homepage`、`data`、`method`（get/post）、`contentType`（json/text）、`type`（fetch/GM_xmlhttpRequest）、`headers`、`handler`（必填）。

`handler` 用内置的纯托管 JS 引擎（Jint）执行，等价于 OCS 的 `Function(handler)()`，并且提供 `localStorage` 和 `console` 垫片——OCS 文档里「用 localStorage 存上下文再改传给 AI」那套写法可以原样搬过来。脚本有超时、语句数、内存三重限制，恶意配置拖不死程序。

校验错误信息与 OCS 官方源码逐字一致（`第 N 个题库的 名字(name) 为空` 等），方便直接对照排查。

### 与 OCS 的两处有意差异

1. **URL 占位符只编码替换值本身**。OCS 会把整个 URL 再 `encodeURIComponent` 一遍，这里不做，避免双重编码把中文题干变成乱码。
2. **未识别的占位符替换成空串**。OCS 会替换成字面量字符串 `"undefined"`。

另外 POST 请求时，如果 header 里的 `Content-Type` 是 `application/x-www-form-urlencoded`，本程序会发标准表单体（比 OCS 的 fetch 分支只会 `JSON.stringify` 更实用）。

---

## 5. 图片识题

搜题页有两个按钮：「截图识题」（截整个屏幕）和「选图片」（从文件选）。识别完自动填进题干和选项框。

四档引擎，在设置页切换：

| 引擎 | 说明 | 需要什么 |
| --- | --- | --- |
| **Umi-OCR**（默认） | PaddleOCR 离线引擎，中文识别效果好，全程本机 | 装 [Umi-OCR](https://github.com/hiroi-sora/Umi-OCR)，设置页点「探测安装位置」 |
| Windows OCR | 系统自带 `Windows.Media.Ocr`，零下载、最省事 | 系统装了中文语言包 |
| Ollama 视觉模型 | 本地大模型读图（如 `qwen2.5vl:7b`） | 本机跑着 Ollama |
| 自定义 HTTP | 任意 OCR 服务，POST 图片 base64 | 自己填 URL 和结果路径 |

Umi-OCR 优先走它的 HTTP 接口（默认 `127.0.0.1:1224`，注意 Umi-OCR 里可以改成别的端口），连不上就自动退回直接调 `PaddleOCR-json.exe` 单图模式，两条路都不需要联网。

识别出的文本会按「题号 + 题干 + A/B/C 选项」的结构切分（设置里可关掉自动切分）。

---

## 6. 局域网题库共享

### 当服务端（把题库借给别人查）

局域网页 →「本机共享设置」：

- 勾「允许其它设备查询我的题库」
- 勾「响应局域网自动发现」→ 别人能自动搜到你
- 勾「对局域网开放」→ 不勾就只允许本机 127.0.0.1 访问（调试用）
- 可选填「访问口令」
- 「保存并应用」

用的是自写的 TcpListener 极简 HTTP 服务，**不需要管理员权限、不需要 urlacl、不用改防火墙规则**（首次监听时 Windows 可能弹一次防火墙授权，允许即可）。

路由：`GET /qbank/ping`、`POST /qbank/query`、`POST /qbank/add`、`GET /qbank/export`，口令走 `x-qbank-token` 头或 `token` 查询参数。

### 当客户端（查别人的题库）

- 点「**搜索局域网节点**」→ UDP 广播自动发现同网段正在共享的机器
- 或者「手动添加」，填 IP 和端口
- 每个节点都能单独测试连通性、移除

搜题时勾上「局域网共享」即可，会并发问所有已添加的节点。

「题库同步」里还能把自己的题推给所有节点（对方要允许远程写入），或者从所有节点把题库拉过来。

---

## 7. 给 OCS 提供题库（让 OCS 脚本自动答题）

这一节是整个程序最有意思的部分：把 QBank 变成一个**OCS 能直接调用的题库接口**，于是 OCS 脚本收题时就会来问本程序，答案可以由本地题库、局域网邻居、甚至大模型给出。

### 怎么用（三步）

1. 打开 设置 →「**OCS 对接 · 让 OCS 脚本自动答题**」，确认状态是「运行中 · 127.0.0.1:18322」。
2. 点「**复制 OCS 配置**」。
3. 到 OCS 的「题库配置」里粘贴，或者用「一键导入 OCS」让它直接把配置链接交给 OCS。

生成的配置长这样（数值随你的设置变化）：

```json
[{
  "name": "题库工坊",
  "url": "http://127.0.0.1:18322/qbank/ocs/search?title=${title}&options=${options}&type=${type}&token=你的口令",
  "method": "get",
  "contentType": "json",
  "type": "fetch",
  "headers": {},
  "handler": "return (res) => { ... }"
}]
```

这就是**标准 OCS AnswererWrapper**，字段与 OCS 官方完全一致——因为本程序内部就是按 OCS 的解析器实现了一遍（连报错文案都逐字对齐），所以它在 OCS 里和普通题库没有区别。

### 接口返回什么

严格按 OCS 约定：

| 情况 | 返回 |
| --- | --- |
| 命中 | `[[null, "B"], [null, "C"]]`（题目为 null，OCS 用当前搜索的题干） |
| 未命中 | `["题库工坊未命中", null]`（会显示成 OCS 的一条通知） |
| 口令错 | `["访问口令不正确", null]` |

`handler` 就是把这几种形态翻译成 OCS 认识的 `[[题目, 答案], ...]`；未命中时转成 `[消息, undefined]`。**这个 handler 不是手写的，是程序按 OCS 规范生成后，再丢进真正的 JS 引擎里跑过一遍验证的**（自测里的「OCS 对接口」那项）。

### 接口清单

| 路由 | 用途 |
| --- | --- |
| `GET /qbank/ocs/ping` | 健康检查 |
| `GET /qbank/ocs/search?title=…&options=…&type=…` | 搜题（也接受 POST JSON / 表单） |
| `GET /qbank/ocs/ocs.json` | **纯配置数组**，可直接当「题库配置链接」导入 OCS |
| `GET /qbank/ocs/config` | 带元信息的包装版 |
| `GET /qbank/ocs/docs` | 接口说明 |
| `GET /qbank/ocs/export` | 导出本地题库 |

CORS 已放开（含 Chrome 私有网络访问需要的 `Access-Control-Allow-Private-Network`），所以网页版 OCS 从 https 页面访问 127.0.0.1 也能通。

### 回答由谁给

卡片上的勾选决定这次搜索会问哪些来源，默认是「本地题库 + 局域网 + AI」：

- **包含本地题库 / 缓存** —— 命中最快，通常几十毫秒
- **包含局域网节点** —— 问同网段其它开着 QBank 的机器
- **包含 AI 模型** —— 前两者都没答案时才值得开，注意会花 token
- **包含在线题库** —— 默认关。开了会绕圈：OCS 问你 → 你问 OCS 里配的题库 → 那个题库可能就是你自己

「最低相似度」控制松紧（默认 0.55，太高容易找不到，太低容易答错）；「最多返回几条」对应 OCS 的多题库候选。

### 常见问题

**OCS 里显示「未命中」但我确实有这道题？** 把「最低相似度」调低到 0.4 试试；或者确认那道题真的在本地题库里（搜题页搜一下）。

**换个程序也要能用吗？** 可以，接口就是普通 HTTP，返回标准 JSON。不会写 handler 就直接用 `[[null,"答案"]]` 这种结构。

**安全吗？** 默认只绑 `127.0.0.1`，只有本机能访问。要跨机器用得勾「允许局域网其它电脑调用」，并建议设一个访问口令。

---

## 8. 命令行自测

`src\QBank.Cli` 是一套离线端到端自测，内置 mock 的 OCS 题库 / OpenAI 兼容接口 / 局域网服务，**不联网、不花 token**：

```powershell
dotnet run --project src\QBank.Cli\QBank.Cli.csproj -c Debug                # 常规 16 项
dotnet run --project src\QBank.Cli\QBank.Cli.csproj -c Debug -- --ocr       # 加上本机 Umi-OCR 实机识别（17 项）
dotnet run --project src\QBank.Cli\QBank.Cli.csproj -c Debug -- --verbose   # 打印每项断言
dotnet run --project src\QBank.Cli\QBank.Cli.csproj -c Debug -- --only 局域网 # 只跑名字含「局域网」的
```

覆盖：相似度打分、题型推断、答案切分（含代码题保护）、OCS 配置校验（错误文案逐字比对）、JS handler 执行与 localStorage、OCS GET/POST 请求、AI 答题（含 json 模式被 400 拒绝后的自动重试）、模型列表拉取（OpenAI 风格 + Ollama 原生）、本地题库增删查、导入导出、历史记录、局域网服务端与客户端（含口令校验）、**OCS 对接口（起真 HTTP 服务 + 把生成的 handler 丢进 JS 引擎跑一遍）**、OCR 题干切分、搜题总编排（多来源并发 + 缓存短路）。

退出码 0 = 全过。

---

## 9. 项目结构

```
QBank/
├─ src/
│  ├─ QBank.Core/          # 引擎（无界面依赖，net8.0-windows10.0.19041.0）
│  │  ├─ Models/           # Question、QuestionType、SearchModels、AppConfig
│  │  ├─ Ocs/              # AnswererWrapper 解析、JS handler 执行、题库请求、答案切分
│  │  ├─ Ai/               # OpenAI 兼容客户端、答题服务、模型目录
│  │  ├─ Search/           # 相似度打分、搜题总编排
│  │  ├─ Storage/          # SQLite 本地题库、搜索历史、配置读写
│  │  ├─ Lan/              # 局域网 HTTP 服务端、UDP 发现、客户端
│  │  ├─ Http/             # 极简 HTTP/1.1 服务（TcpListener，免 urlacl）
│  │  ├─ Bridge/           # OCS 对接口：把搜题结果包装成 OCS 题库
│  │  └─ Ocr/              # Umi-OCR / Windows OCR / Ollama / 自定义、截屏、题干切分
│  ├─ QBank.Cli/           # 离线自测
│  └─ QBank.App/           # WinUI 3 界面
│     ├─ Pages/            # 搜题、本地题库、历史、局域网、设置
│     ├─ Controls/         # 服务商编辑卡
│     └─ Services/         # 应用级单例、配置保存
└─ tools/
   ├─ smoke-shot.ps1       # 起窗口 → 截图 → 关掉（无头烟测）
   └─ scroll-shot.ps1      # 同上，可滚动 / 翻页后再截
```

---

## 10. 常见问题

**点搜题没反应 / 没结果**
先看结果区的来源卡片，每个来源都会写失败原因。最常见是没配 API Key（401）、BaseUrl 少了 `/v1`（404）、或者一个来源都没勾。

**接口返回「不是合法 JSON」**
题库返回的其实是 HTML 错误页或纯文本。把该题目的 `contentType` 改成 `text`，再让 `handler` 按字符串处理。

**「获取模型」失败**
401/403 是 Key 不对；404 检查 BaseUrl 是否要以 `/v1` 结尾；429 是限流或额度不足。Ollama 显示连不上就确认 `ollama serve` 在跑。

**截图识题抓到的图不对**
「截图识题」抓的是整个虚拟桌面（多屏会全抓进来）。只想要某块区域就用「选图片」，或者用系统截图工具截好再选文件。

**中文在命令行输出乱码**
`QBank.Cli` 已强制 UTF-8 输出；如果还是乱，先 `chcp 65001`。

**数据目录到底在哪**
看 `exe\startup.log` 最后几行，会写「应用已启动，数据目录：…」。

**OCS 里点了「提交」但没反应**
先确认设置页那张卡片写着「运行中」；再点「测试接口」，能弹「接口正常」说明服务没问题。还是不行就检查 OCS 里那条题库的 URL 端口和本程序一致（被占用时端口会自动 +1，以界面显示的为准）。

**在 OCS 里改了口令但本程序没改**
两边必须一致。本程序改完口令要「保存设置」才会重建服务，OCS 那边也要重新导入配置。

---

## 11. 已知限制

- 目前只支持 Windows（WinUI 3 + Windows.Media.Ocr + 系统截屏都是 Windows 专属）。
- 「截图识题」是全屏抓图，没有做框选区域。
- OCS 的 `GM_xmlhttpRequest` 类型按 fetch 处理（功能等价，只是不经过浏览器的油猴 API）。
- 局域网服务是明文 HTTP，口令也是明文传输，只适合可信内网。
- OCS 对接口同时只处理有限并发（默认 8 条），OCS 一次提交几十道题时会排队，不会丢题但会慢。
