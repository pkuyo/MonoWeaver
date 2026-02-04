using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MonoWeaver.Utils
{
    public class LruCache<TKey, TValue>
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cacheMap;
        private readonly LinkedList<CacheItem> _lruList;

        private class CacheItem
        {
            public TKey Key { get; set; }
            public TValue Value { get; set; }
        }

        public LruCache(int capacity)
        {
            if (capacity <= 0) throw new ArgumentException("Capacity must be greater than 0");
            _capacity = capacity;
            _cacheMap = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
            _lruList = new LinkedList<CacheItem>();
        }


        public TValue Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                MoveToHead(node);
                return node.Value.Value;
            }

            throw new KeyNotFoundException($"Key '{key}' not found in cache.");
        }


        public bool TryGetValue(TKey key, out TValue? value)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                MoveToHead(node);
                value = node.Value.Value;
                return true;
            }

            value = default;
            return false;
        }


        public void Put(TKey key, TValue value)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                node.Value.Value = value;
                MoveToHead(node);
            }
            else
            {

                if (_cacheMap.Count >= _capacity)
                {
                    RemoveLeastUsed();
                }

                var newItem = new CacheItem { Key = key, Value = value };
                var newNode = new LinkedListNode<CacheItem>(newItem);

                _lruList.AddFirst(newNode);
                _cacheMap.Add(key, newNode);
            }
        }

        private void MoveToHead(LinkedListNode<CacheItem> node)
        {
            if (node != _lruList.First)
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
            }
        }

        private void RemoveLeastUsed()
        {
            var lastNode = _lruList.Last;
            if (lastNode != null)
            {
                _cacheMap.Remove(lastNode.Value.Key);
                _lruList.RemoveLast();
            }
        }
    }
}
