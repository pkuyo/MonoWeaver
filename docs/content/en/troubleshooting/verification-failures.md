# Verification Failures

The log shows a diagnostic type name. Start from the direction below; you do not need to understand the underlying machinery first.

A diagnostic's `ToString()` usually includes an `IL_xxxx` position. Line that position up with your decompiler or your own instruction log and you can normally tell which rewrite caused it.

## Common diagnostics

| Diagnostic | Usual cause | Look at first |
| --- | --- | --- |
| `StackUnderflow` | Earlier code supplied one value too few | Whether `Transform` returns a result, or whether `Discard()` was called by mistake |
| `InvalidExitStackHeight` | The method returns with a leftover value, or without its return value | Whether a value-returning `Before`/`After` should `Discard()` or `Store(...)` |
| `IncompatibleMergeDepth` | Two branches reach the same point with a different number of values | Whether both sides of the condition route the callback result the same way |
| `IncompatibleMergeTypes` | Two branches bring values of different types | Whether the callback return type is compatible with the original game value |
| `InvalidBrTarget` | A branch target was deleted, or does not belong to this method | Do not remove a branched-to position directly; prefer `Replace` or `Remove` |
| `UninitializedLocal` | A path reads before it writes | The position of `StoreLocal`, and every branch |
| `TypeMismatch` | Incompatible argument, return value, field, or conversion | The callback signature against the captured value types |
| `MethodAccess` / `FieldAccess` / `TypeAccess` | The edited code may not access the target member | Use an accessible mod bridge method, or move the callback's declaring type |
| `ResolveFailed` | A game or mod dependency cannot be found | Give Cecil's resolver the game directory, and check for version conflicts |
| `ExceptionHandlerInvalid` | A manual edit broke a `try`/`catch`/`finally` range | Avoid moving the boundaries by hand; narrow the replacement and re-match |

## Reading the full diagnostics

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:catch-diagnostics"
```

`Apply` has already restored the method; there is no rewrite for you to undo.

## Where callback results go wrong

More than half of all verification failures come from this section.

### Transform

`Transform` receives the original value and returns a new one. The return type has to fit where the original value went:

```csharp
// Correct: int -> int
static int ClampDamage(int original) => Math.Max(original, 0);
```

Do not call `Discard()` on a normal `Transform` result — the game's own code will not get the value it needs, and the usual symptom is `StackUnderflow`.

### Observe

`Observe` keeps the original game value. If the callback also returns something, it is discarded by default; store it when you need it:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:store-local"
```

### Before / After

These two do **not** consume the original game value. A value-returning callback leaves its result in place by default, which usually shows up as `InvalidExitStackHeight` or `IncompatibleMergeDepth`. Most logging and notification callbacks do not need the result, so be explicit:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:discard-result"
```

Simpler still: make those callbacks return `void`.

## Access failures

`MethodAccess` / `FieldAccess` / `TypeAccess` mean the rewritten code has no permission to reach the target member from where it now sits. You can test before rewriting:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/TypeChecks.cs:access-checks"
```

The usual fix is to call indirectly through an accessible static mod method rather than emitting the access directly.

## Resolution failures

`ResolveFailed` means Cecil cannot find a type or member. An offline patcher should give `ModuleDefinition.ReadModule` a resolver that can see every dependency in the game directory. At runtime, check whether two incompatible copies of Cecil, or of a game assembly, are loaded at once.

## What to do after a failure

- do not write the method out after catching the exception; log the diagnostics and skip that hook;
- if the feature is optional, print the game version, target method, and pattern name clearly;
- if the feature is required at startup, **refusing to load is safer than running with a broken method**;
- re-match before trying an alternative; do not reuse the match you held before the failure;
- keep at least one automated test sample per supported game version.
