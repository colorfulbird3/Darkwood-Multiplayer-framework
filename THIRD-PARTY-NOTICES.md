# Third-party notices

本仓库在 `libs/` 目录分发了以下第三方二进制文件（仅用于构建引用）：

| 组件 | 文件 | 许可证 | 来源 |
|---|---|---|---|
| BepInEx 5 | `libs/BepInEx.dll` | LGPL-2.1 | https://github.com/BepInEx/BepInEx |
| Harmony 2 | `libs/0Harmony.dll` | MIT | https://github.com/pardeike/Harmony |

构建时还需要用户自行提供（不随仓库分发）：

- Darkwood 游戏程序集（`Darkwood_Data/Managed/`：Assembly-CSharp、UnityEngine 系列、Newtonsoft.Json）——版权归游戏厂商所有。
- Telepathy（运行时装于 BepInEx plugins 目录，见安装包）。

以上组件保留各自版权和许可证。请在下载、安装和再分发前阅读其官方许可条款。
