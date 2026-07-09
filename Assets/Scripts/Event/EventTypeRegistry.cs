using System;
using System.Collections.Generic;

namespace FairyGUI
{
    /// <summary>
    /// Interns event type strings to stable int ids. The public event API stays
    /// string-based; dispatchers convert once at each entry point so per-node lookups
    /// along capture/bubble chains compare ints instead of hashing strings.
    /// Main thread only, like the rest of the event system.
    /// </summary>
    public static class EventTypeRegistry
    {
        static readonly Dictionary<string, int> _ids = new Dictionary<string, int>(64, StringComparer.Ordinal);
        static readonly List<string> _names = new List<string>(64);

        public static int GetId(string type)
        {
            if (type == null)
                throw new Exception("event type cant be null");

            int id;
            if (!_ids.TryGetValue(type, out id))
            {
                id = _names.Count;
                _names.Add(type);
                _ids.Add(type, id);
            }
            return id;
        }

        public static string GetName(int id)
        {
            return _names[id];
        }
    }
}
