# MonoWeaver

[![MonoWeaver NuGet 版本](https://img.shields.io/nuget/v/MonoWeaver.svg?logo=nuget&label=MonoWeaver)](https://www.nuget.org/packages/MonoWeaver)
[![MonoWeaver.Cecil10 NuGet 版本](https://img.shields.io/nuget/v/MonoWeaver.Cecil10.svg?logo=nuget&label=MonoWeaver.Cecil10)](https://www.nuget.org/packages/MonoWeaver.Cecil10)
[![开源协议](https://img.shields.io/github/license/pkuyo/MonoWeaver)](LICENSE)
[![CI](https://github.com/pkuyo/MonoWeaver/actions/workflows/ci.yml/badge.svg)](https://github.com/pkuyo/MonoWeaver/actions/workflows/ci.yml)

[English](README.md)

MonoWeaver 是给 C# Mod 开发者用的代码匹配与改写工具。你只需要描述想找的游戏逻辑，例如“两个参数相加”“读取某个字段”“调用某个方法”或“一段 `if` 条件”，然后选择在它前后追加代码、读取结果，或直接替换它。

大多数时候不需要自己数 IL 指令。编译器即使多生成了一个临时变量，或换了一种条件跳转写法，匹配仍可以保持稳定。

常见用途：

- 修改伤害、价格、冷却时间等计算结果；
- 记录某个值，但不改变游戏原逻辑；
- 替换或删除一次游戏行为；
- 改写带 `&&`、`||` 的判断条件；
- 在离线修改 DLL 和 MonoMod `ILContext` 中使用同一套写法；
- 在保存或执行前检查修改后的方法。

## 兼容范围

| 包 | Mono.Cecil | 目标框架 |
| --- | --- | --- |
| `MonoWeaver` | `0.11.2+` | `netstandard2.0` |
| `MonoWeaver.Cecil10` | `0.10.0` – `0.10.4` | `net46`、`netstandard2.0` |




如果 Mod Loader 已经自带 Mono.Cecil，请先确认版本兼容。Mod 中同时出现多份不兼容的 Cecil，是常见的加载失败原因。

MonoWeaver 只适用于 Mono.Cecil 能读取的 .NET/Mono 托管程序集，不用于 IL2CPP 或原生代码。

## 快速开始

下面的例子在 `Game.Player.ComputeDamage` 中查找 `arg0 + arg1`，把原结果交给 Mod 回调处理，检查修改是否安全，然后写出新的程序集。

```csharp
using System;
using System.Linq;
using Mono.Cecil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;

public static class ModHooks
{
    public static int ClampDamage(int value)
        => Math.Min(Math.Max(value, 0), 999);
}

using var module = ModuleDefinition.ReadModule("Game.dll");

var method = module.Types
    .Single(type => type.FullName == "Game.Player")
    .Methods.Single(candidate => candidate.Name == "ComputeDamage");

var damagePattern = Cil.Value(() =>
    P.Arg<int>(0) + P.Arg<int>(1));

var damage = method.Match(damagePattern).Single();

damage.Transform((Func<int, int>)ModHooks.ClampDamage)
      .Apply(VerifyOptions.Full);

module.Write("Game.Patched.dll");
```

`Single()` 会在“没有找到”或“找到多处”时直接报错。这是有意设计：Mod Hook 应该补充匹配条件，而不是悄悄修改第一个候选位置。

离线写出 DLL 时，包含 `ModHooks` 的 Mod DLL 也要随成品一起部署。实例委托和闭包引用的是当前进程里的对象，只适合运行时 Hook。

## 按目的选择操作

| 想做什么 | 使用 |
| --- | --- |
| 在匹配代码执行前调用回调 | `Before(...)` |
| 在一个值或行为执行后调用回调 | `After(...)` |
| 读取旧值并返回新值 | `Transform(...)` |
| 读取或记录旧值，但不改变它 | `Observe(...)` |
| 跳过原代码，提供完整替代 | `Replace(...)` |
| 删除一段不产生结果的行为 | `Remove()` |

这些操作都会先返回一个 `RewritePlan`，调用 `Apply()` 后才真正修改。Mod 代码建议使用 `Apply(VerifyOptions.Full)`：如果检查失败，MonoWeaver 会恢复修改前的方法并抛出错误，避免留下改了一半的结果。

值、行为和条件支持的操作略有不同。条件可能有多个出口，因此没有唯一的 `After(...)`；应使用 `Transform`、`Observe`、`Replace` 或 `Before`。

## 只修改大表达式中的一部分

完整匹配结果可以直接修改。只有需要选中内部某一段时，才使用 `P.Mark`：

```csharp
var pattern = Cil.Value(() =>
    P.Mark("baseDamage", P.Arg<int>(0)) + P.Arg<int>(1));

var match = method.Match(pattern).Single();
var baseDamage = match.Captures.Value("baseDamage");

baseDamage.Transform((Func<int, int>)ModHooks.ClampDamage)
          .Apply(VerifyOptions.Full);
```

MonoWeaver 默认会跟随来源唯一的编译器临时变量。如果同一个读取位置可能来自多次赋值，它会停止匹配，不会猜测。

## 在 MonoMod 中运行时使用

`ILContext` 中可以直接使用同一套 API：

```csharp
using System;
using MonoMod.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;

public static void Patch(ILContext il)
{
    var pattern = Cil.Value(() =>
        P.Arg<int>(0) + P.Arg<int>(1));

    il.Method.Match(pattern)
      .Single()
      .Transform((Func<int, int>)ModHooks.ClampDamage)
      .Apply(VerifyOptions.Full);
}
```

提交修改时，MonoWeaver 会处理 MonoMod 的跳转标签。此时所有标签都必须已经指向有效位置。

## Mod 项目没有引用游戏类型时

如果项目已经引用游戏 DLL，前面的 lambda 写法最直观。如果不方便引用或加载游戏类型，可以用程序集名、类型名和方法签名来描述：

```csharp
var game = CilSymbols.In("GameAssembly");
var player = game.Type("Game.Player");
var getScore = player.InstanceMethod("GetScore", CilType.Int32);

var scorePattern = Cil.Value(
    P.Arg(0, player.Assignable())
     .Call(getScore));
```

两种写法得到相同类型的匹配结果，后续修改方式完全一致。

## 使用建议

- 发布前优先使用 `Apply(VerifyOptions.Full)`。
- 一个计划提交后，如果还要修改同一个方法，重新匹配一次，避免继续使用过期位置。
- 游戏更新后，即使 Hook 没有报错，也要重新测试实际玩法。
- 离线补丁尽量使用静态回调；实例委托、闭包和多播委托仅用于当前运行时。
- 多匹配时补充外层调用、常量、字段或 `P.Mark` 上下文，不要固定取第一个。

## 详细文档

完整文档（中英双语）：**<https://pkuyo.github.io/MonoWeaver/>**

- [第一个 Hook](https://pkuyo.github.io/MonoWeaver/getting-started/first-hook/)：完整的离线补丁流程。
- [按游戏代码查写法](https://pkuyo.github.io/MonoWeaver/cookbook/)：常见目标函数、对应 Pattern 和实际命中位置。
- [改写操作](https://pkuyo.github.io/MonoWeaver/reference/rewrite-operations/)：`Before`/`After`/`Transform`/`Observe`/`Replace`/`Remove` 在三种匹配种类下的语义。
- [修改后检查](https://pkuyo.github.io/MonoWeaver/reference/verification/)：推荐检查方式和常见报错处理。
- [类型判断](https://pkuyo.github.io/MonoWeaver/reference/type-matching/)：处理游戏类、回调参数和成员访问时常用的类型判断。

## 构建与测试仓库

整个解决方案共用同一个 `CecilFlavor` 开关，因此两代 Cecil 都能跑全量测试：

```bash
dotnet test MonoWeaver.slnx
```

```bash
dotnet test MonoWeaver.slnx -p:CecilFlavor=Latest
```

本地打出两个包到 `artifacts/nupkg/`：

```bash
dotnet pack MonoWeaver/MonoWeaver.csproj -c Release -p:CecilFlavor=Cecil10 -p:Version=0.1.0
```

```bash
dotnet pack MonoWeaver/MonoWeaver.csproj -c Release -p:CecilFlavor=Latest -p:Version=0.1.0
```

仓库中的主要项目：

| 项目 | 用途 |
| --- | --- |
| `MonoWeaver` | Mod 实际引用的库。 |
| `tests/MonoWeaver.PatternTests` | 匹配、改写、委托和 MonoMod 兼容测试。 |
| `tests/MonoWeaver.ILTests` | 修改后检查器的测试。 |
| `tests/MonoWeaver.DocSamples` | 文档里所有代码块的来源，只编译不运行。 |
| `MonoWeaver.Fuzz` | 自动生成大量情况做压力测试。 |
| `benchmarks/MonoWeaver.Benchmarks` | IL 验证吞吐，以及与 MonoMod 的打补丁耗时对比。 |

```bash
dotnet run -c Release --project benchmarks/MonoWeaver.Benchmarks -- --verify-only --max-method-us 50000
```
