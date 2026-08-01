using System;
using System.Collections.Generic;

namespace FairyGUI.Mvvm
{
    /// <summary>
    /// Binds a list property to a GList: when the property is marked dirty, numItems
    /// is refreshed and FairyGUI re-renders items through the supplied renderItem
    /// callback (plain and virtual lists both).
    ///
    /// The primary overloads take a GETTER, not a list instance. The binder can only
    /// see the collection when something asks for it, and two things ask on their own
    /// schedule: the apply (per Flush) and GList's OWN itemRenderer (per scroll, for
    /// virtual lists — with an index space from its cached numItems, which is only
    /// re-synced at Flush). A captured instance therefore renders stale data for ever
    /// once the property is reassigned, and a captured Count can be a lie by the time
    /// the renderer runs. The getter re-reads the property at every use, and the
    /// renderer range-checks against the CURRENT list before indexing.
    /// </summary>
    public static class GListBinder
    {
        public static Binder BindList<T>(this Binder binder, ViewModel vm, int propertyIndex,
            GList list, Func<IReadOnlyList<T>> items, Action<int, T, GObject> renderItem)
        {
            list.itemRenderer = (index, obj) =>
            {
                IReadOnlyList<T> src = items();
                //GList renders on its own schedule (scroll paths call the renderer
                //directly) with an index from its cached numItems; after the model
                //shrank but before the next Flush that index can be out of range.
                //Skipping is safe: the apply below re-syncs and re-renders.
                if (src == null || index < 0 || index >= src.Count)
                    return;
                renderItem(index, src[index], obj);
            };
            return binder.Bind(vm, propertyIndex, () =>
            {
                IReadOnlyList<T> src = items();
                int count = src != null ? src.Count : 0;
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
            GList list, Func<IReadOnlyList<T>> items, Func<T, TKey> keySelector, Action<int, T, GObject> renderItem)
        {
            var differ = new KeyedListDiffer<T, TKey>(keySelector);
            list.itemRenderer = (index, obj) =>
            {
                IReadOnlyList<T> src = items();
                if (src == null || index < 0 || index >= src.Count)
                    return; //see the plain overload
                renderItem(index, src[index], obj);
            };
            return binder.Bind(vm, propertyIndex, () =>
            {
                IReadOnlyList<T> src = items();
                int count = src != null ? src.Count : 0;
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
                    if (src != null)
                        differ.Record(src);
                    else
                        differ.Reset();
                    return;
                }

                if (src != null)
                    differ.Apply(src, i => renderItem(i, src[i], list.GetChildAt(i)));
            });
        }

        /// <summary>
        /// Instance convenience overload — for lists mutated strictly IN PLACE
        /// (add/remove/replace elements, then vm.MarkDirty). If the property is ever
        /// REASSIGNED to a new list instance, this binding keeps rendering the old
        /// one; use the getter overload for reassignable properties.
        /// </summary>
        public static Binder BindList<T>(this Binder binder, ViewModel vm, int propertyIndex,
            GList list, IReadOnlyList<T> items, Action<int, T, GObject> renderItem)
        {
            return BindList(binder, vm, propertyIndex, list, () => items, renderItem);
        }

        /// <summary>Instance convenience overload; same in-place-only caveat as above.</summary>
        public static Binder BindList<T, TKey>(this Binder binder, ViewModel vm, int propertyIndex,
            GList list, IReadOnlyList<T> items, Func<T, TKey> keySelector, Action<int, T, GObject> renderItem)
        {
            return BindList(binder, vm, propertyIndex, list, () => items, keySelector, renderItem);
        }
    }
}
