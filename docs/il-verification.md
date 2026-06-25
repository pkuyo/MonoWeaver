# IL 验证

`ILMethodVerifier` 用于在 Mono.Cecil 改写后、写出程序集前检查方法体。它关注的是 IL/CFG 合法性，不判断业务逻辑是否正确。

## 基本用法

```csharp
using MonoWeaver.CFG;
using MonoWeaver.Utils;

var verifier = method.Verify(
    VerifyOptions.Full,
    maxErrCount: 20);

foreach (var diagnostic in verifier.Diagnostics)
    Console.WriteLine(diagnostic);

verifier.ThrowIfHasErrors();
```

`ThrowIfHasErrors()` 只会因为 `Error` 或 `Fatal` 抛出；`Warning` 会保留在 `Diagnostics` 中供调用方决定是否阻止输出。

## 与 transform plan 配合

`Transform`、`Observe`、`CallVoid` 和 `CallValue` 返回的 `CallResultPlan` 可以直接使用 `ApplyWithVerify`：

```csharp
match.Value("sum")
     .AfterUse()
     .Transform(callback)
     .ApplyWithVerify(VerifyOptions.Full);
```

`ApplyWithVerify` 会先对 method body、locals、exception handlers 和 `MaxStackSize` 做快照，应用修改后运行 verifier；如果应用或验证失败，会恢复修改前的方法体并重新允许该 plan 再次提交。

## 验证模式

| 模式 | 内容 |
| --- | --- |
| `VerifyOptions.Light` | 指令基础检查与栈高度/平衡，适合快速扫描。 |
| `VerifyOptions.Full` | 指令、local 初始化、栈类型与合并、访问规则等完整检查。 |
| 自定义 flags | 可组合 `Instructions`、`LocalInit`、`StackBalance`、`StackTypes`、`AccessTest`。 |

## 主要检查内容

- opcode、prefix 和 operand 类型是否合法。
- branch/switch/leave 目标及 fall-through 是否有效。
- try/catch/filter/finally 等异常区域边界和嵌套关系。
- 每条路径的栈下溢、栈高度、返回点高度和 `MaxStackSize`。
- 控制流合并点的栈深度与栈类型是否兼容。
- local、构造函数中的 `this` 是否在使用前初始化。
- 调用、字段、泛型约束以及类型/成员访问规则。

## 诊断结果

每条 `CFGDiagnostic` 包含：

- `Type`：例如 `StackUnderflow`、`IncompatibleMergeTypes`、`InvalidBrTarget`。
- `Severity`：`Warning`、`Error` 或 `Fatal`。
- `Message`：简要原因。
- `Context`：相关指令、类型、基本块或异常区域。

直接调用 `ToString()` 会尽量带上 `IL_xxxx` 偏移及上下文，适合日志输出。
