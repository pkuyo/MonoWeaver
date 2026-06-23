# MonoWeaver

[English](README.md)

MonoWeaver 是一个基于 Mono.Cecil 的 **语义化 CIL 匹配、插入/重写、Cecil 类型判断与 IL 验证工具集**。

使用本项目，你可以直接描述想找的表达式，再拿到精确的值或条件捕获，选择合适的插入点进行改写而不需要手动查看IL序列。

## 主要特点

- 以表达式匹配参数、local、常量、调用、字段、运算、数组以及短路条件。
- 支持表达式求值前、值产生后以及条件出口上的插入与重写。
- 纯 Cecil 后端适合离线改写；可选 MonoMod 适配器可直接配合 `ILContext` 和 delegate。
- 改写后可检查栈平衡/类型、跳转、异常区域、local 初始化、访问规则和指令操作数。

## 项目结构

| 项目 | 作用 |
| --- | --- |
| `MonoWeaver` | 匹配器、纯 Cecil 重写、类型扩展、CFG 与验证器。 |
| `MonoWeaver.MonoMod` | 可选的 `ILContext`/delegate 适配。 |
| `tests/MonoWeaver.PatternTests` | 表达式匹配与重写测试。 |
| `tests/MonoWeaver.ILTests` | IL 验证语料与测试。 |
| `MonoWeaver.Fuzz` | Fuzz程序，测试可赋值判别是否和CLR一致。 |
| `benchmarks/MonoWeaver.HookBenchmarks` | Hook benchmark。（没什么用，本身项目也以效率为主要目的的） |

## 快速开始：纯 Cecil 重写

下面匹配 `arg0 + arg1`，捕获结果，并在原 consumer 使用该值之前插入一个替换回调。

```csharp
using System;
using System.Linq;
using Mono.Cecil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;

public static class HookCallbacks
{
    public static int ClampDamage(int value) => Math.Max(0, value);
}

using var module = ModuleDefinition.ReadModule("Game.dll");

var method = module.Types
    .Single(t => t.FullName == "Game.Player")
    .Methods.Single(m => m.Name == "ComputeDamage");

var damagePattern = Cil.Value(() =>
    P.Mark("damage", P.Arg<int>(0) + P.Arg<int>(1)));

var callback = CilMethodSpec.From(
    typeof(HookCallbacks).GetMethod(nameof(HookCallbacks.ClampDamage))!);

var damage = method.Match(damagePattern).Single().Value("damage");
damage.AfterUse().Transform(callback);

method.Verify(VerifyOptions.Full).ThrowIfHasErrors();
module.Write("Game.Patched.dll");
```

`Single()` 会主动拒绝“没有匹配”和“存在歧义”两种情况。Hook 应通过补充上下文或捕获变得更精确，而不是默认取第一个结果。

## 插入模型

| 插入点 | 适合场景 |
| --- | --- |
| `match.Before()` / `match.After()` | 在完整匹配段前后插入；分支形式的 condition 没有唯一的 `After`。 |
| `value.BeforeEvaluation()` | 在捕获表达式开始求值之前插入。 |
| `value.AfterUse()` | 针对这一次具体值获取后，通常是最合适的hook方式。 |
| `value.AfterProducer()` | 针对原始值产生位置；若结果先写入临时 local，后续多个使用都可能受影响。 |
| `condition.Transform(...)` | 改写短路条件，即使编译器没有生成一个实际的 Boolean 值。 |

在匹配结果上：

- `Transform` 消费原值，并把回调返回值留给原逻辑。
- `Observe` 复制原值后调用 `void` 回调，原值继续参与原逻辑。
- `CallVoid` / `CallValue` 插入独立调用；非 `void` 结果可留栈、丢弃或写入 local/argument。

## 两套 Pattern DSL

目标类型已经加载时，可直接使用 lambda DSL：

```csharp
var sumPattern = Cil.Value(() =>
    P.Mark("sum", P.Arg<int>(0) + P.Arg<int>(1)));
```

不希望把目标程序集加载进 CLR 时，使用 metadata-native DSL：

```csharp
var game = CilSymbols.In("GameAssembly");
var player = game.Type("Game.Player");
var getScore = player.InstanceMethod("GetScore", CilType.Int32);

var scorePattern = Cil.Value(
    P.Arg(0, player.Assignable(), "player")
     .Call(getScore)
     .Mark("score"));
```

两种 DSL 最终都会生成同一个 `ExpressionPattern`，后续匹配和重写接口完全一致。

## 纯 Cecil 与 MonoMod

`MonoWeaver` 核心项目不创建 delegate、动态方法，也不要求加载目标程序集，适合离线修改。纯 Cecil 回调使用 static `MethodReference` 或 `CilMethodSpec`，并在真正修改目标模块前检查参数与返回值。

`MonoWeaver.MonoMod` 将相同匹配结果接到 `ILContext` 和 delegate：

```csharp
using MonoMod.Cil;
using MonoWeaver.MonoMod.Patterns;

using var context = new ILContext(method);
context.Invoke(il =>
{
    var value = il.Match(damagePattern).Single().Value("damage");
    value.AfterUse(il)
         .Transform((Func<int, int>)HookCallbacks.ClampDamage)
         .LeaveOnStack();
});
```

## 详细文档

- [Cecil 类型扩展](docs/cecil-extensions.md)：`IsSameWith`、可赋值判断、泛型约束与访问检查。
- [匹配、插入与重写](docs/matching-and-rewriting.md)：capture、插入点、value/condition transform。
- [IL 验证](docs/il-verification.md)：验证模式、诊断结果。

## 构建与测试

```bash
dotnet build MonoWeaver.slnx
dotnet test tests/MonoWeaver.PatternTests/MonoWeaver.PatternTests.csproj
dotnet test tests/MonoWeaver.ILTests/MonoWeaver.ILTests.csproj
```

