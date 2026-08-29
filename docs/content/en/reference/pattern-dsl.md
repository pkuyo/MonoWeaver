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

## Parameterized lambdas

`Cil.Value`, `Cil.Effect`, and `Cil.Condition` can all describe target method parameters directly through lambda parameters. Parameters bind by name, and `__this` denotes the current instance:

```csharp
var sum = Cil.Value((int left, int right) => left + right);
var play = Cil.Effect((Player player) => Audio.Play(player.HitSound));
var gate = Cil.Condition((Player player) => player.HasKey && !player.IsDead);
var damage = Cil.Value((Player __this, int amount) => __this.baseDamage + amount);
```

Lambda parameters do not need to follow the target method's declaration order, and only parameters used by the expression need to be declared. `P.Arg` and `P.This` remain useful when target parameter names are unavailable or matching by type or position is required.

Lambda parameters are captures in their own right: each one is equivalent to a `Cil.Arg<T>("parameterName")` matched by the target's parameter name (`__this` is equivalent to `Cil.This<T>()`), and is read back with `match.Arg("parameterName")` / `match.This()`. Using the same parameter twice in the expression means the same argument.

## Inline placeholders (lambda form, no capture)

| Call | Meaning |
| --- | --- |
| `P.This<T>()` | The current object |
| `P.Arg<T>(0)` | The first explicit parameter; `this` is not counted |
| `P.Local<T>(0)` | The first local variable |
| `P.StoreElement(array, index, value)` | An array write |
| `P.StoreField(field, value)` | A field write |

Inside the lambda you write method calls, field and property reads, `new`, array access, conversions, arithmetic, and comparisons as ordinary C#.

## Pattern objects (capturable, reusable)

To capture or reuse a position, declare a pattern object outside the lambda, reference it directly in the expression, and read the capture back through the same object after matching:

```csharp
var tmp = Cil.Local<int>();
var pattern = Cil.Value(() => tmp * 2);
var capture = method.Match(pattern).Single()[tmp];   // LocalCapture
```

| Declaration | Meaning |
| --- | --- |
| `Cil.This<T>()` | The current object |
| `Cil.Arg<T>()` / `Cil.Arg<T>(index)` / `Cil.Arg<T>("name")` | A `T` parameter at any position / at an index / with a given name (same rule as lambda parameters) |
| `Cil.Local<T>()` / `Cil.Local<T>(index)` | Any/a specific `T` local variable |
| `Cil.Local(definedBy)` | A local uniquely defined by the given expression |
| `Cil.Any<T>()` | Anything that produces a `T` (wildcard) |

There is one rule: **objects that bind an identity (this/argument/local) mean "the same one" when repeated within a pattern** — `Cil.Value(() => tmp * tmp)` requires both reads to hit the same variable. `Cil.Any` and embedded fragments bind no identity, so repeating the same object throws at construction time.

In operand and argument positions the object is used directly (`tmp * 2`, `Foo(tmp)`). Three positions need the explicit `.Value`: when the whole body is the object (`Cil.Value(() => tmp.Value)`), as the receiver of a call (`score.Value.ToString()`), and when `T` is `object` or an interface (C#'s built-in conversions bypass the implicit operator).

These objects can also appear directly in lambda parameter position as pattern-local declarations (not reusable across patterns, not readable from the result):

```csharp
var pattern = Cil.Value((CilArg<int> value) => value + value);   // one argument added to itself
```

### Fragment embedding (selecting part of a larger expression)

A complete `Cil.Value` / `Cil.Condition` pattern can be embedded directly into another pattern, and later used to rewrite just the selected part:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:mark-capture"
```

Fragments bind no identity: the same fragment object can be embedded at most once per outer pattern, and a fragment's own `PatternOptions` are superseded by the outer pattern's when embedded.

!!! warning "Do not capture runtime objects in the lambda"
    The lambda is an expression tree that gets parsed, not code that gets run. Apart from pattern objects, an outer variable becomes a closure field read, which will not match a constant in the game. Use pattern objects, a literal, or a static field instead.

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
| `.EqualTo` / `.GreaterThan` / `.LessThan` and friends | Comparison |
| `.AndAlso` / `.OrElse` | Short-circuit logic |
| Operators `+ - * / %`, bitwise, shifts, `!`, `~` | Overloaded, usable directly |

## Type match scope { #type-scope }

In the lambda form, `P.Arg<T>`/`Cil.Arg<T>`, `P.Local<T>`/`Cil.Local<T>`, and `Cil.Any<T>` accept compatible actual types, so a derived class normally matches too.

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

### Local definition constraints

`Cil.Local(definedBy)` declares "a local uniquely defined by this expression":

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:local-defined-by"
```

State the origin once at the declaration, then use the object directly in any pattern.
