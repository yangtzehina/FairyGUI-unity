using System.Collections.Generic;

namespace FairyGUI
{
    /// <summary>
    /// One sortable leaf for AdjacencySorter: a batching key (texture), the leaf's
    /// container-local bounds, and the caller's index into its own leaf list.
    /// </summary>
    public struct AdjacencyEntry
    {
        public object key;
        public float x0, y0, x1, y1;
        public int payload;
    }

    /// <summary>
    /// FairyBatching's adjacency sort (Container.DoFairyBatching), extracted as a
    /// pure list operation so the instanced stream can shrink its texture segments:
    /// each element sinks toward the nearest earlier run with the same key, but never
    /// past an element whose bounds overlap it — so draw order changes only where it
    /// is visually indistinguishable.
    ///
    /// Pure over the list (no scene dependency) so synthetic-data validation can
    /// drive it directly.
    /// </summary>
    public static class AdjacencySorter
    {
        public static void Sort(List<AdjacencyEntry> items)
        {
            int cnt = items.Count;
            for (int i = 0; i < cnt; i++)
            {
                AdjacencyEntry current = items[i];
                object curKey = current.key;
                if (curKey == null)
                    continue;

                int k = -1;
                int m = i;
                object lastKey = null;
                for (int j = i - 1; j >= 0; j--)
                {
                    AdjacencyEntry test = items[j];
                    object testKey = test.key;
                    if (testKey != null)
                    {
                        if (lastKey != testKey)
                        {
                            lastKey = testKey;
                            m = j + 1;
                        }
                        if (curKey == testKey)
                            k = m;
                    }

                    if ((current.x0 > test.x0 ? current.x0 : test.x0)
                        <= (current.x1 < test.x1 ? current.x1 : test.x1)
                        && (current.y0 > test.y0 ? current.y0 : test.y0)
                        <= (current.y1 < test.y1 ? current.y1 : test.y1))
                    {
                        if (k == -1)
                            k = m;
                        break;
                    }
                }
                if (k != -1 && i != k)
                {
                    items.RemoveAt(i);
                    items.Insert(k, current);
                }
            }
        }
    }
}
