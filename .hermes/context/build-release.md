# 构建、发布与许可证边界

## 自动验证

```powershell
dotnet build '.\\src\\DarkwoodMultiplayerFramework.sln' -c Release --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false
dotnet run --project '.\\src\\DarkwoodMultiplayerFramework.SelfTests\\DarkwoodMultiplayerFramework.SelfTests.csproj' -c Release --no-build -p:MSBuildEnableWorkloadResolver=false
```

必须记录 warning/error 数、SelfTests 结果和退出码。Telepathy 停止时的 socket cancel/`ObjectDisposedException` 只有确认是主动关闭且无后续失败时才能标记为预期噪声。

## 发布门禁

1. canonical 源码、baseline tag 和变更集明确；不从旧 GitHub 副本直接覆盖 main。
2. 覆盖前确认游戏进程已关闭并创建可回滚备份。
3. 用 `Get-Content -Encoding UTF8` 读取 manifest，避免中文路径假缺失。
4. 计算核心 DLL SHA256；双端测试包必须一致。
5. ZIP 解压到临时目录后检查清单、安装脚本语法和版本文本。
6. 审核第三方许可证；不公开 `Assembly-CSharp.dll`、反编译源码、密钥或授权不明二进制。

不要修改用户全局 Hermes 配置或写入 API key。可复制示例配置到用户 profile 后填写真实 provider/model slug。
