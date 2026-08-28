# MonoWeaver

[![MonoWeaver on NuGet](https://img.shields.io/nuget/v/MonoWeaver.svg?logo=nuget&label=MonoWeaver)](https://www.nuget.org/packages/MonoWeaver)
[![MonoWeaver.Cecil10 on NuGet](https://img.shields.io/nuget/v/MonoWeaver.Cecil10.svg?logo=nuget&label=MonoWeaver.Cecil10)](https://www.nuget.org/packages/MonoWeaver.Cecil10)
[![License](https://img.shields.io/github/license/pkuyo/MonoWeaver)](LICENSE)
[![CI](https://github.com/pkuyo/MonoWeaver/actions/workflows/ci.yml/badge.svg)](https://github.com/pkuyo/MonoWeaver/actions/workflows/ci.yml)

[简体中文](readme_cn.md)

MonoWeaver helps C# mod developers find a piece of compiled game logic and safely change it. You describe the expression you are looking for—such as a damage calculation, a method call, or an `if` condition—and then choose what should happen before, after, or instead of it.

You normally do not need to search for a fixed list of IL instructions. That makes a hook easier to read and less likely to break when the compiler adds a temporary local or uses a different branch layout.

Typical uses include:

- changing a calculated value such as damage, price, or cooldown;
- logging a value without changing the game result;
- replacing or removing a game action;
- changing a short-circuit `if` condition;
- applying the same matching API to a Cecil `MethodDefinition` or a MonoMod `ILContext`;
- checking the edited method before it is written or executed.

## Compatibility


| Package | Mono.Cecil | Target frameworks |
| --- | --- | --- |
| `MonoWeaver` | `0.11.2+` | `netstandard2.0` |
| `MonoWeaver.Cecil10` | `0.10.0` – `0.10.4` | `netstandard2.0` |

## Quick start

Both examples find `baseDamage + bonus` in `Game.Player.ComputeDamage`. Lambda parameters bind to target-method parameters with the same names. They use this callback:

```csharp
using System;

public static class ModHooks
{
    public static int ClampDamage(int value)
        => Math.Min(Math.Max(value, 0), 999);
}
```

### Runtime hook with MonoMod

Use this form when the mod loader invokes your hook with an `ILContext` for `ComputeDamage`:

```csharp
using System;
using MonoMod.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;

public static class DamagePatch
{
    public static void Patch(ILContext il)
    {
        var pattern = Cil.Value((int baseDamage, int bonus) =>
            baseDamage + bonus);

        il.Method.Match(pattern)
          .Single()
          .Transform((Func<int, int>)ModHooks.ClampDamage)
          .Apply(VerifyOptions.Full);
    }
}
```

MonoWeaver handles MonoMod branch labels while applying the rewrite. Every label must already point to a valid target when `Apply` is called.

### Offline DLL patch

Use this form when the output should be a patched assembly on disk:

```csharp
using System;
using System.Linq;
using Mono.Cecil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;

public static class DamagePatcher
{
    public static void Patch(string inputPath, string outputPath)
    {
        using var module = ModuleDefinition.ReadModule(inputPath);

        var method = module.Types
            .Single(type => type.FullName == "Game.Player")
            .Methods.Single(candidate => candidate.Name == "ComputeDamage");

        var pattern = Cil.Value((int baseDamage, int bonus) =>
            baseDamage + bonus);

        method.Match(pattern)
              .Single()
              .Transform((Func<int, int>)ModHooks.ClampDamage)
              .Apply(VerifyOptions.Full);

        module.Write(outputPath);
    }
}
```

`Single()` is deliberate: it fails when there is no match or when more than one place matches. For a mod hook, making the pattern more specific is safer than silently patching the first candidate.

For an offline patch, deploy the assembly that contains `ModHooks` with the patched game assembly. Instance delegates and closures are runtime-only because they refer to objects in the current process.

## Choose the operation by intent

| What the mod should do | API |
| --- | --- |
| Run a callback before the matched code | `Before(...)` |
| Run a callback after a value or action | `After(...)` |
| Read the old value and return a new one | `Transform(...)` |
| Read or log the old value without changing it | `Observe(...)` |
| Skip the old code and provide a replacement | `Replace(...)` |
| Remove a matched no-result action | `Remove()` |

Every operation creates a `RewritePlan`. Nothing changes until you call `Apply()`. For mod code, prefer `Apply(VerifyOptions.Full)`: if the check fails, MonoWeaver restores the method and throws instead of leaving a half-applied edit.

Values, actions, and conditions have slightly different valid operations. In particular, a branch-based condition has no single `After(...)` point; use `Transform`, `Observe`, `Replace`, or `Before` instead.

## Capturing one part of a larger match

The root match can be edited directly. Use `P.Mark` only when the hook should target an inner value:

```csharp
var pattern = Cil.Value((int baseDamage, int bonus) =>
    P.Mark("baseDamage", baseDamage) + bonus);

var match = method.Match(pattern).Single();
var baseDamage = match.Captures.Value("baseDamage");

baseDamage.Transform((Func<int, int>)ModHooks.ClampDamage)
          .Apply(VerifyOptions.Full);
```

The matcher follows an unambiguous compiler-generated temporary by default. If several assignments could reach the same local read, it refuses to guess.

## When game types are not referenced

If the mod project references the game assembly, the lambda form above is usually the easiest. If you cannot or do not want to load those types, describe them by assembly and type name:

```csharp
var game = CilSymbols.In("GameAssembly");
var player = game.Type("Game.Player");
var getScore = player.InstanceMethod("GetScore", CilType.Int32);

var scorePattern = Cil.Value(
    P.Arg(0, player.Assignable())
     .Call(getScore));
```

Both forms produce the same kind of match result and use the same rewrite operations.

## Documentation

Full documentation, in English and Simplified Chinese: **<https://pkuyo.github.io/MonoWeaver/en/>**

- [Using MonoMod](https://pkuyo.github.io/MonoWeaver/en/getting-started/monomod/) — runtime integration through `ILContext`.
- [Your first hook](https://pkuyo.github.io/MonoWeaver/en/getting-started/first-hook/) — the complete offline patch flow, step by step.
- [Patterns by example](https://pkuyo.github.io/MonoWeaver/en/cookbook/) — common game functions shown beside the pattern and the exact part it finds.
- [Rewrite operations](https://pkuyo.github.io/MonoWeaver/en/reference/rewrite-operations/) — what `Before`, `After`, `Transform`, `Observe`, `Replace`, and `Remove` do per match kind.
- [Verification](https://pkuyo.github.io/MonoWeaver/en/reference/verification/) — recommended checks and plain-language troubleshooting.
- [Type matching](https://pkuyo.github.io/MonoWeaver/en/reference/type-matching/) — practical type comparisons for game classes, callbacks, and member access.

## Build and test the repository

The whole solution follows the same `CecilFlavor` switch, so the tests run against either Cecil generation:

```bash
dotnet test MonoWeaver.slnx
```

```bash
dotnet test MonoWeaver.slnx -p:CecilFlavor=Latest
```

Build both packages locally into `artifacts/nupkg/`:

```bash
dotnet pack MonoWeaver/MonoWeaver.csproj -c Release -p:CecilFlavor=Cecil10 -p:Version=0.1.1
```

```bash
dotnet pack MonoWeaver/MonoWeaver.csproj -c Release -p:CecilFlavor=Latest -p:Version=0.1.1
```

The main projects in this repository are:

| Project | Purpose |
| --- | --- |
| `MonoWeaver` | The library used by mods. |
| `tests/MonoWeaver.PatternTests` | Matching, rewriting, delegate, and MonoMod compatibility tests. |
| `tests/MonoWeaver.ILTests` | Edited-method checker tests. |
| `tests/MonoWeaver.DocSamples` | Source of every code block in the docs; compiled, not run. |
| `MonoWeaver.Fuzz` | Automated stress tests. |
| `benchmarks/MonoWeaver.Benchmarks` | IL verification throughput, plus a patch-time comparison against MonoMod. |

```bash
dotnet run -c Release --project benchmarks/MonoWeaver.Benchmarks -- --verify-only --max-method-us 50000
```
