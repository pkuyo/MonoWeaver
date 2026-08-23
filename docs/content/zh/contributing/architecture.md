# 代码结构

`MonoWeaver/` 下三个目录，各自负责一段独立的职责。改动跨目录时，通常说明抽象层次搞混了。

```text
Patterns/   描述目标 → 在方法里找到它 → 改写它
CFG/        把方法体变成基本块图 → 逐块推演执行栈 → 报告问题
Utils/      Cecil 的类型系统、指令辅助、委托发射，以及两个容器
```

## Patterns/ — 匹配与改写

一次 `Match` 的完整链路：

```text
Cil.Value(lambda)
      ↓  PatternExpressionParser：表达式树 → PatternNode
   Pattern（ValuePattern / EffectPattern / ConditionPattern）
      ↓
method 的 IL
      ↓  MethodModel：只重建 pattern 候选需要的表达式依赖
   符号化的方法模型
      ↓  PatternMatcher：把 PatternNode 对到模型节点上
   CilMatchSet<ValueMatch> / …
      ↓  PatternTransforms：选择操作
   RewritePlan
      ↓  Apply
   修改后的方法体（失败则回滚）
```

| 文件 | 职责 |
| --- | --- |
| `Pattern.cs` | `Cil`、`P` 两个入口，以及三种 Pattern 类型和 `PatternOptions` |
| `PatternExpressionParser.cs` | 把 C# 表达式树翻译成 `PatternNode` 树 |
| `PatternNodes.cs` | Pattern 侧的节点：参数、局部、字段、调用、二元运算、`Mark`… |
| `CilExpr.cs` / `CilSymbols.cs` / `CilMetadataSpecs.cs` | 不引用游戏类型时的符号写法 |
| `MethodModel.cs` | 目标方法的小型符号模型。**它不反编译整个方法**，只保留候选位置需要的表达式依赖 |
| `TargetExpressionNodes.cs` | 目标侧的节点，包括取地址这类 C# 里不可见的形态 |
| `LocalDefinitionIndex.cs` | 某个 `ld` 位置上局部变量的可能来源集合，用来消歧临时变量 |
| `PatternMatcher.cs` | 匹配主循环，同时判定条件是否可安全改写 |
| `CilMatchResults.cs` | `CilMatchSet`、各种 match 与 capture 类型 |
| `PatternTransforms.cs` | 面向用户的改写扩展方法 |
| `PatternTransformImplementation.cs` | 上面那些操作的实际 IL 生成 |
| `RewritePlan.cs` | 计划、回调结果去向、`Apply` 与回滚 |
| `MatchDiagnostics.cs` | 匹配失败时的解释 |

设计上的两个关键取舍：

- **`MethodModel` 是按需的**，不是完整反编译器。它只在候选位置周围重建依赖，所以方法再大也不会整体展开。
- **有歧义就不匹配**。`LocalDefinitionIndex` 发现一个读取位置可能来自多次赋值，或变量被取过地址，就放弃这个候选，而不是猜一个来源。这是“宁可匹配不到，也不要改错地方”的直接体现。

## CFG/ — 修改后检查

| 文件 | 职责 |
| --- | --- |
| `ILBasicBlockGraphBuilder.cs` | 方法体 → 基本块图，含异常处理区域 |
| `StackType.cs` | 执行栈上的值类型模型，以及合并规则 |
| `ILMethodVerifier.cs` | 检查器主体、`VerifyOptions`、异常处理区域模型 |
| `ILMethodVerifier.Verify.cs` | 逐指令推演每条路径上的栈 |
| `ILMethodVerifier.Diagnostic.cs` | 诊断类型、去重、`CfgVerifyException` |

检查器不关心语义，只回答一个问题：**运行时加载这个方法时会不会拒绝它**。诊断清单见 [检查失败](../troubleshooting/verification-failures.md)。

## Utils/ — Cecil 适配层

| 文件 | 职责 |
| --- | --- |
| `CecilTypeSystem*.cs` | 类型判断：`IsSameWith`、`IsAssignableTo`、`CanAccess`、泛型约束… |
| `CecilHelper*.cs` | `Verify` 扩展、符号处理、MonoMod 标签与 Cecil 指令的互转 |
| `CecilInstructionHelpers.cs` | 指令构造与操作数处理 |
| `CecilDelegateEmission.cs` | 把运行时委托变成可发射的调用（静态委托降级为直接调用） |
| `FixSizeDictionary.cs` / `ListStack.cs` | 匹配与验证热路径上的容器，避免频繁分配 |
| `CecilCompat.cs` | 抹平 Cecil `0.10` 与 `0.11+` 的 API 差异 |

`CecilCompat.cs` 是双 flavor 构建能成立的原因。新代码如果要用某个 Cecil API，先确认两代都有；只有一代有的，加到这里做适配，不要在业务代码里写 `#if CECIL_010`。

## 加新功能时

- **新的 Pattern 语法**：`PatternExpressionParser` 加解析、`PatternNodes` 加节点、`PatternMatcher` 加匹配、`MethodModel` 可能要补目标侧形态。
- **新的改写操作**：`PatternTransforms` 加面向用户的重载，`PatternTransformImplementation` 写 IL 生成，然后确认 `RewritePlan` 的结果去向语义说得通。
- **新的诊断**：`ILMethodVerifier.Diagnostic.cs` 加类型，并在 [检查失败](../troubleshooting/verification-failures.md) 的对照表里补一行。
- 任何一项都要在两个 `CecilFlavor` 下跑测试，见 [构建与测试](build-and-test.md)。
