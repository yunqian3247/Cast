# 2026-10-02 性能验收记录

实际 WinForms + WebView2，Runtime 149.0.4022.80。每轮载入 10000 条，随后以 10 ms 目标间隔接收约 6 秒，实测约 64 条/秒；短帧使用 60 条随包预设，长帧为 64 字节 ASCII。持续接收覆盖缓存裁剪、批量桥接与虚拟列表。测量使用独立临时用户目录，持续日志保持默认关闭。

## 日志追加

在同一个真实 WebView 中，对完全相同的 10000 条保留记录执行 1000 个批次，每批 5 条。旧算法取自本轮修改前源码，当前算法直接调用产品 `appendLogs`；每次比较同时核对保留对象序列与字节预算。计时包含数组处理，消息桥和渲染另行测量。

| 数据 | 旧算法 | 当前实现 | 耗时改善 | 保留结果一致 |
|---|---:|---:|---:|---|
| 短帧 | 34.2 ms | 1.0 ms | 34.2 倍 | True |
| 长帧 | 46.6 ms | 1.3 ms | 35.8 倍 | True |

当前实现按新增记录计算字节预算并就地裁剪，行操作直接引用日志对象。滚动历史位置、筛选、搜索、展开、复制及 10000 条回归已通过 Playwright，真实桌面回归同时验证桥接顺序和清空后继续接收。

## 进程内存

下面为宿主及该 WebView 进程合计 Private Bytes，单位 MiB。同机实测数据包含 WebView、字体、DevTools 和垃圾回收时序，属于单轮诊断结果。整体内存仍偏高，持续接收阶段增长需要继续分析。

| 数据 / 阶段 | 修改前 | 当前 | 变化 |
|---|---:|---:|---:|
| 短帧 / 空界面 | 359.8 | 360.3 | +0.1% |
| 短帧 / 载入 1 万条 | 417.2 | 419.1 | +0.5% |
| 短帧 / 持续接收 | 462.8 | 490.5 | +6.0% |
| 长帧 / 空界面 | 351.2 | 357.5 | +1.8% |
| 长帧 / 载入 1 万条 | 432.5 | 439.2 | +1.5% |
| 长帧 / 持续接收 | 480.4 | 503.7 | +4.8% |

UI 常用字体资源约 0.67 MiB，保留原始全字体作为其他字符的后备；OpenType 排版特性和等宽数字完整保留，原生启动字体为约 109 KiB。内存目标选项在支持它的 Runtime 上应用。资源缩小与进程总内存是分别测量的指标。

## 复验

```powershell
dotnet run --project tests/cast.Performance -c Release -- . short audit-fixes-final 6
dotnet run --project tests/cast.Performance -c Release -- . long audit-fixes-final 6
```

原始报告：`artifacts/performance/current-audit/10000-{short,long}.json` 和 `artifacts/performance/audit-fixes-final/10000-{short,long}.json`。同目录 `comparison.json` 汇总指标，PNG 保存实际通信界面。物理串口长时间测试见 [功能验证](functional-verification.md)。
