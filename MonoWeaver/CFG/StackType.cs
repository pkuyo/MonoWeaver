using Mono.Cecil;
using MonoWeaver.Utils;
using System;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using static MonoWeaver.Utils.CecilHelper;
namespace MonoWeaver.CFG
{
    /// <summary>
    /// 评估栈节点
    /// </summary>
    public class EvalStackNode
    {
        public EvalStackNode(StackType type, int depth = 0)
        {
            Type = type;
            Depth = depth;
        }

        public StackType Type;

        public EvalStackNode? Parent;

        public int Depth;

        public void Disconnect()
        {
            Parent = null;
        }

        public EvalStackNode AppendChild(StackType type)
        {
            var node = new EvalStackNode(type, Depth + 1)
            {
                Parent = this
            };
            return node;
        }
    }


    [Flags]
    public enum StackTypeFlags : byte
    {
        None = 0,
        ReadOnly = 1 << 1,
        PermanentHome = 1 << 2,
        ThisPtr = 1 << 3,
    }


    public enum VerificationType : byte
    {
        Invalid = 0,
        BuiltIn = 1,    //无TypeRef
        ByRef = 2,      //有TypeRef
        O = 3,          //除null有TypeRef
        ValueType = 4,  //有TypeRef
        TypeRef = 5,
    }
    

    public enum BuiltInType : byte
    {
        None = 0,
        I4 = 1 << 2,
        I8 = 1 << 3,
        I  = 1 << 4,      
        F = 1 << 5,
        Null = 1 << 6, //这个不属于VerificationType.BuiltIn
    }


    public readonly struct StackType : IEquatable<StackType>
    {

        public static readonly StackType I4 = new StackType(BuiltInType.I4);
        public static readonly StackType I8 = new StackType(BuiltInType.I8);
        public static readonly StackType F = new StackType(BuiltInType.F);
        public static readonly StackType I = new StackType(BuiltInType.I);
        public static readonly StackType Null = new StackType(BuiltInType.Null);
        public static readonly StackType Invalid = new StackType(BuiltInType.None);
        public static readonly StackType TypeRef = new StackType(null, BuiltInType.None, VerificationType.TypeRef, StackTypeFlags.None);

        public static StackType Create(TypeReference type, StackTypeFlags flag = StackTypeFlags.None)
        {
            type = type.StripType();

 
            if (type is PointerType ptrType)
            {
                return CreatePtr(ptrType);
            }
            else if (type is ByReferenceType refType)
            {
                return CreateByRef(refType.ElementType);
            }
          
            var cmpType = type.GetEnumType() ?? type;
            var typeSystem = type.Module.TypeSystem;
            TypeReference? outType = null;
            var verifyType = VerificationType.BuiltIn;
            var builtInType = BuiltInType.None;

            if (typeSystem.Boolean == cmpType || typeSystem.Byte == cmpType || typeSystem.SByte == cmpType ||
                  typeSystem.Int16 == cmpType || typeSystem.UInt16 == cmpType || typeSystem.Char == cmpType ||
                  typeSystem.Int32 == cmpType || typeSystem.UInt32 == cmpType)
            {
                builtInType = BuiltInType.I4;
            }
            else if (typeSystem.Int64 == cmpType || typeSystem.UInt64 == cmpType)
            {
                builtInType = BuiltInType.I8;
            }
            else if (typeSystem.Single == cmpType || typeSystem.Double == cmpType)
            {
                builtInType = BuiltInType.F;
            }
            else if (typeSystem.IntPtr == cmpType || typeSystem.UIntPtr == cmpType)
            {
                builtInType = BuiltInType.I;
            }
            else
            {
                verifyType = cmpType.IsValueType ? VerificationType.ValueType : VerificationType.O;
                outType = cmpType;
            }
            return new StackType(outType, builtInType, verifyType, flag);
        }

        public static StackType CreatePtr(TypeReference type, StackTypeFlags flag = StackTypeFlags.None)
        {
            type = type.StripType(); //未托管指针内可能指向ORef/ValueType/ByRef/Pointer 不取ElementType
            return new StackType(type, BuiltInType.I, VerificationType.BuiltIn, flag);
        }

        public static StackType CreateByRef(TypeReference type, StackTypeFlags flag = StackTypeFlags.None)
        {
            type = type.StripType();
            var typeSystem = type.Module.TypeSystem;

            if (type is PointerType)
            {
                throw new Exception();
            }
            else if (type is ByReferenceType refType)
            {
                throw new Exception();
            }
            if (typeSystem.Boolean == type || typeSystem.Byte == type || typeSystem.SByte == type)
            {
                type = typeSystem.Byte;
            }
            else if(typeSystem.Int16 == type || typeSystem.UInt16 == type || typeSystem.Char == type)
            {
                type = typeSystem.Int16;
            }
            else if(typeSystem.Int32 == type || typeSystem.UInt32 == type)
            {
                type = typeSystem.Int32;
            }
            else if (typeSystem.Int64 == type || typeSystem.UInt64 == type)
            {
                type = typeSystem.Int64;
            }
            else if (typeSystem.IntPtr == type || typeSystem.UIntPtr == type)
            {
                type = typeSystem.IntPtr;
            }
            return new StackType(type, BuiltInType.None, VerificationType.ByRef, flag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static StackType CreateBoxed(TypeReference type)
        {
            if (!type.IsValueType)
                throw new ArgumentException("boxedType must be a value type");
            
            return new StackType(type.GetEnumType() is not null ? type.Module.ImportReference(typeof(Enum)) : type.Module.ImportReference(typeof(ValueType)), 
                BuiltInType.None, VerificationType.O, StackTypeFlags.None, type);

        }

        private StackType(
            TypeReference? type,
            BuiltInType builtIn,
            VerificationType verificationType,
            StackTypeFlags flags,
            TypeReference? boxedType)
        {
            if (builtIn is BuiltInType.None && verificationType is VerificationType.BuiltIn)
                throw new Exception();
            if (builtIn is not BuiltInType.Null && type is null && verificationType is VerificationType.O)
                throw new Exception();
            BuiltInType = builtIn;
            VerifyType = verificationType;
            BoxedType = boxedType;
            Flags = flags;
            Type = type;
        }


        private StackType(
            TypeReference? type,
            BuiltInType builtIn,
            VerificationType verificationType,
            StackTypeFlags flags) : this(type, builtIn, verificationType, flags, null)
        { }


        private StackType(BuiltInType builtIn, VerificationType verifyType = VerificationType.Invalid)
            : this(null, builtIn, builtIn switch
            {
                _ when (verifyType is VerificationType.ByRef) => verifyType,
                BuiltInType.Null => VerificationType.O,
                BuiltInType.None => VerificationType.Invalid,
                _ when (Enum.IsDefined((typeof(BuiltInType)), builtIn)) => VerificationType.BuiltIn,
                _ => throw new ArgumentOutOfRangeException(nameof(builtIn))
            }, StackTypeFlags.None)
        { }


        public bool IsValueType => (VerifyType is VerificationType.ValueType or VerificationType.BuiltIn) && !IsPtr;

        public bool IsBoxedType => VerifyType is VerificationType.O && BoxedType != null;

        //针对Ptr的更多细节处理在外部验证器处理
        public bool IsPtr => VerifyType is VerificationType.BuiltIn && BuiltInType is BuiltInType.I && Type is not null;

        public TypeReference? InnerType
        {
            get
            {
                if(IsPtr) return ((PointerType)Type!).ElementType;
                return Type;
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator StackType(TypeReference type) => Create(type);

  

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StackType UnBox()
        {
            if (!IsBoxedType)
                throw new ArgumentException();
            return Create(BoxedType!);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StackType RefToValue()
        {
            if (VerifyType is not VerificationType.ByRef)
                throw new Exception();
            return Create(Type!);
        }

        //可栈上隐式转换
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool StackValueEqualsTo(StackType right)
        {
            if (right.VerifyType != VerifyType)
                return false;

            //装箱类型处理
            if (IsBoxedType && right.IsBoxedType)
            {
                return BoxedType.IsSameWith(right.BoxedType);

            }
            else if (right.IsBoxedType)
            {
                return false;
            }
            else if (IsBoxedType)
            {
                return BoxedType!.IsILStackAssignableTo(right.Type);
            }

            if (BuiltInType == BuiltInType.Null && right.VerifyType == VerificationType.O)
                return true;
            
            if(VerifyType is VerificationType.BuiltIn &&
                BuiltInType != BuiltInType.None &&
                BuiltInType == right.BuiltInType)
            {
                return true;
            }
            else if(VerifyType is VerificationType.ValueType)
            {
                //值类型
                return Type!.IsSameWith(right!.Type);
            }
            else if (VerifyType is VerificationType.ByRef)
            {
                //BtRef
                if(Type!.IsSameWith(right!.Type))
                {
                    if ((Flags & StackTypeFlags.ReadOnly) <= (right.Flags & StackTypeFlags.ReadOnly))
                        return true;
                }
                return false;
            }

            //非null引用类型
            return Type!.IsILStackAssignableTo(right.Type);
        }

        //合并取公共类
        public StackType Intersect(StackType other)
        {
            if (other.VerifyType != VerifyType)
                return Invalid;

            //值/ByRef类型严格相等
            if(other.VerifyType is VerificationType.ValueType or VerificationType.ByRef)
            {
                const StackTypeFlags RO = StackTypeFlags.ReadOnly;
                var mergedFlags =
                    ((Flags & other.Flags) & ~RO)   // 其他位取与
                  | ((Flags | other.Flags) & RO);
                return Type!.IsSameWith(other!.Type) ? new StackType(Type, BuiltInType, VerifyType, //Readonly取或 其他Flag取与
                   mergedFlags) : Invalid; 
            }

            //指针类型保留
            if (IsPtr && !other.IsPtr && other.BuiltInType is BuiltInType.I)
                return this;
            if (other.IsPtr && !IsPtr && BuiltInType is BuiltInType.I)
                return other;


            //可转换
            if (StackValueEqualsTo(other))
                return other;

            if (other.StackValueEqualsTo(this))
                return this;


            //引用判断是否为Boxed类型不一致，后找基类
            if (other.Type is not null && Type is not null)
            {
                if(other.IsBoxedType && IsBoxedType) //只能是Boxed情况
                {
                    return Create(other.Type); //去除Boxed类型信息
                }

                if(ResolveWithCache(Type)?.IsInterface == true ||
                    ResolveWithCache(other.Type)?.IsInterface == true)
                {
                    return Type.Module.TypeSystem.Object;
                }
                var ret = FindCommonBaseType(other.Type, Type);
                return ret is null ? Invalid : Create(ret);
            }
            return Invalid;
        }

       

        //严格等价
        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public bool Equals(StackType other) 
        {
            if (other.VerifyType != VerifyType || other.Flags != Flags)
                return false;

            if (BuiltInType != other.BuiltInType)
                return false;

            if (BuiltInType == BuiltInType.Null)
                return other.BuiltInType == BuiltInType.Null;

            if (VerifyType is VerificationType.BuiltIn && BuiltInType != BuiltInType.None) //为了弱智PTR
            {
                if (Type is null) return other.Type is null;
                return other.Type is not null && Type.IsSameWith(other.Type);
            }
            //非null引用类型和值类型
            return (BoxedType?.IsSameWith(other.BoxedType) ?? true) && Type!.IsSameWith(other!.Type);
            
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static bool operator==(in StackType a, in StackType b)
        {
            return a.Equals(b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static bool operator!=(in StackType a, in StackType b)
        {
            return !a.Equals(b);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)VerifyType;
                h = (h * 397) ^ (int)BuiltInType;
                h = (h * 397) ^ (int)Flags;
                h = (h * 397) ^ (Type != null ? TypeSig.Create(Type).GetHashCode() : 0);
                h = (h * 397) ^ (BoxedType != null ? TypeSig.Create(BoxedType).GetHashCode() : 0);
                return h;
            }
        }

        public override bool Equals(object? obj) => obj is StackType st && Equals(st);

        public readonly VerificationType VerifyType;
        public readonly StackTypeFlags Flags;

        public readonly BuiltInType BuiltInType;
        public readonly TypeReference? Type;

        public readonly TypeReference? BoxedType;
    }
}
