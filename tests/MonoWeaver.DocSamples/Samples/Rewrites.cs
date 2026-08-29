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
        => method.Match(Cil.Value((int baseDamage, int bonus) => baseDamage + bonus)).Single();

    public static void TransformValue(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:value-transform]
        damage.Transform(Hooks.ClampDamage)
              .Apply(VerifyOptions.Full);

        // static int ClampDamage(int original)
        // --8<-- [end:value-transform]
    }

    public static void ObserveValue(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:value-observe]
        damage.Observe(Hooks.LogDamage)
              .Apply(VerifyOptions.Full);

        // static void LogDamage(int original)
        // --8<-- [end:value-observe]
    }

    public static void ReplaceValue(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:value-replace]
        damage.Replace(Hooks.FixedDamage)
              .Apply(VerifyOptions.Full);

        // static int FixedDamage()
        // --8<-- [end:value-replace]
    }

    public static void BeforeAndAfterValue(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:value-before-after]
        damage.Before(Hooks.OnDamageCalculationStarted)
              .Apply(VerifyOptions.Full);

        damage.After(Hooks.OnDamageCalculated)
              .Apply(VerifyOptions.Full);
        // --8<-- [end:value-before-after]
    }

    public static void EffectOperations(MethodDefinition method)
    {
        var soundPattern = Cil.Effect((int soundId) => GameAudio.Play(soundId));

        // --8<-- [start:effect-ops]
        var effect = method.Match(soundPattern).Single();

        effect.Before(Hooks.BeforeSound)
              .Apply(VerifyOptions.Full);

        effect.After(Hooks.AfterSound)
              .Apply(VerifyOptions.Full);

        effect.Replace(Hooks.PlayCustomSound)
              .Apply(VerifyOptions.Full);

        effect.Remove()
              .Apply(VerifyOptions.Full);
        // --8<-- [end:effect-ops]
    }

    public static void ConditionOperations(MethodDefinition method)
    {
        var gatePattern = Cil.Condition((bool left, bool right) => left && right);

        // --8<-- [start:condition-match]
        var condition = method.Match(gatePattern).Single();
        // --8<-- [end:condition-match]

        // --8<-- [start:condition-transform]
        condition.Transform(Hooks.ChangeGate)
                 .Apply(VerifyOptions.Full);

        // static bool ChangeGate(bool original)
        // --8<-- [end:condition-transform]

        // --8<-- [start:condition-observe]
        condition.Observe(Hooks.LogGate)
                 .Apply(VerifyOptions.Full);
        // --8<-- [end:condition-observe]

        // --8<-- [start:condition-replace]
        condition.Replace(Hooks.CustomGate)
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
                  Hooks.AdjustDamage,
                  args => args
                      .Arg(0)
                      .Constant(999))
              .Apply(VerifyOptions.Full);

        // static int AdjustDamage(int original, int firstMethodArg, int limit)
        // --8<-- [end:extra-args]
    }

    public static void StoreCallbackResult(MethodDefinition method)
    {
        var savedLocal = Cil.Local<int>();
        var pattern = Cil.Value((int damage) =>
            savedLocal + damage);
        var match = method.Match(pattern).Single();
        var damage = (ValueMatch)match;

        // --8<-- [start:store-result]
        var saved = match[savedLocal];

        damage.Observe(Hooks.LogAndNormalize)
              .Store(saved)
              .Apply(VerifyOptions.Full);
        // --8<-- [end:store-result]
    }

    public static void StoreIntoLocal(MethodDefinition method, VariableDefinition logIdLocal)
    {
        var damage = Damage(method);
        // --8<-- [start:store-local]
        damage.Observe(Hooks.RecordAndReturnId)
              .StoreLocal(logIdLocal)
              .Apply(VerifyOptions.Full);
        // --8<-- [end:store-local]
    }

    public static void DiscardCallbackResult(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:discard-result]
        damage.Before(Hooks.CreateTraceId)
              .Discard()
              .Apply(VerifyOptions.Full);
        // --8<-- [end:discard-result]
    }

    public static void PlanThenApply(MethodDefinition method)
    {
        var damage = Damage(method);
        // --8<-- [start:plan]
        var plan = damage.Transform(Hooks.ClampDamage);
        // --8<-- [end:plan]

        // --8<-- [start:apply-full]
        plan.Apply(VerifyOptions.Full);
        // --8<-- [end:apply-full]
    }
}
