# Pattern 代表性示例：游戏函数与匹配写法

这页不枚举所有语法，只保留 Mod 开发中最常遇到的几类目标。

每个例子都按同一顺序给出：目标游戏函数、对应 Pattern、实际命中的内容。示例里的 `Player`、`Reward`、`GameAudio` 等只是占位名，使用时换成目标游戏里的真实类型和函数。

默认已有：

```csharp
using MonoWeaver.Cecil;
using MonoWeaver.Patterns;
```

创建 Pattern 后，通常这样查找：

```csharp
var match = method.Match(pattern).Single();
```

只有确定目标函数中恰好有一处命中时才用 `Single()`。如果找到多处，应把周围的计算或调用也写进 Pattern。

## 先选匹配种类

| 想找什么 | 使用 |
| --- | --- |
| 会产生结果的计算、调用或读取 | `Cil.Value(...)` |
| 没有结果的调用，或结果被直接丢弃的调用 | `Cil.Effect(...)` |
| 决定 `if` 等分支走向的条件 | `Cil.Condition(...)` |

## 1. 匹配一段计算

目标函数：

```csharp
public static int CalculateDamage(int baseDamage, int bonus)
    => baseDamage + bonus;
```

对应 Pattern：

```csharp
var pattern = Cil.Value(() =>
    P.Arg<int>(0) + P.Arg<int>(1));
```

命中说明：匹配完整的 `baseDamage + bonus`。`P.Arg<int>(0)` 和 `P.Arg<int>(1)` 分别代表前两个参数；参数序号不包含实例方法的 `this`。

## 2. 在更大的计算中找到准确调用

目标函数：

```csharp
public static int ReadSelectedScore(Player player)
{
    _ = player.GetScore().ToString();
    return player.GetScore() + 10;
}
```

对应 Pattern：

```csharp
var pattern = Cil.Value(() =>
    P.Mark(
        "score",
        P.Arg<Player>(0).GetScore())
    + 10);
```

命中说明：完整 Pattern 只命中参加 `+ 10` 的那次 `GetScore()`，不会误选上面被丢弃的调用。`score` 标记则单独指向内部的 `GetScore()`：

```csharp
var score = method.Match(pattern)
    .Single()
    .Captures.Value("score");
```

## 3. 匹配只执行行为的调用

目标函数：

```csharp
public static void PlayHitSound(Player player)
    => GameAudio.Play(player.HitSound);
```

对应 Pattern：

```csharp
var pattern = Cil.Effect(() =>
    GameAudio.Play(
        P.Arg<Player>(0).HitSound));
```

命中说明：匹配整个 `GameAudio.Play(...)` 调用，包括传入的 `HitSound`。这种结果适合在调用前后插入 Mod 回调，或替换、移除原调用。

如果方法有返回值，但目标代码直接丢弃了结果，也使用 `Cil.Effect(...)`。

## 4. 匹配 if 条件

目标函数：

```csharp
public static bool TryOpen(Player player)
{
    if (player.HasKey && !player.IsDead)
        return true;

    return false;
}
```

对应 Pattern：

```csharp
var pattern = Cil.Condition(() =>
    P.Arg<Player>(0).HasKey
    && !P.Arg<Player>(0).IsDead);
```

命中说明：匹配决定 `if` 走向的完整条件，包括 `&&` 的短路判断。它不是在搜索函数最后返回的普通 `bool` 值。

## 5. 匹配构造函数、常量和属性

目标函数：

```csharp
public static Reward CreateReward(Player player)
    => new Reward("rare", player.Level * 100);
```

对应 Pattern：

```csharp
var pattern = Cil.Value(() =>
    new Reward(
        "rare",
        P.Mark(
            "amount",
            P.Arg<Player>(0).Level * 100)));
```

命中说明：匹配准确的 `Reward(string, int)` 构造函数、`"rare"` 常量和 `Level * 100`。`amount` 标记只指向奖励数量的计算，方便单独修改它。

方法重载也按参数列表区分。例如 Pattern 中写 `Select("rare")`，不会命中 `Select(1)`。

## 6. 跨过中间局部变量

目标函数：

```csharp
public static int DoubleDamage(int damage)
{
    var adjusted = damage + 1;
    return adjusted * 2;
}
```

对应 Pattern：

```csharp
var pattern = Cil.Value(() =>
    P.Mark(
        "adjusted",
        P.Arg<int>(0) + 1)
    * 2);
```

命中说明：即使 `damage + 1` 先存进了 `adjusted`，只要这个值的来源明确，MonoWeaver 默认仍会命中完整的 `(damage + 1) * 2`。`adjusted` 标记对应当前被乘法使用的那次值。

如果你确实只想找某个局部变量，可以写 `P.Local<int>(0)`；但局部变量序号可能随游戏版本或编译方式改变，通常不如写出它周围的计算稳定。

## 7. 匹配数组读取

目标函数：

```csharp
public static int ReadNext(int[] values, int index)
    => values[index + 1];
```

对应 Pattern：

```csharp
var pattern = Cil.Value(() =>
    P.Arg<int[]>(0)[P.Arg<int>(1) + 1]);
```

命中说明：匹配完整的数组读取，包括数组参数和 `index + 1`。数组长度可直接写 `.Length`；数组写入则使用 `P.StoreElement(...)`。

## 8. 不引用游戏 DLL 时

目标函数：

```csharp
public static int ReadScore(Player player)
    => player.GetScore();
```

先用名称描述游戏类型和函数：

```csharp
var game = CilSymbols.In("GameAssembly");
var player = game.Type("Game.Player");
var getScore = player.InstanceMethod(
    "GetScore",
    CilType.Int32);
```

对应 Pattern：

```csharp
var pattern = Cil.Value(
    P.Arg(0, player.Assignable(), "player")
     .Call(getScore)
     .Mark("score"));
```

命中说明：它和下面的 lambda Pattern 寻找同一处调用，只是不需要在 Mod 项目中引用游戏 DLL：

```csharp
var pattern = Cil.Value(() =>
    P.Mark(
        "score",
        P.Arg<Player>(0).GetScore()));
```

## 常用占位写法

这些写法可以直接放进上面的 Pattern，不必为每一种再记一套规则：

| 需要表示的内容 | 写法 |
| --- | --- |
| 当前实例 | `P.This<Player>()` |
| 第 0 个参数 | `P.Arg<Player>(0)` |
| 任意位置的 `Player` 参数 | `P.Arg<Player>("player")` |
| 第 0 个局部变量 | `P.Local<int>(0)` |
| 任意一段产生 `int` 的内容 | `P.Any<int>("value")` |
| 保存内部命中位置 | `P.Mark("name", expression)` |
| 空引用 | `default(object)` |

字段、属性、方法调用、`new`、数字转换、加减乘除和比较，可以按反编译器中看到的 C# 直接写。类型、参数顺序和方法重载需要与目标代码对应。

## 找不到或找到太多时

- 先确认用了正确的 `Cil.Value`、`Cil.Effect` 或 `Cil.Condition`。
- 找到多处时，把外层调用、常量或计算一并写入 Pattern，不要默认取第一处。
- 找不到时，检查参数序号、类型、方法重载和左右顺序。
- 不要把运行时对象直接捕获进 lambda；用 `P.Arg`、`P.Local`、`P.Any`、常量或静态字段表示它。
- 方法一旦被改写，应重新匹配，再进行下一次修改。

找到目标后如何插入、观察或替换，请继续阅读 [匹配与改写](matching-and-rewriting.md)。
