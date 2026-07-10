using System;
using System.Collections.Generic;

namespace FairyGUI.Mvvm
{
    /// <summary>
    /// Binds an IReadOnlyList property to a GList: when the property is marked dirty,
    /// numItems is refreshed and FairyGUI re-renders items through the supplied
    /// renderItem callback (works with plain and virtual lists). Mutate the list in
    /// place and call vm.MarkDirty(listProperty).
    /// </summary>
    public static class GListBinder
    {
        public static Binder BindList<T>(this Binder binder, ViewModel vm, int propertyIndex,
            GList list, IReadOnlyList<T> items, Action<int, T, GObject> renderItem)
        {
            list.itemRenderer = (index, obj) => renderItem(index, items[index], obj);
            return binder.Bind(vm, propertyIndex, () =>
            {
                int count = items.Count;
                if (list.numItems == count)
                {
                    if (list.isVirtual)
                        list.RefreshVirtualList();
                    else
                        list.numItems = count;
                }
                else
                    list.numItems = count;
            });
        }

        /// <summary>
        /// Keyed variant for non-virtual lists: when the count is unchanged, only items
        /// whose key changed are re-rendered (a full re-render otherwise). Virtual lists
        /// fall back to the plain refresh — their visible items are rebound on demand.
        /// </summary>
        public static Binder BindList<T, TKey>(this Binder binder, ViewModel vm, int propertyIndex,
            GList list, IReadOnlyList<T> items, Func<T, TKey> keySelector, Action<int, T, GObject> renderItem)
        {
            var differ = new KeyedListDiffer<T, TKey>(keySelector);
            list.itemRenderer = (index, obj) => renderItem(index, items[index], obj);
            return binder.Bind(vm, propertyIndex, () =>
            {
                int count = items.Count;
                if (list.isVirtual)
                {
                    if (list.numItems == count)
                        list.RefreshVirtualList();
                    else
                        list.numItems = count;
                    differ.Reset();
                    return;
                }

                if (list.numItems != count)
                {
                    //numItems re-renders every index through itemRenderer
                    list.numItems = count;
                    differ.Record(items);
                    return;
                }

                differ.Apply(items, i => renderItem(i, items[i], list.GetChildAt(i)));
            });
        }
    }
}
