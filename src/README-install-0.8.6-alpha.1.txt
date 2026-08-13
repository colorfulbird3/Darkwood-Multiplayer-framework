Darkwood Multiplayer Framework 0.8.6-alpha.1

这是独立于 0.7 运行时的双端测试版，并加入第一条 Host Authoritative Pickup 链路。

安装：
1. 完全退出 Darkwood。
2. 双击“安装联机框架.bat”。
3. 主机先进入需要共享的存档，再按 F1。
4. 客户端必须停留在主菜单，配置主机地址后只按一次 F2。
5. 等日志出现“Standalone join READY”。

快捷键：F1 主机；F2 客户端连接；F3 停止。

本版新增：客户端 Pickup 只发请求；Host 验证身份、READY、EntityId、revision、距离与背包容量；成功后 Apply once 并同步物品 Despawn；重复 RequestId 不会复制物品。

警告：这是需要真实双端验证的 alpha。目前 Action Core 只覆盖 Pickup，尚未覆盖 Drop、Door、Attack、Craft、切场景与重连。
