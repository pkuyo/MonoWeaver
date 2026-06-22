# MonoWeaver expression-pattern sample

`PatternExamples.cs` demonstrates the intended split:

1. A parameterless expression lambda describes one exact local expression or condition.
2. `P.Mark` selects a nested occurrence while the surrounding expression provides disambiguating context.
3. `Single()` refuses zero or multiple matches.
4. The MonoMod adapter performs ordinary insertion: load selected args/locals, invoke a callback, then leave/store/discard at most one result.

The lambda is never executed and `Expression.Compile()` is never called.

The first implementation intentionally does not match complete statement blocks, loops, `try` statements, arbitrary aliases, or irreducible/obfuscated control flow. See `../../PATTERNS.md` for exact guarantees and limitations.
