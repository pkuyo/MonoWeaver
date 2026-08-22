using Mono.Cecil;

namespace MonoWeaver.Utils;

/// <summary>
/// 解决 Mono.Cecil 0.10.x 与 0.11+ 之间的 API 差异。
/// </summary>
internal static class CecilCompat
{
#if CECIL_010
    public static TypeReference AsTypeReference(this TypeReference constraint) => constraint;
#else
    public static TypeReference AsTypeReference(this GenericParameterConstraint constraint)
        => constraint.ConstraintType;
#endif
}
