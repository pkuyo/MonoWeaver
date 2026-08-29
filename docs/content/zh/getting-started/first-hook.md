# 第一个 Hook

这一页走一遍完整的离线补丁流程：读取游戏 DLL、找到一段计算、把结果交给自己的回调、检查、写出新程序集。

## 完整例子

先准备一个 Mod 回调。它必须是**静态**方法，离线补丁才能在另一个进程里调用到：

```csharp
public static class Hooks
{
    public static int ClampDamage(int original)
        => Math.Min(Math.Max(original, 0), 999);
}
```

然后是补丁本体：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/QuickStart.cs:quickstart"
```

## 逐步说明

### 1. 拿到目标方法

MonoWeaver 的入口是 Mono.Cecil 的 `MethodDefinition`。

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/QuickStart.cs:read-module"
```

在 MonoMod `ILContext` 中则直接用 `il.Method`，见 [在 MonoMod 中使用](monomod.md)。

### 2. 描述想找的逻辑

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:value-pattern"
```

`baseDamage` 和 `bonus` 按名称绑定目标方法的同名参数。三种匹配种类的区别见 [三种匹配种类](../concepts/match-kinds.md)。

### 3. 确认唯一匹配

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:single"
```

`Single()` 是刻意的：没找到会报错，找到多处也会报错。

!!! danger "不要用 `matches[0]`"
    游戏更新后，第一处很可能已经不是原来的逻辑。找到多处时应该补充 Pattern，而不是取第一个。

### 4. 选择改写方式

| 想做什么 | 使用 |
| --- | --- |
| 在匹配代码执行前调用回调 | `Before(...)` |
| 在一个值或行为执行后调用回调 | `After(...)` |
| 读取旧值并返回新值 | `Transform(...)` |
| 读取或记录旧值，但不改变它 | `Observe(...)` |
| 跳过原代码，提供完整替代 | `Replace(...)` |
| 删除一段不产生结果的行为 | `Remove()` |

完整语义和可用范围见 [改写操作](../reference/rewrite-operations.md)。

### 5. 提交并检查

所有改写都先返回一个 `RewritePlan`，调用 `Apply()` 才真正修改方法。

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:apply-full"
```

`Apply(VerifyOptions.Full)` 会在检查失败时**恢复原方法并抛出**，不会留下改了一半的结果。准备发布的 Mod 应该一直用它。

## 部署提示

离线写出 DLL 时，包含 `Hooks` 的 Mod DLL 必须和补丁后的游戏程序集一起部署——补丁里留下的是对那个静态方法的直接调用。

实例委托、闭包和多播委托引用的是当前进程里的对象，只适合运行时 Hook，不要写进离线补丁。

## 只改大表达式中的一部分

根匹配可以直接改写。要改的只是某个参数时，lambda 参数本身就是捕获，按参数名取回（复合子表达式则声明成独立的 `Cil.Value` 片段写进表达式，用同一个对象取回）：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:mark-capture"
```

更多见 [匹配结果与捕获](../concepts/captures.md)。
