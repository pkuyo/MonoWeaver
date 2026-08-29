using System.Linq;
using MonoWeaver.Cecil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class MatchDiagnosticsTests
{
    [Fact]
    public void UnsupportedInstructionIsReported()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "FieldAddressArgument");

        //把字段按 ref 传出去会生成 ldflda，属于模型仍不可表达的指令
        var matches = method.Match(Cil.Value(() => P.Arg<MemberHost>(0).InstanceField));

        Assert.Empty(matches);
        var diagnostic = Assert.Single(matches.Diagnostics,
            candidate => candidate.Kind == MatchDiagnosticKind.UnsupportedInstruction);
        Assert.Contains("ldflda", diagnostic.ToString().ToLowerInvariant());
    }

    [Fact]
    public void AmbiguousLocalExpansionIsReported()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "AddressTakenLocal");

        //local 被取地址，穿透被拒绝；pattern 因此匹配不到 local + 1
        var matches = method.Match(Cil.Value(() => P.Arg<int>(0) + 1));

        Assert.Empty(matches);
        var diagnostic = Assert.Single(matches.Diagnostics,
            candidate => candidate.Kind == MatchDiagnosticKind.AmbiguousLocal);
        Assert.Contains("address taken", diagnostic.Message);
    }

    [Fact]
    public void ReusedStoreResultIsReported()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteAndReturnStaticField");

        //return Type.F = x; 的赋值结果被复用，该写入不作为可删除 effect 提供
        var value = Cil.Any<int>();
        var matches = method.Match(Cil.Effect(() =>
            P.StoreField(MemberHost.StaticField, value)));

        Assert.Empty(matches);
        Assert.Contains(matches.Diagnostics, candidate =>
            candidate.Kind == MatchDiagnosticKind.UnsupportedInstruction
            && candidate.Message.Contains("reused by later code"));
    }

    [Fact]
    public void LocalConstraintFailureIsReported()
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "MultipleDefinitions");

        var ret = Cil.Local(Cil.Value(() => Ops.XXX()));
        var pattern = Cil.Value(() => ret.Value);
        var matches = method.Match(pattern);

        Assert.Empty(matches);
        Assert.Contains(matches.Diagnostics, candidate =>
            candidate.Kind == MatchDiagnosticKind.LocalConstraintFailed
            && candidate.Message.Contains("reaching definitions"));
    }

    [Fact]
    public void SingleFailureMessageIncludesDiagnostics()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "AddressTakenLocal");

        var matches = method.Match(Cil.Value(() => P.Arg<int>(0) + 1));
        var exception = Assert.Throws<CilPatternMatchException>(() => matches.Single());

        Assert.Contains("Possible reasons:", exception.Message);
        Assert.Contains("address taken", exception.Message);
    }

    [Fact]
    public void CleanMethodProducesNoDiagnostics()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Chain");

        var matches = method.Match(Cil.Value(() => P.Arg<A>(0).B().C()));

        Assert.Single(matches);
        Assert.Empty(matches.Diagnostics);
        Assert.Contains("No diagnostics were recorded", matches.ExplainFailure());
    }

    [Fact]
    public void ExplainFailureListsEveryDiagnostic()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "AddressTakenLocal");

        var matches = method.Match(Cil.Value(() => P.Arg<int>(0) + 1));
        var report = matches.ExplainFailure();

        Assert.Contains("Match diagnostics for", report);
        foreach (var diagnostic in matches.Diagnostics)
            Assert.Contains(diagnostic.ToString(), report);
    }
}
