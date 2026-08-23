# 改写操作

以下写法都是“**分别选择一种**”，不要把它们连续应用到同一个旧匹配结果。每次 `Apply` 之后，要继续改就重新 `Match`。

## 值

### Transform：读取旧值并返回新值

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-transform"
```

原值作为回调的**第一个参数**传入，回调返回值交回游戏原逻辑。返回类型必须能放回原位置。

### Observe：只观察，不改值

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-observe"
```

适合日志、统计和触发 Mod 事件。原值仍会交给游戏。回调若有返回值，默认丢弃。

### Replace：完全跳过原计算

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-replace"
```

不会先执行原表达式，回调也**不会自动收到原值**——需要的参数必须全部在 `args` 中提供。

### Before / After：前后追加行为

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-before-after"
```

`After` 只表示“计算完成后调用”，不会自动把原值传给回调。需要读取原值时用 `Observe`。

## 行为

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:effect-ops"
```

四种独立选择：前面插入、后面插入、整体替换、删除。

## 条件

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-match"
```

=== "Transform"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-transform"
    ```

    修改最终真假结果。回调必须返回 `bool`。

=== "Observe"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-observe"
    ```

    只记录最终结果，不改变分支走向。

=== "Replace"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-replace"
    ```

    完全替换原判断。

条件还支持 `Before`，但**没有 `After`**：一个条件可能从不同位置结束，没有唯一的“后面一行”。

改写前先确认可行：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-can-rewrite"
```

## 回调可以是什么

| 来源 | 适合 |
| --- | --- |
| 强类型委托，如 `Func<int, int>` | 运行时 Mod |
| `CilMethodSpec` | 用签名描述静态回调 |
| Cecil `MethodReference` | 已拿到方法引用的离线修改器 |

静态委托会降级成对该静态方法的直接调用。实例委托、闭包和多播委托只用于运行时 Hook。

## 给回调补充参数

`Transform` 和 `Observe` 自动把原值放在第一个参数。其余参数由 `args => ...` 按顺序提供：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:extra-args"
```

| 写法 | 传给回调的内容 |
| --- | --- |
| `args.This()` | 当前对象 |
| `args.Arg(0)` | 目标方法的第 0 个显式参数 |
| `args.Arg(argumentCapture)` | 捕获到的参数 |
| `args.Local(localCapture)` | 捕获到的局部变量当前值 |
| `args.Capture(valueCapture)` | 某个已捕获表达式的值 |
| `args.Constant(100)` | 常量（各基础类型都有重载） |
| `args.ConstantI4(value, nominalType)` | 以指定名义类型传入的整数常量 |
| `args.Null(type)` | 空引用 |

MonoWeaver 会检查参数个数、顺序和类型。如果某个捕获值在回调位置还不可用，也会在修改前拒绝。

## 回调返回值的去向

见 [改写计划](../concepts/rewrite-plan.md#callback-result)。要点：

- `Transform` 和值 `Replace` 的返回值直接交回游戏，不需要配置；
- `Observe` 的非 `void` 结果**默认丢弃**；
- `Before` / `After` 的非 `void` 结果**默认留在栈上**，普通 Hook 应显式 `Discard()` 或 `Store(...)`。

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:store-local"
```
