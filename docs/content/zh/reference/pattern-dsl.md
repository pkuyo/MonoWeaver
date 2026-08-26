# Pattern 写法

## 两种入口

Pattern 有两种等价的写法，产生的匹配结果和改写 API 完全相同。

=== "lambda：项目已引用游戏 DLL"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:lambda-form"
    ```

    代码最接近原游戏表达式，可读性最好。

=== "符号：不引用或不加载游戏类型"

    ```csharp
    --8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:symbol-form"
    ```

    用程序集名、类型名和方法签名描述目标。

## 带参数的 lambda

`Cil.Value`、`Cil.Effect` 和 `Cil.Condition` 都可以直接用 lambda 参数描述目标方法的参数。参数按名称绑定，`__this` 表示当前实例：

```csharp
var sum = Cil.Value((int left, int right) => left + right);
var play = Cil.Effect((Player player) => Audio.Play(player.HitSound));
var gate = Cil.Condition((Player player) => player.HasKey && !player.IsDead);
var damage = Cil.Value((Player __this, int amount) => __this.baseDamage + amount);
```

lambda 参数无需按目标方法的声明顺序排列，也只需声明表达式实际使用的参数。`P.Arg` 和 `P.This` 仍可用于目标参数没有可靠名称或需要按类型、位置匹配的情况。

lambda 参数不会隐式创建捕获；需要在匹配结果中取得参数时，请使用 `P.Mark("name", parameter)` 显式捕获。

## 占位符（lambda 写法）

| 写法 | 含义 |
| --- | --- |
| `P.This<T>()` | 当前对象 |
| `P.This<T>("name")` | 当前对象，并命名捕获 |
| `P.Arg<T>(0)` | 第 0 个显式参数，不包含 `this` |
| `P.Arg<T>(0, "name")` | 同上，并命名捕获 |
| `P.Arg<T>("name")` | 任意位置的一个 `T` 参数 |
| `P.Local<T>(0)` | 第 0 个局部变量 |
| `P.Local<T>(0, "name")` | 同上，并命名捕获 |
| `P.Local<T>("name")` | 任意一个 `T` 局部变量 |
| `P.Any<T>("name")` | 任意一段产生 `T` 的内容 |
| `P.Mark("name", value)` | 给某段内部表达式命名，之后可单独改写 |
| `P.StoreElement(array, index, value)` | 数组写入 |

lambda 中可以像普通 C# 一样写方法调用、字段与属性读取、`new`、数组访问、类型转换、算术和比较运算。

!!! warning "不要把运行时对象捕获进 lambda"
    lambda 是被解析的表达式树，不是要执行的代码。外部变量会变成闭包字段读取，匹配不到游戏里的常量。用 `P.Arg`、`P.Local`、`P.Any`、字面常量或静态字段来表示。

## 符号写法

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:symbol-form-short"
```

| 入口 | 得到 |
| --- | --- |
| `CilSymbols.In("Assembly")` | `CilAssemblySpec` |
| `assembly.Type("Ns.Type")` | `CilTypeSpec` |
| `CilSymbols.Type(typeReference)` | 由已有 Cecil 引用得到 `CilTypeSpec` |
| `type.InstanceMethod(name, returnType, params...)` | `CilMethodSpec` |
| `type.StaticMethod(name, returnType, params...)` | `CilMethodSpec` |
| `type.Constructor(params...)` | `CilMethodSpec` |
| `type.InstanceField(name, fieldType)` | `CilFieldSpec` |
| `type.StaticField(name, fieldType)` | `CilFieldSpec` |

`CilType` 提供内置类型：`CilType.Int32`、`CilType.Boolean`、`CilType.String`、`CilType.Void` 等，也有 `CilType.I4`、`CilType.Bool` 这样的短别名。

类型构造：`MakeArrayType(rank)`、`MakeByReferenceType()`、`MakePointerType()`、`MakeGenericType(...)`。

### CilExpr 的链式写法

| 写法 | 含义 |
| --- | --- |
| `.Call(method, args...)` | 方法调用 |
| `.Field(field)` | 字段读取 |
| `.ElementAt(index, elementType)` | 数组读取 |
| `.Length()` | 数组长度 |
| `.ConvertTo(type)` / `.As(type)` | 转换 |
| `.Mark("name")` | 命名捕获 |
| `.EqualTo` / `.GreaterThan` / `.LessThan` … | 比较 |
| `.AndAlso` / `.OrElse` | 短路逻辑 |
| `+ - * / %`、`& | ^ << >>`、`! ~` | 运算符重载，可直接用 |

## 类型匹配范围 { #type-scope }

lambda 写法中，`P.Arg<T>`、`P.Local<T>` 和 `P.Any<T>` 接受兼容的实际类型，派生类通常也能匹配。

符号写法**默认精确匹配**。要允许子类，显式加 `Assignable()`：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:exact-vs-assignable"
```

| 模式 | 方法 |
| --- | --- |
| 精确相同 | 默认 |
| 允许子类/可赋值 | `.Assignable()` |
| 运行时栈表示兼容 | `.StackCompatible()` |

对 Mod Hook，只有确实希望同时支持子类时才加 `Assignable()`。范围过宽会导致多匹配。更多见 [类型判断](type-matching.md)。

## PatternOptions

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:temporary-off"
```

| 选项 | 默认 | 作用 |
| --- | --- | --- |
| `TemporaryNormalization` | `UniqueDefinitions` | 是否跟随来源唯一的编译器临时变量 |
| `IgnoreCallOpcodeDifference` | `true` | 是否忽略 `call` 与 `callvirt` 的差异 |
| `IgnoreTransparentControlFlow` | `true` | 是否忽略不影响求值的跳转 |

### LocalDefinedBy

约束某个局部变量捕获必须来自指定表达式：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:local-defined-by"
```

`Cil.Value`、`Cil.Effect`、`Cil.Condition` 得到的 Pattern 都支持它。
