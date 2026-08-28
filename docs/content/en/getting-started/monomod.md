# Using MonoMod

Runtime hooks and offline patches use the same API. The only difference is where the method comes from.

## ILContext

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/MonoModUsage.cs:monomod-usings"
```

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/MonoModUsage.cs:monomod-patch"
```

`il.Method` is an ordinary `MethodDefinition`:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/MonoModUsage.cs:monomod-method"
```

## Branch labels

MonoMod represents branch targets as `ILLabel`; Cecil uses `Instruction`. `Apply` converts labels to instruction targets and converts them back when it is done, so the normal path needs nothing from you.

The precondition is that **every label already points at a valid position when you call `Apply`**. If you also make manual Cecil edits inside the same `ILContext`, convert them yourself:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/MonoModUsage.cs:monomod-labels"
```

## What a callback can be

| Source | Good for |
| --- | --- |
| A strongly typed delegate such as `Func<int, int>` | Runtime mods; the most direct form |
| `CilMethodSpec` | Describing a static callback by signature, without referencing its declaring type |
| A Cecil `MethodReference` | Offline patchers that already hold a method reference |

A static delegate is lowered to a **direct call** to that static method — no runtime delegate overhead.

!!! warning "Instance delegates are runtime-only"
    Instance delegates, closures, and multicast delegates capture objects in the current process. They work fine in a runtime hook, but never write them into a patch that gets saved to disk and loaded in another process.
## Runtime trade-offs

- Runtime methods are usually small. Use `Apply(VerifyOptions.Full)`. Verifying one method costs far less than one failed game load.
- When several mods patch the same method, order affects what matches. Adding pattern context is more reliable than depending on load order.
- After each rewrite, previously obtained match positions may be stale. To keep editing the same method, `Match` again.
