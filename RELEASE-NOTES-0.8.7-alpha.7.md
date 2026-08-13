# Darkwood Multiplayer Framework 0.8.7-alpha.7

- 修复 Radmin/VPN 慢链路下 5 MB 存档传输被 Telepathy 写队列灌满而断线的问题。
- 存档分块调整为 128 KiB，世界快照分块调整为 64 KiB。
- 主机对存档/快照每帧最多发送一个大块，增加中文传输进度日志。
- Telepathy 设置 256 KiB 消息上限、60 秒发送超时并保持 NoDelay。
- 版本：0.8.7-alpha.7。
