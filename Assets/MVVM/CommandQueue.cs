using System;

namespace FairyGUI.Mvvm
{
    /// <summary>
    /// The upstream half of the one-way loop: UI event handlers enqueue commands, game
    /// logic drains them once per frame. Ring buffer, no allocation after warm-up.
    /// Use a struct command type to stay allocation-free.
    /// </summary>
    public class CommandQueue<T>
    {
        T[] _items;
        int _head;
        int _count;

        public CommandQueue(int capacity = 16)
        {
            _items = new T[Math.Max(4, capacity)];
        }

        public int count
        {
            get { return _count; }
        }

        public void Enqueue(in T command)
        {
            if (_count == _items.Length)
            {
                T[] grown = new T[_items.Length * 2];
                for (int i = 0; i < _count; i++)
                    grown[i] = _items[(_head + i) % _items.Length];
                _items = grown;
                _head = 0;
            }

            _items[(_head + _count) % _items.Length] = command;
            _count++;
        }

        public bool TryDequeue(out T command)
        {
            if (_count == 0)
            {
                command = default;
                return false;
            }

            command = _items[_head];
            _items[_head] = default;
            _head = (_head + 1) % _items.Length;
            _count--;
            return true;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _head = 0;
            _count = 0;
        }
    }
}
