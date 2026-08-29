using System.Linq;
using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

/// <summary>pattern 对象（leaf 与片段）自身的行为：独立 Match、跨 Match 隔离、options 归属。</summary>
public sealed class PatternObjectTests
{
    [Fact]
    public void LeafIsACompletePatternAndMatchesStandalone()
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "LocalRead");
        var local = Cil.Local<int>(0);

        var match = method.Match(local).Single();

        Assert.Equal(0, match[local].Variable.Index);
        Assert.True(match.DefinitionInstruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0);
    }

    [Fact]
    public void SameLeafInTwoIndependentMatchesResolvesIndependently()
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var localRead = PatternTestSupport.FixtureMethod(module, "LocalRead");
        var transparent = PatternTestSupport.FixtureMethod(module, "TransparentLocal");
        var local = Cil.Local<int>();
        var pattern = Cil.Value(() => local.Value);

        //同一个 pattern 对象对不同方法各自解析，互不影响。
        var first = localRead.Match(pattern);
        var second = transparent.Match(pattern);

        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Same(localRead, first.First()[local].Method);
        Assert.Same(transparent, second.First()[local].Method);
    }

    [Fact]
    public void FragmentOptionsAreIgnoredWhenEmbedded()
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "TransparentLocal");

        //片段自带 TemporaryNormalization.None，但嵌入后以外层 options 为准（外层默认允许穿透）。
        var strictFragment = Cil.Value(
            () => P.Arg<int>(0) + 1,
            new PatternOptions { TemporaryNormalization = TemporaryNormalization.None });
        var outer = Cil.Value(() => strictFragment * 2);

        Assert.NotEmpty(method.Match(outer));
    }

    [Fact]
    public void SameFragmentInTwoDifferentOuterPatternsIsAllowed()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Add");
        var fragment = Cil.Value(() => P.Arg<int>(0));

        var first = Cil.Value(() => fragment + P.Arg<int>(1));
        var second = Cil.Value(() => fragment + P.Arg<int>(1));

        Assert.Single(method.Match(first));
        Assert.Single(method.Match(second));
    }

    [Fact]
    public void MetadataLeafFactoriesBehaveLikeTypedOnes()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument1");
        var text = Cil.Arg(CilType.String.Assignable(), "text");
        var any = Cil.Any(CilType.String.Assignable());

        var byName = method.Match(Cil.Value(text.Expr)).Single()[text];
        var wildcard = method.Match(Cil.Value(any.Expr));

        Assert.Equal(1, byName.ParameterIndex);
        Assert.Equal("text", text.ParameterName);
        Assert.NotEmpty(wildcard);
        Assert.All(wildcard, match => Assert.IsAssignableFrom<ValueCapture>(match[any]));
    }

    [Fact]
    public void SharedCilExprNodeIsNotTreatedAsRootInsideAnotherPattern()
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "TransparentLocal");

        //同一个 CilExpr 子树先作为 p1 的根，再作为 p2 的子节点。
        //"根"由本次匹配判定：p2 里它不是根，临时变量穿透必须照常工作。
        var sum = P.Arg(0, CilType.Int32) + P.Constant(1);
        var p1 = Cil.Value(sum);
        var p2 = Cil.Value(sum * P.Constant(2));

        Assert.NotEmpty(method.Match(p1));
        Assert.NotEmpty(method.Match(p2));
    }

    [Fact]
    public void RepeatedLocalLeafUnifiesToTheSameVariable()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(PatternObjectTests), nameof(SquareLocal));
        var local = Cil.Local<int>();
        var pattern = Cil.Value(() => local * local,
            new PatternOptions { TemporaryNormalization = TemporaryNormalization.None });

        var match = method.Match(pattern).Single();

        Assert.NotNull(match[local].Variable);
    }

    public static int SquareLocal(int seed)
    {
        var value = seed + 1;
        return value * value;
    }
}
