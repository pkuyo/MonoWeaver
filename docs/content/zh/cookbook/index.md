# 按游戏代码查写法

这页不枚举所有语法，只保留 Mod 开发中最常遇到的几类目标。

每个例子按同一顺序给出：**目标游戏函数 → 对应 Pattern → 实际命中的内容**。示例里的 `Player`、`Reward`、`GameAudio` 只是占位名，使用时换成目标游戏里的真实类型和函数。

默认已有：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:cookbook-usings"
```

创建 Pattern 后，通常这样查找：

```csharp
var match = method.Match(pattern).Single();
```

只有确定目标函数中恰好有一处命中时才用 `Single()`。找到多处时，应把周围的计算或调用也写进 Pattern。

先按 [三种匹配种类](../concepts/match-kinds.md) 确定用 `Cil.Value`、`Cil.Effect` 还是 `Cil.Condition`。

## 1. 匹配一段计算

=== "目标函数"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-calculate-damage"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-calculation"
    ```

命中完整的 `baseDamage + bonus`。`P.Arg<int>(0)` 和 `P.Arg<int>(1)` 分别代表前两个参数；参数序号不包含实例方法的 `this`。

## 2. 在更大的计算中找到准确调用

=== "目标函数"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-read-selected-score"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-inner-call"
    ```

完整 Pattern 只命中参加 `+ 10` 的那次 `GetScore()`，不会误选上面被丢弃的调用。`score` 标记则单独指向内部的 `GetScore()`：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-inner-call-capture"
```

## 3. 匹配只执行行为的调用

=== "目标函数"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-play-hit-sound"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-effect-call"
    ```

匹配整个 `GameAudio.Play(...)` 调用，包括传入的 `HitSound`。这种结果适合在调用前后插入回调，或替换、移除原调用。

如果方法有返回值，但目标代码直接丢弃了结果，也用 `Cil.Effect(...)`。

## 4. 匹配 if 条件

=== "目标函数"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-try-open"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-if-condition"
    ```

匹配决定 `if` 走向的完整条件，包括 `&&` 的短路判断。它不是在搜索函数最后返回的那个普通 `bool` 值。

## 5. 匹配构造函数、常量和属性

=== "目标函数"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-create-reward"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-constructor"
    ```

匹配准确的 `Reward(string, int)` 构造函数、`"rare"` 常量和 `Level * 100`。`amount` 标记只指向奖励数量的计算，方便单独修改它。

方法重载也按参数列表区分：Pattern 中写 `Select("rare")` 不会命中 `Select(1)`。

## 6. 跨过中间局部变量

=== "目标函数"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-double-damage"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-across-local"
    ```

即使 `damage + 1` 先存进了 `adjusted`，只要这个值的来源明确，默认仍会命中完整的 `(damage + 1) * 2`。`adjusted` 标记对应当前被乘法使用的那次值。

如果确实只想找某个局部变量，可以写 `P.Local<int>(0)`；但局部变量序号会随游戏版本或编译方式改变，通常不如写出它周围的计算稳定。详见 [编译器临时变量](../concepts/captures.md#temporaries)。

## 7. 匹配数组读取

=== "目标函数"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-read-next"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-array-read"
    ```

匹配完整的数组读取，包括数组参数和 `index + 1`。数组长度直接写 `.Length`；数组写入用 `P.StoreElement(...)`。

## 8. 不引用游戏 DLL 时

=== "目标函数"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/GameFixtures.cs:target-read-score"
    ```

=== "Pattern"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-symbols-decl"

    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-symbols"
    ```

=== "等价的 lambda 写法"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-lambda-equivalent"
    ```

两者寻找同一处调用，只是符号写法不需要在 Mod 项目中引用游戏 DLL。`Match`、`Captures` 和改写 API 完全相同。

## 常用占位写法

这些写法可以直接放进上面的 Pattern：

| 需要表示的内容 | 写法 |
| --- | --- |
| 当前实例 | `P.This<Player>()` |
| 第 0 个参数 | `P.Arg<Player>(0)` |
| 任意位置的 `Player` 参数 | `P.Arg<Player>("player")` |
| 第 0 个局部变量 | `P.Local<int>(0)` |
| 任意一段产生 `int` 的内容 | `P.Any<int>("value")` |
| 保存内部命中位置 | `P.Mark("name", expression)` |
| 空引用 | `default(object)` |

字段、属性、方法调用、`new`、数字转换、加减乘除和比较，可以按反编译器里看到的 C# 直接写。类型、参数顺序和方法重载需要与目标代码对应。

完整清单见 [Pattern 写法](../reference/pattern-dsl.md)。找不到或找到太多时见 [匹配不到或匹配过多](../troubleshooting/no-match.md)。
