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

lambda 参数本身就是捕获：每个参数等价于一个按目标参数名匹配的 `Cil.Arg<T>("参数名")`（`__this` 等价于 `Cil.This<T>()`），匹配后用 `match.Arg("参数名")` / `match.This()` 取回。同一参数在表达式里出现两次表示同一个实参。

## 内联占位符（lambda 写法，不捕获）

| 写法 | 含义 |
| --- | --- |
| `P.This<T>()` | 当前对象 |
| `P.Arg<T>(0)` | 第 0 个显式参数，不包含 `this` |
| `P.Local<T>(0)` | 第 0 个局部变量 |
| `P.StoreElement(array, index, value)` | 数组写入 |
| `P.StoreField(field, value)` | 字段写入 |

lambda 中可以像普通 C# 一样写方法调用、字段与属性读取、`new`、数组访问、类型转换、算术和比较运算。

## pattern 对象（可捕获、可复用）

要捕获或复用某个位置时，在 lambda 外声明一个 pattern 对象，在表达式里直接引用它，匹配后用同一个对象从结果取回：

```csharp
var tmp = Cil.Local<int>();
var pattern = Cil.Value(() => tmp * 2);
var capture = method.Match(pattern).Single()[tmp];   // LocalCapture
```

| 声明 | 含义 |
| --- | --- |
| `Cil.This<T>()` | 当前对象 |
| `Cil.Arg<T>()` / `Cil.Arg<T>(index)` / `Cil.Arg<T>("name")` | 任意/指定序号/指定参数名的 `T` 参数（按名与 lambda 参数同一规则） |
| `Cil.Local<T>()` / `Cil.Local<T>(index)` | 任意/指定序号的 `T` 局部变量 |
| `Cil.Local(definedBy)` | 由指定表达式唯一定义的局部变量 |
| `Cil.Any<T>()` | 任意一段产生 `T` 的内容（通配） |

规则只有一条：**绑定身份的对象（this/参数/局部变量）在同一个 pattern 里重复出现表示"同一个"**——`Cil.Value(() => tmp * tmp)` 要求两处读同一个变量；`Cil.Any` 和内嵌片段不绑定身份，同一对象出现两次会在构造时报错。

运算数、实参这些位置直接写对象即可（`tmp * 2`、`Foo(tmp)`）。三种位置要用 `对象.Value` 显式引用：整个 body 就是它（`Cil.Value(() => tmp.Value)`）、作为方法调用的接收者（`score.Value.ToString()`）、`T` 是 `object` 或接口（C# 的内建转换会绕过隐式转换）。

这些对象也可以直接写在 lambda 参数位，作为 pattern 局部的声明（不可跨 pattern、不可从结果取回）：

```csharp
var pattern = Cil.Value((CilArg<int> value) => value + value);   // 同一参数加自身
```

### 片段内嵌（选中大表达式中的一段）

一个完整的 `Cil.Value` / `Cil.Condition` pattern 可以直接嵌进另一个 pattern，之后用它单独改写被选中的那一段：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:mark-capture"
```

片段不绑定身份，同一片段对象每个外层 pattern 只能嵌一次；片段自带的 `PatternOptions` 在嵌入后以外层为准。

!!! warning "不要把运行时对象捕获进 lambda"
    lambda 是被解析的表达式树，不是要执行的代码。除 pattern 对象之外的外部变量会变成闭包字段读取，匹配不到游戏里的常量。用 pattern 对象、字面常量或静态字段来表示。

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
| `.EqualTo` / `.GreaterThan` / `.LessThan` … | 比较 |
| `.AndAlso` / `.OrElse` | 短路逻辑 |
| `+ - * / %`、`& | ^ << >>`、`! ~` | 运算符重载，可直接用 |

## 类型匹配范围 { #type-scope }

lambda 写法中，`P.Arg<T>`/`Cil.Arg<T>`、`P.Local<T>`/`Cil.Local<T>` 和 `Cil.Any<T>` 接受兼容的实际类型，派生类通常也能匹配。

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

### 局部变量的定义约束

`Cil.Local(definedBy)` 声明"由指定表达式唯一定义的局部变量"：

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:local-defined-by"
```

声明处写清来源，之后在任何 pattern 里直接使用这个对象即可。
