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
    }
}
