# Cecil 类型判断：Mod 开发中的实用写法

大多数 Mod 只需要 Pattern 和改写 API，MonoWeaver 会自动检查回调参数与返回值。只有在自己查找方法、字段，或生成额外 Cecil 代码时，才经常需要本页的类型工具。

先引入：

```csharp
using MonoWeaver.Utils;
```

## 最常用的三个判断

```csharp
bool exactlySame = actual.IsSameWith(expected);
bool canAssign = actual.IsAssignableTo(expected);
bool sameMeaning = expected.IsAssignableFrom(actual);
```

可以这样理解：

| 方法 | 在 Mod 中回答的问题 |
| --- | --- |
| `a.IsSameWith(b)` | 这两个是否就是同一个类型 |
| `source.IsAssignableTo(target)` | `source` 的值能否传给需要 `target` 的回调参数或变量 |
| `target.IsAssignableFrom(source)` | 与上一项相同，只是阅读方向相反 |

记住一行普通 C# 赋值即可：

```csharp
Target value = source;

// source.IsAssignableTo(Target)
// Target.IsAssignableFrom(source)
```

例如，`EliteEnemy` 继承 `Enemy`：

- `EliteEnemy.IsSameWith(Enemy)` 是 `false`；
- `EliteEnemy.IsAssignableTo(Enemy)` 是 `true`；
- `Enemy.IsAssignableTo(EliteEnemy)` 通常是 `false`。

## 什么时候必须精确相同

查找指定字段、方法重载或序列化成员时，通常应使用 `IsSameWith`：

```csharp
var overload = type.Methods.Single(method =>
    method.Name == "SetTarget" &&
    method.Parameters.Count == 1 &&
    method.Parameters[0].ParameterType.IsSameWith(enemyType));
```

不要只比较 `FullName`。两个不同程序集可能声明同名类型，数组、引用参数、指针和泛型参数也可能显示出相似名称，却不是同一种类型。

## 什么时候看“能不能传进去”

给回调传游戏对象时，通常关心的是可赋值：

```csharp
TypeReference gameValueType = captured.ValueType;
TypeReference callbackParameterType = callback.Parameters[0].ParameterType;

if (!gameValueType.IsAssignableTo(callbackParameterType))
    throw new InvalidOperationException("回调参数不能接收这个游戏值。");
```

`Transform`、`Observe`、`Replace` 和 `CallArguments` 已经会做这类检查。手动调用主要用于在创建计划前给出自己的错误信息，或从多个回调中挑选兼容项。

## Pattern 中的类型范围

项目已经引用游戏类型时，lambda 写法通常最省心：

```csharp
var pattern = Cil.Value(() =>
    P.Arg<Enemy>(0).GetHealth());
```

`P.Arg<T>`、`P.Local<T>` 和 `P.Any<T>` 会接受兼容的实际类型，因此派生类通常也能匹配。

不引用游戏类型、改用名称描述时，默认是精确匹配：

```csharp
var game = CilSymbols.In("GameAssembly");
var enemy = game.Type("Game.Enemy");

var exact = P.Arg(0, enemy);
var allowDerived = P.Arg(0, enemy.Assignable());
```

对于 Mod Hook，只有确实希望同时支持子类时才加 `Assignable()`。范围过宽可能导致多匹配。

## 访问游戏成员前先判断

自己生成方法调用或字段访问时，可以先检查当前位置是否有权访问目标：

```csharp
TypeReference callerType = method.DeclaringType;

bool canCall = callerType.CanAccess(targetMethod);
bool canReadField = callerType.CanAccess(targetField);
bool canUseType = callerType.CanAccess(targetType);
```

如果结果是 `false`，更稳妥的做法通常是：

- 从一个可访问的 Mod 静态方法间接调用；
- 使用游戏公开 API；
- 把需要的逻辑放到能合法访问该成员的位置。

检查通过只说明访问范围允许，不代表参数、对象实例和调用时机一定正确。

## 常用辅助方法

```csharp
var plainType = type.StripType();
var parent = type.BaseType();
var commonParent = CecilTypeSystem.FindCommonBaseType(left, right);

var interfaces = new List<TypeReference>();
CecilTypeSystem.CollectAllInterfaces(type, interfaces);

bool typeArgumentsOk = genericType.CheckConstraints();
bool methodArgumentsOk = genericMethod.CheckConstraints();
```

| 方法 | 实际用途 |
| --- | --- |
| `StripType()` | 去掉 Cecil 的附加修饰，方便继续判断；不会把引用参数或指针误当成普通类型 |
| `BaseType()` | 取得父类，并尽量保留已经填入的泛型参数 |
| `FindCommonBaseType(a, b)` | 找两种值都能当成的共同父类型 |
| `CollectAllInterfaces(...)` | 收集类型及其父类实现的全部接口 |
| `CheckConstraints()` | 检查填入的泛型类型是否满足 `class`、`struct`、父类等限制 |
| `IsEnum()` | 判断是否为枚举 |
| `GetEnumBackingFieldType()` | 获取枚举实际保存数据的整数类型 |

这些方法解析不到游戏依赖时可能得不到预期结果。离线修改器应给 `ModuleDefinition.ReadModule` 配置能找到游戏 DLL 的解析器。

## 只有自定义 IL 检查才常用的判断

```csharp
bool compatibleOnRuntimeStack = from.IsILStackAssignableTo(to);
```

`IsILStackAssignableTo` 按运行时保存值的方式判断，例如某些小整数在方法执行时会使用相同的表示。它主要给检查器或自定义 IL 生成器使用。

普通 Mod 业务判断不要用它代替 `IsAssignableTo`。能以相同方式暂存，不代表 C# 语义上可以直接赋值。

## 实用选择表

| 你的问题 | 使用 |
| --- | --- |
| 是否为准确的游戏类型或准确重载 | `IsSameWith` |
| 游戏值能否传给回调参数 | `IsAssignableTo` |
| 回调参数能否接收游戏值，想按目标方向阅读 | `IsAssignableFrom` |
| Pattern 是否允许子类 | `CilTypeSpec.Assignable()` |
| 当前方法能否直接访问成员 | `CanAccess` |
| 泛型实参是否合法 | `CheckConstraints` |
| 正在编写自己的执行栈检查 | `IsILStackAssignableTo` |

如果只是调用 MonoWeaver 的高层改写 API，优先让它自动验证；只有需要筛选目标、输出更清楚的日志或编写自定义改写时，再直接使用这些工具。
