# Fork 维护

上游：`Viemean/qmtui` 的 `tui` 分支。Fork：`bootids/qmtui`，默认分支为 `main`。

## 分支职责

| 分支 | 职责 |
| --- | --- |
| `tui` | 上游镜像，只快进同步，不提交本地功能 |
| `main` | 包含上游更新、macOS 支持及本 fork 扩展的集成版本 |
| `feat/*`、`fix/*` | 从最新 `main` 创建的功能和修复分支，通过 PR 合入 |
| `sync/*` | 同步出现冲突时，从 `main` 创建的临时整合分支 |

普通功能 PR 可以 squash；**上游同步 PR 必须用 merge commit**，保留上游祖先关系。

## 本地配置

```sh
git remote add upstream https://github.com/Viemean/qmtui.git
git remote set-branches origin '*'
git fetch origin --prune
git fetch upstream --prune --no-tags
git branch --set-upstream-to=origin/main main
```

已经存在 `upstream` 时无需重复添加。浅克隆首次补齐历史使用 `git fetch origin --unshallow`。

开始功能开发：

```sh
git switch main
git pull --ff-only
git switch -c feat/herdr-plugin
```

## 定期同步

`Sync upstream` 工作流每天北京时间 **07:20** 检查上游，也支持在 Actions 页面手动运行。

1. 将 `upstream/tui` 快进推送到 fork 的 `tui`，遇到分叉则失败，不覆盖独有提交。
2. 上游有未整合提交时创建或复用 `tui → main` PR；没有更新时不创建 PR。
3. 检查 Linux 测试和 macOS 构建结果，处理冲突后，使用 **Create a merge commit** 合入。

初期保留人工合并。GitHub 仓库需开启 Actions，并在 **Settings → Actions → General → Workflow permissions** 允许 GitHub Actions 创建和批准 PR。机器人创建的 PR 如出现 **Approve workflows to run**，需点击批准才会启动验证。

定时工作流从默认分支运行，因此仓库默认分支必须为 `main`。GitHub 的定时任务可能延迟；公共仓库长时间无活动时可能停用定时任务，可在 Actions 页面重新启用。

有冲突时可以在本地整合：

```sh
git fetch origin --prune
git switch -c sync/upstream origin/main
git merge --no-ff origin/tui
# 处理冲突并提交，保留两边的有效修复。
dotnet test tests/QmTui.Tests/QmTui.Tests.csproj -c Release
git push -u origin sync/upstream
```

将临时分支通过 PR 合入 `main`，同样使用 merge commit；确认 `main` 已包含 `tui` 后关闭原同步 PR。
