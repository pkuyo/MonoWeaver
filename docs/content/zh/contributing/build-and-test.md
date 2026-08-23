# 构建与测试

## 双 Cecil 构建

整个解决方案共用同一个 `CecilFlavor` 开关，因此两代 Cecil 都能跑全量测试。

| 值 | Mono.Cecil | 产出包 |
| --- | --- | --- |
| `Cecil10`（默认） | `[0.10.0, 0.10.4]` | `MonoWeaver.Cecil10` |
| `Latest` | `[0.11.2,)` | `MonoWeaver` |

```bash
dotnet test MonoWeaver.slnx
```

```bash
dotnet test MonoWeaver.slnx -p:CecilFlavor=Latest
```

两个都要通过。开关定义在仓库根的 `Directory.Build.props`。

## 打包

本地打出两个包到 `artifacts/nupkg/`：

```bash
dotnet pack MonoWeaver/MonoWeaver.csproj -c Release -p:CecilFlavor=Cecil10 -p:Version=0.1.0
```

```bash
dotnet pack MonoWeaver/MonoWeaver.csproj -c Release -p:CecilFlavor=Latest -p:Version=0.1.0
```

## 仓库里的项目

| 项目 | 用途 |
| --- | --- |
| `MonoWeaver` | Mod 实际引用的库 |
| `tests/MonoWeaver.PatternTests` | 匹配、改写、委托和 MonoMod 兼容测试 |
| `tests/MonoWeaver.ILTests` | 修改后检查器的测试 |
| `tests/MonoWeaver.DocSamples` | 文档里所有代码块的来源，只编译不运行 |
| `MonoWeaver.Fuzz` | 自动生成大量情况做压力测试 |
| `benchmarks/MonoWeaver.Benchmarks` | IL 验证吞吐，以及与 MonoMod 的打补丁耗时对比 |

```bash
dotnet run -c Release --project benchmarks/MonoWeaver.Benchmarks -- --verify-only --max-method-us 50000
```

## 文档站

文档源码在 `docs/content/`，`zh/` 是默认语言，`en/` 是翻译。

```bash
python -m venv .venv && .venv/Scripts/activate && pip install -r docs/requirements.txt
```

本地预览（**必须从仓库根运行**，代码片段路径是相对工作目录解析的）：

```bash
mkdocs serve -f docs/mkdocs.yml
```

严格构建，和 CI 一致：

```bash
mkdocs build -f docs/mkdocs.yml --strict
```
