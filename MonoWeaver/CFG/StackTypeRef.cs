using Mono.Cecil;
using MonoWeaver.Utils;
using System;
namespace MonoWeaver.CFG
{
    public enum VerificationType : byte
    {
        Invalid = 0,
        BuiltIn = 1,
        ByRef = 2,
        O = 3,
        ValueType = 4,
    }

    [Flags]
    public enum BuiltInType : ushort
    {
        None = 0,
        I1 = 1 << 0,
        I2 = 1 << 1,
        I4 = 1 << 2,
        I8 = 1 << 3,
        I  = 1 << 4,      // native int
        R4 = 1 << 5,
        R8 = 1 << 6,
        Null = 1 << 7,  // not built in
        F = R4 | R8,
    }

    public class StackTypeRef
    {
        public static readonly StackTypeRef I4 = new StackTypeRef(BuiltInType.I4);
        public static readonly StackTypeRef I8 = new StackTypeRef(BuiltInType.I8);
        public static readonly StackTypeRef I = new StackTypeRef(BuiltInType.I);
        public static readonly StackTypeRef Ptr = I;
        public static readonly StackTypeRef F = new StackTypeRef(BuiltInType.F);
        public static readonly StackTypeRef Null = new StackTypeRef(BuiltInType.Null);
        public static readonly StackTypeRef Invalid = new StackTypeRef(BuiltInType.None);

        public static StackTypeRef Create(BuiltInType type)
        {
            return type switch
            {
                BuiltInType.I4 => I4,
                BuiltInType.I8 => I8,
                BuiltInType.I => I,
                BuiltInType.F => F,
                _ => new StackTypeRef(type)
            };
        }

        public static StackTypeRef Create(TypeReference type)
        {
            type = type.StripType();

            var typeSystem = type.Module.TypeSystem;


            if (type is PointerType)
            {
                return Ptr;
            }
            else
            {
                var cmpType = type.GetEnumType() ?? type;
                if (typeSystem.Boolean == cmpType ||
                   typeSystem.Int32 == cmpType || typeSystem.UInt32 == cmpType ||
                   typeSystem.Int16 == cmpType || typeSystem.UInt16 == cmpType ||
                   typeSystem.Byte == cmpType || typeSystem.Char == cmpType ||
                   typeSystem.SByte == cmpType)
                {
                    return I4;
                }
                if (typeSystem.Int64 == cmpType || typeSystem.UInt64 == cmpType)
                {
                    return I8;
                }

                if (typeSystem.IntPtr == cmpType || typeSystem.UIntPtr == cmpType)
                {
                    return I;
                }
                if (typeSystem.Single == cmpType || typeSystem.Double == cmpType)
                {
                    return F;
                }
            }
            return new StackTypeRef(type);
        }


        private StackTypeRef(BuiltInType builtIn)
        {
            BuiltInType = builtIn;
            VerifyType = BuiltInType switch
            {
                BuiltInType.Null => VerificationType.O,
                BuiltInType.None => VerificationType.Invalid,
                _ when (Enum.IsDefined((typeof(BuiltInType)), BuiltInType)) => VerificationType.BuiltIn,
                _ => throw new ArgumentOutOfRangeException(nameof(builtIn))
            };

            if (builtIn != BuiltInType.Null)
                VerifyType = VerificationType.BuiltIn;
            else
                VerifyType = VerificationType.O;
        }


        private StackTypeRef(TypeReference type)
        {
            var typeSystem = type.Module.TypeSystem;
            if (type is ByReferenceType refType)
            {
                var eleType = refType.ElementType;
                var enumType = eleType.GetEnumType();
                Type = type; //对于ByRef的类型不能直接合并
                if (enumType != null) eleType = enumType;

                VerifyType = VerificationType.ByRef;

                if (typeSystem.Boolean == eleType || typeSystem.Byte == eleType || typeSystem.SByte == eleType)
                {
                    BuiltInType |= BuiltInType.I1;
                    return;
                }
                if (typeSystem.Int16 == eleType || typeSystem.UInt16 == eleType || typeSystem.Char == eleType)
                {
                    BuiltInType |= BuiltInType.I2;
                    return;
                }
                if (typeSystem.Int32 == eleType || typeSystem.UInt32 == eleType)
                {
                    BuiltInType |= BuiltInType.I4;
                    return;
                }
                if (typeSystem.Int64 == eleType || typeSystem.UInt64 == eleType)
                {
                    BuiltInType |= BuiltInType.I8;
                    return;
                }
                if (typeSystem.Single == eleType)
                {
                    BuiltInType |= BuiltInType.R4;
                    return;
                }
                if (typeSystem.Double == eleType)
                {
                    BuiltInType |= BuiltInType.R8;
                    return;
                }
                if (typeSystem.IntPtr == eleType || typeSystem.UIntPtr == eleType)
                {
                    BuiltInType |= BuiltInType.I;
                    return;
                }

            }
            if (type.IsValueType) VerifyType = VerificationType.ValueType;
            else VerifyType = VerificationType.O;
            Type = type;
        }

        public bool IsValueType => VerifyType is VerificationType.ValueType or VerificationType.BuiltIn;

        public bool IsBuiltInNum => VerifyType is VerificationType.BuiltIn;

        public readonly VerificationType VerifyType;
        public readonly BuiltInType BuiltInType;

        public readonly TypeReference? Type;

        public static implicit operator StackTypeRef(TypeReference type) => Create(type);



        public bool CanConvertTo(StackTypeRef? right)
        {
            if (right is null)
                return false;

            if (right.VerifyType != VerifyType)
                return false;

            if (BuiltInType == BuiltInType.Null && right.VerifyType == VerificationType.O)
                return true;
            
            if(VerifyType is VerificationType.ByRef or VerificationType.BuiltIn && 
                BuiltInType != BuiltInType.None && 
                BuiltInType == right.BuiltInType)
            {
                return true;
            }
            else if(VerifyType is VerificationType.ByRef or VerificationType.ValueType)
            {
                return Type!.IsSameType(right!.Type);
            }
           
            return Type!.IsILStackAssignableTo(right.Type);
        }

        //合并取公共类
        public StackTypeRef? Intersect(StackTypeRef? other)
        {
            if (other is null || other.VerifyType != VerifyType)
                return null;


            if (Narrowest(other) is { } re)
                return re;

            if(other.VerifyType is VerificationType.ValueType or VerificationType.ByRef)
            {
                return Type!.IsSameType(other!.Type) ? this : null;
            }

            if (CanConvertTo(other))
                return other;

            if (other.CanConvertTo(this))
                return this;

          

            if (other.Type is not null && Type is not null)
            {
                if(CecilHelper.ResolveWithCache(Type)?.IsInterface == true ||
                    CecilHelper.ResolveWithCache(other.Type)?.IsInterface == true)
                {
                    return Type.Module.TypeSystem.Object;
                }
                var ret = CecilHelper.FindCommonBaseType(other.Type, Type);
                return ret is null ? null : Create(ret);
            }
            return null;
        }

        //约束收窄 仅针对builtIn
        public StackTypeRef? Narrowest(StackTypeRef? to) 
        {
            if (to is null)
                return null;

            if (to.VerifyType != VerificationType.BuiltIn || VerifyType != VerificationType.BuiltIn)
                return null;

            var builtIn = to.BuiltInType & BuiltInType;
            if (builtIn != 0)
                return Create(builtIn);

            return null;
        }
    }
}
