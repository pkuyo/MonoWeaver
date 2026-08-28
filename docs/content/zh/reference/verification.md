# 修改后检查

Mod 改写代码后，最麻烦的问题往往不是“效果不对”，而是游戏在加载方法时直接报错或崩溃。MonoWeaver 的检查器会在保存 DLL 或继续运行前，先确认方法是否仍能被 .NET/Mono 正常接受。

## 推荐用法

对 MonoWeaver 生成的改写，直接在提交时做完整检查：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-transform"
```

这一步会：

1. 保存修改前的方法状态；
2. 应用改写；
3. 运行完整检查；
4. 成功时保留修改；
5. 失败时恢复原方法并抛出错误。

准备发布的 Mod 应该用 `Mod` 或 `Full` 做检查，而不是只调用 `Apply()`。

## Light 还是 Full

| 模式 | 会检查什么 | 建议用途 |
| --- | --- | --- |
| `VerifyOptions.Light` | 指令基本格式、每条执行路径上的值数量 | 开发时频繁快速检查 |
| `VerifyOptions.Full` | Light 的内容，加上值类型、变量初始化、成员访问等 | 发布前、自动测试、未知游戏版本 |
| `VerifyOptions.Mod` | `Full` 去掉成员访问检查 | 声明了 `SkipVerification`、通过 publicized 程序集访问游戏非公开成员的 Mod |

- 运行时 hook（MonoMod `ILContext`）→ `Mod`。Mod 通常直接访问游戏的私有成员，`Full` 多出来的访问检查在这里只会误报。
- 离线补丁 → `Full`。
- Mod 方法一般都很小，检查很快；确实觉得慢再换 `Light`。

也可以组合单项：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:verify-options-combo"
```

| 单项 | 检查内容 |
| --- | --- |
| `Instructions` | 指令与操作数基本格式 |
| `StackBalance` | 每条路径上的值数量 |
| `StackTypes` | 值的类型（含 `StackBalance`） |
| `LocalInit` | 局部变量是否先赋值后读取 |
| `AccessTest` | 成员访问权限 |

## 检查手动修改的方法

如果方法还经过了自己的 Cecil 修改，可以单独检查：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:verify-usings"
```

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:verify-manual"
```

`ThrowIfHasErrors()` 会在出现 `Error` 或 `Fatal` 时抛出；`Warning` 会保留在 `Diagnostics` 中，由 Mod 自己决定是否阻止加载。

想读取 `Apply(VerifyOptions.Full)` 抛出的详细结果，可以捕获：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:catch-diagnostics"
```

此时方法已经由 `Apply` 恢复，不需要自己撤销改写。

## 它具体能拦住什么

用 Mod 开发中的常见现象来理解：

- 回调吃掉了原值，却没有返回替代值；
- `Before` 或 `After` 的回调多留下了一个无人使用的返回值；
- `if` 的两条路径到达同一位置时，带来的值数量或类型不同；
- 删除代码后，某个跳转仍指向已删除位置；
- 从 `try/catch/finally` 中用了不允许的方式跳出或跳入；
- 局部变量在某条路径上还没有赋值就被读取；
- 调用参数、返回值或字段类型不兼容；
- Mod 代码尝试直接访问当前上下文无权访问的私有成员；
- 方法需要的类型或成员无法从游戏依赖中解析。

## 它不会检查什么

即使 `Full` 通过，也仍可能存在：

- 匹配到了错误但格式合法的位置；
- 伤害、概率、时间单位等业务含义写错；
- 回调抛出异常；
- 多个 Mod 修改同一方法后的顺序冲突；
- 游戏更新改变了调用时机，但保留了相似表达式；
- 只在特定存档、地图、联机状态下出现的问题。

把完整检查、唯一匹配、日志和真实游戏测试一起使用，不要把任意一项当成全部保障。

出现具体报错时见 [检查失败](../troubleshooting/verification-failures.md)。
