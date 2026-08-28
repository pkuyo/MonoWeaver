# Build and Test

## Dual Cecil builds

The whole solution follows one `CecilFlavor` switch, so the full test suite runs against either Cecil generation.

| Value | Mono.Cecil | MonoMod | Package produced |
| --- | --- | --- | --- |
| `Cecil10` (default) | `[0.10.0, 0.10.4]` (tested with `0.10.4`) | `19.9.1.6` | `MonoWeaver.Cecil10` |
| `Latest` | `[0.11.2,)` (tested with `0.11.6`) | `22.7.31.1` | `MonoWeaver` |

```bash
dotnet test MonoWeaver.slnx
```

```bash
dotnet test MonoWeaver.slnx -p:CecilFlavor=Latest
```

Both have to pass. The switch is defined in `Directory.Build.props` at the repository root.


## Projects in this repository

| Project | Purpose |
| --- | --- |
| `MonoWeaver` | The library mods actually reference |
| `tests/MonoWeaver.PatternTests` | Matching, rewriting, delegate, and MonoMod compatibility tests |
| `tests/MonoWeaver.ILTests` | Tests for the edited-method verifier |
| `tests/MonoWeaver.DocSamples` | The source of every code block in the docs; compiled, not run |
| `MonoWeaver.Fuzz` | Automated stress tests |
| `benchmarks/MonoWeaver.Benchmarks` | IL verification throughput, plus a patch-time comparison against MonoMod |

```bash
dotnet run -c Release --project benchmarks/MonoWeaver.Benchmarks -- --verify-only --max-method-us 50000
```

## The documentation site

Sources live in `docs/content/`. `zh/` is the default language and `en/` holds the translations.

```bash
python -m venv .venv && .venv/Scripts/activate && pip install -r docs/requirements.txt
```

Preview locally. **Run from the repository root** — snippet paths resolve relative to the working directory:

```bash
mkdocs serve -f docs/mkdocs.yml
```

A strict build, the same one CI runs:

```bash
mkdocs build -f docs/mkdocs.yml --strict
```
