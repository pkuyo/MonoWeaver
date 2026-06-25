# MonoWeaver

[English](README.md)

MonoWeaver 是一个基于 Mono.Cecil 的 **语义化 CIL 匹配、插入/重写、Cecil 类型判断与 IL 验证工具集**。

使用本项目，你可以直接描述想找的表达式，再拿到精确的值或条件捕获，选择合适的插入点进行改写而不需要手动查看IL序列。

## 主要特点

- 以表达式匹配参数、local、常量、调用、字段、运算、数组以及短路条件。
- 支持表达式求值前、值产生后以及条件出口上的插入与重写。
- 同一套 API 同时支持离线改写、运行时 delegate 回调 (兼容MonoMod Runtimer Detour)。
- 改写后可检查栈平衡/类型、跳转、异常区域、local 初始化、访问规则和指令操作数。

## 项目结构

| 项目 | 作用 |
| --- | --- |
| `MonoWeaver` | 匹配器、纯 Cecil 重写、类型扩展、CFG 与验证器。 |
| `tests/MonoWeaver.PatternTests` | 表达式匹配、重写、运行时 delegate 与 MonoMod `ILContext` 兼容测试。 |
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
damage.AfterUse()
    .Transform(callback)
    .ApplyWithVerify(VerifyOptions.Full);

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
- `Call` 插入独立调用；非 `void` 结果可留栈、丢弃或写入 local/argument。
- transform 接口会先返回 `CallResultPlan`；必须调用 `Apply()` 或 `ApplyWithVerify(...)` 才会真正修改 IL。

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

## 回调与 ILContext

metadata-native `CilMethodSpec` 和强类型 delegate：

```csharp
damage.AfterUse()
    .Transform((Func<int, int>)HookCallbacks.ClampDamage)
    .ApplyWithVerify(VerifyOptions.Full);
```

静态 delegate 会被生成为直接调用；实例方法、闭包和 multicast delegate 会存入运行时引用表，并通过 Cecil 生成的 helper invoker 调用。

MonoMod 的 `ILContext` 也可以直接使用同一套 API：

```csharp
using System;
using MonoMod.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;

public static void Patch(ILContext il)
{
    var value = il.Method.Match(damagePattern).Single().Value("damage");
    value.AfterUse()
         .Transform((Func<int, int>)HookCallbacks.ClampDamage)
         .ApplyWithVerify(VerifyOptions.Full);
}
```

如果 `ILContext` 中使用了 MonoMod `ILLabel` branch operand，MonoWeaver 在 `Apply()` 期间会把检测到的 label 解析成 Cecil `Instruction` target，并在提交后恢复回 label operand。需保证ILLabel在Apply时均有有效指向目标。

## 详细文档

- [Cecil 类型扩展](docs/cecil-extensions.md)：`IsSameWith`、可赋值判断、泛型约束与访问检查。
- [匹配、插入与重写](docs/matching-and-rewriting.md)：语义匹配、修改。
- [IL 验证](docs/il-verification.md)：验证模式、诊断结果。

## 构建与测试

```bash
dotnet build MonoWeaver.slnx
dotnet test tests/MonoWeaver.PatternTests/MonoWeaver.PatternTests.csproj
dotnet test tests/MonoWeaver.ILTests/MonoWeaver.ILTests.csproj
```

当前分支面向 Mono.Cecil `[0.10.0, 0.10.4]`；测试项目使用 Mono.Cecil `0.10.4` 和 MonoMod `19.9.1.6`。
