# Pattern DSL

## Two entry points

Patterns can be written two equivalent ways. Both produce the same match results and use the same rewrite API.

=== "Lambda: your project references the game DLL"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:lambda-form"
    ```

    Closest to the original game expression, and the easiest to read.

=== "Symbols: game types not referenced or not loaded"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:symbol-form"
    ```

    Describes the target by assembly name, type name, and signature.

## Placeholders (lambda form)

| Call | Meaning |
| --- | --- |
| `P.This<T>()` | The current object |
| `P.This<T>("name")` | The current object, captured under a name |
| `P.Arg<T>(0)` | The first explicit parameter; `this` is not counted |
| `P.Arg<T>(0, "name")` | Same, captured under a name |
| `P.Arg<T>("name")` | A `T` parameter at any position |
| `P.Local<T>(0)` | The first local variable |
| `P.Local<T>(0, "name")` | Same, captured under a name |
| `P.Local<T>("name")` | Any `T` local variable |
| `P.Any<T>("name")` | Anything that produces a `T` |
| `P.Mark("name", value)` | Name an inner expression so it can be rewritten on its own |
| `P.StoreElement(array, index, value)` | An array write |

Inside the lambda you write method calls, field and property reads, `new`, array access, conversions, arithmetic, and comparisons as ordinary C#.

!!! warning "Do not capture runtime objects in the lambda"
    The lambda is an expression tree that gets parsed, not code that gets run. An outer variable becomes a closure field read, which will not match a constant in the game. Use `P.Arg`, `P.Local`, `P.Any`, a literal, or a static field instead.

## Symbol form

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:symbol-form-short"
```

| Entry point | Produces |
| --- | --- |
| `CilSymbols.In("Assembly")` | `CilAssemblySpec` |
| `assembly.Type("Ns.Type")` | `CilTypeSpec` |
| `CilSymbols.Type(typeReference)` | `CilTypeSpec` from an existing Cecil reference |
| `type.InstanceMethod(name, returnType, params...)` | `CilMethodSpec` |
| `type.StaticMethod(name, returnType, params...)` | `CilMethodSpec` |
| `type.Constructor(params...)` | `CilMethodSpec` |
| `type.InstanceField(name, fieldType)` | `CilFieldSpec` |
| `type.StaticField(name, fieldType)` | `CilFieldSpec` |

`CilType` provides the built-ins: `CilType.Int32`, `CilType.Boolean`, `CilType.String`, `CilType.Void`, and short aliases such as `CilType.I4` and `CilType.Bool`.

Type construction: `MakeArrayType(rank)`, `MakeByReferenceType()`, `MakePointerType()`, `MakeGenericType(...)`.

### CilExpr fluent calls

| Call | Meaning |
| --- | --- |
| `.Call(method, args...)` | A method call |
| `.Field(field)` | A field read |
| `.ElementAt(index, elementType)` | An array read |
| `.Length()` | Array length |
| `.ConvertTo(type)` / `.As(type)` | Conversion |
| `.Mark("name")` | Named capture |
| `.EqualTo` / `.GreaterThan` / `.LessThan` and friends | Comparison |
| `.AndAlso` / `.OrElse` | Short-circuit logic |
| Operators `+ - * / %`, bitwise, shifts, `!`, `~` | Overloaded, usable directly |

## Type match scope { #type-scope }

In the lambda form, `P.Arg<T>`, `P.Local<T>`, and `P.Any<T>` accept compatible actual types, so a derived class normally matches too.

The symbol form is **exact by default**. Add `Assignable()` to allow subclasses:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:exact-vs-assignable"
```

| Mode | Call |
| --- | --- |
| Exactly the same type | default |
| Subclasses / assignable | `.Assignable()` |
| Compatible runtime stack representation | `.StackCompatible()` |

For a mod hook, add `Assignable()` only when you genuinely want subclasses too. Too wide a scope produces multiple matches. See [Type Matching](type-matching.md).

## PatternOptions

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:temporary-off"
```

| Option | Default | Effect |
| --- | --- | --- |
| `TemporaryNormalization` | `UniqueDefinitions` | Whether to follow a compiler temporary with an unambiguous definition |
| `IgnoreCallOpcodeDifference` | `true` | Whether `call` and `callvirt` are treated as the same |
| `IgnoreTransparentControlFlow` | `true` | Whether branches that do not affect evaluation are ignored |

### LocalDefinedBy

Constrain a local capture to come from a specific expression:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:local-defined-by"
```

Available on patterns from `Cil.Value`, `Cil.Effect`, and `Cil.Condition` alike.
