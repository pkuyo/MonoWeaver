using System;
using Mono.Cecil;

namespace MonoWeaver.Utils
{
    internal class HashUtils
    {



        private const int Prime = 23;

        public static int GetTypeHash(TypeReference type, bool includeScope = false)
            => HashType(type, 17, includeScope);

        public static int GetMethodHash(
            MethodReference method,
            bool includeScope = false,
            bool includeThisAndCallConv = true,
            bool includeParamAttributes = false)
            => HashMethod(method, 17, includeScope, includeThisAndCallConv, includeParamAttributes);

        private static int HashMethod(
            MethodReference method,
            int hash,
            bool includeScope,
            bool includeThisAndCallConv,
            bool includeParamAttributes)
        {
            unchecked
            {
                if (method == null) return Mix(hash, 0);

                if (method is MethodSpecification ms)
                {
                    hash = Mix(hash, 0x4D535045); // "MSPE"
                    hash = HashMethod(ms.ElementMethod, hash, includeScope, includeThisAndCallConv, includeParamAttributes);

                    if (method is GenericInstanceMethod gim)
                    {
                        hash = Mix(hash, 0x47494D); // "GIM"
                        hash = Mix(hash, gim.GenericArguments.Count);
                        foreach (var ga in gim.GenericArguments)
                            hash = HashType(ga, hash, includeScope);
                    }

                    return hash;
                }

                // 普通方法
                hash = Mix(hash, 0x4D524546); // "MREF"

                hash = HashType(method.DeclaringType, hash, includeScope);

                hash = Mix(hash, method.Name == null ? 0 : OrdinalHash(method.Name));

               
                if (method.HasGenericParameters)
                {
                    hash = Mix(hash, 0x47454E50); // "GENP"
                    hash = Mix(hash, method.GenericParameters.Count);
                    foreach (var gp in method.GenericParameters)
                        hash = HashGenericParameter(gp, hash);
                }
                else
                {
                    hash = Mix(hash, 0);
                }

                // 返回类型
                hash = HashType(method.ReturnType, hash, includeScope);

                // this/调用约定（是否纳入取决于你想不想更“签名级别”）
                if (includeThisAndCallConv)
                {
                    hash = Mix(hash, (int)method.CallingConvention);
                    hash = Mix(hash, method.HasThis ? 1 : 0);
                    hash = Mix(hash, method.ExplicitThis ? 1 : 0);
                }

                // 参数
                hash = Mix(hash, 0x5041524D); // "PARM"
                hash = Mix(hash, method.Parameters.Count);
                foreach (var p in method.Parameters)
                {
                    hash = HashType(p.ParameterType, hash, includeScope);
                    if (includeParamAttributes)
                        hash = Mix(hash, (int)p.Attributes);
                }

                return hash;
            }
        }

        private static int HashType(TypeReference type, int hash, bool includeScope)
        {
            unchecked
            {
                if (type == null) return Mix(hash, 0);

                // GenericParameter
                if (type is GenericParameter gp)
                    return HashGenericParameter(gp, hash);

                // TypeSpecification
                if (type is TypeSpecification spec)
                    return HashTypeSpecification(spec, hash, includeScope);

                hash = Mix(hash, 0x54524546); // "TREF"

                if (includeScope)
                {
                    var scopeName = type.Scope?.Name;
                    hash = Mix(hash, scopeName == null ? 0 : OrdinalHash(scopeName));
                }

                if (type.IsNested && type.DeclaringType != null)
                {
                    hash = HashType(type.DeclaringType, hash, includeScope);
                    hash = Mix(hash, 0x2F); // '/'
                    hash = Mix(hash, OrdinalHash(type.Name));
                }
                else
                {
                    hash = Mix(hash, type.Namespace == null ? 0 : OrdinalHash(type.Namespace));
                    hash = Mix(hash, OrdinalHash(type.Name));
                }

                return hash;
            }
        }

        private static int HashTypeSpecification(TypeSpecification spec, int hash, bool includeScope)
        {
            unchecked
            {
                hash = Mix(hash, 0x53504543); // "SPEC"
                hash = HashType(spec.ElementType, hash, includeScope);

                switch (spec)
                {
                    case GenericInstanceType git:
                        hash = Mix(hash, 0x474954); // "GIT"
                        hash = Mix(hash, git.GenericArguments.Count);
                        foreach (var ga in git.GenericArguments)
                            hash = HashType(ga, hash, includeScope);
                        break;

                    case ArrayType at:
                        hash = Mix(hash, 0x415252); // "ARR"
                        hash = Mix(hash, at.Rank);
                        hash = Mix(hash, at.Dimensions?.Count ?? 0);
                        if (at.Dimensions != null)
                        {
                            foreach (var d in at.Dimensions)
                            {
                                hash = Mix(hash, d.LowerBound ?? 0);
                                hash = Mix(hash, d.UpperBound ?? 0);
                            }
                        }
                        break;

                    case ByReferenceType _:
                        hash = Mix(hash, 0x425952); // "BYR"
                        break;

                    case PointerType _:
                        hash = Mix(hash, 0x505452); // "PTR"
                        break;

                    case PinnedType _:
                        hash = Mix(hash, 0x50494E); // "PIN"
                        break;

                    case SentinelType _:
                        hash = Mix(hash, 0x53454E); // "SEN"
                        break;

                    case RequiredModifierType rmt:
                        hash = Mix(hash, 0x4D4F4452); // "MODR"
                        hash = HashType(rmt.ModifierType, hash, includeScope);
                        break;

                    case OptionalModifierType omt:
                        hash = Mix(hash, 0x4D4F444F); // "MODO"
                        hash = HashType(omt.ModifierType, hash, includeScope);
                        break;

                    case FunctionPointerType fpt:
                        hash = Mix(hash, 0x464E5054); // "FNPT"
                        hash = Mix(hash, (int)fpt.CallingConvention);
                        hash = Mix(hash, fpt.HasThis ? 1 : 0);
                        hash = Mix(hash, fpt.ExplicitThis ? 1 : 0);

                        hash = HashType(fpt.ReturnType, hash, includeScope);

                        hash = Mix(hash, fpt.Parameters.Count);
                        foreach (var p in fpt.Parameters)
                            hash = HashType(p.ParameterType, hash, includeScope);
                        break;

                    default:
                        hash = Mix(hash, OrdinalHash(spec.GetType().FullName ?? spec.GetType().Name));
                        break;
                }

                return hash;
            }
        }

        private static int HashGenericParameter(GenericParameter gp, int hash)
        {
            unchecked
            {
                // Cecil FullName：!0（类型形参）/ !!0（方法形参）——核心是 Type + Position
                hash = Mix(hash, 0x47454E47); // "GENG"
                hash = Mix(hash, (int)gp.Type);
                hash = Mix(hash, gp.Position);
                hash = Mix(hash, gp.Name == null ? 0 : OrdinalHash(gp.Name));
                return hash;
            }
        }

        private static int Mix(int hash, int value) => unchecked(hash * Prime + value);

        private static int OrdinalHash(string s)
        {
            unchecked
            {
                const uint fnvOffset = 2166136261;
                const uint fnvPrime = 16777619;

                uint h = fnvOffset;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= fnvPrime;
                }
                return (int)h;
            }
        }
    }

}
