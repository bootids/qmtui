# qmtui Herdr 插件

用 **Ctrl+B，然后按 M** 在 qmtui 和原工作标签页之间切换。若改过 Herdr 前缀键，请使用自己的前缀键。

首次呼出会创建一个专用播放器标签页；再次按快捷键返回工作标签页，音乐继续播放。以后呼出复用同一播放器、播放队列和界面。每个 Herdr 会话保留一个播放器，在播放器里按 `Q` 确认退出才会停止播放。

要求：Linux / macOS、Herdr **0.9.5+**、Python **3.9+**，以及可独立运行的 `qmtui`。

## Kitty 跨机器快捷键

安装本机 Herdr 插件后，可给 Kitty 增加两个固定连接 **Local** 的入口：

| 快捷键 | 效果 |
| --- | --- |
| `F8` | 在 Kitty 标签页中显示本机 qmtui |
| `F9` | 在覆盖当前 Kitty 窗口的叠加窗口中显示本机 qmtui |
| 播放器中的 `F8` / `F9` | 收起当前显示窗口，音乐继续后台播放 |
| 播放器中的 `Ctrl+B`，然后 `q` | 断开当前显示窗口，音乐继续后台播放 |

这两个入口不受 Herdr 当前选中机器影响，操作同一个 Local 播放器。新入口会接管已有显示连接，避免多个窗口同时输入；收起显示连接不会退出播放器。在 qmtui 中按 `Q` 并确认退出会停止播放器。

在仓库根目录安装本机启动器和 Kitty 配置：

```sh
mkdir -p "$HOME/.local/bin" "$HOME/.config/kitty"
install -m 755 plugins/herdr/kitty_launch.py "$HOME/.local/bin/qmtui-local"
cp plugins/herdr/kitty.conf "$HOME/.config/kitty/qmtui.conf"
```

在 Kitty 的 `kitty.conf` 末尾添加一次 `include qmtui.conf`，然后按 `Ctrl+Shift+F5` 重载配置。Kitty 的启动环境需能从 `PATH` 找到 `qmtui-local`、`herdr` 和 `python3`；本机 Local Herdr 服务端需保持运行。

启动器每次从本机插件注册信息寻找当前代码，因此 `herdr plugin update qmtui` 后会自动使用更新后的播放器连接逻辑。更改 `kitty_launch.py` 或 `kitty.conf` 时需重新执行上面的复制命令。

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
