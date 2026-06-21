using System;
using System.Collections.Generic;
using System.Text;

namespace MonoWeaver.Utils
{
    using System;
    using System.Collections.Generic;

    public sealed class ListStack<T>
    {
        private readonly List<T> _items;
        private int _version;

        public ListStack()
        {
            _items = new List<T>();
        }

        public ListStack(int capacity)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _items = new List<T>(capacity);
        }

        public int Count => _items.Count;

        public bool IsEmpty => _items.Count == 0;

        public void Push(T item)
        {
            _items.Add(item);
            _version++;
        }

        public T Pop()
        {
            if (_items.Count == 0)
                throw new InvalidOperationException("Stack is empty.");

            int lastIndex = _items.Count - 1;
            T value = _items[lastIndex];

            _items.RemoveAt(lastIndex);
            _version++;

            return value;
        }

        public bool TryPop(out T value)
        {
            if (_items.Count == 0)
            {
                value = default!;
                return false;
            }

            int lastIndex = _items.Count - 1;
            value = _items[lastIndex];

            _items.RemoveAt(lastIndex);
            _version++;

            return true;
        }

        public T Peek()
        {
            if (_items.Count == 0)
                throw new InvalidOperationException("Stack is empty.");

            return _items[_items.Count - 1];
        }

        public bool TryPeek(out T value)
        {
            if (_items.Count == 0)
            {
                value = default!;
                return false;
            }

            value = _items[_items.Count - 1];
            return true;
        }

        public void Clear()
        {
            _items.Clear();
            _version++;
        }


        public BottomToTopEnumerable BottomToTop()
        {
            return new BottomToTopEnumerable(this);
        }


        public TopToBottomEnumerable TopToBottom()
        {
            return new TopToBottomEnumerable(this);
        }

        public readonly struct BottomToTopEnumerable
        {
            private readonly ListStack<T> _stack;

            internal BottomToTopEnumerable(ListStack<T> stack)
            {
                _stack = stack;
            }

            public BottomToTopEnumerator GetEnumerator()
            {
                return new BottomToTopEnumerator(_stack);
            }
        }

        public struct BottomToTopEnumerator
        {
            private readonly ListStack<T> _stack;
            private readonly int _version;
            private int _index;

            internal BottomToTopEnumerator(ListStack<T> stack)
            {
                _stack = stack;
                _version = stack._version;
                _index = -1;
            }

            public T Current
            {
                get
                {
                    if (_index < 0 || _index >= _stack._items.Count)
                        throw new InvalidOperationException();

                    return _stack._items[_index];
                }
            }

            public bool MoveNext()
            {
                if (_version != _stack._version)
                    throw new InvalidOperationException("Stack was modified during enumeration.");

                int nextIndex = _index + 1;

                if (nextIndex >= _stack._items.Count)
                    return false;

                _index = nextIndex;
                return true;
            }
        }

        public readonly struct TopToBottomEnumerable
        {
            private readonly ListStack<T> _stack;

            internal TopToBottomEnumerable(ListStack<T> stack)
            {
                _stack = stack;
            }

            public TopToBottomEnumerator GetEnumerator()
            {
                return new TopToBottomEnumerator(_stack);
            }
        }

        public struct TopToBottomEnumerator
        {
            private readonly ListStack<T> _stack;
            private readonly int _version;
            private int _index;

            internal TopToBottomEnumerator(ListStack<T> stack)
            {
                _stack = stack;
                _version = stack._version;
                _index = stack._items.Count;
            }

            public T Current
            {
                get
                {
                    if (_index < 0 || _index >= _stack._items.Count)
                        throw new InvalidOperationException();

                    return _stack._items[_index];
                }
            }

            public bool MoveNext()
            {
                if (_version != _stack._version)
                    throw new InvalidOperationException("Stack was modified during enumeration.");

                int nextIndex = _index - 1;

                if (nextIndex < 0)
                    return false;

                _index = nextIndex;
                return true;
            }
        }
    }
}
