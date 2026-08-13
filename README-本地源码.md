# Darkwood 联机框架本地源码

本目录保存 Darkwood Multiplayer Framework 的本地源码、恢复参考源码和编译依赖。

## 目录

- `src/`：0.8 新架构源码与 Darkwood 适配层。
- `legacy-reference/`：从现有 0.7.0 DLL 恢复并修复到可编译状态的参考源码。
- `legacy-reference-first-pass/`：第一轮反编译结果，仅用于行为比对。
- `Payload/`：本地编译引用所需的 BepInEx、Mirror、Telepathy 等二进制依赖；它不是本目录的安装入口。
- `.tools/`：本地反编译和检查工具。

## 编译

在本目录运行：

```powershell
dotnet build .\src\DarkwoodMultiplayerFramework.sln -c Release
```

构建结果输出到：

```text
F:\Private Blog\DMF Local Build\bin
```

这里生成的 0.8 适配层目前属于隔离测试版本，不会自动复制到 Darkwood 的 `BepInEx\plugins`。

## 本地保存约束

本目录只在本机保存。未经明确确认，不执行 Git 提交、GitHub 推送或正式游戏 DLL 覆盖。

可直接安装的 0.7.0 发布包位于同级目录 `Darkwood联机框架-安装包-v0.7.0`。
