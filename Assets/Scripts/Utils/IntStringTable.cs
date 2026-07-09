using System.Collections.Generic;

namespace FairyGUI
{
    /// <summary>
    /// Memoized number-to-string conversion for allocation-free counters. The text-change
    /// pipeline itself (parse, layout, mesh rebuild) is allocation-free; the remaining
    /// per-change garbage of a typical counter is the ToString/concat on the caller side.
    /// Get returns the same cached string instance for the same value, so repeated values
    /// allocate nothing and TextField's same-value short-circuit can kick in.
    ///
    /// Each distinct value outside 0..9999 is cached forever (one small string plus
    /// dictionary slot) — intended for bounded counters (gold, ammo, damage numbers),
    /// not for values that never repeat (timestamps). Call Clear between levels if needed.
    /// </summary>
    public static class IntStringTable
    {
        const int SmallRange = 10000;
        static readonly string[] _small = new string[SmallRange];
        static readonly Dictionary<long, string> _large = new Dictionary<long, string>();

        public static string Get(long value)
        {
            if (value >= 0 && value < SmallRange)
            {
                string s = _small[value];
                if (s == null)
                {
                    s = value.ToString();
                    _small[value] = s;
                }
                return s;
            }

            string cached;
            if (!_large.TryGetValue(value, out cached))
            {
                cached = value.ToString();
                _large.Add(value, cached);
            }
            return cached;
        }

        public static void Clear()
        {
            _large.Clear();
        }
    }

    public static class TextFieldExtensions
    {
        /// <summary>
        /// Sets the text to a number without allocating for repeated values. Keep label
        /// and value in separate text objects ("Gold:" + counter) so the counter side
        /// stays allocation-free.
        /// </summary>
        public static void SetIntText(this GTextField textField, long value)
        {
            textField.text = IntStringTable.Get(value);
        }

        public static void SetIntText(this TextField textField, long value)
        {
            textField.text = IntStringTable.Get(value);
        }
    }
}
