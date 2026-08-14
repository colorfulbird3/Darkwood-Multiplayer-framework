# ADR-0001：Hermes/DS V4 与游戏运行时分层

- 状态：Accepted
- 日期：2026-08-13
- 基线：0.8.7-alpha.9

## 决策

Hermes 负责开发任务编排和上下文，DS V4 Pro 负责模型推理；Darkwood 运行时继续使用 BepInEx、Harmony、DMF Protocol 和 Telepathy。Protocol DTO/codec 不引用任何模型或供应商命名空间。

## 原因

目前没有用户提供的 Hermes/DS V4 游戏网络 SDK、Unity/Mono 适配方式、线程/可靠性语义或许可证。假设运行时 API 会造成不可编译、不可验证且难以回滚的改动。

## 未来迁移条件

收到准确 SDK 文档后，先定义并测试 `IAuthoritativeTransport` contract，再实现独立供应商适配器。Telepathy 至少保留一个 alpha 版本作为 fallback；握手、Action、库存和快照 wire schema 不因传输供应商改变。
