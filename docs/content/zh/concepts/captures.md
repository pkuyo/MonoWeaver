# 匹配结果与捕获

## 唯一匹配

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:single"
```

`Single()` 只接受恰好一个结果：

- **没找到**：方法选错了、游戏更新了，或者 Pattern 和实际代码对不上；
- **找到多处**：Pattern 还不够具体；
- **恰好一处**：返回可改写的目标。

调试时可以先列出所有候选位置：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:list-candidates"
```

匹配失败时，结果集里还带着诊断信息，解释为什么某个位置被跳过：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:explain-failure"
```

诊断的存在不代表失败——它只在结果不符合预期时用来解释原因。

## 捕获内部的一段

根匹配可以直接改写。要改的是某个参数时，lambda 参数本身就是捕获，按参数名取回：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:mark-capture"
```

要改的是复合子表达式时，把那一段声明成独立的 `Cil.Value`/`Cil.Condition` 片段直接写进表达式；局部变量用 `Cil.Local`。这些捕获按对象取回，返回类型由声明的对象决定：

| 索引 | 得到的内容 |
| --- | --- |
| `match.Arg("参数名")` / `match.This()` | lambda 参数对应的实参（`ArgumentCapture`） |
| `match.Local("参数名")` | `CilLocal<T>` 类型的 lambda 参数（`LocalCapture`） |
| `match[Cil.Any<T>() 对象]` | 一个可读取或改写的值（`ValueCapture`） |
| `match[Cil.Arg<T>() / Cil.This<T>() 对象]` | 捕获的参数（`ArgumentCapture`） |
| `match[Cil.Local<T>() 对象]` | 捕获的局部变量（`LocalCapture`） |
| `match[内嵌的 ValuePattern]` | 被选中的值片段（`ValueCapture`） |
| `match[内嵌的 ConditionPattern]` | 大条件中的一个子条件（`ConditionCapture`） |

拿错对象会立即抛 `KeyNotFoundException`——捕获身份就是对象身份，没有字符串名可写错。

## 编译器临时变量 { #temporaries }

Debug 和 Release 编译结果经常不同。同一段源码，某个中间值可能直接留在栈上，也可能先存进一个临时变量：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-double-damage"
```

MonoWeaver 默认会**跟随来源唯一的临时变量**，因此同一个 Pattern 往往能同时兼容两种编译结果：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-across-local"
```

它保持保守。如果一个读取位置可能来自多次赋值，或者变量被取过地址，它不会猜——这个位置就不会成为匹配。

### 明确指定来源

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:local-defined-by"
```

这表示 `tmp * 2` 中的 `tmp` 必须来自 `damage + 1`；`tmp` 对象之后可以在多个 pattern 里复用，含义不变。

### 完全关闭跟随

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:temporary-off"
```

关闭后按局部变量读取本身来匹配。局部变量序号会随游戏版本和编译方式改变，一般不如写出它周围的计算稳定。

## 在某处之后匹配

结果集可以按 IL 位置筛选：`After(x)` 只留起点在 x 之后的匹配，`Before(x)` 只留终点在 x 之前的，`Between(a, b)` 两者都要。x 可以是一条指令，也可以是之前的匹配或捕获（以它覆盖的整段为界）：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:match-after"
```

这是 ILCursor "从这里往后找下一个"的对应写法，区别是 `Single()` 仍然要求筛选后唯一，不会静默取第一个。位置按 IL 顺序比较，不考虑控制流；锚点必须在当前方法体里——方法改写后旧锚点若已被删除会直接报错，重新匹配即可。

## 进阶：匹配位置

内置的改写操作已经会选择合适的插入点。只有做日志、调试器或自定义改写时才需要这些属性：

| 属性 | 可理解为 |
| --- | --- |
| `FirstInstruction` | 当前匹配从哪里开始 |
| `ResultInstruction` | 当前这次取值在哪里完成 |
| `DefinitionFirstInstruction` | 原值计算从哪里开始 |
| `DefinitionInstruction` | 原值最初在哪里产生 |
| `ConsumerInstruction` | 能确定时，下一步在哪里使用这个值 |
| `LastInstruction`（行为/条件） | 当前匹配覆盖到哪里结束 |
| `IsAddressBacked` | 该位置是否经由取地址指令到达 |

存在临时变量时，“原值产生位置”和“当前读取位置”会不同。

!!! warning "手动改完指令后要重新匹配"
    直接用 Cecil 改动方法体之后，之前拿到的匹配应视为过期。
