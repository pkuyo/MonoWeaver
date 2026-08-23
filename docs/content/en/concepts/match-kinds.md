# The Three Match Kinds

MonoWeaver splits targets into three kinds. Picking the wrong kind is the number one reason nothing matches, so decide this first.

| Target | When to use it | Examples |
| --- | --- | --- |
| `Cil.Value(...)` | The code produces a value | damage calculation, field read, method return value |
| `Cil.Effect(...)` | The code only performs an action and leaves nothing behind | playing a sound, raising an event, calling a `void` method |
| `Cil.Condition(...)` | The code decides which branch is taken | `if`, `while`, `&&`, `\|\|` |

## Value

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:value-pattern"
```

A value match points at one occurrence where that value is produced. It can be read, replaced, or surrounded with new code.

## Effect

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:effect-pattern"
```

An effect match covers a complete piece of code that produces no result. If the target method **does** return a value but the call site discards it, still use `Cil.Effect(...)`; in the `CilExpr` form that is `Cil.Discard(expression)`.

## Condition

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:condition-pattern"
```

A condition match finds the test that **decides control flow**, including the short-circuit structure of `&&` and `||`. It is not searching for an ordinary `bool` value.

!!! note "A condition is not a bool value"
    In `return a && b;` the `a && b` is a value. In `if (a && b)` it is a condition. Same source, different IL shape.

## Which operations each kind supports

|  | `Before` | `After` | `Transform` | `Observe` | `Replace` | `Remove` |
| --- | :---: | :---: | :---: | :---: | :---: | :---: |
| Value | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Effect | ✓ | ✓ | — | — | ✓ | ✓ |
| Condition | ✓ | — | ✓ | ✓ | ✓ | — |

The two gaps have reasons:

- **An effect has no `Transform`/`Observe`**: there is no value to read or replace.
- **A condition has no `After`**: a condition can finish at different places (a short circuit jumps away early), so there is no single "line after it".

Some very complex conditions can be recognised but not safely rewritten. Check first:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:condition-can-rewrite"
```

Per-operation semantics: [Rewrite Operations](../reference/rewrite-operations.md).
