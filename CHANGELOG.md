# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Fixed

- `Apply(VerifyOptions.Full)` inside a MonoMod `ILHook` no longer fails with `AssemblyResolutionException`. When a plan is bound to the current process (runtime delegate callback, or a method hosted in an `ILContext`), the verifier resolves references against the assemblies loaded in the process via the new `RuntimeAssemblyResolver`; offline weaving keeps using the module's own resolver. `RewritePlan.Apply` and `Verify` also accept an explicit `IAssemblyResolver`.
- Unresolvable member references are reported as `ResolveFailed` diagnostics instead of throwing.
- Method bodies with `MaxStackSize == 0` (in-memory Cecil bodies, MonoMod DMD copies) are normalized to 65535 before rewriting, so verification no longer reports spurious `StackOverflow` errors. Cecil recomputes the value on write.

### Changed

- `MonoWeaver.Cecil10` now ships `netstandard2.0` only. The `net46` asset was removed.

## [0.1.1] - 2026-08-26

### Added

- Added matching support for lambda parameters.
- Added internal integration support for the standalone HookPattern generator.

### Fixed

- Condition patterns using `x == null`, `x != null`, `x == 0`, or `x != 0` now match equivalent `brtrue`/`brfalse` IL truthiness branches.

### Changed

- Synchronized IL verifier test cases with the upstream .NET runtime cases.
- Compatibility tests now validate both supported dependency pairs: Mono.Cecil 0.10.4 with MonoMod 19.9.1.6, and Mono.Cecil 0.11.6 with MonoMod 22.7.31.1.
- CI is split by validation responsibility, with job names stating the compatibility target and purpose.

## [0.1.0] - 2026-08-23

### Added

- Initial NuGet release of `MonoWeaver` and `MonoWeaver.Cecil10`.

[0.1.1]: https://github.com/pkuyo/MonoWeaver/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/pkuyo/MonoWeaver/releases/tag/v0.1.0
