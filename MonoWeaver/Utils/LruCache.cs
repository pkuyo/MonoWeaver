using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MonoWeaver.Utils
{
    public class LruCache<TKey, TValue> 
    {
        private struct Entry
        {
            public int Next;
            public int Previous;
            public TKey Key;
            public TValue Value;
        }

        private readonly int _capacity;
        private readonly Dictionary<TKey, int> _indexMap;
        private readonly Entry[] _entries;

        private int _head;
        private int _tail;
        private int _count;

        private const int NullPtr = -1;

        public LruCache(int capacity)
        {
            if (capacity <= 0) throw new ArgumentException("Capacity must be > 0");
            _capacity = capacity;
            _indexMap = new Dictionary<TKey, int>(capacity);
            _entries = new Entry[capacity];

            _head = NullPtr;
            _tail = NullPtr;
            _count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TValue Get(TKey key)
        {
            if (_indexMap.TryGetValue(key, out int index))
            {
                MoveToHead(index);
                return _entries[index].Value;
            }
            throw new KeyNotFoundException();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(TKey key, out TValue value)
        {
            if (_indexMap.TryGetValue(key, out int index))
            {
                MoveToHead(index);
                value = _entries[index].Value;
                return true;
            }
            value = default;
            return false;
        }

        public void Put(TKey key, TValue value)
        {
            if (_indexMap.TryGetValue(key, out int index))
            {
                _entries[index].Value = value;
                MoveToHead(index);
            }
            else
            {
                if (_count < _capacity)
                {
                    index = _count;
                    _count++;
                    _entries[index].Key = key;
                    _entries[index].Value = value;

                    AddToHead(index);
                }
                else
                {
                    index = _tail;

                    _indexMap.Remove(_entries[index].Key);
                    RemoveFromTail();
                    _entries[index].Key = key;
                    _entries[index].Value = value;
                    AddToHead(index);
                }

                _indexMap[key] = index;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MoveToHead(int index)
        {
            if (index == _head) return;

    
            int prev = _entries[index].Previous;
            int next = _entries[index].Next;

            if (prev != NullPtr) _entries[prev].Next = next;
            if (next != NullPtr) _entries[next].Previous = prev;

            if (index == _tail) _tail = prev;

            _entries[index].Next = _head;
            _entries[index].Previous = NullPtr;

            if (_head != NullPtr) _entries[_head].Previous = index;
            _head = index;

            if (_tail == NullPtr) _tail = index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddToHead(int index)
        {
  
            _entries[index].Next = _head;
            _entries[index].Previous = NullPtr;

            if (_head != NullPtr) _entries[_head].Previous = index;
            _head = index;

            if (_tail == NullPtr) _tail = index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RemoveFromTail()
        {
            if (_tail == NullPtr) return;

            int prev = _entries[_tail].Previous;
            if (prev != NullPtr)
            {
                _entries[prev].Next = NullPtr;
            }
            else
            {
                _head = NullPtr;
            }
            _tail = prev;
        }
    }
}
