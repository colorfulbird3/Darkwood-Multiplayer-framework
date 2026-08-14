# Darkwood 适配层规则

`DarkwoodAdapter` 是唯一允许直接接触 Unity 和 `Assembly-CSharp` 类型的边界。Core、Protocol、Network、Actions、Inventory 和 Snapshots 不得引用 Darkwood 游戏类型。

- 每个 Harmony patch 必须记录精确目标签名、Prefix/Postfix 选择和是否阻断原版写入。
- 反编译内容只作行为证据，不复制第三方反编译源码到公开仓库。
- Client 应用远端状态时设置明确的 applying-remote guard，并冻结会与 Host 镜像竞争的 AI/本地回调。
- 场景变化先暂停 Action/Delta，再 rebuild registry、应用 snapshot、通过 READY 门禁后恢复。
- 未确认的方法签名或字段语义标记为 `BLOCKED_API_SPEC`，不能凭模型猜测。
