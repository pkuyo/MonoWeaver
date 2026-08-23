# 安装与选包

MonoWeaver 发布两个包。它们的 API 完全相同，区别只在编译时链接的 Mono.Cecil 代数。

| 包 | Mono.Cecil | 目标框架 |
| --- | --- | --- |
| `MonoWeaver` | `0.11.2+` | `netstandard2.0` |
| `MonoWeaver.Cecil10` | `0.10.0` – `0.10.4` | `net46`、`netstandard2.0` |

## 选哪一个

先看游戏或 Mod Loader 已经加载了哪一代 Cecil，再选包。

- **老 Unity 游戏、MonoMod 19.x** → `MonoWeaver.Cecil10`。它的 `net46` 构建直接对应 Unity 的 .NET 4.x 运行时，不经过 `netstandard.dll` 门面，在老版本 Unity 上更可靠。运行时集成在 MonoMod `19.9.1.6` 和 Mono.Cecil `0.10.4` 上测试。
- **其他情况** → `MonoWeaver`。

```bash
dotnet add package MonoWeaver
```

```bash
dotnet add package MonoWeaver.Cecil10
```

!!! warning "同时出现两份 Cecil 是最常见的加载失败原因"
    如果 Mod Loader 已经自带 Mono.Cecil，请先确认版本兼容，不要让自己的 Mod 再带一份不兼容的进去。

## 命名空间

四个命名空间按职责分开，日常 Hook 一般四个都要：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/QuickStart.cs:usings"
```

| 命名空间 | 里面有什么 |
| --- | --- |
| `MonoWeaver.Patterns` | `Cil`、`P`、`CilSymbols`，以及匹配结果与捕获类型 |
| `MonoWeaver.Cecil` | `Match`、各种改写操作、`RewritePlan` |
| `MonoWeaver.CFG` | `VerifyOptions`、`ILMethodVerifier` |
| `MonoWeaver.Utils` | `Verify` 扩展、Cecil 类型判断辅助 |

## 不适用的情况

- IL2CPP 编译的游戏：没有托管 IL 可读，用不了。
- 原生代码、混淆到 Cecil 无法解析的程序集。
- 需要修改的逻辑被内联到调用方之后，原表达式可能已经不存在。

下一步：[第一个 Hook](first-hook.md)。
