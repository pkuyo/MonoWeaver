using System;
using System.Collections.Generic;
using System.Text;

namespace MonoWeaver.Utils
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;

    public sealed class FixedSizeDictionary<TKey, TValue>
    {
        private sealed class Node
        {
            private object? _value;

            public Node(TValue value)
            {
                _value = (object?)value;
            }

            public TValue? GetValue()
            {
                return (TValue?)Volatile.Read(ref _value);
            }

            public void SetValue(TValue value)
            {
                Volatile.Write(ref _value, (object?)value);
            }
        }

        private struct QueueItem
        {
            public readonly TKey Key;
            public readonly Node Node;

            public QueueItem(TKey key, Node node)
            {
                Key = key;
                Node = node;
            }
        }

        private readonly int _capacity;
        private readonly ConcurrentDictionary<TKey, Node> _dict;
        private readonly ConcurrentQueue<QueueItem> _queue;

        private int _count;
        private int _trimming;

        public FixedSizeDictionary(int capacity)
            : this(capacity, null)
        {
        }

        public FixedSizeDictionary(int capacity, IEqualityComparer<TKey>? comparer)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException("capacity");

            _capacity = capacity;

            if (comparer == null)
                comparer = EqualityComparer<TKey>.Default;

            int concurrencyLevel = Math.Max(1, Environment.ProcessorCount * 2);

            _dict = new ConcurrentDictionary<TKey, Node>(
                concurrencyLevel,
                capacity,
                comparer);

            _queue = new ConcurrentQueue<QueueItem>();
        }

        public int Capacity
        {
            get { return _capacity; }
        }

        public int Count
        {
            get { return Volatile.Read(ref _count); }
        }


        public bool TryGetValue(TKey key, out TValue? value)
        {
            Node node;
            if (_dict.TryGetValue(key, out node))
            {
                value = node.GetValue();
                return true;
            }

            value = default(TValue);
            return false;
        }

        public TValue? GetOrAdd(TKey key, TValue value)
        {
            while (true)
            {
                Node existingNode;
                if (_dict.TryGetValue(key, out existingNode))
                    return existingNode.GetValue();

                Node newNode = new Node(value);
                if (_dict.TryAdd(key, newNode))
                {
                    Interlocked.Increment(ref _count);
                    _queue.Enqueue(new QueueItem(key, newNode));
                    TrimIfNeeded();
                    return value;
                }
            }
        }

        public TValue? GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (valueFactory == null)
                throw new ArgumentNullException("valueFactory");

            while (true)
            {
                Node existingNode;
                if (_dict.TryGetValue(key, out existingNode))
                    return existingNode.GetValue();

                TValue value = valueFactory(key);
                Node newNode = new Node(value);
                if (_dict.TryAdd(key, newNode))
                {
                    Interlocked.Increment(ref _count);
                    _queue.Enqueue(new QueueItem(key, newNode));
                    TrimIfNeeded();
                    return value;
                }
            }
        }


        public void AddOrUpdate(TKey key, TValue value)
        {
            while (true)
            {
                Node existingNode;

                if (_dict.TryGetValue(key, out existingNode))
                {
                    existingNode.SetValue(value);
                    if (_dict.TryUpdate(key, existingNode, existingNode))
                        return;

                    continue;
                }

                Node newNode = new Node(value);

                if (_dict.TryAdd(key, newNode))
                {
                    Interlocked.Increment(ref _count);
                    _queue.Enqueue(new QueueItem(key, newNode));

                    TrimIfNeeded();
                    return;
                }
            }
        }


        public bool TryAdd(TKey key, TValue value)
        {
            Node node = new Node(value);

            if (!_dict.TryAdd(key, node))
                return false;

            Interlocked.Increment(ref _count);
            _queue.Enqueue(new QueueItem(key, node));

            TrimIfNeeded();
            return true;
        }


        public bool TryUpdate(TKey key, TValue value)
        {
            while (true)
            {
                Node node;

                if (!_dict.TryGetValue(key, out node))
                    return false;

                node.SetValue(value);

                if (_dict.TryUpdate(key, node, node))
                    return true;
            }
        }

        public bool TryRemove(TKey key)
        {
            return TryRemove(key, out _);
        }

        public bool TryRemove(TKey key, out TValue? value)
        {
            Node node;

            if (_dict.TryRemove(key, out node))
            {
                value = node.GetValue();
                Interlocked.Decrement(ref _count);
                return true;
            }

            value = default(TValue);
            return false;
        }

        public bool ContainsKey(TKey key)
        {
            return _dict.ContainsKey(key);
        }

        public KeyValuePair<TKey, TValue?>[] ToArray()
        {
            KeyValuePair<TKey, Node>[] source = _dict.ToArray();
            KeyValuePair<TKey, TValue?>[] result =
                new KeyValuePair<TKey, TValue?>[source.Length];

            for (int i = 0; i < source.Length; i++)
            {
                result[i] = new KeyValuePair<TKey, TValue?>(
                    source[i].Key,
                    source[i].Value.GetValue());
            }

            return result;
        }

        private void TrimIfNeeded()
        {
            if (Volatile.Read(ref _count) <= _capacity)
                return;

            if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
                return;

            try
            {
                while (Volatile.Read(ref _count) > _capacity)
                {
                    QueueItem item;

                    if (!_queue.TryDequeue(out item))
                        return;

                    Node currentNode;

                    if (!_dict.TryGetValue(item.Key, out currentNode))
                        continue;

                    if (!object.ReferenceEquals(currentNode, item.Node))
                        continue;

                    if (TryRemoveExact(item.Key, item.Node))
                    {
                        Interlocked.Decrement(ref _count);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _trimming, 0);
            }
        }

        private bool TryRemoveExact(TKey key, Node expectedNode)
        {
            ICollection<KeyValuePair<TKey, Node>> collection = _dict;

            return collection.Remove(
                new KeyValuePair<TKey, Node>(key, expectedNode));
        }
    }

}
