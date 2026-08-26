# Changelog

All notable changes to this project are documented in this file.

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
