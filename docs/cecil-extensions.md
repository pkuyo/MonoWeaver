# Cecil 类型扩展

MonoWeaver 在 `MonoWeaver.Utils.CecilTypeSystem` 中补充了一组常用的 Cecil 类型判断。这里不列全部接口，只说明最容易混淆、也最常用的几类。

## 类型相同与可赋值不是一回事

```csharp
using MonoWeaver.Utils;

bool same = candidate.IsSameWith(expected);
bool assignable = source.IsAssignableTo(target);
bool reverse = target.IsAssignableFrom(source);
```

| 方法 | 含义 |
| --- | --- |
| `a.IsSameWith(b)` | 比较 metadata 类型身份/结构，而不是比较 `TypeReference` 对象引用。 |
| `source.IsAssignableTo(target)` | 判断 `source` 的值能否赋给 `target`。 |
| `target.IsAssignableFrom(source)` | 与上一项方向相反、语义相同。 |

不要用 `FullName` 代替 `IsSameWith`。程序集 scope、泛型参数所属位置、数组/byref/pointer 结构等信息都可能影响真实类型身份。

可赋值判断的方向可直接按赋值语句记忆：

```csharp
Target value = source;
// source.IsAssignableTo(Target)
// Target.IsAssignableFrom(source)
```

## 常用辅助方法

```csharp
var raw = type.StripType();
var baseType = type.BaseType();
var common = CecilTypeSystem.FindCommonBaseType(left, right);

var interfaces = new List<TypeReference>();
CecilTypeSystem.CollectAllInterfaces(type, interfaces);

bool constraintsOk = genericInstance.CheckConstraints();
bool accessible = callerType.CanAccess(targetMethod);
```

- `StripType()` 去掉 `modopt`、`modreq`、`pinned`、`sentinel` 等修饰，但不会把 byref/pointer 当成普通类型。
- `BaseType()` 和 `CollectAllInterfaces()` 处理 Cecil 引用，并尽量保留已实例化的泛型信息。
- `CheckConstraints()` 可用于构造泛型类型或泛型方法引用。
- `CanAccess()` 用于类型、字段和方法的可访问性判断。
