# qmtui Herdr 插件

用 **Ctrl+B，然后按 M** 在 qmtui 和原工作标签页之间切换。若改过 Herdr 前缀键，请使用自己的前缀键。

首次呼出会创建一个专用播放器标签页；再次按快捷键返回工作标签页，音乐继续播放。以后呼出复用同一播放器、播放队列和界面。每个 Herdr 会话保留一个播放器，在播放器里按 `Q` 确认退出才会停止播放。

要求：Linux / macOS、Herdr **0.9.5+**、Python **3.9+**，以及可独立运行的 `qmtui`。

## 本地安装

在仓库根目录运行：

```sh
herdr plugin link "$PWD/plugins/herdr"
```

将 [keybinding.toml](keybinding.toml) 中的配置追加到 Herdr 的 `config.toml`（通常是 `~/.config/herdr/config.toml`），然后重新加载：

```sh
herdr server reload-config
```

快捷键可以修改为其他未占用组合，例如 `key = "f8"`。不要重复添加同一绑定。

插件在当前选中的 Herdr 服务端执行。本机 Linux 播放需先在机器列表选择 **Local**；即使客户端运行在 Linux，选中 MacMini 等 SSH 远端时，使用的也是远端的插件和自定义命令配置。本机安装不会自动安装到远端。

也可以在 Herdr 中手动调用：

```sh
herdr plugin action invoke qmtui.toggle
```

插件目录无需编译；GitHub 安装方式为：

```sh
herdr plugin install bootids/qmtui/plugins/herdr --ref feat/herdr-plugin
```

合入 `main` 后可将 `--ref` 改为 `main`。本地链接安装与 GitHub 安装二选一。

## qmtui 路径

默认从 Herdr 服务端的 `PATH` 查找 `qmtui`。需要指定其他可执行文件时：

```sh
PLUGIN_CONFIG=$(herdr plugin config-dir qmtui)
printf '%s\n' '/absolute/path/to/qmtui' > "$PLUGIN_CONFIG/qmtui-path"
```

该文件只填写可执行文件路径，不包含命令行参数。macOS 安装包通常为 `/usr/local/bin/qmtui`。

插件通过 `HERDR_BIN_PATH` 调用当前 Herdr，只保存会话对应的播放器和返回终端 ID，不保存登录凭据。播放器退出后，下次快捷键会重新启动；原工作终端已关闭时，可以从标签栏切换至其他工作标签，再按快捷键更新返回位置。

Herdr 服务端退出会结束播放器；插件不提供跨服务端重启的音频进程保活。
