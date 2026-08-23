# Architecture

`MonoWeaver/` has three directories, each owning one responsibility. A change that spreads across all of them usually means the layers got confused.

```text
Patterns/   describe the target, find it in a method, rewrite it
CFG/        method body to basic-block graph, simulate the stack per block, report problems
Utils/      Cecil type system, instruction helpers, delegate emission, two containers
```

## Patterns/ — matching and rewriting

The full path of one `Match`:

```text
Cil.Value(lambda)
      |  PatternExpressionParser: expression tree -> PatternNode
   Pattern (ValuePattern / EffectPattern / ConditionPattern)
      |
the method IL
      |  MethodModel: rebuild only the expression dependencies a candidate needs
   a symbolic model of the method
      |  PatternMatcher: line PatternNodes up against model nodes
   CilMatchSet<ValueMatch> / ...
      |  PatternTransforms: pick an operation
   RewritePlan
      |  Apply
   the edited method body (rolled back on failure)
```

| File | Responsibility |
| --- | --- |
| `Pattern.cs` | The `Cil` and `P` entry points, the three pattern types, and `PatternOptions` |
| `PatternExpressionParser.cs` | Translates a C# expression tree into a `PatternNode` tree |
| `PatternNodes.cs` | Pattern-side nodes: argument, local, field, call, binary operation, `Mark`, and so on |
| `CilExpr.cs` / `CilSymbols.cs` / `CilMetadataSpecs.cs` | The symbol form, for when game types are not referenced |
| `MethodModel.cs` | A small symbolic model of the target method. It **does not decompile the whole method**; it keeps only the expression dependencies a candidate needs |
| `TargetExpressionNodes.cs` | Target-side nodes, including shapes that are invisible in C# such as address-taking |
| `LocalDefinitionIndex.cs` | The set of stores that could reach a given load, used to disambiguate temporaries |
| `PatternMatcher.cs` | The matching loop, and the decision on whether a condition can be safely rewritten |
| `CilMatchResults.cs` | `CilMatchSet` plus the match and capture types |
| `PatternTransforms.cs` | The user-facing rewrite extension methods |
| `PatternTransformImplementation.cs` | The actual IL emission behind those operations |
| `RewritePlan.cs` | The plan, callback result routing, `Apply`, and rollback |
| `MatchDiagnostics.cs` | Explanations for a failed match |

Two design choices matter most:

- **`MethodModel` is demand-driven**, not a full decompiler. It only rebuilds dependencies around candidate positions, so a large method never gets expanded wholesale.
- **Ambiguity means no match.** When `LocalDefinitionIndex` finds that a load could come from several stores, or that the variable had its address taken, the candidate is dropped rather than guessed. This is "rather miss a match than patch the wrong place", made concrete.

## CFG/ — verification

| File | Responsibility |
| --- | --- |
| `ILBasicBlockGraphBuilder.cs` | Method body to basic-block graph, including exception regions |
| `StackType.cs` | The model of values on the evaluation stack, and the merge rules |
| `ILMethodVerifier.cs` | The verifier itself, `VerifyOptions`, and the exception-region model |
| `ILMethodVerifier.Verify.cs` | Per-instruction stack simulation along every path |
| `ILMethodVerifier.Diagnostic.cs` | Diagnostic types, deduplication, `CfgVerifyException` |

The verifier does not care about semantics. It answers one question: **would the runtime reject this method when loading it?** The diagnostic list is in [Verification Failures](../troubleshooting/verification-failures.md).

## Utils/ — the Cecil layer

| File | Responsibility |
| --- | --- |
| `CecilTypeSystem*.cs` | Type comparisons: `IsSameWith`, `IsAssignableTo`, `CanAccess`, generic constraints |
| `CecilHelper*.cs` | The `Verify` extension, symbol handling, MonoMod label to Cecil instruction conversion |
| `CecilInstructionHelpers.cs` | Instruction construction and operand handling |
| `CecilDelegateEmission.cs` | Turns a runtime delegate into an emittable call; a static delegate is lowered to a direct call |
| `FixSizeDictionary.cs` / `ListStack.cs` | Containers on the matching and verification hot paths, to avoid repeated allocation |
| `CecilCompat.cs` | Papers over the API differences between Cecil `0.10` and `0.11+` |

`CecilCompat.cs` is why the dual-flavor build works at all. Before using a Cecil API in new code, confirm both generations have it; if only one does, add the adaptation here rather than writing `#if CECIL_010` in ordinary code.

## Adding something

- **New pattern syntax**: parse it in `PatternExpressionParser`, add a node in `PatternNodes`, match it in `PatternMatcher`, and possibly add the target-side shape to `MethodModel`.
- **A new rewrite operation**: add the user-facing overloads in `PatternTransforms`, emit the IL in `PatternTransformImplementation`, then confirm the `RewritePlan` result-routing semantics still make sense.
- **A new diagnostic**: add the type in `ILMethodVerifier.Diagnostic.cs` and add a row to the table in [Verification Failures](../troubleshooting/verification-failures.md).
- Whatever you add, run the tests under both `CecilFlavor` values — see [Build and Test](build-and-test.md).
