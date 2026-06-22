using System;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.MonoMod.Patterns;
using MonoWeaver.Patterns;

namespace MonoWeaver.Patterns.Sample;

/// <summary>
/// Small, compileable examples. The methods model the body of an ILHook or a HarmonyX
/// ILManipulator; the pattern lambdas are inspected but are never compiled or executed.
/// </summary>
public static class PatternExamples
{
    /// <summary>
    /// Matches the complete BuildRequest().Send() chain, but selects only BuildRequest().
    /// The resulting IL is equivalent to ModifyRequest(actor.BuildRequest(), bonus).Send().
    /// </summary>
    public static void TransformOneOccurrenceInACallChain(ILContext il)
    {
        var pattern = Cil.Value(() =>
            P.Mark("request", P.Arg<Actor>(0).BuildRequest()).Send());

        var match = il.Match(pattern).Single();
        var request = match.Value("request");

        request.AfterUse(il)
            .Transform((Func<Request, int, Request>)Callbacks.ModifyRequest,
                args => args.Arg(1)) // target method parameter 1: bonus
            .LeaveOnStack();          // Send() consumes the replacement Request
    }

    /// <summary>
    /// Observes a value without replacing it. Observe emits dup before calling the callback,
    /// so the original Request remains available to Send().
    /// </summary>
    public static void ObserveOneOccurrence(ILContext il)
    {
        var request = il.Match(Cil.Value(() =>
                P.Mark("request", P.Arg<Actor>(0).BuildRequest()).Send()))
            .Single()
            .Value("request");

        request.AfterUse(il).Observe(
            (Action<Request, Actor>)Callbacks.LogRequest,
            args => args.Arg(0));
    }

    /// <summary>
    /// Matches a large ordered short-circuit condition while selecting only its left subcondition.
    /// The callback receives the already evaluated result of HasPermission(player) &amp;&amp; player.IsReady().
    /// Returning true continues into ServerOpen() || OverrideEnabled(); returning false follows the
    /// original false continuation. Evaluation order and short-circuiting inside the marked part remain intact.
    /// </summary>
    public static void TransformSubcondition(ILContext il)
    {
        var pattern = Cil.Condition(() =>
            P.Mark("gate",
                Rules.HasPermission(P.Arg<Player>(0)) &&
                P.Arg<Player>(0).IsReady())
            && (Rules.ServerOpen() || Rules.OverrideEnabled()));

        var gate = il.Match(pattern).Single().Condition("gate");
        gate.Transform(il,
            (Func<bool, Player, bool>)Callbacks.ModifyGate,
            args => args.Arg(0));
    }

    /// <summary>
    /// Precisely identifies a bool local by requiring that the unique store reaching this load
    /// contains Rules.ComputeAllowed(player). A method with two reaching definitions is rejected.
    /// </summary>
    public static void MatchACompilerTemporary(ILContext il)
    {
        var pattern = Cil.Condition(() => P.Local<bool>("allowed"))
            .LocalDefinedBy("allowed",
                Cil.Value(() => Rules.ComputeAllowed(P.Arg<Player>(0))));

        var match = il.Match(pattern).Single();
        var allowed = match.Local("allowed");

        // This is a normal insertion before the condition statement. The callback is void,
        // so it does not alter the evaluation stack used by the original ldloc/brfalse.
        match.Before(il).Call(
            (Action<bool>)Callbacks.LogAllowed,
            args => args.Local(allowed));
    }

    /// <summary>
    /// A non-void callback result remains on the stack by default. Select an explicit destination
    /// when the result is auxiliary rather than part of the original expression.
    /// </summary>
    public static void StoreAnAuxiliaryResult(ILContext il, VariableDefinition destination)
    {
        var match = il.Match(Cil.Value(() => P.Arg<Actor>(0).BuildRequest())).Single();

        match.Before(il)
            .Call((Func<Actor, int>)Callbacks.ComputeTag,
                args => args.Arg(0))
            .StoreLocal(destination);
    }

    /// <summary>
    /// Effect means that a non-void expression is discarded (normally by pop), or that the
    /// expression itself returns void. After() is positioned after the complete statement.
    /// </summary>
    public static void InsertAfterAnEffectStatement(ILContext il)
    {
        var statement = il.Match(Cil.Effect(() =>
                P.Arg<Actor>(0).BuildRequest()))
            .Single();

        statement.After(il).Call((Action)Callbacks.AfterDiscardedRequest);
    }
}

// Stand-in types used only so the sample compiles. A real mod uses game types and methods.
public sealed class Actor
{
    public Request BuildRequest() => throw new NotSupportedException();
}

public sealed class Request
{
    public Response Send() => throw new NotSupportedException();
}

public sealed class Response { }

public sealed class Player
{
    public bool IsReady() => throw new NotSupportedException();
}

public static class Rules
{
    public static bool HasPermission(Player player) => throw new NotSupportedException();
    public static bool ServerOpen() => throw new NotSupportedException();
    public static bool OverrideEnabled() => throw new NotSupportedException();
    public static bool ComputeAllowed(Player player) => throw new NotSupportedException();
}

public static class Callbacks
{
    public static Request ModifyRequest(Request request, int bonus) => request;
    public static void LogRequest(Request request, Actor actor) { }
    public static bool ModifyGate(bool original, Player player) => original;
    public static void LogAllowed(bool allowed) { }
    public static int ComputeTag(Actor actor) => 0;
    public static void AfterDiscardedRequest() { }
}
