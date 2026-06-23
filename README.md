# MonoWeaver

[简体中文](readme_cn.md)

MonoWeaver is a Mono.Cecil-based toolkit for **semantic CIL matching, safe insertion/rewrite, Cecil type queries, and post-rewrite IL verification**.

Instead of binding a hook to a fragile opcode sequence, MonoWeaver lets you describe the expression you want, capture the exact value or condition, select an insertion site, and verify the modified method before writing it back.

## Highlights

- Match values, effects, calls, fields, operators, arrays, locals, arguments, and short-circuit conditions as expressions.
- Normalize compiler-generated temporary locals when their reaching definition is unambiguous.
- Insert before an expression, after a specific value use, after its producer, or across every exit of a branch-based condition.
- Use a pure Cecil backend for offline rewriting, or the optional MonoMod adapter for `ILContext` and delegates.
- Validate stack balance/types, branches, exception regions, locals, access rules, and instruction operands after rewriting.

## Projects

| Project | Purpose |
| --- | --- |
| `MonoWeaver` | Matcher, pure Cecil transforms, type-system helpers, CFG and verifier. Targets `netstandard2.0`. |
| `MonoWeaver.MonoMod` | Optional `ILContext`/delegate adapter. |
| `tests/MonoWeaver.PatternTests` | Pattern matching and transform tests. |
| `tests/MonoWeaver.ILTests` | IL verifier corpus and adapter tests. |
| `MonoWeaver.Fuzz` | Fuzz program. |
| `benchmarks/MonoWeaver.HookBenchmarks` | Hooking benchmarks. |

## Quick start: pure Cecil rewrite

The example below finds `arg0 + arg1`, captures its result, and inserts a callback that replaces the value before the original consumer sees it.

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

`Single()` intentionally rejects both no-match and ambiguous-match cases. A hook should become more specific rather than silently selecting the first candidate.

## Insertion model

| Site | Use it for |
| --- | --- |
| `match.Before()` / `match.After()` | Insert around the complete matched value/effect. A branch-based condition has no single `After` site. |
| `value.BeforeEvaluation()` | Run code before the captured expression starts evaluating. |
| `value.AfterUse()` | Rewrite or observe this exact value occurrence. This is usually the safest value hook. |
| `value.AfterProducer()` | Rewrite the original producer. If the value is stored in a temporary, all later uses may observe the change. |
| `condition.Transform(...)` | Transform a logical result even when the compiler emitted several short-circuit branches instead of one Boolean value. |

At a matched value site:

- `Transform` consumes the original value and leaves the callback result for the original consumer.
- `Observe` duplicates the original value, calls a `void` callback, and preserves the original value.
- `CallVoid` and `CallValue` insert an independent call; non-void results can be left on the stack, discarded, or stored.

## Two pattern DSLs

Use the lambda DSL when the referenced runtime types are available:

```csharp
var sumPattern = Cil.Value(() =>
    P.Mark("sum", P.Arg<int>(0) + P.Arg<int>(1)));
```

Use the metadata-native DSL when the target assembly should not be loaded into the CLR:

```csharp
var game = CilSymbols.In("GameAssembly");
var player = game.Type("Game.Player");
var getScore = player.InstanceMethod("GetScore", CilType.Int32);

var scorePattern = Cil.Value(
    P.Arg(0, player.Assignable(), "player")
     .Call(getScore)
     .Mark("score"));
```

Both DSLs produce the same `ExpressionPattern` and use the same matcher and transform APIs.

## Pure Cecil and MonoMod

The core `MonoWeaver` project performs offline transforms without delegates, dynamic methods, or loading the target assembly. Pure Cecil callbacks are static `MethodReference`/`CilMethodSpec` methods and are checked before the target module is modified.

`MonoWeaver.MonoMod` adapts the same matches to `ILContext` and delegate emission:

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

## Focused guides

- [Cecil type extensions](docs/cecil-extensions.md): `IsSameWith`, assignability, stack assignability, constraints, and access checks.
- [Matching, insertion, and rewriting](docs/matching-and-rewriting.md): captures, insertion points, value/condition transforms, and safe rewrite workflow.
- [IL verification](docs/il-verification.md): verification modes, diagnostics, and post-rewrite checks.

## Build and test

```bash
dotnet build MonoWeaver.slnx
dotnet test tests/MonoWeaver.PatternTests/MonoWeaver.PatternTests.csproj
dotnet test tests/MonoWeaver.ILTests/MonoWeaver.ILTests.csproj
```

The core projects currently reference Mono.Cecil `0.11.4`; the optional adapter references MonoMod `21.11.1.1`.
