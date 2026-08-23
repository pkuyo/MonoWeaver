# MonoWeaver

MonoWeaver is a matching and rewriting tool for C# mod developers. You describe the game logic you are looking for — "two arguments added together", "a field read", "a method call", an `if` condition — and then choose what should happen before, after, or instead of it.

You normally do not need to count IL instructions. A hook stays stable even when the compiler introduces an extra temporary local or picks a different branch layout.

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:value-pattern"
```

Typical uses:

- changing a calculated value such as damage, price, or cooldown;
- logging a value without changing the game result;
- replacing or removing a game action;
- rewriting a condition built from `&&` and `||`;
- using one API for both offline DLL patching and MonoMod `ILContext`;
- checking the edited method before it is written or executed.

## Where to start

<div class="grid cards" markdown>

-   __Get it running__

    ---

    Pick a package, add it, and land your first offline patch.

    [:octicons-arrow-right-24: Installation](getting-started/installation.md)

-   __Look up a pattern__

    ---

    Find the pattern that matches what your decompiler shows, or read the full DSL and rewrite tables.

    [:octicons-arrow-right-24: Cookbook](cookbook/index.md)

-   __Fix a problem__

    ---

    Nothing matched, too much matched, or the verifier rejected your edit.

    [:octicons-arrow-right-24: Troubleshooting](troubleshooting/no-match.md)

</div>

## Scope

MonoWeaver works on managed .NET/Mono assemblies that Mono.Cecil can read. It does not work on IL2CPP or native code.
