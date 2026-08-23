# Rewrite Operations

Everything below is **pick one**. Do not chain several of them onto the same old match. After each `Apply`, `Match` again if you want to keep editing.

## Value

### Transform: read the old value, return a new one

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-transform"
```

The original value arrives as the callback's **first parameter**, and the return value goes back to the game's own logic. The return type has to fit where the original value went.

### Observe: look, do not change

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-observe"
```

For logging, statistics, and raising mod events. The original value still reaches the game. If the callback returns something, it is discarded by default.

### Replace: skip the original computation

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-replace"
```

The original expression is not executed, and the callback does **not** automatically receive the old value. Every argument it needs must come from `args`.

### Before / After: add behaviour around it

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-before-after"
```

`After` only means "call once the computation is finished". It does not pass the original value to the callback — use `Observe` for that.

## Effect

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:effect-ops"
```

Four independent choices: insert before, insert after, replace wholesale, delete.

## Condition

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-match"
```

=== "Transform"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-transform"
    ```

    Change the final true/false result. The callback must return `bool`.

=== "Observe"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-observe"
    ```

    Record the outcome without changing which branch is taken.

=== "Replace"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-replace"
    ```

    Replace the test entirely.

Conditions also support `Before`, but **not `After`**: a condition can finish at different places, so there is no single "line after it".

Check first that a rewrite is possible at all:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-can-rewrite"
```

## What a callback can be

| Source | Good for |
| --- | --- |
| A strongly typed delegate such as `Func<int, int>` | Runtime mods |
| `CilMethodSpec` | Describing a static callback by signature |
| A Cecil `MethodReference` | Offline patchers that already hold a method reference |

A static delegate is lowered to a direct call to that static method. Instance delegates, closures, and multicast delegates are runtime-only.

## Extra callback arguments

`Transform` and `Observe` put the original value in the first parameter automatically. The rest come from `args => ...`, in order:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:extra-args"
```

| Call | What the callback receives |
| --- | --- |
| `args.This()` | The current object |
| `args.Arg(0)` | The target method's first explicit parameter |
| `args.Arg(argumentCapture)` | A captured parameter |
| `args.Local(localCapture)` | The current value of a captured local |
| `args.Capture(valueCapture)` | The value of a captured expression |
| `args.Constant(100)` | A constant; overloads exist for every primitive |
| `args.ConstantI4(value, nominalType)` | An integer constant passed under a specific nominal type |
| `args.Null(type)` | A null reference |

MonoWeaver checks argument count, order, and types. If a captured value is not yet available at the callback site, it refuses before touching the method.

## Where the callback result goes

See [The Rewrite Plan](../concepts/rewrite-plan.md#callback-result). The short version:

- `Transform` and value `Replace` return values go straight back to the game; nothing to configure;
- a non-`void` `Observe` result is **discarded by default**;
- a non-`void` `Before` / `After` result is **left on the stack by default**, so a normal hook should call `Discard()` or `Store(...)` explicitly.

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:store-local"
```
