# 匹配与改写：Mod 开发者指南

这篇文档按 Mod 开发流程介绍 MonoWeaver，不要求先会手写 IL。常用流程只有五步：

```text
拿到目标方法 -> 描述游戏逻辑 -> 确认唯一匹配 -> 选择修改方式 -> 提交并检查
```

本文示例中的 `Player`、`GameAudio` 和 `Hooks` 都代表你自己的游戏类型与 Mod 回调。

## 1. 准备目标方法

MonoWeaver 的入口是 Mono.Cecil 的 `MethodDefinition`。

离线修改游戏 DLL 时，可以先读取程序集并找到方法：

```csharp
using System.Linq;
using Mono.Cecil;

using var module = ModuleDefinition.ReadModule("Game.dll");

var method = module.Types
    .Single(type => type.FullName == "Game.Player")
    .Methods.Single(candidate => candidate.Name == "ComputeDamage");
```

在 MonoMod `ILContext` 中，直接使用 `il.Method`：

```csharp
public static void Patch(ILContext il)
{
    MethodDefinition method = il.Method;
    // 后续匹配与改写都针对 method。
}
```

常用命名空间：

```csharp
using System;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
```

## 2. 描述想找的游戏逻辑

MonoWeaver 把目标分成三类：

| 目标 | 什么时候用 | 例子 |
| --- | --- | --- |
| `Cil.Value(...)` | 这段代码会得到一个值 | 伤害计算、字段读取、方法返回值 |
| `Cil.Effect(...)` | 这段代码只执行行为，不留下结果 | 播放音效、发送事件、调用 `void` 方法 |
| `Cil.Condition(...)` | 这段代码决定是否进入某条分支 | `if`、`while`、`&&`、`||` |

### 值

```csharp
var damagePattern = Cil.Value(() =>
    P.Arg<int>(0) + P.Arg<int>(1));
```

### 行为

```csharp
var soundPattern = Cil.Effect(() =>
    GameAudio.Play(P.Arg<int>(0)));
```

### 条件

```csharp
var gatePattern = Cil.Condition(() =>
    P.Arg<bool>(0) && P.Arg<bool>(1));
```

在 lambda 中可以像普通 C# 一样写方法调用、字段读取、运算、数组访问和类型转换。以下占位符用来描述“这里应该是什么”：

| 写法 | 含义 |
| --- | --- |
| `P.This<T>()` | 当前对象 |
| `P.Arg<T>(0)` | 第 0 个显式参数，不包含 `this` |
| `P.Local<T>(0)` | 第 0 个局部变量 |
| `P.Any<T>("name")` | 任意一个兼容的 `T` 值，并命名为 `name` |
| `P.Mark("name", value)` | 给某段内部表达式命名，之后可单独修改 |

`P.Arg`、`P.Local` 和 `P.This` 也有带名字的重载，例如 `P.Arg<int>(0, "amount")`。

## 3. 获取匹配结果

完整结果可以直接改写：

```csharp
var damage = method.Match(damagePattern).Single();

damage.Transform((Func<int, int>)Hooks.ClampDamage)
      .Apply(VerifyOptions.Full);
```

`Single()` 只接受恰好一个结果：

- 没找到：说明游戏版本、目标方法或 Pattern 不一致；
- 找到多处：说明 Pattern 还不够具体；
- 恰好一处：返回可修改的目标。

调试时可以先打印所有候选位置：

```csharp
var candidates = method.Match(damagePattern);

foreach (var candidate in candidates)
    Console.WriteLine($"IL_{candidate.FirstInstruction.Offset:X4}");
```

不要在正式 Mod 中长期使用 `matches[0]`。游戏更新后，第一处可能已经不是原来的逻辑。

## 4. 捕获大表达式中的一小段

如果要修改完整计算，直接使用根匹配即可。只有要选中内部某一段时才需要捕获：

```csharp
var pattern = Cil.Value(() =>
    P.Mark("baseDamage", P.Arg<int>(0)) + P.Arg<int>(1));

var match = method.Match(pattern).Single();
var baseDamage = match.Captures.Value("baseDamage");

baseDamage.Transform((Func<int, int>)Hooks.ClampBaseDamage)
          .Apply(VerifyOptions.Full);
```

`Captures` 提供几种按用途读取的入口：

| 入口 | 得到的内容 |
| --- | --- |
| `Value("name")` | 一个可读取或改写的值 |
| `Argument("name")` | 明确捕获的参数 |
| `Local("name")` | 明确捕获的局部变量 |
| `Condition("name")` | 大条件中的一个子条件 |
| `Effect("name")` | 一个无结果的行为 |

使用错误的入口会立即报错，例如把参数捕获当成条件读取。

## 5. 处理编译器临时变量

Debug 和 Release 编译结果经常不同。某个计算可能直接使用，也可能先保存到临时变量：

```csharp
var temp = BuildDamage();
return temp * 2;
```

MonoWeaver 默认会跟随来源唯一的临时变量，因此同一个 Pattern 往往能兼容两种写法。如果一个读取位置可能来自多次赋值，或者变量被取地址，它不会猜测。

需要明确临时变量来源时，可以加 `LocalDefinedBy`：

```csharp
var pattern = Cil.Value(() =>
        P.Local<int>("tmp") * 2)
    .LocalDefinedBy(
        "tmp",
        Cil.Value(() => P.Arg<int>(0) + 1));
```

这表示：`tmp * 2` 中的 `tmp` 必须来自 `arg0 + 1`。

如果希望完全按局部变量读取来匹配，可以关闭自动跟随：

```csharp
var options = new PatternOptions
{
    TemporaryNormalization = TemporaryNormalization.None
};

var pattern = Cil.Value(
    () => P.Local<int>(0),
    options);
```

## 6. 修改一个值

以下写法都是“分别选择一种”，不要把它们连续应用到同一个旧匹配结果。

### 读取旧值并返回新值

```csharp
damage.Transform((Func<int, int>)Hooks.ClampDamage)
      .Apply(VerifyOptions.Full);

// static int ClampDamage(int original)
```

`Transform` 会把原值作为第一个参数传给回调。回调返回值会交回游戏原逻辑。

### 只观察，不改值

```csharp
damage.Observe((Action<int>)Hooks.LogDamage)
      .Apply(VerifyOptions.Full);

// static void LogDamage(int original)
```

`Observe` 适合日志、统计和触发 Mod 事件。原值仍会交给游戏。

### 完全跳过原计算

```csharp
damage.Replace((Func<int>)Hooks.FixedDamage)
      .Apply(VerifyOptions.Full);

// static int FixedDamage()
```

`Replace` 不会先执行原表达式，回调也不会自动收到原值。它适合完全接管计算。

### 在计算前后追加行为

```csharp
damage.Before((Action)Hooks.OnDamageCalculationStarted)
      .Apply(VerifyOptions.Full);

damage.After((Action)Hooks.OnDamageCalculated)
      .Apply(VerifyOptions.Full);
```

`After` 只表示“计算完成后调用”，不会自动把原值传给回调。需要读取原值时使用 `Observe`。

## 7. 修改一个行为

对 `Cil.Effect(...)` 的匹配，可以在前后追加代码、整体替换或删除：

```csharp
var effect = method.Match(soundPattern).Single();

effect.Before((Action)Hooks.BeforeSound)
      .Apply(VerifyOptions.Full);

effect.After((Action)Hooks.AfterSound)
     .Apply(VerifyOptions.Full);

effect.Replace((Action)Hooks.PlayCustomSound)
      .Apply(VerifyOptions.Full);

effect.Remove()
      .Apply(VerifyOptions.Full);
```

上面同样是四种独立选择。修改同一个方法后，应重新运行匹配再做下一次修改。

如果要匹配“返回值随后被丢弃”的调用，在不引用游戏类型的写法中可以使用 `Cil.Discard(expression)`。

## 8. 修改一个条件

条件可能由多个短路判断组成。MonoWeaver 会把它当成一个整体，不要求你自己找到每条跳转。

```csharp
var condition = method.Match(gatePattern).Single();
```

修改最终真假结果：

```csharp
condition.Transform((Func<bool, bool>)Hooks.ChangeGate)
         .Apply(VerifyOptions.Full);

// static bool ChangeGate(bool original)
```

只记录最终结果：

```csharp
condition.Observe((Action<bool>)Hooks.LogGate)
         .Apply(VerifyOptions.Full);
```

完全替换原判断：

```csharp
condition.Replace((Func<bool>)Hooks.CustomGate)
         .Apply(VerifyOptions.Full);
```

条件还支持 `Before`，但没有 `After`。原因很简单：一个条件可能从不同位置结束，没有唯一的“后面一行”。

有些非常复杂的条件只能识别，不能安全改写。可先检查：

```csharp
if (!condition.CanRewrite)
    Console.WriteLine(condition.RewriteFailureReason);
```

## 9. 给回调补充参数

`Transform` 和 `Observe` 会自动把原值放在回调第一个参数。其余参数由 `args => ...` 按顺序提供：

```csharp
damage.Transform(
          (Func<int, int, int, int>)Hooks.AdjustDamage,
          args => args
              .Arg(0)
              .Constant(999))
      .Apply(VerifyOptions.Full);

// static int AdjustDamage(int original, int firstMethodArg, int limit)
```

常用来源：

| 写法 | 传给回调的内容 |
| --- | --- |
| `args.This()` | 当前对象 |
| `args.Arg(0)` | 目标方法的第 0 个显式参数 |
| `args.Local(localCapture)` | 捕获到的局部变量当前值 |
| `args.Capture(valueCapture)` | 某个已捕获表达式的值 |
| `args.Constant(100)` | 常量 |
| `args.Null(type)` | 空引用 |

MonoWeaver 会检查参数个数、顺序和类型。如果某个捕获值在回调位置还不可用，也会在修改前拒绝。

`Replace` 不自动传入原值，因为原计算会被跳过；需要的参数必须全部在 `args` 中提供。

## 10. 处理回调返回值

`Transform` 和值 `Replace` 的返回值本来就是游戏需要的结果，不需要额外设置。

`Before`、`After` 和 `Observe` 也允许使用有返回值的回调。此时可以决定结果去向：

| 写法 | 用途 |
| --- | --- |
| `Discard()` | 不需要回调结果 |
| `Store(capture)` | 写入捕获到的局部变量或显式参数 |
| `StoreLocal(...)` | 写入指定局部变量 |
| `StoreArgument(...)` | 写入指定显式参数，不能写 `this` |
| `LeaveOnStack()` | 把结果留给后面的代码，属于进阶用法 |

例如，让观察回调保留原游戏值，同时把回调的另一个结果写入已捕获的局部变量：

```csharp
var saved = match.Captures.Local("saved");

damage.Observe((Func<int, int>)Hooks.LogAndNormalize)
      .Store(saved)
      .Apply(VerifyOptions.Full);
```

`Observe` 的非 `void` 结果默认丢弃。`Before` 和 `After` 的非 `void` 结果默认留在当前位置；普通 Mod Hook 通常应显式调用 `Discard()` 或 `Store(...)`，避免后续代码收到多余值。

`LeaveOnStack()` 只应在明确知道后续代码需要这个结果时使用。拿不准时用完整检查，它会发现大部分此类错误。

## 11. 提交修改

所有改写操作都会先生成一个 `RewritePlan`：

```csharp
var plan = damage.Transform((Func<int, int>)Hooks.ClampDamage);
```

此时目标方法尚未改变。推荐这样提交：

```csharp
plan.Apply(VerifyOptions.Full);
```

如果应用或检查失败，方法会恢复到提交前的状态。也可以只调用 `Apply()` 跳过检查，但不建议用于准备发布的 Mod。

注意：

- 一个计划只能成功提交一次；
- 方法被修改后，旧匹配位置可能已经失效；
- 连续修改同一方法时，优先“修改一次、重新匹配、再修改”；
- 最后仍要在真实游戏流程中测试，检查器不知道你的玩法意图。

## 12. 两种 Pattern 写法

### Mod 项目已经引用游戏 DLL

优先使用 lambda，代码最接近原游戏表达式：

```csharp
var pattern = Cil.Value(() =>
    P.Arg<Player>(0).GetScore());
```

### 不引用或不加载游戏类型

用名称和签名描述：

```csharp
var game = CilSymbols.In("GameAssembly");
var player = game.Type("Game.Player");
var enemy = game.Type("Game.Enemy");
var getDamage = player.InstanceMethod(
    "GetDamage",
    CilType.Int32,
    enemy);

var pattern = Cil.Value(
    P.Arg(0, player.Assignable(), "player")
     .Call(getDamage, P.Arg(1, enemy.Assignable()))
     .Mark("damage"));
```

`Assignable()` 表示允许实际类型是该类型的子类。默认不加时会按精确类型匹配。

两种写法只影响 Pattern 的创建方式，`Match`、`Captures` 和改写 API 完全相同。

## 13. 回调与 MonoMod

回调可以使用三种来源：

- 强类型委托，例如 `Func<int, int>`，最适合运行时 Mod；
- `CilMethodSpec`，适合用方法签名描述静态回调；
- Cecil `MethodReference`，适合已经拿到方法引用的离线修改器。

静态委托会变成对该静态方法的直接调用。请确保包含回调的 Mod DLL 能被游戏加载。

实例委托、闭包和多播委托会引用当前进程里的对象，因此只用于运行时 Hook，不要把它们写进 DLL 后拿到另一个进程运行。

在 `ILContext` 中正常调用 `Apply` 时，MonoWeaver 会临时处理 MonoMod 的跳转标签，完成后再恢复。只有在计划之外手动混用 Cecil 修改时，才需要自己转换：

```csharp
using MonoWeaver.Utils;

CecilHelper.BranchLabelsToTarget(il);
try
{
    // 自己的 Cecil 分析或修改。
}
finally
{
    CecilHelper.BranchTargetsToLabels(il);
}
```

## 14. 进阶：查看匹配位置

内置的 `Before`、`After`、`Transform`、`Observe` 和 `Replace` 已经会选择合适位置。只有做日志、调试器或自定义改写时，通常才需要以下属性：

| 属性 | 可理解为 |
| --- | --- |
| `FirstInstruction` | 当前匹配从哪里开始 |
| `ResultInstruction` | 当前这次取值在哪里完成 |
| `DefinitionFirstInstruction` | 原值计算从哪里开始 |
| `DefinitionInstruction` | 原值最初在哪里产生 |
| `ConsumerInstruction` | 能确定时，下一步在哪里使用这个值 |
| effect/condition 的 `LastInstruction` | 当前匹配覆盖到哪里结束 |

临时变量存在时，“原值产生位置”和“当前读取位置”可能不同。普通 Mod Hook 不要据此手动选择插入点，优先使用语义明确的高层操作。

手动修改指令后，之前的匹配应视为过期并重新获取。

## 15. 常见失败

| 现象 | 优先检查 |
| --- | --- |
| `No matching expression was found` | 方法是否选对、游戏是否更新、常量和重载是否写对 |
| 找到多个结果 | 给 Pattern 增加外层调用、字段、常量或 `P.Mark` 上下文 |
| 回调参数数量错误 | `Transform/Observe` 已自动提供第一个原值参数 |
| 回调返回类型错误 | `Transform` 必须返回能替代原值的类型，条件必须返回 `bool` |
| `match is stale` | 方法已被其他修改改变，重新 Match |
| 提交后检查失败 | 查看 [修改后检查](il-verification.md) 中的错误对照 |
