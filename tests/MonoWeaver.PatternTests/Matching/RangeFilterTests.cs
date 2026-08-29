using System;
using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

/// <summary>结果集按 IL 位置筛选：After / Before / Between。</summary>
public sealed class RangeFilterTests
{
    private static ValuePattern<B> CallB() => Cil.Value((A value) => value.B());

    [Fact]
    public void AfterPreviousMatchSelectsTheNextOccurrence()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Ambiguous");
        var all = method.Match(CallB());
        Assert.Equal(2, all.Count);

        //ILCursor 式的“从这里往后找下一个”，但仍要求唯一。
        var second = all.After(all[0]).Single();

        Assert.Same(all[1].DefinitionInstruction, second.DefinitionInstruction);
    }

    [Fact]
    public void BeforePreviousMatchSelectsTheEarlierOccurrence()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Ambiguous");
        var all = method.Match(CallB());

        var first = all.Before(all[1]).Single();

        Assert.Same(all[0].DefinitionInstruction, first.DefinitionInstruction);
    }

    [Fact]
    public void InstructionAnchorsAndBetweenKeepOnlyTheEnclosedMatches()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Ambiguous");
        var body = method.Body.Instructions;
        var all = method.Match(CallB());

        //IL: ldarg.0 callvirt pop ldarg.0 callvirt ret —— 两处匹配各覆盖 [ldarg.0, callvirt]。
        //边界不含：第一处从 body[0] 开始，被 After(body[0]) 排除；两处都在 ret 之前结束。
        Assert.Single(all.After(body[0]));
        Assert.Equal(2, all.Before(body[body.Count - 1]).Count);
        Assert.Single(all.Between(body[0], body[body.Count - 1]));
        Assert.Single(all.After(all[0].DefinitionInstruction));
        Assert.Empty(all.After(body[body.Count - 1]));
        Assert.Empty(all.Before(body[0]));
    }

    [Fact]
    public void FilteredSetKeepsPatternAndDiagnostics()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Ambiguous");
        var all = method.Match(CallB());

        var filtered = all.After(all[0]);

        Assert.Same(all.Pattern, filtered.Pattern);
        Assert.Same(all.Diagnostics, filtered.Diagnostics);
    }

    [Fact]
    public void AnchorOutsideTheMethodBodyIsRejected()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Ambiguous");
        var other = PatternTestSupport.FixtureMethod(module, "Chain");
        var all = method.Match(CallB());

        Assert.Throws<InvalidOperationException>(() => all.After(Instruction.Create(OpCodes.Nop)));
        Assert.Throws<InvalidOperationException>(() => all.After(other.Match(CallB()).Single()));
    }
}
