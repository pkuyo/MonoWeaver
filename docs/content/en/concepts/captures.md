# Matches and Captures

## Exactly one match

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:single"
```

`Single()` accepts exactly one result:

- **nothing found**: wrong method, the game changed, or the pattern does not describe the real code;
- **more than one found**: the pattern is not specific enough;
- **exactly one**: you get a rewritable target.

While debugging, list every candidate first:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:list-candidates"
```

When matching fails, the result set carries diagnostics explaining why a location was skipped:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:explain-failure"
```

Diagnostics being present does not mean the match failed — they exist to explain a result that surprised you.

## Capturing something inside

The root match can be rewritten directly. `P.Mark` is only needed to target something inside it:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:mark-capture"
```

`Captures` exposes one entry point per meaning:

| Entry point | What you get |
| --- | --- |
| `Value("name")` | A value you can read or rewrite |
| `Argument("name")` | An explicitly captured parameter |
| `Local("name")` | An explicitly captured local variable |
| `Condition("name")` | One sub-condition inside a larger condition |
| `Effect("name")` | A no-result action |

Using the wrong entry point fails immediately — reading an argument capture as a condition, for example. That is deliberate: a wrong type assumption is much harder to track down once it reaches the rewrite stage.

## Compiler temporaries { #temporaries }

Debug and release builds differ. The same source may leave an intermediate value on the stack, or store it in a temporary local first:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-double-damage"
```

By default MonoWeaver **follows a temporary whose definition is unambiguous**, so one pattern usually covers both compilations:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-across-local"
```

It stays conservative. If a load could come from more than one store, or the variable had its address taken, it does not guess — that candidate is simply not a match.

### Pin down the source

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:local-defined-by"
```

This says: the `tmp` in `tmp * 2` must come from `damage + 1`.

### Turn following off entirely

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:temporary-off"
```

Now the local read itself is what gets matched. Local variable indices shift between game versions and build configurations, so writing out the surrounding calculation is usually more stable.

## Advanced: match positions

The built-in rewrite operations already pick the right insertion point. These properties are for logging, debuggers, and custom rewrites:

| Property | Read it as |
| --- | --- |
| `FirstInstruction` | Where this match begins |
| `ResultInstruction` | Where this occurrence finishes producing its value |
| `DefinitionFirstInstruction` | Where the original computation begins |
| `DefinitionInstruction` | Where the original value is first produced |
| `ConsumerInstruction` | Where the value is used next, when that is knowable |
| `LastInstruction` (effect/condition) | Where this match ends |
| `IsAddressBacked` | Whether this occurrence was reached through an address-taking instruction |

When a temporary is involved, "where the value is produced" and "where it is read" are different instructions.

!!! warning "Re-match after manual instruction edits"
    Once you change the method body directly through Cecil, treat any match you already hold as stale.
