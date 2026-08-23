# 改写计划

所有改写操作都不会立刻修改方法。它们先返回一个 `RewritePlan`：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:plan"
```

此时目标方法还没有任何变化。这中间的空档用来配置回调返回值的去向，然后一次性提交。

## 提交

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:apply-full"
```

`Apply(VerifyOptions.Full)` 的完整流程：

1. 保存修改前的方法状态；
2. 应用改写；
3. 运行完整检查；
4. 成功时保留修改；
5. **失败时恢复原方法并抛出错误**。

也可以只调用 `Apply()` 跳过检查，但不建议用于准备发布的 Mod。检查细节见 [修改后检查](../reference/verification.md)。

## 回调返回值的去向 { #callback-result }

`Transform` 和值 `Replace` 的返回值本来就是游戏需要的结果，不需要额外设置。

`Before`、`After` 和 `Observe` 也允许用有返回值的回调。此时要决定这个结果去哪：

| 写法 | 用途 |
| --- | --- |
| `Discard()` | 不需要回调结果 |
| `Store(capture)` | 写入捕获到的局部变量或显式参数 |
| `StoreLocal(...)` | 写入指定局部变量 |
| `StoreArgument(...)` | 写入指定显式参数，不能写 `this` |
| `LeaveOnStack()` | 把结果留给后面的代码，属于进阶用法 |

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:store-result"
```

默认行为有区别，容易踩：

- `Observe` 的非 `void` 结果**默认丢弃**；
- `Before` 和 `After` 的非 `void` 结果**默认留在当前位置**。

第二条是 `InvalidExitStackHeight` 这类报错的常见来源。普通 Mod Hook 应该显式 `Discard()` 或 `Store(...)`：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:discard-result"
```

更简单的做法是让这类回调直接返回 `void`。

!!! danger "`LeaveOnStack()` 是进阶用法"
    只在明确知道后续代码需要这个结果时使用。拿不准就用完整检查，它会发现大部分此类错误。

## 计划的生命周期

- 一个计划只能成功提交一次；
- 方法被修改后，**旧的匹配位置可能已经失效**；
- 连续修改同一个方法时，优先“改一次 → 重新匹配 → 再改”；
- 检查通过不代表玩法正确，最后仍要在真实游戏流程中测试。

```text
Match → (Mark/Captures) → 选择操作 → RewritePlan → 配置结果去向 → Apply
                  ↑                                                    │
                  └──────────── 要继续改同一个方法就回到这里 ←──────────┘
```
