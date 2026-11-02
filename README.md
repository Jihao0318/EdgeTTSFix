<div align="center">
  <img src="Icon.png" width="120" alt="EdgeTTSFix">
  <h1>EdgeTTSFix</h1>
  <p>为 ClassIsland 提供 Microsoft Edge 在线语音合成</p>
  <p>
    <img alt="ClassIsland 2.1.x" src="https://img.shields.io/badge/ClassIsland-2.1.x-blue?style=flat-square">
    <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square">
    <img alt="Windows x64" src="https://img.shields.io/badge/Platform-Windows%20x64-lightgrey?style=flat-square">
    <img alt="MIT" src="https://img.shields.io/badge/License-MIT-green?style=flat-square">
  </p>
  <p>
    <a href="#安装">安装</a> ·
    <a href="#配置">配置</a> ·
    <a href="#构建">构建</a>
  </p>
</div>

---

**EdgeTTSFix** 是一个轻量的 ClassIsland 语音服务插件，把朗读请求接入 Edge 在线朗读，支持 `Sec-MS-GEC` V2 令牌、自动重试与本地缓存。

## 特性

- Edge 155 新端点与 `Sec-MS-GEC` V2 令牌
- 连接或握手失败时自动重试，并尝试上一窗口令牌 / 旧令牌组合
- 长文本按长度自动放宽超时（最长 300 秒）
- 内置中、英、日、韩常用音色，支持自定义 ShortName
- 语速、音量、音调可调
- 本地缓存（文件名含文本摘要），设置页一键清空
- 使用 ClassIsland 官方音频播放接口

## 安装

1. 获取 `EdgeTTSFix.cipx`。
2. 打开 ClassIsland → **设置 → 插件 → 安装插件**，选择该文件。
3. 重启 ClassIsland。
4. 打开 **设置 → 语音**，选择 **Edge TTS Fix**，选择音色并测试。

首次朗读需要联网合成，之后会直接使用本地缓存。

## 配置

| 项目 | 说明 |
| --- | --- |
| 音色 | 常用音色下拉选择，默认 `zh-CN-XiaoxiaoNeural` |
| 自定义音色 | 直接填写音色 ShortName |
| 语速 | 百分比，例如 `-10%`、`+20%` |
| 音量 | 百分比，例如 `-20%`、`+20%` |
| 音调 | Hz，例如 `-20Hz`、`+20Hz` |
| 缓存 | 缓存目录展示与一键清空 |

缓存位置：`<ClassIsland 数据目录>/Config/Plugins/EdgeTTSFix/EdgeTtsCache/<音色>/<文本前 24 字>_<短哈希>.mp3`

同一文本修改语速、音量或音调时会生成独立缓存，因为音频内容不同。

## 构建

```bash
dotnet build EdgeTTSFix/EdgeTTSFix/EdgeTTSFix.csproj -c Release -p:CreateCipx=true -p:GenerateHashSummary=false
```

产物：`EdgeTTSFix/EdgeTTSFix/cipx/EdgeTTSFix.cipx`

## 反馈

使用中遇到问题，欢迎提交 Issue。

## 许可证

[MIT](LICENSE.txt) · 派生自 [ClassIsland-MiMoTTS](https://github.com/lladlam/ClassIsland-MiMoTTS)

---

<sub>本项目与 Microsoft 无关联。Edge TTS 为 Microsoft 服务，请遵守其服务条款。</sub>
