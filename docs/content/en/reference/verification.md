# Verification

After a mod rewrites code, the painful failure is usually not "the effect is wrong" — it is the game throwing or crashing while loading the method. MonoWeaver's verifier checks that the method is still something .NET/Mono will accept, before you save the DLL or keep running.

## Recommended usage

For rewrites MonoWeaver produced, verify in full at the moment you apply:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Rewrites.cs:value-transform"
```

This will:

1. save the method state before the edit;
2. apply the rewrite;
3. run the full check;
4. keep the edit on success;
5. restore the original method and throw on failure.

A mod you intend to release should prefer `Apply(VerifyOptions.Full)` over a bare `Apply()`.

## Light or Full

| Mode | What it checks | Suggested use |
| --- | --- | --- |
| `VerifyOptions.Light` | Instruction shape, and how many values sit on each execution path | Fast, frequent checks while developing |
| `VerifyOptions.Full` | Everything in Light plus value types, local initialisation, member access | Before release, in automated tests, on unknown game versions |

Mod methods are usually small, so prefer `Full`. Only reach for `Light` once you have confirmed full verification is a bottleneck.

Individual flags can be combined:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:verify-options-combo"
```

| Flag | Checks |
| --- | --- |
| `Instructions` | Instruction and operand shape |
| `StackBalance` | How many values are on each path |
| `StackTypes` | The types of those values; includes `StackBalance` |
| `LocalInit` | That locals are assigned before they are read |
| `AccessTest` | Member access permissions |

## Verifying a hand-edited method

If the method also went through your own Cecil edits, check it separately:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:verify-usings"
```

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:verify-manual"
```

`ThrowIfHasErrors()` throws on `Error` or `Fatal`. A `Warning` stays in `Diagnostics`, and it is up to the mod whether that should block loading.

To read the detail behind a throw from `Apply(VerifyOptions.Full)`, catch it:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Verification.cs:catch-diagnostics"
```

`Apply` has already restored the method at this point; there is no rewrite for you to undo.

## What it actually catches

In terms a mod developer meets in practice:

- a callback consumed the original value but returned no replacement;
- a `Before` or `After` callback left behind a return value nobody uses;
- the two paths of an `if` reach the same point carrying a different number or type of values;
- after deleting code, a branch still points at the deleted position;
- an illegal jump into or out of a `try`/`catch`/`finally`;
- a local read on a path where it was never assigned;
- an incompatible argument, return value, or field type;
- mod code reaching a private member the current context may not access;
- a type or member the method needs that cannot be resolved from the game's dependencies.

## What it does not check

Even when `Full` passes, all of this is still possible:

- you matched the wrong place, and it happens to be well-formed;
- damage, probability, or time units are semantically wrong;
- the callback throws;
- several mods edit the same method and the order conflicts;
- a game update changed *when* something is called while keeping a similar expression;
- a problem that only shows up on a particular save, map, or in multiplayer.

Use full verification, a unique match, logging, and real playtesting together. None of them alone is the guarantee.

When you get a concrete error, see [Verification Failures](../troubleshooting/verification-failures.md).
