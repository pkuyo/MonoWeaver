# 检查失败

日志中会出现一个英文诊断类型名。先按下面的方向排查，不必先理解全部底层细节。

诊断的 `ToString()` 通常带上 `IL_xxxx` 位置。把这个位置与反编译器或自己的指令日志对照，通常能快速找到是哪次改写造成的。

## 常见诊断对照

| 诊断 | 常见原因 | 优先处理 |
| --- | --- | --- |
| `StackUnderflow` | 前面的代码少提供了一个值 | 检查 `Transform` 是否返回结果，或是否错误调用了 `Discard()` |
| `InvalidExitStackHeight` | 方法返回时还多了值，或缺少返回值 | 检查有返回值的 `Before`/`After` 是否应 `Discard()` 或 `Store(...)` |
| `IncompatibleMergeDepth` | 两条分支到达同一处时，值数量不同 | 检查条件两边是否都执行了相同去向的回调结果处理 |
| `IncompatibleMergeTypes` | 两条分支带来的值类型不同 | 确认回调返回类型与游戏原值兼容 |
| `InvalidBrTarget` | 跳转目标已被删除或不属于该方法 | 不要直接移除被跳转的位置；优先用 `Replace` 或 `Remove` |
| `UninitializedLocal` | 某条路径先读取、后赋值 | 检查 `StoreLocal` 的位置和所有分支 |
| `TypeMismatch` | 参数、返回值、字段或转换类型不兼容 | 对照回调签名和捕获值类型 |
| `MethodAccess` / `FieldAccess` / `TypeAccess` | 修改后的代码不能直接访问目标成员 | 改用可访问的 Mod 桥接方法，或调整回调所在类型 |
| `ResolveFailed` | 找不到游戏或 Mod 依赖 | 给 Cecil 的解析器补齐游戏目录中的依赖，并检查版本冲突 |
| `ExceptionHandlerInvalid` | 手动修改破坏了 `try/catch/finally` 范围 | 避免手动移动边界；缩小替换范围后重新匹配 |

## 读取完整诊断

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:catch-diagnostics"
```

方法已经由 `Apply` 恢复，不需要自己撤销改写。

## 回调结果最容易出错的地方

超过一半的检查失败来自这一节。

### Transform

`Transform` 接收原值并返回新值。返回类型必须能放回游戏原位置：

```csharp
// 正确：int -> int
static int ClampDamage(int original) => Math.Max(original, 0);
```

不要对正常的 `Transform` 结果调用 `Discard()`，否则游戏原代码拿不到需要的值——典型表现是 `StackUnderflow`。

### Observe

`Observe` 会保留原游戏值。回调如果还返回另一个值，默认丢弃；需要时可以保存：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:store-local"
```

### Before / After

这两个操作**不会自动消费游戏原值**。有返回值的回调默认把结果留在当前位置，典型表现是 `InvalidExitStackHeight` 或 `IncompatibleMergeDepth`。多数日志或通知回调不需要它，应明确处理：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:discard-result"
```

更简单的做法是让这类回调直接返回 `void`。

## 访问权限失败

`MethodAccess` / `FieldAccess` / `TypeAccess` 表示改写后的代码在当前位置没有权限访问目标成员。改写前可以先判断：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/TypeChecks.cs:access-checks"
```

通常的解法是从一个可访问的 Mod 静态方法间接调用，而不是直接生成访问指令。

## 解析失败

`ResolveFailed` 表示 Cecil 找不到某个类型或成员。离线修改器应给 `ModuleDefinition.ReadModule` 配置能找到游戏目录全部依赖的解析器；运行时则检查是否有两份不兼容的 Cecil 或游戏程序集同时加载。

## 检查失败后的安全处理

- 不要在捕获异常后继续写出当前方法，先记录诊断并跳过该 Hook；
- 如果这是可选功能，让 Mod 输出清楚的游戏版本、目标方法和 Pattern 名称；
- 如果这是启动必需功能，**停止加载比带着损坏的方法继续运行更安全**；
- 重新匹配后再尝试其他方案，不要继续使用失败前保存的旧匹配；
- 对每个支持的游戏版本保存至少一个自动测试样本。
