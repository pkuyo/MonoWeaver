# No Match, or Too Many

## Read the diagnostics first

When matching fails, the result set carries an explanation:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:explain-failure"
```

It reports which IL in the method could not be modelled, which temporary-local passthrough was rejected, and why a `LocalDefinedBy` was not satisfied. Read it before changing the pattern.

## Nothing matched

Check in this order:

1. **Right match kind?** Value, effect, and condition are three different things — see [The Three Match Kinds](../concepts/match-kinds.md). The test in `if (a && b)` is a condition; the one in `return a && b;` is a value.
2. **Right method?** Check the `FullName` spelling, the overload, and the `+` separator for nested types.
3. **Did the game update?** Confirm the expression is still there and the argument order did not change.
4. **Right parameter name?** A pattern-lambda parameter must have exactly the same name as the target parameter in metadata.
5. **Right constants and overloads?** `Select("rare")` will not match `Select(1)`; `1` and `1L` are different constants.
6. **Right operand order?** `a - b` is not `b - a`, and the same goes for comparisons.
7. **Did you capture a runtime object in the lambda?** An outer variable becomes a closure field read and will never match a game value. Use a pattern-lambda parameter, a DSL placeholder, a literal, or a static field.
8. **Is a temporary in the way?** Temporaries with an unambiguous definition are followed by default, but an ambiguous origin is never guessed. Pin the origin with `LocalDefinedBy` — see [compiler temporaries](../concepts/captures.md#temporaries).

## Too many matched

Multiple matches are more dangerous than none: the pattern is not describing a unique piece of logic.

Do not take the first one. Add context instead:

- fold in the **enclosing call**: look for `GetScore() + 10`, not just `GetScore()`;
- fold in **constants**: `Level * 100` is more specific than `Level * P.Any<int>("x")`;
- fold in a **field or property**;
- use `P.Mark` for the small part you actually want to change, and let the outer expression do the locating.

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-inner-call"
```

This pattern only matches the call that feeds `+ 10`, and the `score` mark then points at the inner `GetScore()`.

If you really do need to edit several places, enumerate them explicitly and decide per candidate rather than relying on order:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:list-candidates"
```

## Type scope too wide

The symbol form is exact by default, and `Assignable()` widens it to subclasses. When you get multiple matches, check whether `Assignable()` was applied more broadly than intended:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:exact-vs-assignable"
```

## Stale matches

Once a method is rewritten, previously obtained positions may no longer be valid.

- a `RewritePlan` can only be applied successfully once;
- to keep editing the same method, `Match` again;
- the same applies after any manual Cecil edit to the method body.

## Quick reference

| Symptom | Check first |
| --- | --- |
| `No matching expression was found` | The method, whether the game updated, constants and overloads |
| More than one result | Add enclosing calls, fields, constants, or a `P.Mark` context |
| Wrong callback argument count | `Transform`/`Observe` already supply the original value as the first argument |
| Wrong callback return type | `Transform` must return something that can replace the original value; a condition must return `bool` |
| `match is stale` | Another edit changed the method — `Match` again |
| The check fails after applying | See [Verification Failures](verification-failures.md) |
