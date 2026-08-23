# Installation

MonoWeaver ships two packages. The API is identical; they differ only in which Mono.Cecil generation they are compiled against.

| Package | Mono.Cecil | Target frameworks |
| --- | --- | --- |
| `MonoWeaver` | `0.11.2+` | `netstandard2.0` |
| `MonoWeaver.Cecil10` | `0.10.0` – `0.10.4` | `net46`, `netstandard2.0` |

## Which one

Check which Cecil generation the game or mod loader already loads, then pick.

- **Older Unity games, MonoMod 19.x** → `MonoWeaver.Cecil10`. Its `net46` build matches Unity's .NET 4.x runtime without going through the `netstandard.dll` facade, which is more reliable on older Unity versions. Runtime integration is tested with MonoMod `19.9.1.6` and Mono.Cecil `0.10.4`.
- **Everything else** → `MonoWeaver`.

```bash
dotnet add package MonoWeaver
```

```bash
dotnet add package MonoWeaver.Cecil10
```

!!! warning "Two incompatible copies of Cecil is the most common load failure"
    If your mod loader already ships Mono.Cecil, confirm the version is compatible instead of bringing a second, incompatible copy along with your mod.

## Namespaces

Four namespaces, split by responsibility. A normal hook uses all four:

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/QuickStart.cs:usings"
```

| Namespace | What is in it |
| --- | --- |
| `MonoWeaver.Patterns` | `Cil`, `P`, `CilSymbols`, plus match and capture types |
| `MonoWeaver.Cecil` | `Match`, the rewrite operations, `RewritePlan` |
| `MonoWeaver.CFG` | `VerifyOptions`, `ILMethodVerifier` |
| `MonoWeaver.Utils` | The `Verify` extension and Cecil type-comparison helpers |

## When it does not apply

- IL2CPP builds: there is no managed IL to read.
- Native code, or assemblies obfuscated past what Cecil can parse.
- Logic that was inlined into its caller — the original expression may no longer exist there.

Next: [Your First Hook](first-hook.md).
