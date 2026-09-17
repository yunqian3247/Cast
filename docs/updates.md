# 在线更新与预发布打包

serial 使用 Velopack 1.2.0。SDK 与仓库本地 `vpk` 工具保持同一版本，应用标识为 `serial`，当前通道为 `win-x64-preview`。

`1.0.3-preview.20260917` 起统一使用 `serial` 标识和 `serial.exe`。此前试验包的应用标识不同，首次切换使用新的安装包或便携包；后续版本继续沿用 `serial`。旧配置首次启动时自动迁移到 `%LOCALAPPDATA%\serial\Data\serial.json`，原文件保留。

## 应用内更新

顶部「关于」弹窗底部提供「检查更新」「下载更新」「安装并重启」。检查和下载由用户触发，下载过程中可以关闭弹窗并继续使用应用。下次打开弹窗时保留进度或下载结果。下载完成后，也可以退出应用，在下次启动后安装。

安装前保存当前输入和配置；保存失败会保留当前窗口并显示错误。发送或工作流运行期间，安装按钮保持禁用。确认安装后，应用关闭串口、停止后台任务，再由 Velopack 安装并重启。内存中的通信记录随退出清空，重要记录应提前导出。

## 配置托管地址

源文件为 `src/serial.Desktop/update-settings.json`，构建时复制到程序目录。当前 `feedUrl` 留空，界面显示「更新地址尚未配置」，此状态下不会发出更新请求。

```json
{
  "feedUrl": "",
  "channel": "win-x64-preview"
}
```

获得托管地址后，将 `feedUrl` 设置为包含更新索引和包文件的 HTTPS 目录地址。地址支持静态网站、对象存储或 CDN。此配置使用 Velopack `SimpleWebSource`，GitHub 仓库页面或 Releases 页面需要对应的源适配。配置内容在启动时读取，修改后重新启动应用。打包脚本默认读取源文件，也支持参数覆盖。每次发布都应包含同一更新地址，避免后续版本将地址覆盖为空。

`channel` 必须与打包通道一致。本机验证允许 `http://localhost` 或回环 IP；远程地址要求 HTTPS。地址中不接受用户名、密码、查询参数或片段。

## 打包

在项目根目录运行，环境要求为 Windows 和 .NET 8 SDK：

```powershell
pwsh -NoProfile -File scripts/Publish-Preview.ps1
```

指定版本及已确定的托管地址：

```powershell
pwsh -NoProfile -File scripts/Publish-Preview.ps1 `
  -Version 1.0.3-preview.20260918 `
  -FeedUrl https://updates.example.com/serial
```

示例地址仅用于说明参数。脚本使用仓库锁定的 `vpk` 版本，生成包含 .NET 和更纱黑体的 win-x64 包，并声明 WebView2 安装依赖。输出位于 `artifacts/releases/<版本>/`：

- `serial-win-x64-preview-Setup.exe`：安装包。
- `serial-win-x64-preview-Portable.zip`：便携包，保留解压后的完整目录结构，运行根目录的 `serial.exe`。
- `serial-<版本>-win-x64-preview-full.nupkg`：在线更新包。
- `releases.win-x64-preview.json`：更新索引。
- `assets.win-x64-preview.json`、`RELEASES-win-x64-preview`：Velopack 生成的发布元数据。

预发布版本号必须逐次递增。同一输出目录已存在时，脚本终止，避免覆盖已有发布文件。当前脚本生成完整更新包。普通 `dotnet publish` 输出可用于开发调试，在线更新使用 Velopack 安装包或便携包。

## 托管发布

1. 先上传 `.nupkg`，确认可通过 HTTPS 下载。
2. 再上传 `releases.win-x64-preview.json`，确保索引中引用的文件位于同一目录。索引设置短缓存或重新验证策略，包文件可长期缓存。
3. 通过较旧的 Velopack 安装版或便携版检查、下载、安装并重启，核对版本和用户配置。

Velopack 校验下载包的大小和哈希，失败时保留重试入口。更新索引与包需要受信任的托管服务。当前生成的文件未做代码签名；生产发布可在 `vpk pack` 中接入代码签名参数。

## 验证

```powershell
dotnet test serial.sln -c Release
npm --prefix tests/web test
```

更新专项测试覆盖空配置、非法配置、普通构建、版本检查、下载进度、失败重试、重复请求、待重启恢复，以及真实 Velopack 客户端从本地 HTTP 服务读取索引、下载和校验包。前端测试覆盖状态呈现、保存失败、运行中禁止安装、主题与窗口尺寸。正式托管地址的访问和实际跨版本安装重启需要配置地址后联调。

参考：[Velopack .NET 集成](https://docs.velopack.io/getting-started/csharp)、[更新源](https://docs.velopack.io/integrating/update-sources)、[打包](https://docs.velopack.io/packaging/overview)。
