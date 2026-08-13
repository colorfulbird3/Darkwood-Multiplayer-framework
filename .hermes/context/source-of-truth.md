# Canonical 源码与 GitHub 同步

当前本地 alpha.10 源码树：`F:\SteamLibrary\steamapps\common\Darkwood\Darkwood Multiplayer framework`（开发主树，本身不是 Git 根目录）。

GitHub 工作副本：`F:\SteamLibrary\steamapps\common\Darkwood\Darkwood Multiplayer framework-GitHub`。

Remote：`https://github.com/colorfulbird3/Darkwood-Multiplayer-framework`（main 分支）。

## 同步规则（每轮发布）

1. 开发只在 canonical 树进行；发布完成（构建 0/0、SelfTests 全过、DLL/安装包/ZIP 就绪）后同步源码。
2. 把 canonical 树中除 `Payload/`、`.tools/`、`*.zip`、`install-*.ps1` 外的内容同步到 GitHub 工作副本。
3. 以普通提交推送（**禁止 force push**），提交说明带版本号与任务 ID。
4. 创建 `v<version>` tag 并推送。
5. 公开仓库只放可分发源码、文档和构建说明；游戏程序集、反编译产物、第三方二进制、个人存档与 API key 一律不进仓库。
