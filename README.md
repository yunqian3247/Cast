# cast

Windows 串口调试工具，界面和功能以 [Pebrel 设计稿](ui-preview/index-pebrel.html) 为基准。顶部配置串口，左侧管理预设，中间查看通信并发送数据，右侧编排工作流，底部显示引脚与字节计数。

## 运行

当前预发布版本为 `1.0.6-preview.20260918`。执行下方打包命令后，在 `artifacts/releases/1.0.6-preview.20260918/` 中运行 `cast-win-x64-preview-Setup.exe`，或解压 `cast-win-x64-preview-Portable.zip` 后运行根目录的 `cast.exe`。运行包自带 .NET 和 Sarasa Gothic SC 字体，安装包声明 Microsoft Edge WebView2 Runtime 依赖。

顶部「关于」弹窗底部提供 Velopack 在线更新，支持检查、下载和安装重启。默认从 [GitHub Releases](https://github.com/yunqian3247/Cast/releases) 获取 `win-x64-preview` 通道的预发布更新，配置及打包流程见[在线更新](docs/updates.md)。

选择串口及波特率，通过「8-N-1」配置校验、流控和编码，再点击「打开端口」。串口列表来自 Windows 当前设备，悬停可查看设备名称和硬件标识。启动时保持断开。

启动时先在后台完成配置、字体、布局和主界面首帧加载，再在当前显示器的可用区域中央播放 176 × 188 的图标小窗动画。图标居中，配合淡入、轻微呼吸和跳点，约 0.63 秒后淡出并一次性显示已准备好的主界面；窗口背景与当前主题一致，切换时保持布局稳定。关闭启动画面或按 Esc 可退出；初始化失败时显示错误提示窗口。尺寸随系统 DPI 缩放，系统 UI 效果关闭时直接显示已加载的主界面。

顶部标题导航与 Pebrel 设计稿一致，提供设置、浅色与深色切换、操作提示和关于入口，右侧支持窗口置顶、最小化、最大化及关闭。拖动标题栏可移动窗口，双击可最大化或还原。应用统一使用随包提供的 Sarasa Gothic SC（更纱黑体简体中文版）；普通控件默认 12px、常规字重，标题加粗，辅助信息默认 11px。设置中可调整界面字号和监控字号，监控与发送输入框共用字号，选择后即时预览，取消时恢复。设置还支持左右侧栏功能互换、分别指定启动时显示、保留上次输入、发送后清空和悬浮提示开关。自动滚屏在设置中调整，默认开启；DTR / RTS / CTS / DSR 区域默认隐藏，可在设置中开启显示，隐藏时保留引脚状态。主题、时间戳通过主界面控件调整。发送输入框使用 Enter 发送、Shift + Enter 换行，默认保留内容并在重启后恢复；偏好随应用配置保存。

## 功能

- 文本、HEX、双显及协议翻译；RX/TX/SYS 筛选、搜索定位、时间戳、滚屏、清屏、双击复制。通信窗口按可视范围渲染，向上查看历史时保持位置，回到底部后继续跟随新数据。
- 手动发送、逐行发送、自定义 HEX 后缀、定时循环、Modbus CRC16、HEX 规范化和发送历史；支持 UTF-8、GBK 和 ASCII。
- 预设新建、编辑、删除、过滤、载入、发送及 JSON 导入导出；可配置中文释义和应答匹配规则。
- 工作流步骤添加、排序、删除和延时；单次、无限循环、五轮模式，支持暂停、继续、单步和停止。
- DTR/RTS 输出与 CTS/DSR 输入状态；硬件流控启用时由驱动管理 RTS。
- TXT、CSV、JSON 日志导出；支持全部或筛选结果，JSON 始终保留原始字节，CSV 使用 UTF-8 BOM。

发送区上栏显示 HEX、后缀和定时循环，右侧省略号菜单提供追加 CRC16、规范 HEX。底栏显示逐行发送、行数与字节数、清空和发送。窄窗口下按功能组换行，循环开关、间隔与单位保持同行。

单次手动输入支持最多 1 Mi 字符，逐行发送支持最多 100,000 行，准备发送的编码总量上限为 4 MiB。输入校验按整段统计并显示错误行。发送等待期限按字节数、波特率和串口帧格式计算，手动发送期间按钮显示「停止发送」。

默认窗口为 1120 × 700，最小为 880 × 560。预设栏随窗口宽度在 180～320px 之间调整，工作流栏在 230～340px 之间调整，长名称自动换行。左右面板可收起；发送区随窗口增高最多自动增加 60px，并支持手动拖动。单击数据行立即展开或收起协议释义，双击复制原始数据帧并恢复原来的展开状态。运行期间固定发送快照，发送入口互斥；停止、断开和退出会取消后续步骤。

预设文件示例：[主控语音播报指令](samples/presets/main-controller-voice/main-controller-voice-presets.json)，含 60 条完整 HEX 报文；协议与校验说明见[使用说明](samples/presets/main-controller-voice/README.md)。

## 构建与验证

环境要求：Windows 10/11、.NET 8 SDK、WebView2 Runtime。前端测试另需 Node.js 20 或更高版本和 Playwright Chromium。

```powershell
dotnet test .\cast.sln -c Release
npm --prefix tests/web ci
npx --prefix tests/web playwright install chromium
npm --prefix tests/web test
pwsh -NoProfile -File scripts/Publish-Preview.ps1
```

验收步骤和环境限制见 [功能验证](docs/functional-verification.md)。测试生成的截图位于 `artifacts/pebrel-checks`，由本地保留。

字体文件、来源、校验值和 SIL Open Font License 1.1 位于 [Web/fonts](src/cast.Desktop/Web/fonts/README.md)，构建时自动复制到运行包。字体通过本地文件加载，可离线使用。

## 数据与设计

应用设置、预设、工作流及最近 50 条发送历史位于 `%LOCALAPPDATA%\cast\Data\cast.json`，保存时生成 `.bak` 备份。首次启动优先读取 `serial` 版本的数据及备份，缺失时读取更早版本配置，迁移后保留旧文件；已有 `cast` 配置时继续使用当前配置。通信日志默认最多保留 10,000 条，退出前可导出。

主配置损坏或缺失时读取有效备份；恢复后再次保存会将损坏主文件留存为 `.corrupt-*` 诊断文件，并保护有效备份。配置采用分块传输，整份紧凑 JSON 上限为 64 Mi 字符。标题栏关闭、系统菜单及 Alt+F4 统一保存最新输入与偏好；保存失败时保留窗口并提示重试。日志导出使用临时文件写入，完成后替换目标文件。

设置中的「最大保留记录数」支持 100～100,000 条，默认 10,000 条；「界面刷新间隔 (ms)」支持 20～1,000 ms，默认 50 ms。保存成功后生效，重启后保留，恢复默认可重置。降低记录上限会立即淘汰最早记录，取消或保存失败会保留原上限与历史。刷新间隔用于宿主批量推送和界面合并刷新，串口持续接收。

通信缓存同时受记录数与 16 MiB 文本预算限制，超过任一上限时淘汰最早记录；预算按文本和 HEX 字符串的 UTF-16 大小估算。搜索、复制和导出覆盖保留记录，页面按需显示可见行。性能测量工具位于 `tests/cast.Performance`，报告输出到本地 `artifacts/performance/`。

旧界面、文件传输及历史会话入口已移出产品。旧版用户数据保留在原磁盘位置。

- [当前实施状态](docs/implementation-status.md)
- [产品术语](CONTEXT.md)

## 仓库内容

`src/` 保存源码与随包资源，`tests/` 保存自动化测试和性能工具，`samples/` 保存测试使用的协议预设样例，`scripts/` 保存打包脚本，`ui-preview/` 保存设计参考，`docs/` 保存使用与验证说明。

`.gitignore` 排除构建输出、发布包、依赖缓存、测试截图、运行数据、IDE 个人配置、凭据文件和 `docs/task/` 下的本地任务记录。这些文件可继续保留在开发目录。更纱字体及其许可证、第三方声明和依赖锁定文件随源码纳入版本管理。

更新源配置只包含公开的 GitHub 仓库地址；发布时可通过 `scripts/Publish-Preview.ps1 -Source static -FeedUrl` 切换到 HTTPS 静态托管。访问令牌和签名私钥应保存在本机凭据存储或 CI 密钥配置中。
