# Type Matching

Most mods only need patterns and the rewrite API — MonoWeaver checks callback parameters and return values for you. The tools on this page matter when you look up methods and fields yourself, or emit extra Cecil code.

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/TypeChecks.cs:typecheck-usings"
```

## The three checks you will actually use

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/TypeChecks.cs:three-checks"
```

| Method | The question it answers for a mod |
| --- | --- |
| `a.IsSameWith(b)` | Are these the same type |
| `source.IsAssignableTo(target)` | Can a `source` value be passed to a parameter or variable that wants `target` |
| `target.IsAssignableFrom(source)` | Same question, read from the other direction |

One line of ordinary C# is enough to remember it:

```csharp
Target value = source;

// source.IsAssignableTo(Target)
// Target.IsAssignableFrom(source)
```

For example, with `EliteEnemy` deriving from `Enemy`:

- `EliteEnemy.IsSameWith(Enemy)` is `false`;
- `EliteEnemy.IsAssignableTo(Enemy)` is `true`;
- `Enemy.IsAssignableTo(EliteEnemy)` is normally `false`.

## When it has to be exact

Looking up a specific field, overload, or serialised member calls for `IsSameWith`:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/TypeChecks.cs:exact-overload"
```

!!! danger "Never compare `FullName` alone"
    Two assemblies can declare types with the same name. Arrays, by-reference parameters, pointers, and generic parameters can also print similar names while being different types.

## When you mean "can this be passed in"

Handing a game object to a callback is an assignability question:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/TypeChecks.cs:assignability"
```

`Transform`, `Observe`, `Replace`, and `CallArguments` already run checks like this. Calling it yourself is mainly for producing your own error message before building a plan, or picking a compatible callback out of several.

## Type scope in patterns

See [Pattern DSL, type match scope](pattern-dsl.md#type-scope). In short: the lambda form accepts compatible types, the symbol form is exact by default, and `Assignable()` opts into subclasses.

## Check access before touching a game member

When you emit a call or field access yourself, check that the current position is allowed to reach the target:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/TypeChecks.cs:access-checks"
```

If the answer is `false`, the safer options are usually:

- call indirectly through an accessible static mod method;
- use the game's public API;
- move the logic somewhere that may legally access the member.

Passing the check only means access is permitted. It says nothing about arguments, the receiver, or timing.

## Helpers

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/TypeChecks.cs:type-helpers"
```

| Method | What it is for |
| --- | --- |
| `StripType()` | Removes Cecil's extra decoration so you can keep comparing; does not mistake a by-ref parameter or pointer for a plain type |
| `BaseType()` | The base class, keeping already-substituted generic arguments where possible |
| `FindCommonBaseType(a, b)` | A common base both values can be treated as |
| `CollectAllInterfaces(...)` | Every interface implemented by the type and its bases |
| `CheckConstraints()` | Whether the supplied generic arguments satisfy `class`, `struct`, base-class, and similar constraints |
| `IsEnum()` | Whether the type is an enum |
| `GetEnumBackingFieldType()` | The integer type an enum actually stores |

!!! warning "Unresolvable dependencies make the results unreliable"
    An offline patcher should give `ModuleDefinition.ReadModule` a resolver that can find the game's DLLs; otherwise these methods may not return what you expect.

## Only for custom IL checking

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/TypeChecks.cs:stack-assignable"
```

`IsILStackAssignableTo` compares how values are represented at runtime — some small integers share a representation while a method executes. It exists for verifiers and custom IL emitters.

Do not use it in place of `IsAssignableTo` for ordinary mod logic. Sharing a stack representation does not mean the C# assignment is valid.

## Which one do I want

| Your question | Use |
| --- | --- |
| Is this exactly that game type, or exactly that overload | `IsSameWith` |
| Can this game value be passed to that callback parameter | `IsAssignableTo` |
| Can that parameter receive this value, read target-first | `IsAssignableFrom` |
| Should the pattern allow subclasses | `CilTypeSpec.Assignable()` |
| May the current method access this member directly | `CanAccess` |
| Are these generic arguments legal | `CheckConstraints` |
| I am writing my own stack verifier | `IsILStackAssignableTo` |
