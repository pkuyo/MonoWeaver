using System;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;

namespace MonoWeaver.DocSamples;

public static class RewriteSamples
{
    private static ValueMatch Damage(MethodDefinition method)
        => method.Match(Cil.Value(() => P.Arg<int>(0) + P.Arg<int>(1))).Single();

    public static void TransformValue(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:value-transform]
        damage.Transform((Func<int, int>)Hooks.ClampDamage)
              .Apply(VerifyOptions.Full);

        // static int ClampDamage(int original)
        // --8<-- [end:value-transform]
    }

    public static void ObserveValue(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:value-observe]
        damage.Observe((Action<int>)Hooks.LogDamage)
              .Apply(VerifyOptions.Full);

        // static void LogDamage(int original)
        // --8<-- [end:value-observe]
    }

    public static void ReplaceValue(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:value-replace]
        damage.Replace((Func<int>)Hooks.FixedDamage)
              .Apply(VerifyOptions.Full);

        // static int FixedDamage()
        // --8<-- [end:value-replace]
    }

    public static void BeforeAndAfterValue(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:value-before-after]
        damage.Before((Action)Hooks.OnDamageCalculationStarted)
              .Apply(VerifyOptions.Full);

        damage.After((Action)Hooks.OnDamageCalculated)
              .Apply(VerifyOptions.Full);
        // --8<-- [end:value-before-after]
    }

    public static void EffectOperations(MethodDefinition method)
    {
        var soundPattern = Cil.Effect(() => GameAudio.Play(P.Arg<int>(0)));

        // --8<-- [start:effect-ops]
        var effect = method.Match(soundPattern).Single();

        effect.Before((Action)Hooks.BeforeSound)
              .Apply(VerifyOptions.Full);

        effect.After((Action)Hooks.AfterSound)
              .Apply(VerifyOptions.Full);

        effect.Replace((Action)Hooks.PlayCustomSound)
              .Apply(VerifyOptions.Full);

        effect.Remove()
              .Apply(VerifyOptions.Full);
        // --8<-- [end:effect-ops]
    }

    public static void ConditionOperations(MethodDefinition method)
    {
        var gatePattern = Cil.Condition(() => P.Arg<bool>(0) && P.Arg<bool>(1));

        // --8<-- [start:condition-match]
        var condition = method.Match(gatePattern).Single();
        // --8<-- [end:condition-match]

        // --8<-- [start:condition-transform]
        condition.Transform((Func<bool, bool>)Hooks.ChangeGate)
                 .Apply(VerifyOptions.Full);

        // static bool ChangeGate(bool original)
        // --8<-- [end:condition-transform]

        // --8<-- [start:condition-observe]
        condition.Observe((Action<bool>)Hooks.LogGate)
                 .Apply(VerifyOptions.Full);
        // --8<-- [end:condition-observe]

        // --8<-- [start:condition-replace]
        condition.Replace((Func<bool>)Hooks.CustomGate)
                 .Apply(VerifyOptions.Full);
        // --8<-- [end:condition-replace]

        // --8<-- [start:condition-can-rewrite]
        if (!condition.CanRewrite)
            Console.WriteLine(condition.RewriteFailureReason);
        // --8<-- [end:condition-can-rewrite]
    }

    public static void ExtraArguments(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:extra-args]
        damage.Transform(
                  (Func<int, int, int, int>)Hooks.AdjustDamage,
                  args => args
                      .Arg(0)
                      .Constant(999))
              .Apply(VerifyOptions.Full);

        // static int AdjustDamage(int original, int firstMethodArg, int limit)
        // --8<-- [end:extra-args]
    }

    public static void StoreCallbackResult(MethodDefinition method)
    {
        var pattern = Cil.Value(() =>
            P.Local<int>("saved") + P.Arg<int>(0));
        var match = method.Match(pattern).Single();
        var damage = (ValueMatch)match;

        // --8<-- [start:store-result]
        var saved = match.Captures.Local("saved");

        damage.Observe((Func<int, int>)Hooks.LogAndNormalize)
              .Store(saved)
              .Apply(VerifyOptions.Full);
        // --8<-- [end:store-result]
    }

    public static void StoreIntoLocal(MethodDefinition method, VariableDefinition logIdLocal)
    {
        var damage = Damage(method);
        // --8<-- [start:store-local]
        damage.Observe((Func<int, int>)Hooks.RecordAndReturnId)
              .StoreLocal(logIdLocal)
              .Apply(VerifyOptions.Full);
        // --8<-- [end:store-local]
    }

    public static void DiscardCallbackResult(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:discard-result]
        damage.Before((Func<int>)Hooks.CreateTraceId)
              .Discard()
              .Apply(VerifyOptions.Full);
        // --8<-- [end:discard-result]
    }

    public static void PlanThenApply(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:plan]
        var plan = damage.Transform((Func<int, int>)Hooks.ClampDamage);
        // --8<-- [end:plan]

        // --8<-- [start:apply-full]
        plan.Apply(VerifyOptions.Full);
        // --8<-- [end:apply-full]
    }
}
