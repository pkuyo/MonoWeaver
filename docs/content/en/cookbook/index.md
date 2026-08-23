# Patterns by Example

This page does not enumerate the syntax. It keeps the handful of targets that come up most in mod development.

Every example follows the same order: **the game method, the pattern, and what it actually matches**. `Player`, `Reward`, and `GameAudio` are placeholder names; substitute the real types and methods from your game.

Assumed present:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:cookbook-usings"
```

After building a pattern, you normally search like this:

```csharp
var match = method.Match(pattern).Single();
```

Only use `Single()` when you are sure the target method contains exactly one occurrence. When more than one matches, fold the surrounding calculation or call into the pattern.

Decide between `Cil.Value`, `Cil.Effect`, and `Cil.Condition` first — see [The Three Match Kinds](../concepts/match-kinds.md).

## 1. A calculation

=== "Game method"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-calculate-damage"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-calculation"
    ```

Matches the whole `baseDamage + bonus`. `P.Arg<int>(0)` and `P.Arg<int>(1)` are the first two parameters; the index does not count `this` on an instance method.

## 2. One specific call inside a larger calculation

=== "Game method"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-read-selected-score"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-inner-call"
    ```

The full pattern only matches the `GetScore()` that feeds `+ 10`; it will not pick the discarded call above it. The `score` mark points at the inner `GetScore()` by itself:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-inner-call-capture"
```

## 3. A call that only performs an action

=== "Game method"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-play-hit-sound"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-effect-call"
    ```

Matches the whole `GameAudio.Play(...)` call including the `HitSound` argument. This is the result you want when inserting callbacks around the call, or replacing or removing it.

If the method returns a value but the target code discards it, still use `Cil.Effect(...)`.

## 4. An if condition

=== "Game method"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-try-open"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-if-condition"
    ```

Matches the complete condition that decides the `if`, including the short circuit. It is not searching for the plain `bool` the function returns at the end.

## 5. A constructor, a constant, and a property

=== "Game method"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-create-reward"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-constructor"
    ```

Matches the exact `Reward(string, int)` constructor, the `"rare"` constant, and `Level * 100`. The `amount` mark points only at the reward amount, so you can change just that.

Overloads are distinguished by parameter list: a pattern written as `Select("rare")` will not match `Select(1)`.

## 6. Across an intermediate local

=== "Game method"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-double-damage"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-across-local"
    ```

Even though `damage + 1` is stored into `adjusted` first, the complete `(damage + 1) * 2` still matches as long as the origin of the value is unambiguous. The `adjusted` mark refers to the occurrence the multiplication consumes.

If you really do want a specific local, write `P.Local<int>(0)` — but local indices shift between game versions and build configurations, so writing out the surrounding calculation is usually more stable. See [compiler temporaries](../concepts/captures.md#temporaries).

## 7. An array read

=== "Game method"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-read-next"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-array-read"
    ```

Matches the whole array read including the array argument and `index + 1`. Array length is just `.Length`; array writes use `P.StoreElement(...)`.

## 8. Without referencing the game DLL

=== "Game method"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-read-score"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-symbols-decl"

    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-symbols"
    ```

=== "Equivalent lambda"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-lambda-equivalent"
    ```

Both find the same call. The symbol form just does not require a reference to the game DLL in your mod project. `Match`, `Captures`, and the rewrite API are identical.

## Common placeholders

These drop straight into the patterns above:

| What you need to express | How to write it |
| --- | --- |
| The current instance | `P.This<Player>()` |
| The first parameter | `P.Arg<Player>(0)` |
| A `Player` parameter at any position | `P.Arg<Player>("player")` |
| The first local variable | `P.Local<int>(0)` |
| Anything that produces an `int` | `P.Any<int>("value")` |
| Name an inner position | `P.Mark("name", expression)` |
| A null reference | `default(object)` |

Fields, properties, method calls, `new`, numeric conversions, arithmetic, and comparisons are written as the C# your decompiler shows. Types, argument order, and overloads have to line up with the target code.

Full list: [Pattern DSL](../reference/pattern-dsl.md). Nothing matched, or too much did: [No Match, or Too Many](../troubleshooting/no-match.md).
