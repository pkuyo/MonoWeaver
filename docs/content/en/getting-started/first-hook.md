# Your First Hook

This page walks through a complete offline patch: read the game DLL, find a calculation, send the result through your own callback, verify, and write a new assembly.

## The whole thing

First, a mod callback. It must be **static** so an offline patch can still reach it from another process:

```csharp
public static class Hooks
{
    public static int ClampDamage(int original)
        => Math.Min(Math.Max(original, 0), 999);
}
```

Then the patch itself:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/QuickStart.cs:quickstart"
```

## Step by step

### 1. Get the target method

MonoWeaver's entry point is a Mono.Cecil `MethodDefinition`.

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/QuickStart.cs:read-module"
```

Inside a MonoMod `ILContext`, use `il.Method` instead — see [Using MonoMod](monomod.md).

### 2. Describe the logic you want

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:value-pattern"
```

`P.Arg<int>(0)` is the target method's first **explicit** parameter; the index does not count `this` on an instance method. The three match kinds are explained in [The Three Match Kinds](../concepts/match-kinds.md).

### 3. Confirm exactly one match

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:single"
```

`Single()` is deliberate: it fails when nothing matched, and it fails when more than one place matched.

!!! danger "Do not use `matches[0]`"
    After a game update, the first occurrence may no longer be the logic you meant. When more than one place matches, make the pattern more specific instead of picking the first one.

### 4. Choose the operation by intent

| What the mod should do | API |
| --- | --- |
| Run a callback before the matched code | `Before(...)` |
| Run a callback after a value or action | `After(...)` |
| Read the old value and return a new one | `Transform(...)` |
| Read or log the old value without changing it | `Observe(...)` |
| Skip the old code and provide a replacement | `Replace(...)` |
| Remove a matched no-result action | `Remove()` |

Full semantics and availability per kind: [Rewrite Operations](../reference/rewrite-operations.md).

### 5. Apply and verify

Every operation returns a `RewritePlan` first. Nothing changes until you call `Apply()`.

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:apply-full"
```

If the check fails, `Apply(VerifyOptions.Full)` **restores the method and throws** instead of leaving a half-applied edit. A mod you intend to release should always use it.

## Deployment

When you write a patched DLL, the mod DLL containing `Hooks` has to ship alongside it — the patch left behind a direct call to that static method.

Instance delegates, closures, and multicast delegates refer to objects in the current process. They only make sense for a runtime hook; do not put them in an offline patch.

## Targeting one part of a larger expression

The root match can be rewritten directly. Use `P.Mark` only when the hook should target something inside it:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:mark-capture"
```

More in [Matches and Captures](../concepts/captures.md).
