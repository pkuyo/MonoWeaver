# The Rewrite Plan

No rewrite operation edits the method immediately. They all return a `RewritePlan` first:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:plan"
```

At this point the target method is unchanged. The gap is where you decide what happens to the callback result, before committing everything at once.

## Applying

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:apply-full"
```

What `Apply(VerifyOptions.Full)` does:

1. saves the method state;
2. applies the rewrite;
3. runs the full check;
4. keeps the edit on success;
5. **restores the original method and throws on failure**.

You can call plain `Apply()` to skip the check, but not for a mod you intend to release. Details in [Verification](../reference/verification.md).

## Where the callback result goes { #callback-result }

For `Transform` and value `Replace`, the return value is already the result the game needs — nothing to configure.

`Before`, `After`, and `Observe` also accept callbacks that return a value. Then you have to say where it goes:

| Call | Use |
| --- | --- |
| `Discard()` | The callback result is not needed |
| `Store(capture)` | Write into a captured local or explicit parameter |
| `StoreLocal(...)` | Write into a specific local variable |
| `StoreArgument(...)` | Write into a specific explicit parameter; `this` is not allowed |
| `LeaveOnStack()` | Leave the result for the following code — advanced |

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:store-result"
```

The defaults differ, and this catches people out:

- a non-`void` result from `Observe` is **discarded by default**;
- a non-`void` result from `Before` or `After` is **left in place by default**.

The second one is a common source of `InvalidExitStackHeight`. A normal mod hook should be explicit:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:discard-result"
```

Simpler still: make those callbacks return `void`.

!!! danger "LeaveOnStack() is an advanced tool"
    Use it only when you know the following code needs that result. When in doubt, verify in full — it catches most mistakes of this kind.

## Plan lifecycle

- a plan can only be applied successfully once;
- once the method changes, **older match positions may be stale**;
- to make several edits to one method, prefer edit, re-match, edit;
- passing the check does not mean the gameplay is right; still test in the real game.

```text
Match -> (Mark/Captures) -> pick an operation -> RewritePlan -> route the result -> Apply
   ^                                                                                 |
   +------------------ come back here to keep editing the same method <--------------+
```
