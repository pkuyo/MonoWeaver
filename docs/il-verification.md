# 修改后检查：在进游戏前发现问题

Mod 改写代码后，最麻烦的问题往往不是“效果不对”，而是游戏在加载方法时直接报错或崩溃。MonoWeaver 的检查器会在保存 DLL 或继续运行前，先检查方法是否仍能被 .NET/Mono 正常接受。

它能发现跳转失效、回调参数不匹配、某条路径多出或少了一个值、局部变量未赋值等问题；它不知道“伤害应该是多少”，因此不能代替实际游戏测试。

## 推荐用法

对 MonoWeaver 生成的改写，直接在提交时做完整检查：

```csharp
damage.Transform((Func<int, int>)Hooks.ClampDamage)
      .Apply(VerifyOptions.Full);
```

这一步会：

1. 保存修改前的方法状态；
2. 应用改写；
3. 运行完整检查；
4. 成功时保留修改；
5. 失败时恢复原方法并抛出错误。

因此，准备发布的 Mod 应优先使用 `Apply(VerifyOptions.Full)`，而不是只调用 `Apply()`。

## 检查手动修改的方法

如果方法还经过了自己的 Cecil 修改，可以单独检查：

```csharp
using MonoWeaver.CFG;
using MonoWeaver.Utils;

var report = method.Verify(
    VerifyOptions.Full,
    maxErrCount: 20);

foreach (var item in report.Diagnostics)
    Console.WriteLine(item);

report.ThrowIfHasErrors();
```

`ThrowIfHasErrors()` 会在出现 `Error` 或 `Fatal` 时抛出；`Warning` 会保留在 `Diagnostics` 中，由 Mod 自己决定是否阻止加载。

如果想读取 `Apply(VerifyOptions.Full)` 抛出的详细结果，可以捕获：

```csharp
try
{
    plan.Apply(VerifyOptions.Full);
}
catch (ILMethodVerifier.CfgVerifyException error)
{
    foreach (var item in error.Diagnostics)
        Console.Error.WriteLine(item);

    throw;
}
```

此时方法已经由 `Apply` 恢复，不需要自己撤销改写。

## Light 还是 Full

| 模式 | 会检查什么 | 建议用途 |
| --- | --- | --- |
| `VerifyOptions.Light` | 指令基本格式、每条执行路径上的值数量 | 开发时频繁快速检查 |
| `VerifyOptions.Full` | Light 的内容，加上值类型、变量初始化、成员访问等 | 发布前、自动测试、未知游戏版本 |

一般 Mod 方法并不大，优先使用 `Full`。只有确认完整检查成为性能瓶颈时，再考虑 `Light`。

也可以组合单项：

```csharp
var options =
    VerifyOptions.Instructions |
    VerifyOptions.StackBalance |
    VerifyOptions.LocalInit;

method.Verify(options).ThrowIfHasErrors();
```

## 它具体能拦住什么

用 Mod 开发中的常见现象来理解：

- 回调吃掉了原值，却没有返回替代值；
- `Before` 或 `After` 的回调多留下了一个无人使用的返回值；
- `if` 的两条路径到达同一位置时，带来的值数量或类型不同；
- 删除代码后，某个跳转仍指向已删除位置；
- 从 `try/catch/finally` 中用了不允许的方式跳出或跳入；
- 局部变量在某条路径上还没有赋值就被读取；
- 调用参数、返回值或字段类型不兼容；
- Mod 代码尝试直接访问当前上下文无权访问的私有成员；
- 方法需要的类型或成员无法从游戏依赖中解析。

## 常见诊断对照

日志中会出现一个英文类型名。先按下面方向排查，不必先理解全部底层细节。

| 诊断 | 常见原因 | 优先处理 |
| --- | --- | --- |
| `StackUnderflow` | 前面的代码少提供了一个值 | 检查 `Transform` 是否返回结果，或是否错误调用了 `Discard()` |
| `InvalidExitStackHeight` | 方法返回时还多了值，或缺少返回值 | 检查有返回值的 `Before/After` 是否应 `Discard()` 或 `Store(...)` |
| `IncompatibleMergeDepth` | 两条分支到达同一处时，值数量不同 | 检查条件两边是否都执行了相同去向的回调结果处理 |
| `IncompatibleMergeTypes` | 两条分支带来的值类型不同 | 确认回调返回类型与游戏原值兼容 |
| `InvalidBrTarget` | 跳转目标已被删除或不属于该方法 | 不要直接移除被跳转的位置；优先用 `Replace` 或 `Remove` |
| `UninitializedLocal` | 某条路径先读取、后赋值 | 检查 `StoreLocal` 的位置和所有分支 |
| `TypeMismatch` | 参数、返回值、字段或转换类型不兼容 | 对照回调签名和捕获值类型 |
| `MethodAccess` / `FieldAccess` / `TypeAccess` | 修改后的代码不能直接访问目标成员 | 改用可访问的 Mod 桥接方法，或调整回调所在类型 |
| `ResolveFailed` | 找不到游戏或 Mod 依赖 | 给 Cecil 的解析器补齐游戏目录中的依赖，并检查版本冲突 |
| `ExceptionHandlerInvalid` | 手动修改破坏了 `try/catch/finally` 范围 | 避免手动移动边界；缩小替换范围后重新匹配 |

`ToString()` 通常会带上 `IL_xxxx` 位置。把这个位置与反编译器或自己的指令日志对照，通常能快速找到是哪次改写造成的。

## 回调结果最容易出错的地方

### Transform

`Transform` 接收原值并返回新值。返回类型必须能放回游戏原位置：

```csharp
// 正确：int -> int
static int ClampDamage(int original) => Math.Max(original, 0);
```

不要对正常的 `Transform` 结果调用 `Discard()`，否则游戏原代码会拿不到需要的值。

### Observe

`Observe` 会保留原游戏值。回调如果还返回另一个值，默认会丢弃；需要时可以保存：

```csharp
damage.Observe((Func<int, int>)Hooks.RecordAndReturnId)
      .StoreLocal(logIdLocal)
      .Apply(VerifyOptions.Full);
```

### Before / After

这两个操作不会自动消费游戏原值。有返回值的回调默认把结果留在当前位置。多数日志或通知回调不需要它，应明确处理：

```csharp
damage.Before((Func<int>)Hooks.CreateTraceId)
      .Discard()
      .Apply(VerifyOptions.Full);
```

更简单的做法是让这类回调直接返回 `void`。

## 检查失败后的安全处理

- 不要在捕获异常后继续写出当前方法，先记录诊断并跳过该 Hook；
- 如果这是可选功能，让 Mod 输出清楚的游戏版本、目标方法和 Pattern 名称；
- 如果这是启动必需功能，停止加载比带着损坏的方法继续运行更安全；
- 重新匹配后再尝试其他方案，不要继续使用失败前保存的旧匹配；
- 对每个支持的游戏版本保存至少一个自动测试样本。

## 它不会检查什么

即使 `Full` 通过，也仍可能存在：

- 匹配到了错误但格式合法的位置；
- 伤害、概率、时间单位等业务含义写错；
- 回调抛出异常；
- 多个 Mod 修改同一方法后的顺序冲突；
- 游戏更新改变了调用时机，但保留了相似表达式；
- 只在特定存档、地图、联机状态下出现的问题。

建议把完整检查、唯一匹配、日志和真实游戏测试一起使用，而不是把任意一项当成全部保障。
