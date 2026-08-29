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

The root match can be rewritten directly. When the target is one of the parameters, the lambda parameter is already the capture — read it back by parameter name:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:mark-capture"
```

When the target is a compound sub-expression, declare that part as a standalone `Cil.Value`/`Cil.Condition` fragment and use it directly in the expression; use `Cil.Local` for a local variable. These captures are read back by object, and the return type follows the declared object:

| Indexer | What you get |
| --- | --- |
| `match.Arg("parameterName")` / `match.This()` | The argument bound to a lambda parameter (`ArgumentCapture`) |
| `match.Local("parameterName")` | A `CilLocal<T>`-typed lambda parameter (`LocalCapture`) |
| `match[a Cil.Any<T>() object]` | A value you can read or rewrite (`ValueCapture`) |
| `match[a Cil.Arg<T>() / Cil.This<T>() object]` | The captured parameter (`ArgumentCapture`) |
| `match[a Cil.Local<T>() object]` | The captured local variable (`LocalCapture`) |
| `match[an embedded ValuePattern]` | The selected value fragment (`ValueCapture`) |
| `match[an embedded ConditionPattern]` | One sub-condition inside a larger condition (`ConditionCapture`) |

Indexing with an unrelated object throws `KeyNotFoundException` immediately — the capture identity is the object identity, so there is no string name to misspell.

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

This says: the `tmp` in `tmp * 2` must come from `damage + 1`. The `tmp` object can then be reused across patterns with the same meaning.

### Turn following off entirely

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:temporary-off"
```

Now the local read itself is what gets matched. Local variable indices shift between game versions and build configurations, so writing out the surrounding calculation is usually more stable.

## Matching after a position

A result set can be filtered by IL position: `After(x)` keeps matches that start after x, `Before(x)` keeps those that end before x, and `Between(a, b)` requires both. x is an instruction, or an earlier match or capture (its whole range is the boundary):

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:match-after"
```

This is the counterpart of ILCursor's "find the next one from here", except that `Single()` still requires the filtered set to be unique instead of silently taking the first. Positions compare in IL order, not control-flow order; the anchor must be in the current method body — after a rewrite, an anchor whose instructions were removed throws, so match again.

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
