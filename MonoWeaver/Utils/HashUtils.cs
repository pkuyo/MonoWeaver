using Mono.Cecil;
using System;
using System.Runtime.CompilerServices;

namespace MonoWeaver.Utils
{
    
    public readonly struct Hash128 : IEquatable<Hash128>
    {
        public readonly ulong Low;
        public readonly ulong High;

        public Hash128(ulong low, ulong high)
        {
            Low = low;
            High = high;
        }
        

        public bool Equals(Hash128 other) => Low == other.Low && High == other.High;
        public override bool Equals(object? obj) => obj is Hash128 h && Equals(h);

        public override int GetHashCode()
        {
            unchecked
            {
                ulong x = Low ^ High;
                return (int)x ^ (int)(x >> 32);
            }
        }

        public override string ToString()
        {
            return High.ToString("x16") + Low.ToString("x16");
        }

        public static bool operator ==(Hash128 a, Hash128 b) => a.Equals(b);
        public static bool operator !=(Hash128 a, Hash128 b) => !a.Equals(b);
    }

    internal static class Hasher128Cache
    {
        [ThreadStatic] private static Hasher128? _h;

        public static Hasher128 Rent(ulong seed)
        {
            var h = _h;
            if (h == null) _h = h = new Hasher128(seed);
            h.Reset(seed);
            return h;
        }
    }

    public sealed unsafe class Hasher128
    {
        // MurmurHash3 x64 128 常量
        private const ulong C1 = 0x87c37b91114253d5UL;
        private const ulong C2 = 0x4cf5ad432745937fUL;

        private ulong _h1;
        private ulong _h2;
        private ulong _length;
        private int _tailLen;
        private readonly byte[] _tail = new byte[16];

        public Hasher128(ulong seed = 0) => Reset(seed);

        public void Reset(ulong seed = 0)
        {
            _h1 = seed;
            _h2 = seed;
            _length = 0;
            _tailLen = 0;
        }
        
        public void Update(byte[] data) => Update(data, 0, data.Length);

        public void Update(byte[] data, int offset, int count)
        {
            if (count == 0) return;

            fixed (byte* p0 = data)
            {
                Update(p0 + offset, count);
            }
        }
        
        public void UpdateString(string? s, bool includeLength = true)
        {
            if (s == null)
            {
                if (includeLength) UpdateInt32(-1);
                return;
            }

            if (includeLength) UpdateInt32(s.Length);

            fixed (char* cp = s)
            {
                Update((byte*)cp, checked(s.Length * 2));
            }
        }

      
        public void UpdateInt32(int v) { Update((byte*)&v, 4); }
        public void UpdateUInt32(uint v) { Update((byte*)&v, 4); }
        public void UpdateInt64(long v) { Update((byte*)&v, 8); }
        public void UpdateUInt64(ulong v) { Update((byte*)&v, 8); }
        public void UpdateSingle(float v) { Update((byte*)&v, 4); }
        public void UpdateDouble(double v) { Update((byte*)&v, 8); }
        public void UpdateBoolean(bool v) { byte b = v ? (byte)1 : (byte)0; Update(&b, 1); }

        public void UpdateGuid(Guid g)
        {
            Guid tmp = g;
            Update((byte*)&tmp, 16);
        }
        

        public Hash128 Digest()
        {
            ulong h1 = _h1;
            ulong h2 = _h2;
            
            ulong k1 = 0;
            ulong k2 = 0;

            fixed (byte* t = _tail)
            {
                switch (_tailLen)
                {
                    case 15: k2 ^= (ulong)t[14] << 48; goto case 14;
                    case 14: k2 ^= (ulong)t[13] << 40; goto case 13;
                    case 13: k2 ^= (ulong)t[12] << 32; goto case 12;
                    case 12: k2 ^= (ulong)t[11] << 24; goto case 11;
                    case 11: k2 ^= (ulong)t[10] << 16; goto case 10;
                    case 10: k2 ^= (ulong)t[9] << 8; goto case 9;
                    case 9:
                        k2 ^= (ulong)t[8];
                        k2 *= C2; k2 = Rotl64(k2, 33); k2 *= C1; h2 ^= k2;
                        goto case 8;

                    case 8: k1 ^= (ulong)t[7] << 56; goto case 7;
                    case 7: k1 ^= (ulong)t[6] << 48; goto case 6;
                    case 6: k1 ^= (ulong)t[5] << 40; goto case 5;
                    case 5: k1 ^= (ulong)t[4] << 32; goto case 4;
                    case 4: k1 ^= (ulong)t[3] << 24; goto case 3;
                    case 3: k1 ^= (ulong)t[2] << 16; goto case 2;
                    case 2: k1 ^= (ulong)t[1] << 8; goto case 1;
                    case 1:
                        k1 ^= (ulong)t[0];
                        k1 *= C1; k1 = Rotl64(k1, 31); k1 *= C2; h1 ^= k1;
                        break;
                }
            }

            // finalize
            ulong len = _length;
            h1 ^= len;
            h2 ^= len;

            h1 += h2;
            h2 += h1;

            h1 = Fmix64(h1);
            h2 = Fmix64(h2);

            h1 += h2;
            h2 += h1;

            return new Hash128(low: h1, high: h2);
        }

        private void Update(byte* data, int count)
        {
            if (count <= 0) return;

            _length += (ulong)count;

 
            if (_tailLen != 0)
            {
                int need = 16 - _tailLen;
                fixed (byte* t = _tail)
                {
                    if (count < need)
                    {
                        Buffer.MemoryCopy(data, t + _tailLen, need, count);
                        _tailLen += count;
                        return;
                    }
                    
                    Buffer.MemoryCopy(data, t + _tailLen, need, need);

                    ulong k1 = *(ulong*)(t + 0);
                    ulong k2 = *(ulong*)(t + 8);
                    BodyMix(ref _h1, ref _h2, k1, k2);
                }
                _tailLen = 0;
                data += need;
                count -= need;
            }
            
            int nblocks = count >> 4;
            for (int i = 0; i < nblocks; i++)
            {
                byte* block = data + (i << 4);
                ulong k1 = *(ulong*)(block + 0);
                ulong k2 = *(ulong*)(block + 8);
                BodyMix(ref _h1, ref _h2, k1, k2);
            }

            int consumed = nblocks << 4;
            data += consumed;
            count -= consumed;
            
            if (count > 0)
            {
                fixed (byte* t = _tail)
                    Buffer.MemoryCopy(data, t, 16, count);
                _tailLen = count;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BodyMix(ref ulong h1, ref ulong h2, ulong k1, ulong k2)
        {
            unchecked
            {
                k1 *= C1; k1 = Rotl64(k1, 31); k1 *= C2; h1 ^= k1;
                h1 = Rotl64(h1, 27); h1 += h2; h1 = h1 * 5 + 0x52dce729UL;

                k2 *= C2; k2 = Rotl64(k2, 33); k2 *= C1; h2 ^= k2;
                h2 = Rotl64(h2, 31); h2 += h1; h2 = h2 * 5 + 0x38495ab5UL;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Rotl64(ulong x, int r) => (x << r) | (x >> (64 - r));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Fmix64(ulong k)
        {
            unchecked
            {
                k ^= k >> 33;
                k *= 0xff51afd7ed558ccdUL;
                k ^= k >> 33;
                k *= 0xc4ceb9fe1a85ec53UL;
                k ^= k >> 33;
                return k;
            }
        }
    }
    internal class HashUtils
    {

        public static Hash128 GetTypeHash(TypeReference type, bool includeScope = false)
        {
            var hasher = Hasher128Cache.Rent(17); 
            HashType(type, hasher, includeScope);
            return hasher.Digest();
        }

        public static Hash128 GetMethodHash(
            MethodReference method,
            bool includeScope = false,
            bool includeThisAndCallConv = true,
            bool includeParamAttributes = false)
        {
            var hasher = Hasher128Cache.Rent(17);
            HashMethod(method, hasher, includeScope, includeThisAndCallConv, includeParamAttributes);
            return hasher.Digest();
        }

        private static void HashMethod(
            MethodReference method,
            Hasher128 hasher,
            bool includeScope,
            bool includeThisAndCallConv,
            bool includeParamAttributes)
        {
            unchecked
            {
                if (method is MethodSpecification ms)
                {
                    hasher.UpdateInt32(0x4D535045); // "MSPE"
                    HashMethod(ms.ElementMethod, hasher, includeScope, includeThisAndCallConv, includeParamAttributes);

                    if (method is GenericInstanceMethod gim)
                    {
                        hasher.UpdateInt32(0x47494D); // "GIM"
                        hasher.UpdateInt32(gim.GenericArguments.Count);
                        foreach (var ga in gim.GenericArguments)
                            HashType(ga, hasher, includeScope);
                    }
                }

                // 普通方法
                hasher.UpdateInt32( 0x4D524546); // "MREF"

                HashType(method.DeclaringType, hasher, includeScope);

                hasher.UpdateString(method.Name ?? string.Empty);

               
                if (method.HasGenericParameters)
                {
                    hasher.UpdateInt32( 0x47454E50); // "GENP"
                    hasher.UpdateInt32( method.GenericParameters.Count);
                    foreach (var gp in method.GenericParameters)
                        HashGenericParameter(gp, hasher);
                }
                else
                {
                    hasher.UpdateInt32(0);
                }

                // 返回类型
                HashType(method.ReturnType, hasher, includeScope);

                // this/调用约定（是否纳入取决于你想不想更“签名级别”）
                if (includeThisAndCallConv)
                {
                    hasher.UpdateInt32((int)method.CallingConvention);
                    hasher.UpdateBoolean(method.HasThis);
                    hasher.UpdateBoolean(method.ExplicitThis);
                }

                // 参数
                hasher.UpdateInt32(0x5041524D); // "PARM"
                hasher.UpdateInt32(method.Parameters.Count);
                foreach (var p in method.Parameters)
                {
                    HashType(p.ParameterType, hasher, includeScope);
                    if (includeParamAttributes)
                        hasher.UpdateInt32((int)p.Attributes);
                }
            }
        }

        private static void HashType(TypeReference type, Hasher128 hasher, bool includeScope)
        {
            unchecked
            {
                // GenericParameter
                if (type is GenericParameter gp)
                {
                    HashGenericParameter(gp, hasher);
                    return;
                }

                // TypeSpecification
                if (type is TypeSpecification spec)
                {
                    HashTypeSpecification(spec, hasher, includeScope);
                    return;
                }

                hasher.UpdateInt32( 0x54524546); // "TREF"

                if (includeScope)
                {
                    var scopeName = type.Scope?.Name;
                    hasher.UpdateString(scopeName ?? string.Empty);
                }

                if (type.IsNested && type.DeclaringType != null)
                { 
                    HashType(type.DeclaringType, hasher, includeScope);
                    hasher.UpdateInt32(0x2F); // '/'
                    hasher.UpdateString(type.Name);
                }
                else
                {
                    hasher.UpdateString(type.Namespace ?? string.Empty);
                    hasher.UpdateString(type.Name);
                }
            }
        }

        private static void HashTypeSpecification(TypeSpecification spec, Hasher128 hasher, bool includeScope)
        {
            unchecked
            {
                hasher.UpdateInt32(0x53504543); // "SPEC"
                HashType(spec.ElementType, hasher, includeScope);

                switch (spec)
                {
                    case GenericInstanceType git:
                        hasher.UpdateInt32(0x474954); // "GIT"
                        hasher.UpdateInt32(git.GenericArguments.Count);
                        foreach (var ga in git.GenericArguments)
                            HashType(ga, hasher, includeScope);
                        break;

                    case ArrayType at:
                        hasher.UpdateInt32(0x415252); // "ARR"
                        hasher.UpdateInt32(at.Rank);
                        hasher.UpdateInt32(at.Dimensions?.Count ?? 0);
                        if (at.Dimensions != null)
                        {
                            foreach (var d in at.Dimensions)
                            {
                                hasher.UpdateInt32(d.LowerBound ?? 0);
                                hasher.UpdateInt32(d.UpperBound ?? 0);
                            }
                        }
                        break;

                    case ByReferenceType _:
                        hasher.UpdateInt32(0x425952); // "BYR"
                        break;

                    case PointerType _:
                        hasher.UpdateInt32(0x505452); // "PTR"
                        break;

                    case PinnedType _:
                        hasher.UpdateInt32(0x50494E); // "PIN"
                        break;

                    case SentinelType _:
                        hasher.UpdateInt32(0x53454E); // "SEN"
                        break;

                    case RequiredModifierType rmt:
                        hasher.UpdateInt32(0x4D4F4452); // "MODR"
                        HashType(rmt.ModifierType, hasher, includeScope);
                        break;

                    case OptionalModifierType omt:
                        hasher.UpdateInt32(0x4D4F444F); // "MODO"
                        HashType(omt.ModifierType, hasher, includeScope);
                        break;

                    case FunctionPointerType fpt:
                        hasher.UpdateInt32(0x464E5054); // "FNPT"
                        hasher.UpdateInt32((int)fpt.CallingConvention);
                        hasher.UpdateInt32( fpt.HasThis ? 1 : 0);
                        hasher.UpdateInt32(fpt.ExplicitThis ? 1 : 0);

                        HashType(fpt.ReturnType, hasher, includeScope);

                        hasher.UpdateInt32( fpt.Parameters.Count);
                        foreach (var p in fpt.Parameters)
                            HashType(p.ParameterType, hasher, includeScope);
                        break;

                    default:
                        hasher.UpdateString(spec.GetType().FullName ?? spec.GetType().Name);
                        break;
                }
            }
        }

        private static void HashGenericParameter(GenericParameter gp, Hasher128 hasher)
        {
            unchecked
            {
                // Cecil FullName：!0（类型形参）/ !!0（方法形参） Type + Position
                hasher.UpdateInt32(0x47454E47); // "GENG"
                hasher.UpdateInt32((int)gp.Type);
                hasher.UpdateInt32(gp.Position);
                hasher.UpdateString(gp.Name ?? string.Empty);
            }
        }
        
    }

}
