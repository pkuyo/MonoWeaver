using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Mono.Cecil;
// ReSharper disable InconsistentNaming
namespace MonoWeaver.CFG 
{

    public enum StackSlotKind : byte 
    {
        Unknown = 0,
        I4,
        I8,
        I,      // native int
        R4,
        R8,
        ByRef,
        Ptr,
        O,      // object ref
        ValueType
    }
    
    public readonly struct StackSlotType : IEquatable<StackSlotType>
    {
        public readonly StackSlotKind Kind;
        public readonly TypeReference Type;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StackSlotType(StackSlotKind kind, TypeReference type) 
        {
            Kind = kind;
            Type = type;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static StackSlotType I4(ModuleDefinition m) { return new StackSlotType(StackSlotKind.I4, m.TypeSystem.Int32); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static StackSlotType I8(ModuleDefinition m) { return new StackSlotType(StackSlotKind.I8, m.TypeSystem.Int64); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static StackSlotType I(ModuleDefinition m) { return new StackSlotType(StackSlotKind.I, m.TypeSystem.IntPtr); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static StackSlotType O(ModuleDefinition m) { return new StackSlotType(StackSlotKind.O, m.TypeSystem.Object); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(StackSlotType other)
        {
            return Kind == other.Kind && ReferenceEquals(Type, other.Type);
        }

        public override bool Equals(object? obj) { return obj is StackSlotType type && Equals(type); }

        public override int GetHashCode() 
        {
            unchecked 
            {
                int h = ((int)Kind * 486187739);
                int th;
                var tok = Type != null ? Type.MetadataToken : default;
                if (tok.RID != 0) th = tok.ToInt32();
                else th = Type != null ? RuntimeHelpers.GetHashCode(Type) : 0;
                return (h * 16777619) ^ th;
            }
        }

        public override string ToString() { return Kind + ":" + (Type != null ? Type.FullName : "null"); }
    }

    public readonly struct StackStateId(int v) : IEquatable<StackStateId>
    {
        public int Value { get; } = v;
        public bool Equals(StackStateId other) { return Value == other.Value; }
        public override int GetHashCode() { return Value; }
        public override string ToString() { return "S" + Value; }
    }
    
    public struct StackSlice 
    {
        public readonly StackSlotType[] Array;
        public readonly int Offset;
        public readonly int Length;

        public StackSlice(StackSlotType[] array, int offset, int length) {
            Array = array;
            Offset = offset;
            Length = length;
        }

        public StackSlotType this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Array[Offset + index];
        }
    }

    internal struct StackStateDesc 
    {
        public int Chunk;
        public int Offset;
        public ushort Length;
        public int Hash;
        public int Next;
    }

    internal struct Fingerprint : IEquatable<Fingerprint> {
        public int Hash;
        public ushort Len;
        public Fingerprint(int hash, ushort len) { Hash = hash; Len = len; }
        public bool Equals(Fingerprint other) { return Hash == other.Hash && Len == other.Len; }
        public override int GetHashCode() { return Hash ^ (Len << 16); }
    }


    public sealed class EvalStackStatePool {
        private readonly int _chunkSize;
        private readonly List<StackSlotType[]> _chunks = new();
        private int _curChunk = -1;
        private int _curPos;

        private readonly List<StackStateDesc> _states = new();
        private readonly Dictionary<Fingerprint, int> _heads = new();

        public static readonly StackStateId Empty = new (0);

        public EvalStackStatePool(int chunkSizeSlots = -1) 
        {
            _chunkSize = chunkSizeSlots <= 0 ? (1 << 8) : chunkSizeSlots;

            EnsureChunk(0);
            _states.Add(new StackStateDesc { Chunk = 0, Offset = 0, Length = 0, Hash = 0, Next = -1 });
        }

        /// <summary>
        /// 获取栈状态
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StackSlice GetSlice(StackStateId id) 
        {
            var d = _states[id.Value];
            return new StackSlice(_chunks[d.Chunk], d.Offset, d.Length);
        }

        /// <summary>
        /// 记录新的栈状态
        /// </summary>
        /// <param name="arr">类型</param>
        /// <param name="offset">起始位置（用于数组切片）</param>
        /// <param name="length">长度（用于数组切片）</param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public StackStateId Intern(StackSlotType[] arr, int offset, int length)
        {
            if (length == 0) return Empty;
            if (length < 0 || length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(length));

            int hash = HashSlots(arr, offset, length);
            var fp = new Fingerprint(hash, (ushort)length);

            int head;
            if (_heads.TryGetValue(fp, out head))
            {
                for (int i = head; i != -1; i = _states[i].Next) 
                {
                    var d = _states[i];
                    if (d.Hash != hash || d.Length != length) continue;
                    if (SequenceEqual(_chunks[d.Chunk], d.Offset, arr, offset, length))
                        return new StackStateId(i);
                }
            }

            EnsureCapacity(length);
            int chunk = _curChunk;
            int off = _curPos;

            //存储仅chunk的一段区域 (offset length)
            var dst = _chunks[chunk];
            for (int i = 0; i < length; i++) {
                dst[off + i] = arr[offset + i];
            }
            _curPos += length;

            int newIndex = _states.Count;
            var oldHead = HeadExists(fp) ? _heads[fp] : -1; //hash重复，采用链表存储
            
            //添加索引
            _states.Add(new StackStateDesc 
            {
                Chunk = chunk,
                Offset = off,
                Length = (ushort)length,
                Hash = hash,
                Next = oldHead
            });

            _heads[fp] = newIndex;
            return new StackStateId(newIndex);
        }

        private bool HeadExists(Fingerprint fp)
        {
            return _heads.TryGetValue(fp, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int HashSlots(StackSlotType[] arr, int offset, int length) 
        {
            unchecked 
            {
                uint h = 2166136261;
                for (int i = 0; i < length; i++) 
                {
                    h = (uint)((h ^ arr[offset + i].GetHashCode()) * 16777619U);
                }
                return (int)h;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SequenceEqual(
            StackSlotType[] a, int aOff,
            StackSlotType[] b, int bOff,
            int length
        ) {
            for (int i = 0; i < length; i++) {
                if (!a[aOff + i].Equals(b[bOff + i])) return false;
            }
            return true;
        }

        /// <summary>
        /// 确保chunk足够
        /// </summary>
        /// <param name="index"></param>
        private void EnsureChunk(int index) 
        {
            while (_chunks.Count <= index) 
            {
                _chunks.Add(new StackSlotType[_chunkSize]);
            }
        }

        /// <summary>
        /// 确保空间足够
        /// </summary>
        /// <param name="need"></param>
        private void EnsureCapacity(int need) 
        {
            if (_curChunk < 0) {
                _curChunk = 0;
                _curPos = 0;
                EnsureChunk(0);
                return;
            }
            if (_curPos + need <= _chunkSize) return;
            _curChunk++;
            _curPos = 0;
            EnsureChunk(_curChunk);
        }
    }


    public struct EvalStackTransfer(ushort keepFromEntry, StackStateId pushed, short deltaHeight)
    {
        public ushort KeepFromEntry = keepFromEntry;
        public StackStateId Pushed  = pushed;
        public short DeltaHeight = deltaHeight;
    }


    public struct ExitStackView 
    {
        private StackSlice _kept;
        private StackSlice _pushed;

        public int Length => _kept.Length + _pushed.Length;

        public ExitStackView(StackSlice entry, EvalStackTransfer tr, EvalStackStatePool pool) 
        {
            _kept = new StackSlice(entry.Array, entry.Offset, tr.KeepFromEntry);
            _pushed = pool.GetSlice(tr.Pushed);
        }

        public StackSlotType this[int index] 
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (index < _kept.Length) return _kept[index];
                return _pushed[index - _kept.Length];
            }
        }
    }

    public static class ScratchStackBuffer 
    {
        [ThreadStatic] private static StackSlotType[]? _buf;

        public static StackSlotType[] Rent(int minLen) 
        {
            var b = _buf;
            if (b == null || b.Length < minLen) 
            {
                int newLen = 1;
                while (newLen < minLen) newLen <<= 1;
                b = new StackSlotType[newLen];
                _buf = b;
            }
            return b;
        }
    }


    public struct StackSlotKey 
    {
        public StackSlotKind Kind;
        public int TypeToken;
    }


    public sealed class TypeTokenResolver 
    {
        private readonly ModuleDefinition _module;
        private readonly Dictionary<int, TypeReference> _cache = new();

        public TypeTokenResolver(ModuleDefinition module) 
        {
            _module = module;
            var ts = module.TypeSystem;
            Add(ts.Int32);
            Add(ts.Int64);
            Add(ts.IntPtr);
            Add(ts.Object);
        }

        private void Add(TypeReference tr)
        {
            var tok = tr.MetadataToken;
            if (tok.RID == 0) return;
            _cache[tok.ToInt32()] = tr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TypeReference Resolve(int token)
        {
            if (_cache.TryGetValue(token, out var tr)) return tr;
            
            var prov = _module.LookupToken(token);
            tr = prov as TypeReference;
            _cache[token] = tr ?? throw new InvalidOperationException("Token is not a TypeReference: " + token);
            return tr;
        }
    }
}