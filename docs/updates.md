# 在线更新与预发布打包

cast 使用 Velopack 1.2.0。SDK 与仓库本地 `vpk` 工具保持同一版本，应用标识为 `cast`，当前通道为 `win-x64-preview`。

`1.0.4-preview.20260917` 起统一使用 `cast` 标识和 `cast.exe`。此前 `serial` 试验包的应用标识不同，首次切换使用新的安装包或便携包；后续版本继续沿用 `cast`。首次启动优先读取 `%LOCALAPPDATA%\serial\Data\serial.json`，缺失时读取更早版本配置，再迁移到 `%LOCALAPPDATA%\cast\Data\cast.json`，原文件保留。已有 `cast` 配置时继续使用当前配置。

## 应用内更新

顶部「关于」弹窗底部提供「检查更新」「下载更新」「安装并重启」。检查和下载由用户触发，下载过程中可以关闭弹窗并继续使用应用。下次打开弹窗时保留进度或下载结果。下载完成后，也可以退出应用，在下次启动后安装。

安装前保存当前输入和配置；保存失败会保留当前窗口并显示错误。发送或工作流运行期间，安装按钮保持禁用。确认安装后，应用关闭串口、停止后台任务，再由 Velopack 安装并重启。内存中的通信记录随退出清空，重要记录应提前导出。

## 配置托管地址

源文件为 `src/cast.Desktop/update-settings.json`，构建时复制到程序目录。默认使用公开仓库 `yunqian3247/Cast` 的 GitHub Releases，启用预发布更新：

```json
{
  "feedUrl": "https://github.com/yunqian3247/Cast",
  "channel": "win-x64-preview",
  "source": "github",
  "includePrereleases": true
}
```

`source` 为 `github` 时使用 Velopack `GithubSource`，`feedUrl` 填写仓库根地址。`includePrereleases` 控制是否包含 GitHub 预发布版本，当前预发布通道设为 `true`。客户端通过公开接口读取索引和附件，无需访问令牌。源码推送后，还需要在 Release 中上传更新索引和包文件，应用才能发现新版本。

静态网站、对象存储或 CDN 使用 `source: "static"`，`feedUrl` 填写包含更新索引和包文件的 HTTPS 目录地址。旧配置省略 `source` 时继续使用静态更新源。配置内容在启动时读取，修改后重新启动应用。打包脚本默认读取源文件，也支持参数覆盖。每次发布都应包含同一更新地址。`feedUrl` 为空时显示「更新地址尚未配置」，检查按钮禁用。

`channel` 必须与打包通道一致。本机验证允许 `http://localhost` 或回环 IP；远程地址要求 HTTPS。地址中不接受用户名、密码、查询参数或片段。

## 打包

在项目根目录运行，环境要求为 Windows 和 .NET 8 SDK：

```powershell
pwsh -NoProfile -File scripts/Publish-Preview.ps1
```

覆盖为静态托管地址：

```powershell
pwsh -NoProfile -File scripts/Publish-Preview.ps1 `
  -Version 1.0.7-preview.20260918 `
  -Source static `
  -FeedUrl https://updates.example.com/cast
```

示例地址仅用于说明参数。脚本使用仓库锁定的 `vpk` 版本，生成包含 .NET 和更纱黑体的 win-x64 包，并声明 WebView2 安装依赖。输出位于 `artifacts/releases/<版本>/`：

- `cast-win-x64-preview-Setup.exe`：安装包。
- `cast-win-x64-preview-Portable.zip`：便携包，保留解压后的完整目录结构，运行根目录的 `cast.exe`。
- `cast-<版本>-win-x64-preview-full.nupkg`：在线更新包。
- `releases.win-x64-preview.json`：更新索引。
- `assets.win-x64-preview.json`、`RELEASES-win-x64-preview`：Velopack 生成的发布元数据。

预发布版本号必须逐次递增。同一输出目录已存在时，脚本终止，避免覆盖已有发布文件。当前脚本生成完整更新包。普通 `dotnet publish` 输出可用于开发调试，在线更新使用 Velopack 安装包或便携包。

## 托管发布

### GitHub Releases

1. 为已推送的提交创建 Release，标签与打包版本对应，例如 `v1.0.6-preview.20260917`，勾选预发布。
2. 在草稿中上传 `artifacts/releases/<版本>/` 内生成的安装包、便携包、完整 `.nupkg`、`releases.win-x64-preview.json` 及其余发布元数据。保留文件名。
3. 确认附件齐全后发布 Release。应用按 `win-x64-preview` 通道读取索引，通过公开附件地址下载更新。
4. 通过较旧的、已配置 GitHub 更新源的 Velopack 安装版或便携版检查、下载、安装并重启，核对版本和用户配置。

`1.0.5` 及更早版本的更新地址为空，首次启用时需使用 `1.0.6` 的安装包或便携包。源码构建通过普通 `dotnet publish` 输出时，界面会提示使用 Velopack 安装包或便携包。GitHub 公共 API 有访问频率限制，检查失败时可稍后重试。

### 静态托管

1. 先上传 `.nupkg`，确认可通过 HTTPS 下载。
2. 再上传 `releases.win-x64-preview.json`，确保索引中引用的文件位于同一目录。索引设置短缓存或重新验证策略，包文件可长期缓存。
3. 通过较旧的 Velopack 安装版或便携版检查、下载、安装并重启，核对版本和用户配置。

Velopack 校验下载包的大小和哈希，失败时保留重试入口。更新索引与包需要受信任的托管服务。当前生成的文件未做代码签名；生产发布可在 `vpk pack` 中接入代码签名参数。

## 验证

```powershell
dotnet test cast.sln -c Release
npm --prefix tests/web test
```

更新专项测试覆盖空配置、非法配置、普通构建、版本检查、下载进度、失败重试、重复请求、待重启恢复，以及真实 Velopack 客户端从本地 HTTP 服务读取索引、下载和校验包。GitHub 专项测试通过模拟接口验证预发布筛选、通道索引、公开附件下载、损坏包拒绝和待重启恢复。前端测试覆盖状态呈现、保存失败、运行中禁止安装、主题与窗口尺寸。实际跨版本安装重启需在 Release 发布后联调。

参考：[Velopack .NET 集成](https://docs.velopack.io/getting-started/csharp)、[更新源](https://docs.velopack.io/integrating/update-sources)、[打包](https://docs.velopack.io/packaging/overview)。
