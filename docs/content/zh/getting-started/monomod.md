# 在 MonoMod 中使用

运行时 Hook 和离线补丁用的是同一套 API，区别只在于方法从哪里来。

## ILContext

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/MonoModUsage.cs:monomod-usings"
```

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/MonoModUsage.cs:monomod-patch"
```

`il.Method` 就是一个普通的 `MethodDefinition`：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/MonoModUsage.cs:monomod-method"
```

## 跳转标签

MonoMod 用 `ILLabel` 表示跳转目标，Cecil 用 `Instruction`。`Apply` 会临时把标签转成指令目标，完成后再转回去，所以正常流程什么都不用做。

前提是**调用 `Apply` 时所有标签都已经指向有效位置**。如果你在同一个 `ILContext` 里还手动混用了 Cecil 修改，需要自己转换：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/MonoModUsage.cs:monomod-labels"
```

## 回调可以用什么

| 来源 | 适合 |
| --- | --- |
| 强类型委托，如 `Func<int, int>` | 运行时 Mod，最直接 |
| `CilMethodSpec` | 用签名描述静态回调，不需要引用回调所在类型 |
| Cecil `MethodReference` | 已经拿到方法引用的离线修改器 |

静态委托会被降级成对该静态方法的**直接调用**，没有运行时委托开销。

!!! warning "实例委托只用于运行时"
    实例委托、闭包和多播委托会引用当前进程里的对象。它们在运行时 Hook 中完全可用，但不要写进要保存到磁盘、之后在另一个进程里加载的补丁。

## 运行时的取舍

- 运行时方法通常不大，直接用 `Apply(VerifyOptions.Full)`。一个方法的完整检查远比游戏加载一次失败便宜。
- 多个 Mod 改同一个方法时，顺序会影响匹配结果。补充 Pattern 上下文比依赖加载顺序可靠。
- 每次改写完成后，之前拿到的匹配位置就可能过期。要继续改同一个方法，重新 `Match`。
