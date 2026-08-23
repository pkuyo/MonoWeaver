# MonoWeaver

MonoWeaver 是给 C# Mod 开发者用的代码匹配与改写工具。你描述想找的游戏逻辑——例如“两个参数相加”“读取某个字段”“调用某个方法”或“一段 `if` 条件”——然后选择在它前后追加代码、读取结果，或直接替换它。

大多数时候不需要自己数 IL 指令。编译器即使多生成了一个临时变量，或换了一种条件跳转写法，匹配仍可以保持稳定。

```csharp
--8<-- "tests/MonoWeaver.DocSamples/Samples/Patterns.cs:value-pattern"
```

常见用途：

- 修改伤害、价格、冷却时间等计算结果；
- 记录某个值，但不改变游戏原逻辑；
- 替换或删除一次游戏行为；
- 改写带 `&&`、`||` 的判断条件；
- 在离线修改 DLL 和 MonoMod `ILContext` 中使用同一套写法；
- 在保存或执行前检查修改后的方法。

## 从哪里开始

<div class="grid cards" markdown>

-   __我要上手__

    ---

    选包、装依赖，然后跑通第一个离线补丁。

    [:octicons-arrow-right-24: 安装与选包](getting-started/installation.md)

-   __我要查写法__

    ---

    按游戏代码的样子找到对应 Pattern，或翻完整的 DSL 与改写操作表。

    [:octicons-arrow-right-24: 示例集](cookbook/index.md)

-   __我要排错__

    ---

    匹配不到、匹配到多处，或者改完之后检查器报错。

    [:octicons-arrow-right-24: 排错](troubleshooting/no-match.md)

</div>

## 适用范围

MonoWeaver 只适用于 Mono.Cecil 能读取的 .NET/Mono 托管程序集，不用于 IL2CPP 或原生代码。
