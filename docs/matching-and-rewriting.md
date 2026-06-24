# 匹配、插入与重写

MonoWeaver 的核心流程只有五步：

```text
定义 Pattern -> Match -> 选择 Capture -> 选择插入点 -> Rewrite + Verify
```

重点不是“找到某条 opcode”，而是找到一段表达式的具体片段。

## 1. 定义并匹配表达式

```csharp
using MonoWeaver.Cecil;
using MonoWeaver.Patterns;

var pattern = Cil.Value(() =>
    P.Mark("sum", P.Arg<int>(0) + P.Arg<int>(1)));
    /// 等价于匹配
    /// 
    /// ldarg.0
    /// ldarg.1
    /// add
var match = method.Match(pattern).Single();
var sum = match.Value("sum");
```

Pattern 分为三类：

- `Cil.Value(...)`：表达式产生一个值。
- `Cil.Effect(...)`：表达式只产生副作用。
- `Cil.Condition(...)`：Boolean，可能由多条短路分支组成。

`P.Arg`、`P.Local`、`P.Any` 和 `P.Mark` 可以产生 capture。`Single()` 会拒绝零匹配或多匹配，避免把不稳定的候选直接拿去改写。

需要约束临时 local 来源时，可使用 `LocalDefinedBy`：

```csharp
var pattern = Cil.Value(() => P.Local<int>("tmp") * 2)
    .LocalDefinedBy(
        "tmp",
        Cil.Value(() => P.Arg<int>(0) + 1));
/// 等价于匹配
/// ldloc x (x表示任意int类型local变量)
/// ld.i4.2
/// mul
/// 
/// 且这个local变量的来源为
/// ldarg.0
/// ld.i4.1
/// add
```

## 2. 选择正确的插入点

假设源码被编译成：

```csharp
var temp = Build();
return Consume(temp);
```


| 插入点 | 实际含义 |
| --- | --- |
| `match.Before()` | 完整匹配段第一条指令之前。 |
| `match.After()` | 完整 value/effect 匹配段最后一条指令之后。condition 不适用。 |
| `value.BeforeEvaluation()` | 当前捕获表达式开始求值之前。 |
| `value.AfterUse()` | 当前这一次获取之后；上例中可对应调用Consume前的 `ldloc temp`来进行改写，局部修改，一般使用这个。 |
| `value.AfterProducer()` | 原值产生之后；上例中可对应 `Build()`后进行改写，改写结果会先存入 `temp`，从而影响后续所有使用。 |

一般优先使用 `AfterUse()`。只有明确希望修改产生值的位置的所有后续使用时，才选 `AfterProducer()`。

## 3. 插入和 value 重写

### 替换原值

```csharp
var callback = CilMethodSpec.From(
    typeof(Hooks).GetMethod(nameof(Hooks.Rewrite))!);

sum.AfterUse().Transform(callback);

// static int Rewrite(int original)
```

`Transform` 把匹配到的接轨哦作为回调第一个参数，回调必须返回可替代原值的类型。返回值会自动留在原 consumer 需要的位置。

附加参数由插入点显式加载：

```csharp
var rewriteWithContext = CilMethodSpec.From(
    typeof(Hooks).GetMethod(nameof(Hooks.RewriteWithContext))!);

sum.AfterUse().Transform(rewriteWithContext, args =>
    args.Arg(0)
        .Constant(100));

// static int RewriteWithContext(int original, int firstArg, int limit)
```

### 观察但不改变原值

```csharp
sum.AfterUse().Observe(observer);

// static void Observe(int original)
```

`Observe` 会先 `dup`，因此原始值仍交给原执行代码。

### 普通插入

```csharp
match.Before().CallVoid(touch, args => args.Arg(0));

match.Before()
     .CallValue(factory)
     .StoreLocal(method.Body.Variables[0]);

match.Before()
     .CallValue(factory)
     .Store(match.Value("target")); // 自动写回捕获到的 local 或 argument
```

纯 Cecil 的 `CallValue` 在选择 `LeaveOnStack`、`Discard`、`StoreLocal`、`StoreArgument`
或 `Store(capture)` 前不会提交修改。

## 4. 条件重写

短路条件通常不是一个在栈上的 `bool`：

比如如下函数
```csharp
    public string Foo(bool a, bool b)
    {
        if (a && b)
            return "2";
        return "1";
    }
```
对应IL大致是:
```msil
    .method public hidebysig instance string Foo(bool a, bool b) cil managed 
    {
        .maxstack 2
        .locals init (
            [0] bool,
            [1] string
        )
        ldarg.1
        brfalse.s lb_false
        ldarg.2
        brfalse.s lb_false
        ldstr "2"
        ret
lb_false:
        ldstr "1"
        ret
    }
```

```csharp
var conditionPattern = Cil.Condition(() =>
    P.Arg<bool>(0) && P.Arg<bool>(1));

var gate = method.Match(conditionPattern)
                 .Single()
                 .Condition();

gate.Transform(rewriteCondition);

/// 等价于  if (RewriteCondition(a && b))
```

如果只想消费原始 bool，但不改变条件走向，使用 `Observe`：

```csharp
gate.Observe(observer);

// static void Observe(bool original)
```

`Observe` 的 callback 也可以返回非 void 值；返回值默认丢弃，也可以显式存回 local 或 argument：

```csharp
var target = match.Value("target");

gate.Observe(factory, args => args.Arg(0))
    .Store(target);

// static int Factory(bool original, int value)
```

MonoWeaver 会处理该条件分支的 true/false 出口，而不是假设存在唯一的“条件后方”指令。因此 condition 没有普通的 `match.After()`；需要改结果时使用 `Transform`，需要保留结果但观察或存其他值时使用 `Observe`，需要提前执行代码时使用 `BeforeEvaluation()`。

## 5. 插入时做了什么

纯 Cecil 插入会处理几项容易遗漏的工作：

- 插入前先展开 short branch，避免新增指令后跳转距离溢出。
- 若 `Before` 的 anchor 本身是 branch/switch 或 EH 边界目标，则把 incoming target 移到新插入段的第一条指令。
- 不相关的 branch target 不会被改变。
- 按插入调用所需的额外栈槽更新 `MaxStackSize`。
- 回调参数、返回值和 static/open-generic 等限制会先验证，再导入引用和修改方法体。

## 6. MonoMod 适配器

`MonoWeaver.MonoMod` 使用同一个 pattern/match，只把 emission 接到 `ILContext`：

```csharp
using var context = new ILContext(method);
context.Invoke(il =>
{
    var value = il.Match(pattern).Single().Value("sum");
    value.AfterUse(il)
         .Transform((Func<int, int>)Hooks.Rewrite)
         .LeaveOnStack();
});
```
