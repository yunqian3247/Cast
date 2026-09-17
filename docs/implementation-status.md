# 实施状态

当前产品以 `ui-preview/index-pebrel.html` 为唯一设计基准，桌面界面位于 `src/serial.Desktop/Web`。WinForms 承载 WebView2，本地消息桥连接 C# 串口服务。

## 已实现

- 顶部串口工具条、左侧预设、中部通信终端与发送区、右侧工作流和底部状态条。
- 本机端口枚举、设备身份复核、连接与关闭、编码与后缀、真实引脚接口。
- 预设及协议规则持久化，工作流快照、发送互斥、暂停、单步、停止与退出超时处理。
- 终端筛选、搜索、显示模式、复制与 TXT/CSV/JSON 导出。
- 运行包包含 Web 页面、Lucide 图标、CRC 库及 WebView2 桥接依赖。

旧设计与旧功能入口已经移出当前产品，历史备份由开发者在仓库外保留。

## 验证

测试结果和限制记录在 [功能验证](functional-verification.md)，测试截图保存在本地 `artifacts/pebrel-checks/`。真实串口设备、物理引脚电平和多显示器 DPI 切换需要外部环境验证。
