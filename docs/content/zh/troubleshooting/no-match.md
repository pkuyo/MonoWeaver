# 匹配不到或匹配过多

## 先看诊断

匹配失败时，结果集里带着解释：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:explain-failure"
```

诊断会说明方法中哪些 IL 无法表达、哪次临时变量穿透被拒绝、`LocalDefinedBy` 为什么没满足。先读它，再改 Pattern。

## 匹配不到

按这个顺序查：

1. **匹配种类选对了吗**：值、行为、条件是三种不同的东西，见 [三种匹配种类](../concepts/match-kinds.md)。`if (a && b)` 里的判断是条件，`return a && b;` 里的是值。
2. **方法选对了吗**：`FullName` 拼写、重载、嵌套类型的 `+` 分隔符。
3. **游戏更新了吗**：表达式还在不在，参数顺序变没变。
4. **参数序号对吗**：`P.Arg<T>(0)` 不包含实例方法的 `this`。
5. **常量和重载对吗**：`Select("rare")` 不会命中 `Select(1)`；`1` 和 `1L` 是不同的常量。
6. **左右顺序对吗**：`a - b` 和 `b - a` 不同；比较运算同理。
7. **有没有把运行时对象捕获进 lambda**：外部变量会变成闭包字段读取，匹配不到游戏里的值。用 `P.Arg`、`P.Local`、`P.Any`、字面常量或静态字段。
8. **是不是被临时变量挡住了**：默认会跟随来源唯一的临时变量，但来源有歧义时不会猜。用 `LocalDefinedBy` 明确来源，见 [编译器临时变量](../concepts/captures.md#temporaries)。

## 匹配过多

多匹配比匹配不到更危险——它意味着 Pattern 描述的不是唯一一处逻辑。

不要取第一个，而是给 Pattern 增加上下文：

- 把**外层调用**写进去：不是找 `GetScore()`，而是找 `GetScore() + 10`；
- 把**常量**写进去：`Level * 100` 比 `Level * P.Any<int>("x")` 具体；
- 把**字段或属性**写进去；
- 用 `P.Mark` 标记真正要改的那一小段，外层负责定位。

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Cookbook.cs:pattern-inner-call"
```

这个 Pattern 只命中参加 `+ 10` 的那次调用，`score` 标记再单独指向内部的 `GetScore()`。

如果确实需要改多处，显式遍历并逐个判断，不要依赖顺序：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:list-candidates"
```

## 类型范围过宽

符号写法默认精确匹配，`Assignable()` 会放宽到子类。多匹配时先检查是不是 `Assignable()` 加多了：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:exact-vs-assignable"
```

## 匹配过期

方法一旦被改写，之前拿到的匹配位置可能已经失效。

- 一个 `RewritePlan` 只能成功提交一次；
- 要继续改同一个方法，重新 `Match`；
- 手动用 Cecil 改动方法体之后同样要重新匹配。

## 常见失败速查

| 现象 | 优先检查 |
| --- | --- |
| `No matching expression was found` | 方法是否选对、游戏是否更新、常量和重载是否写对 |
| 找到多个结果 | 给 Pattern 增加外层调用、字段、常量或 `P.Mark` 上下文 |
| 回调参数数量错误 | `Transform`/`Observe` 已自动提供第一个原值参数 |
| 回调返回类型错误 | `Transform` 必须返回能替代原值的类型，条件必须返回 `bool` |
| `match is stale` | 方法已被其他修改改变，重新 `Match` |
| 提交后检查失败 | 见 [检查失败](verification-failures.md) |
