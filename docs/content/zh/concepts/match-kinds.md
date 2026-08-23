# 三种匹配种类

MonoWeaver 把目标分成三类。选错种类是匹配不到的第一大原因，所以先确定要找的东西属于哪一类。

| 目标 | 什么时候用 | 例子 |
| --- | --- | --- |
| `Cil.Value(...)` | 这段代码会得到一个值 | 伤害计算、字段读取、方法返回值 |
| `Cil.Effect(...)` | 这段代码只执行行为，不留下结果 | 播放音效、发送事件、调用 `void` 方法 |
| `Cil.Condition(...)` | 这段代码决定是否进入某条分支 | `if`、`while`、`&&`、`\|\|` |

## 值

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:value-pattern"
```

一个值匹配指向“某次取到这个值”的位置。它可以被读取、替换，或者在前后插入代码。

## 行为

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:effect-pattern"
```

行为匹配覆盖一整段不产生结果的代码。如果目标方法**有**返回值但调用处直接丢弃了结果，仍然用 `Cil.Effect(...)`；用 `CilExpr` 写法时对应 `Cil.Discard(expression)`。

## 条件

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:condition-pattern"
```

条件匹配找的是**决定分支走向**的那段判断，包括 `&&`、`||` 的短路结构。它不是在搜索一个普通的 `bool` 值。

!!! note "条件不等于 bool 值"
    `return a && b;` 里的 `a && b` 是一个值，`if (a && b)` 里的才是条件。同一段源码，编译出的 IL 形状不同。

## 各自支持的操作

|  | `Before` | `After` | `Transform` | `Observe` | `Replace` | `Remove` |
| --- | :---: | :---: | :---: | :---: | :---: | :---: |
| 值 | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| 行为 | ✓ | ✓ | — | — | ✓ | ✓ |
| 条件 | ✓ | — | ✓ | ✓ | ✓ | — |

两个空缺是有原因的：

- **行为没有 `Transform`/`Observe`**：没有值可以读取或替换。
- **条件没有 `After`**：一个条件可能从不同位置结束（短路时提前跳走），没有唯一的“后面一行”。

有些非常复杂的条件只能识别、不能安全改写，先检查一下：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-can-rewrite"
```

每种操作的具体语义见 [改写操作](../reference/rewrite-operations.md)。
