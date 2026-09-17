# 主控语音通信预设

导入文件：[main-controller-voice-presets.json](main-controller-voice-presets.json)。`source.json` 也已更新为相同的预设数组，两个文件均可导入当前版本软件。

文件包含 60 条 HEX 预设，名称和播报释义中的称呼统一为“小E”。每条名称末尾附有原始播报编号，便于区分同名指令。

全部预设已补齐校验字节，报文结构为 `A5 FA 81 00 data1 check FB`，每条共 7 字节。预设使用 HEX 发送，帧尾已包含 `FB`。

依据用户提供的有效报文 `A5 FA 81 00 05 25 FB`，采用校验位之前的 5 个字节累加取低 8 位：`check = (0xA5 + 0xFA + 0x81 + 0x00 + data1) & 0xFF`，等价于 `(0x20 + data1) & 0xFF`。该规则与主机关闭样例吻合，其余编号仍需设备验证。

`protocol-source.json` 保存原协议资料及三个待补充项目：音量增大、音量减小和“滴”。设备实际播报内容由语音模块固件决定。

通过左侧预设栏的导入按钮选择 `main-controller-voice-presets.json`。导入会替换当前预设库，已有预设可先导出保存。

运行 `node generate-presets.mjs` 可从协议资料重新生成完整预设数组，并核对主机关闭报文。重新生成会覆盖这两个导入文件；编辑后的预设应通过软件另行导出保存。
