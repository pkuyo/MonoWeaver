# MonoWeaver

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

MonoWeaver works with managed .NET/Mono assemblies that Mono.Cecil can read. It is not an IL2CPP or native-code patcher.

## Compatibility

- MonoWeaver targets .NET Framework 4.8.
- It references Mono.Cecil `[0.10.0, 0.10.4]`.
- Runtime integration is tested with MonoMod `19.9.1.6` and Mono.Cecil `0.10.4`.
- No package feed or publish workflow is configured in this repository. Add the project to your solution, or build and reference `MonoWeaver.dll`.

If your mod loader already ships Mono.Cecil, make sure it uses a compatible version. Cecil version conflicts are a common cause of load-time failures in mod projects.

## Add it to a mod project

Either add a project reference and adjust the path for your repository layout:

```xml
<ItemGroup>
  <ProjectReference Include="..\MonoWeaver\MonoWeaver\MonoWeaver.csproj" />
</ItemGroup>
```

Or build a release DLL:

```bash
dotnet build MonoWeaver/MonoWeaver.csproj -c Release
```

The output is under `MonoWeaver/bin/Release/net48/`.

## Quick start

This example finds `arg0 + arg1` in `Game.Player.ComputeDamage`, sends the original result through a mod callback, checks the edited method, and writes a patched assembly.

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

`Single()` is deliberate: it fails when there is no match or when more than one place matches. For a mod hook, making the pattern more specific is safer than silently patching the first candidate.

For an offline patch, deploy the assembly that contains `ModHooks` with the patched game assembly. Instance delegates and closures only make sense for a runtime patch because they refer to objects in the current process.

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
var pattern = Cil.Value(() =>
    P.Mark("baseDamage", P.Arg<int>(0)) + P.Arg<int>(1));

var match = method.Match(pattern).Single();
var baseDamage = match.Captures.Value("baseDamage");

baseDamage.Transform((Func<int, int>)ModHooks.ClampDamage)
          .Apply(VerifyOptions.Full);
```

The matcher follows an unambiguous compiler-generated temporary by default. If several assignments could reach the same local read, it refuses to guess.

## Runtime use with MonoMod

The same API can be used inside an `ILContext` hook:

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

MonoWeaver handles MonoMod branch labels while a plan is being applied. All labels must already point to valid targets at that time.

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

## Guides

- [Matching and rewriting](docs/matching-and-rewriting.md) — start here for patterns, captures, callbacks, and common hook recipes.
- [Checking an edited method](docs/il-verification.md) — recommended checks and plain-language troubleshooting.
- [Working with Cecil types](docs/cecil-extensions.md) — practical type comparisons for game classes, callbacks, and member access.

The focused guides are currently written in Simplified Chinese.

## Build and test the repository

```bash
dotnet build MonoWeaver.slnx
dotnet test tests/MonoWeaver.PatternTests/MonoWeaver.PatternTests.csproj
dotnet test tests/MonoWeaver.ILTests/MonoWeaver.ILTests.csproj
```

The main projects in this repository are:

| Project | Purpose |
| --- | --- |
| `MonoWeaver` | The library used by mods. |
| `tests/MonoWeaver.PatternTests` | Matching, rewriting, delegate, and MonoMod compatibility tests. |
| `tests/MonoWeaver.ILTests` | Edited-method checker tests. |
| `MonoWeaver.Fuzz` | Automated stress tests. |
| `benchmarks/MonoWeaver.HookBenchmarks` | Hooking benchmarks. |
