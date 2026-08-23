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

根匹配可以直接改写。只有要选中内部某一段时，才需要 `P.Mark`：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:mark-capture"
```

`Captures` 按用途提供几个入口：

| 入口 | 得到的内容 |
| --- | --- |
| `Value("name")` | 一个可读取或改写的值 |
| `Argument("name")` | 明确捕获的参数 |
| `Local("name")` | 明确捕获的局部变量 |
| `Condition("name")` | 大条件中的一个子条件 |
| `Effect("name")` | 一个无结果的行为 |

用错入口会立即报错——例如把参数捕获当成条件读取。这是刻意的：错误的类型假设在改写阶段才暴露会难查得多。

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

这表示 `tmp * 2` 中的 `tmp` 必须来自 `arg0 + 1`。

### 完全关闭跟随

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:temporary-off"
```

关闭后按局部变量读取本身来匹配。局部变量序号会随游戏版本和编译方式改变，一般不如写出它周围的计算稳定。

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
