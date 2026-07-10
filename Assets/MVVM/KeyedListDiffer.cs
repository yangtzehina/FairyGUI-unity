using System;
using System.Collections.Generic;

namespace FairyGUI.Mvvm
{
    /// <summary>
    /// Index-aligned keyed diff for list bindings: remembers the key of each index and
    /// invokes render only for indices whose key changed since the last Apply. A count
    /// change falls back to a full pass (list controls re-render everything on resize
    /// anyway). Pure C#, no UI dependencies — unit-testable with simulated data.
    /// </summary>
    public class KeyedListDiffer<T, TKey>
    {
        readonly Func<T, TKey> _keySelector;
        readonly List<TKey> _keys = new List<TKey>();

        public KeyedListDiffer(Func<T, TKey> keySelector)
        {
            if (keySelector == null)
                throw new ArgumentNullException("keySelector");
            _keySelector = keySelector;
        }

        /// <summary>
        /// Forgets recorded keys; the next Apply renders everything.
        /// </summary>
        public void Reset()
        {
            _keys.Clear();
        }

        /// <summary>
        /// Records current keys without rendering — for when the caller just re-rendered
        /// everything through another path (e.g. GList.numItems).
        /// </summary>
        public void Record(IReadOnlyList<T> items)
        {
            _keys.Clear();
            int count = items.Count;
            for (int i = 0; i < count; i++)
                _keys.Add(_keySelector(items[i]));
        }

        /// <summary>
        /// Renders indices whose key changed; returns how many were rendered.
        /// </summary>
        public int Apply(IReadOnlyList<T> items, Action<int> render)
        {
            int count = items.Count;
            int rendered = 0;

            if (_keys.Count != count)
            {
                _keys.Clear();
                for (int i = 0; i < count; i++)
                {
                    _keys.Add(_keySelector(items[i]));
                    render(i);
                    rendered++;
                }
                return rendered;
            }

            var comparer = EqualityComparer<TKey>.Default;
            for (int i = 0; i < count; i++)
            {
                TKey key = _keySelector(items[i]);
                if (!comparer.Equals(key, _keys[i]))
                {
                    _keys[i] = key;
                    render(i);
                    rendered++;
                }
            }
            return rendered;
        }
    }
}
