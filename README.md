# qmtui

`qmtui` 是一款运行于 Linux / macOS 终端的 QQ 音乐 TUI 客户端。

本 fork 的分支职责和定期上游同步方式见 [Fork 维护说明](docs/maintenance.md)。

在 Herdr 中使用快捷键呼出播放器并保持后台播放，或使用 Kitty 的 `F8` / `F9` 跨机器呼出本机界面和悬浮窗口，见 [Herdr 插件](plugins/herdr/README.md)。

## macOS / M5 Mac 安装

支持 **macOS 15 及以上、Apple Silicon（M1 / M2 / M3 / M4 / M5）**。
这是原生 ARM64 终端应用，安装后在终端中使用。

1. 在本仓库的 [Releases](https://github.com/bootids/qmtui/releases) 下载 `qmtui-<版本>-macos-arm64.pkg`。
2. 双击安装包，按照 macOS 安装器提示完成安装。完整 `.pkg` 已包含 GStreamer 音频运行库，**无需安装 .NET、Homebrew 或 Rosetta**，安装过程可离线完成。
3. 打开终端输入 `qmtui`，或在“应用程序”中双击 `qmtui.command`。在线歌曲播放、登录和搜索需要联网及用户自己的账号权限。

升级安装后，请退出之前正在运行的 qmtui，再重新启动，让新版本生效。

本构建尚未使用 Apple Developer ID 签名和公证。若 macOS 阻止打开安装包，核对下载来源及 `SHA256SUMS` 后，在“系统设置 → 隐私与安全性”中点击本次安装对应的“仍要打开”。

```sh
qmtui --version
qmtui --check-audio
qmtui --check-media-session
qmtui
```

`--check-audio` 会用临时 WAV 验证本机 GStreamer 加载、解码、播放进度及跳转；使用静音输出，不验证物理扬声器。系统 Terminal 支持文字界面，封面图片需使用支持 Kitty 图像协议的终端。

macOS 系统媒体集成通过公开的 MediaPlayer API 发布歌曲、歌手、专辑、进度及播放状态，并接收播放、暂停、切歌和跳转命令。0.4.6 为终端应用补充了主线程 Cocoa 事件循环，让这些系统媒体回调能够持续处理；命令行启动方式保持不变。

`--check-media-session` 会短暂发布一首虚构的 qmtui 测试歌曲，检查原生媒体信息读回、控制回调与主线程事件循环，然后清除这次测试的媒体信息。它不播放音频、不访问网络或账号凭据，也不操作控制中心界面；检查通过不代表已自动验证控制中心的实际显示或外部设备的媒体按键。

macOS 使用 CoreAudio 播放，并支持系统剪贴板及默认浏览器登录。**实时录音识曲和后台预录目前仅支持 Linux**；macOS 可使用 `qmtui --recognize <音频文件>` 进行文件识曲。

完整安装包会把 GStreamer 安装到官方共享目录 `/Library/Frameworks/GStreamer.framework`。次要下载项 `.tar.gz` 只包含 qmtui，需要已有 GStreamer；首次安装请选 `.pkg`。安装位置、依赖来源及卸载说明见 [macOS 安装说明](scripts/macos/INSTALL.txt)。

### 自行构建

在原生 Apple Silicon Mac 上安装 .NET 10 SDK 和 Xcode Command Line Tools 后运行：

```sh
./build-macos.sh 0.4.6 dist
```

脚本校验官方 GStreamer 运行库的固定 SHA-256，生成原生 `.pkg`、独立压缩包及校验文件。GitHub 的 [macOS 构建流程](.github/workflows/macos.yml) 会在 ARM64 runner 上安装完整包，再验证发布后二进制音频解码、原生媒体会话、终端启动退出和测试套件。也可在 Actions 中手动运行并选择发布预发行版。

## 界面预览

### 主界面

![主界面](assets/preview-main.png)

### 播放界面

![播放界面](assets/preview-nowplaying.png)

## 终端支持

- **基础文本模式**：支持所有标准终端环境。
- **封面图像显示**：基于 Kitty 图像协议，支持具备图像图层渲染的终端：
  - [Kitty](https://sw.kovidgoyal.net/kitty/)
  - [Ghostty](https://ghostty.org/)
  - [Rio](https://rioterm.com)
  - 在不支持图像协议的终端中，界面将自动回退至基础文本模式。
  - **图像协议环境变量**：
    - 强制开启：若您确定终端支持 Kitty 图像协议且实现规范可在使用以下环境变量强制开启 `QQMUSIC_FORCE_IMAGE=1`
    - 显式禁用：通过设置 `QQMUSIC_DISABLE_IMAGE=1`（或 `QQMUSIC_NO_IMAGE=1`）停用图像渲染

## 免责声明

1. **独立第三方开源工具**：`qmtui` 为开源社区独立开发与维护的终端播放客户端，与任何在线音乐服务平台或其关联公司**不存在任何形式的商业合作、授权、从属或背书关系**。
2. **版权与内容归属**：本项目不存储、托管或分发任何音频、歌词、封面及元数据文件。运行期间请求的所有音频流、歌词文本与多媒体资源之知识产权均归其合法版权方、唱片公司或平台所有。
3. **合法鉴权与非越权承诺**：本项目基于用户合法获取的凭据（如 Session/Cookie、本地媒体库或 WebDAV）进行标准化服务交互，**不提供任何绕过权限校验、破解付费限制、解密受保护数字版权管理（DRM）资产或批量抓取**的技术手段，播放权限与可用性完全由上游服务端依据用户账号状态裁定。
4. **非商业性与免责声明**：本项目仅供个人技术研究与学习交流使用，严禁用于任何商业盈利、二次包装销售或非法用途。用户使用本项目产生的网络请求与行为后果均由使用者自行负责，开发者不对因使用本软件引发的任何争议承担法律责任。
5. **账号风控与使用风险自负**：用户需知悉使用第三方客户端进行网络交互可能存在触发服务提供商安全策略或风控机制的潜在风险，由此产生的一切账号状态变动（如二次验证、接口频控、功能受限等）均由使用者自行承担。

## 许可证 (License)

本项目代码基于 [MIT License](LICENSE) 开源发布。
